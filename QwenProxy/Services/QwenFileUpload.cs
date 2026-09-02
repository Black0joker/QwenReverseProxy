using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using QwenProxy.Config;
using QwenProxy.Logging;

namespace QwenProxy.Services;

/// <summary>
/// Port of module.py OSS-based file upload helpers (STS token, V4 HMAC
/// signing, upload) plus the upload_file_content entry point.
/// </summary>
public static class QwenFileUpload
{
    private const string FileUploadBaseUrl = "https://chat.qwen.ai";

    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseCookies = false })
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    private static readonly QwenLogger Log = new();

    private static readonly Dictionary<string, string> FileContentTypes = new()
    {
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".json"] = "application/json",
        [".csv"] = "text/csv",
        [".html"] = "text/html",
        [".xml"] = "application/xml",
        [".py"] = "text/x-python",
        [".js"] = "application/javascript",
        [".css"] = "text/css",
        [".log"] = "text/plain",
    };

    /// <summary>Request an STS token from the API for file upload.</summary>
    public static async Task<JsonObject> GetStsTokenAsync(
        string filename, long filesize, string filetype = "file", string? token = null, CancellationToken ct = default)
    {
        var cookies = token is not null
            ? CookieStore.BuildCookiesFromToken(token)
            : new CookieStore().BuildCookiesWithActiveToken();

        var req = new HttpRequestMessage(HttpMethod.Post, $"{FileUploadBaseUrl}/api/v2/files/getstsToken")
        {
            Content = new StringContent(
                new JsonObject
                {
                    ["filename"] = filename,
                    ["filesize"] = filesize.ToString(),
                    ["filetype"] = filetype,
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json"),
        };

        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36");
        req.Headers.TryAddWithoutValidation("source", "web");
        if (cookies.Count > 0)
        {
            req.Headers.TryAddWithoutValidation(
                "Cookie", string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        var response = await Http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
        return json!["data"]!.AsObject();
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    /// <summary>Compute the OSS V4 HMAC-SHA256 signature.</summary>
    public static string ComputeSignatureV4(
        string secret, string dateString, string region, string product, string canonicalRequest)
    {
        string date = dateString.Split('T')[0];
        string hashedCanonicalRequest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLowerInvariant();
        string stringToSign =
            $"OSS4-HMAC-SHA256\n{dateString}\n{date}/{region}/{product}/aliyun_v4_request\n{hashedCanonicalRequest}";

        byte[] s = HmacSha256(Encoding.UTF8.GetBytes("aliyun_v4" + secret), date);
        byte[] a = HmacSha256(s, region);
        byte[] u = HmacSha256(a, product);
        byte[] c = HmacSha256(u, "aliyun_v4_request");

        return Convert.ToHexString(HmacSha256(c, stringToSign)).ToLowerInvariant();
    }

    /// <summary>
    /// Percent-encode like Python's urllib.parse.quote(s, safe='/'):
    /// unreserved characters and '/' pass through; everything else is %XX (uppercase).
    /// </summary>
    private static string QuotePath(string value)
    {
        var sb = new StringBuilder(value.Length * 2);
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            char ch = (char)b;
            if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')
                || ch is '-' or '_' or '.' or '~' or '/')
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2"));
            }
        }
        return sb.ToString();
    }

    /// <summary>Build the canonical request string for OSS V4 upload signing.</summary>
    private static string BuildUploadCanonicalRequest(
        string method, string bucket, string objectPath,
        IReadOnlyDictionary<string, string> putHeaders, string payloadHash)
    {
        var filtered = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in putHeaders)
        {
            string lowerKey = kv.Key.ToLowerInvariant();
            if (lowerKey is "content-type" or "content-md5" || lowerKey.StartsWith("x-oss-"))
            {
                filtered[lowerKey] = kv.Value.Trim();
            }
        }

        var headersSb = new StringBuilder();
        foreach (var kv in filtered)
        {
            headersSb.Append(kv.Key).Append(':').Append(kv.Value).Append('\n');
        }
        string canonicalHeadersStr = headersSb.ToString();

        string canonicalUri = QuotePath($"/{bucket}/{objectPath}").Replace("%2F", "/");

        return $"{method.ToUpperInvariant()}\n{canonicalUri}\n\n{canonicalHeadersStr}\n\n{payloadHash}";
    }

    /// <summary>Upload file data to Alibaba Cloud OSS using STS credentials.</summary>
    public static async Task UploadToOssAsync(
        JsonObject stsData, byte[] fileData, string contentType = "text/plain", CancellationToken ct = default)
    {
        string accessKeyId = stsData["access_key_id"]!.GetValue<string>();
        string accessKeySecret = stsData["access_key_secret"]!.GetValue<string>();
        string securityToken = stsData["security_token"]!.GetValue<string>();
        string bucket = stsData["bucketname"]!.GetValue<string>();
        string objectPath = stsData["file_path"]!.GetValue<string>();
        string region = stsData["region"]!.GetValue<string>();
        string regionForSignature = region.Replace("oss-", "");
        string endpoint = stsData["endpoint"]!.GetValue<string>();

        const string method = "PUT";
        const string payloadHash = "UNSIGNED-PAYLOAD";
        string xOssDate = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");

        var putHeaders = new Dictionary<string, string>
        {
            ["Host"] = $"{bucket}.{endpoint}",
            ["x-oss-date"] = xOssDate,
            ["x-oss-security-token"] = securityToken,
            ["x-oss-content-sha256"] = payloadHash,
            ["content-type"] = contentType,
        };

        string canonicalRequest = BuildUploadCanonicalRequest(method, bucket, objectPath, putHeaders, payloadHash);
        string signature = ComputeSignatureV4(accessKeySecret, xOssDate, regionForSignature, "oss", canonicalRequest);

        putHeaders["authorization"] =
            $"OSS4-HMAC-SHA256 " +
            $"Credential={accessKeyId}/{xOssDate.Split('T')[0]}/{regionForSignature}/oss/aliyun_v4_request," +
            $"Signature={signature}";

        var req = new HttpRequestMessage(HttpMethod.Put, $"https://{bucket}.{endpoint}/{objectPath}")
        {
            Content = new ByteArrayContent(fileData),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        foreach (var kv in putHeaders)
        {
            if (kv.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue; // set by transport
            if (kv.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase)) continue; // set on content
            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }

        var response = await Http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Build the send_prompt-compatible file object after an OSS upload.
    /// Object path format: {user_id}/{file_id}_{filename}.
    /// </summary>
    public static JsonObject BuildFileObject(
        JsonObject stsData, string filename, long filesize, string contentType, bool isImage)
    {
        string objectPath = stsData["file_path"]!.GetValue<string>();
        string pathFilename = objectPath.Split('/').Last();
        // Strip only the trailing "_{filename}" suffix (Replace would remove
        // every occurrence, corrupting IDs when the filename repeats).
        string fileIdSuffix = $"_{filename}";
        string fileId = pathFilename.EndsWith(fileIdSuffix, StringComparison.Ordinal)
            ? pathFilename[..^fileIdSuffix.Length]
            : pathFilename;

        string itemId = Guid.NewGuid().ToString();
        string uploadTaskId = Guid.NewGuid().ToString()[..16];
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string url = stsData["file_url"]?.GetValue<string>() ?? "";
        string userId = stsData["user_id"]?.GetValue<string>() ?? "";

        var meta = new JsonObject
        {
            ["name"] = filename,
            ["size"] = filesize,
            ["content_type"] = contentType,
        };
        if (!isImage)
        {
            meta["parse_meta"] = new JsonObject { ["parse_status"] = "success" };
        }

        var innerFile = new JsonObject
        {
            ["created_at"] = nowMs,
            ["data"] = new JsonObject(),
            ["filename"] = filename,
            ["hash"] = null,
            ["id"] = fileId,
            ["user_id"] = userId,
            ["meta"] = meta,
            ["update_at"] = nowMs,
            ["lastModified"] = nowMs,
            ["name"] = filename,
            ["webkitRelativePath"] = "",
            ["size"] = filesize,
            ["type"] = contentType,
        };

        return new JsonObject
        {
            ["type"] = isImage ? "image" : "file",
            ["file"] = innerFile,
            ["id"] = fileId,
            ["url"] = url,
            ["name"] = filename,
            ["collection_name"] = "",
            ["progress"] = 0,
            ["status"] = "uploaded",
            ["greenNet"] = "success",
            ["size"] = filesize,
            ["error"] = "",
            ["itemId"] = itemId,
            ["file_type"] = contentType,
            ["showType"] = isImage ? "image" : "file",
            ["file_class"] = isImage ? "vision" : "document",
            ["uploadTaskId"] = uploadTaskId,
        };
    }

    /// <summary>
    /// Upload content as a file and return the file object for use in the
    /// send_prompt files array (port of module.upload_file_content / _handle_file_upload).
    /// </summary>
    public static async Task<JsonObject> UploadFileContentAsync(
        string content, string filename = "tool_output.txt", string? token = null, CancellationToken ct = default)
    {
        byte[] fileData = Encoding.UTF8.GetBytes(content);
        long filesize = fileData.Length;

        string ext = Path.GetExtension(filename).ToLowerInvariant();
        string contentType = FileContentTypes.TryGetValue(ext, out var ct1) ? ct1 : "text/plain";

        Log.Info($"file_upload: uploading '{filename}' ({filesize} bytes) as {contentType}");

        var stsData = await GetStsTokenAsync(filename, filesize, "file", token, ct);
        await UploadToOssAsync(stsData, fileData, contentType, ct);

        var fileObj = BuildFileObject(stsData, filename, filesize, contentType, isImage: false);
        Log.Success($"file_upload: uploaded '{filename}' -> {fileObj["url"]}");
        return fileObj;
    }
}

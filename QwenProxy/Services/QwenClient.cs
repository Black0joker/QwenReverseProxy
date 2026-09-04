using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QwenProxy.Config;
using QwenProxy.Logging;

namespace QwenProxy.Services;

/// <summary>Result of SendPromptAsync: either a JSON payload, a captcha challenge, or a live SSE stream.</summary>
public sealed class SendPromptResult : IDisposable
{
    /// <summary>Non-streaming JSON response body (e.g. upstream error).</summary>
    public JsonNode? JsonPayload { get; init; }

    /// <summary>Captcha challenge: {"error":"captcha","url":...}.</summary>
    public JsonObject? Captcha { get; init; }

    /// <summary>Live streaming response (SSE). Caller owns disposal.</summary>
    public HttpResponseMessage? StreamResponse { get; init; }

    public void Dispose() => StreamResponse?.Dispose();
}

/// <summary>
/// Port of module.QwenClient: client for the Qwen chat API.
/// A fresh client is created per API request, authenticated with the
/// caller-supplied token (forwarded to Qwen as a 'token' cookie).
/// </summary>
public sealed class QwenClient
{
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        // Cookies are attached manually per request (per-token sessions).
        UseCookies = false,
        AllowAutoRedirect = true,
    })
    {
        // Streaming completions can run for minutes; HttpClient.Timeout only
        // applies until response headers arrive when using ResponseHeadersRead.
        Timeout = TimeSpan.FromMinutes(10),
    };

    private static readonly QwenLogger Log = new();

    private readonly Dictionary<string, string> _cookies;

    public string Model { get; set; }
    public string Mode { get; set; }

    private const string NewChatUrl = "https://chat.qwen.ai/api/v2/chats/new";

    public QwenClient(string model = "qwen3.8-max", string mode = "normal", string? token = null, string? s5Token = null, CookieStore? cookieStore = null)
    {
        Model = model;
        Mode = mode;

        // Use an explicit token (e.g. passed via request header) when provided;
        // otherwise fall back to cookies loaded from the JSON config file.
        _cookies = token is not null
            ? CookieStore.BuildCookiesFromToken(token)
            : (cookieStore ?? new CookieStore()).BuildCookiesWithActiveToken();

        // Forward the decrypted s5_token to Qwen as the 'x5sec' cookie.
        if (token is not null && !string.IsNullOrEmpty(s5Token))
        {
            _cookies["x5sec"] = s5Token;
        }
    }

    private string BuildCookieHeader()
    {
        if (_cookies.Count == 0) return "";
        return string.Join("; ", _cookies.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    /// <summary>
    /// Build a request with fresh per-request headers (unique X-Request-Id,
    /// current Timezone) and the session cookies.
    /// </summary>
    private HttpRequestMessage BuildRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        QwenHeaders.ApplyTo(req, QwenHeaders.BuildHeaders());
        string cookieHeader = BuildCookieHeader();
        if (cookieHeader.Length > 0)
            req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        return req;
    }

    private static StringContent JsonContent(JsonNode payload)
    {
        var options = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return new StringContent(payload.ToJsonString(options), Encoding.UTF8, "application/json");
    }

    /// <summary>Centralized request with standard error handling (raise_for_status).</summary>
    private async Task<HttpResponseMessage> RequestAsync(
        HttpMethod method, string url, HttpContent? content = null, CancellationToken ct = default)
    {
        var req = BuildRequest(method, url);
        if (content is not null) req.Content = content;
        var response = await SharedHttp.SendAsync(req, ct).ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            response.Dispose();
            throw;
        }
        return response;
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct = default)
    {
        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonNode.Parse(body);
        }
    }

    // ------------------------------------------------------------------
    // Chat operations
    // ------------------------------------------------------------------

    public async Task<string> CreateChatAsync(CancellationToken ct = default)
    {
        long timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payload = new JsonObject
        {
            ["models"] = new JsonArray(Model),
            ["chat_mode"] = "normal",
            ["chat_type"] = "t2t",
            ["timestamp"] = timestampMs,
            ["project_id"] = "",
        };

        using var response = await RequestAsync(HttpMethod.Post, NewChatUrl, JsonContent(payload), ct);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return json!["data"]!["id"]!.GetValue<string>();
    }

    /// <summary>Fetches a list of chat sessions.</summary>
    public async Task<JsonNode?> FetchChatsAsync(CancellationToken ct = default)
    {
        Log.Info("Fetching chat sessions...");
        using var response = await RequestAsync(
            HttpMethod.Get,
            "https://chat.qwen.ai/api/v2/chats/?page=1&exclude_project=true",
            ct: ct);
        var json = await ReadJsonAsync(response, ct);
        var chats = json?["data"];
        Log.Success($"Fetched chat sessions.");
        return chats;
    }

    /// <summary>Fetches all chat sessions with full history in a single request.</summary>
    public async Task<JsonNode?> FetchAllChatsAsync(CancellationToken ct = default)
    {
        Log.Info("Fetching all chats with full history...");
        using var response = await RequestAsync(
            HttpMethod.Get, "https://chat.qwen.ai/api/v2/chats/all", ct: ct);
        var data = await ReadJsonAsync(response, ct);
        bool success = data is JsonObject o &&
                       o.TryGetPropertyValue("success", out var s) &&
                       s is not null && s.GetValueKind() == JsonValueKind.True;
        if (!success)
        {
            Log.Error($"fetch_all_chats returned unsuccessful response: {data?.ToJsonString()}");
            throw new InvalidOperationException(
                $"Server returned success=false for /api/v2/chats/all: {data?.ToJsonString()}");
        }
        int count = data?["data"] is JsonArray arr ? arr.Count : 0;
        Log.Success($"Fetched {count} chat(s) with full history.");
        return data;
    }

    /// <summary>Gets the current/parent message ID for a chat session.</summary>
    public async Task<string?> GetChatParentIdAsync(string chatSessionId, CancellationToken ct = default)
    {
        Log.Info($"Fetching chat history for session: {chatSessionId}");
        using var response = await RequestAsync(
            HttpMethod.Get, $"https://chat.qwen.ai/api/v2/chats/{chatSessionId}", ct: ct);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        var node = json?["data"]?["currentId"];
        Log.Success($"Fetched history for session: {chatSessionId}");
        return node?.GetValue<string>();
    }

    /// <summary>Gets the full chat history for a given chat session.</summary>
    public async Task<JsonNode?> GetChatHistoryAsync(string chatSessionId, CancellationToken ct = default)
    {
        Log.Info($"Fetching chat history for session: {chatSessionId}");
        using var response = await RequestAsync(
            HttpMethod.Get, $"https://chat.qwen.ai/api/v2/chats/{chatSessionId}", ct: ct);
        var data = await ReadJsonAsync(response, ct);
        Log.Success($"Fetched history for session: {chatSessionId}");
        return data;
    }

    public async Task<string?> GetChatTitleAsync(string chatSessionId, CancellationToken ct = default)
    {
        Log.Info($"Fetching chat title for session: {chatSessionId}");
        using var response = await RequestAsync(
            HttpMethod.Get, $"https://chat.qwen.ai/api/v2/chats/{chatSessionId}", ct: ct);
        var data = await ReadJsonAsync(response, ct);
        Log.Success($"Fetched title for session: {chatSessionId}");
        var node = data?["data"]?["title"];
        return node?.GetValue<string>();
    }

    /// <summary>Gets the chat history with the 'data' payload wrapped in a list.</summary>
    public async Task<JsonNode?> GetChatMessagesAsync(string chatSessionId, CancellationToken ct = default)
    {
        Log.Info($"Fetching chat history for session: {chatSessionId}");
        using var response = await RequestAsync(
            HttpMethod.Get, $"https://chat.qwen.ai/api/v2/chats/{chatSessionId}", ct: ct);
        var data = await ReadJsonAsync(response, ct) as JsonObject;
        Log.Success($"Fetched history for session: {chatSessionId}");
        if (data is not null)
        {
            var messages = data["data"];
            data["data"] = new JsonArray(messages?.DeepClone());
        }
        return data;
    }

    /// <summary>Deletes a chat session.</summary>
    public async Task<JsonNode?> DeleteChatAsync(string chatSessionId, CancellationToken ct = default)
    {
        Log.Info($"Deleting chat session: {chatSessionId}");
        using var response = await RequestAsync(
            HttpMethod.Delete, $"https://chat.qwen.ai/api/v2/chats/{chatSessionId}", ct: ct);
        var data = await ReadJsonAsync(response, ct);
        Log.Success($"Deleted chat session: {chatSessionId}");
        return data;
    }

    /// <summary>Upload a chat (in-memory JSON payload) via multipart form.</summary>
    public async Task<JsonNode?> UploadChatAsync(JsonObject data, CancellationToken ct = default)
    {
        Log.Info("Uploading chat from in-memory JSON data...");
        var req = BuildRequest(HttpMethod.Post, "https://chat.qwen.ai/api/v2/chats/import");

        var options = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        byte[] jsonBytes = Encoding.UTF8.GetBytes(data.ToJsonString(options));

        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(jsonBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(fileContent, "file", "upload.json");
        req.Content = form;

        var response = await SharedHttp.SendAsync(req, ct);
        try
        {
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            response.Dispose();
            throw;
        }
        var result = await ReadJsonAsync(response, ct);
        Log.Success($"Chat uploaded: {result?.ToJsonString()}");
        return result;
    }

    /// <summary>Upload an image via the OSS pipeline; returns a send_prompt-compatible file object.</summary>
    public async Task<JsonObject> UploadImageAsync(byte[] fileData, string filename, CancellationToken ct = default)
    {
        long filesize = fileData.Length;

        string ext = Path.GetExtension(filename).ToLowerInvariant();
        string contentType = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".tiff" or ".tif" => "image/tiff",
            _ => "image/jpeg",
        };

        Log.Info($"Uploading image for chat: {filename} ({filesize} bytes, {contentType})");

        // The explicit token used to authenticate this client (may be null when
        // the client was built from the cookie store).
        string? token = _cookies.TryGetValue("token", out var t) ? t : null;

        var stsData = await QwenFileUpload.GetStsTokenAsync(filename, filesize, "image", token, ct);
        await QwenFileUpload.UploadToOssAsync(stsData, fileData, contentType, ct);

        var fileObj = QwenFileUpload.BuildFileObject(stsData, filename, filesize, contentType, isImage: true);
        Log.Success($"Image uploaded: {filename} -> {fileObj["url"]}");
        return fileObj;
    }

    public async Task StopSendingPromptAsync(string chatId, string parentMessageId, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["chat_id"] = chatId,
            ["response_id"] = parentMessageId,
        };
        using var response = await RequestAsync(
            HttpMethod.Post,
            $"https://chat.qwen.ai/api/v2/chat/completions/stop?chat_id={Uri.EscapeDataString(chatId)}",
            JsonContent(payload),
            ct);
        // Response intentionally ignored (matches module.StopSendingPrompt).
    }

    /// <summary>
    /// Send a prompt. Returns either a JSON payload (upstream error / captcha)
    /// or a live streaming response whose content is the Qwen SSE stream.
    /// When solved captcha cookies are provided inline in the request, they
    /// are all attached to the upstream request as cookies.
    /// </summary>
    public async Task<SendPromptResult> SendPromptAsync(
        string prompt,
        string chatId,
        string? parentMessageId = null,
        List<JsonObject>? files = null,
   
        CancellationToken ct = default)
    {
        if (files is { Count: > 0 })
            Log.Info($"send_prompt: attaching {files.Count} file(s) to message payload");


        JsonObject featureConfig;
        if (Mode == "thinking")
        {
            featureConfig = new JsonObject
            {
                ["thinking_enabled"] = true,
                ["output_schema"] = "phase",
                ["research_mode"] = "normal",
                ["auto_thinking"] = true,
                ["thinking_mode"] = "Thinking",
                ["thinking_format"] = "summary",
                ["auto_search"] = true,
            };
        }
        else
        {
            featureConfig = new JsonObject
            {
                ["thinking_enabled"] = false,
                ["output_schema"] = "phase",
                ["research_mode"] = "normal",
                ["auto_thinking"] = false,
                ["thinking_mode"] = "Fast",
                ["auto_search"] = true,
            };
        }

        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var filesArray = new JsonArray();
        if (files is not null)
        {
            foreach (var f in files) filesArray.Add(f.DeepClone());
        }

        var message = new JsonObject
        {
            ["fid"] = Guid.NewGuid().ToString(),
            ["parentId"] = parentMessageId,
            ["childrenIds"] = new JsonArray(Guid.NewGuid().ToString()),
            ["role"] = "user",
            ["content"] = prompt,
            ["user_action"] = "chat",
            ["files"] = filesArray,
            ["timestamp"] = timestamp,
            ["models"] = new JsonArray(Model),
            ["chat_type"] = "t2t",
            ["feature_config"] = featureConfig,
            ["extra"] = new JsonObject
            {
                ["meta"] = new JsonObject { ["subChatType"] = "t2t" },
            },
            ["sub_chat_type"] = "t2t",
        };

        var payload = new JsonObject
        {
            ["stream"] = true,
            ["version"] = "2.1",
            ["incremental_output"] = true,
            ["chat_id"] = chatId,
            ["chat_mode"] = "normal",
            ["model"] = Model,
            ["parent_id"] = parentMessageId,
            ["messages"] = new JsonArray(message),
            ["timestamp"] = timestamp + 1,
        };

        var req = BuildRequest(HttpMethod.Post,
            $"https://chat.qwen.ai/api/v2/chat/completions?chat_id={Uri.EscapeDataString(chatId)}");
        req.Content = JsonContent(payload);

        var response = await SharedHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        string contentType = response.Content.Headers.ContentType?.MediaType ?? "";

        if (contentType.Contains("application/json"))
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            Log.Error($"send_prompt error: {body}");
            JsonNode? data = JsonNode.Parse(body);
            if (data is JsonObject obj && obj.ContainsKey("ret"))
            {
                return new SendPromptResult
                {
                    Captcha = new JsonObject
                    {
                        ["error"] = "captcha",
                        ["url"] = obj["data"]?["url"]?.DeepClone(),
                    },
                };
            }
            return new SendPromptResult { JsonPayload = data };
        }

        // Non-JSON error responses (e.g. HTML/text error pages): surface as an
        // upstream failure instead of streaming raw content to the client.
        if (!response.IsSuccessStatusCode)
        {
            int status = (int)response.StatusCode;
            string body = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            Log.Error($"send_prompt upstream error {status}: {body}");
            throw new HttpRequestException(
                $"Upstream returned {status} for chat completions: {body}");
        }

        return new SendPromptResult { StreamResponse = response };
    }

    public async Task<JsonNode?> MeAsync(CancellationToken ct = default)
    {
        using var response = await RequestAsync(
            HttpMethod.Get, "https://chat.qwen.ai/api/v1/auths/", ct: ct);
        return await ReadJsonAsync(response, ct);
    }

    /// <summary>Update the user's system prompt / personalization instruction.</summary>
    public async Task<JsonNode?> UpdateSystemPromptAsync(string instruction, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["personalization"] = new JsonObject
            {
                ["name"] = "",
                ["description"] = "",
                ["instruction"] = instruction,
            },
        };
        using var response = await RequestAsync(
            HttpMethod.Post,
            "https://chat.qwen.ai/api/v2/users/user/settings/update",
            JsonContent(payload),
            ct);
        return await ReadJsonAsync(response, ct);
    }

    public async Task<JsonNode?> UpdateSettingsAsync(CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["tools_enabled"] = new JsonObject
            {
                ["web_extractor"] = false,
                ["web_search_image"] = false,
                ["web_search"] = false,
                ["image_gen_tool"] = false,
                ["code_interpreter"] = false,
                ["history_retriever"] = false,
                ["image_edit_tool"] = false,
                ["bio"] = false,
                ["image_zoom_in_tool"] = false,
            },
        };
        using var response = await RequestAsync(
            HttpMethod.Post,
            "https://chat.qwen.ai/api/v2/users/user/settings/update",
            JsonContent(payload),
            ct);
        return await ReadJsonAsync(response, ct);
    }
}

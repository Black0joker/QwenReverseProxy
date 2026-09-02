using System.Text.Json;
using System.Text.Json.Nodes;

namespace QwenProxy.Config;

/// <summary>
/// Port of configData.py cookie/token persistence (config/cookies.json).
///
/// The on-disk format uses a 'tokens' list (multi-token rotation); outgoing
/// sessions use a single 'token' field set to the active (first) token.
/// Cookies are never cached: every access re-reads the file so token rotation
/// is always reflected.
/// </summary>
public sealed class CookieStore
{
    public string CookieFile { get; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    public CookieStore(string? baseDir = null)
    {
        var basePath = baseDir ?? Directory.GetCurrentDirectory();
        CookieFile = Path.Combine(basePath, "config", "cookies.json");
        EnsureCookieFile();
    }

    private void EnsureCookieFile()
    {
        if (!File.Exists(CookieFile))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CookieFile)!);
            File.WriteAllText(CookieFile, "{}");
        }
    }

    /// <summary>Load cookies from the JSON file. Returns empty object if missing/invalid.</summary>
    public JsonObject LoadCookies()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CookieFile)!);
            if (File.Exists(CookieFile))
            {
                var node = JsonNode.Parse(File.ReadAllText(CookieFile));
                if (node is JsonObject obj) return obj;
            }
        }
        catch
        {
            // fall through to empty dict
        }
        return new JsonObject();
    }

    /// <summary>Save cookies to the JSON file. Returns true on success.</summary>
    public bool SaveCookies(JsonObject cookies)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CookieFile)!);
            File.WriteAllText(CookieFile, cookies.ToJsonString(JsonOpts));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Get the first active token. Supports both the 'tokens' list and the
    /// legacy 'token' field. Returns null when no token is available.
    /// </summary>
    public string? GetActiveToken()
    {
        var cookies = LoadCookies();
        if (cookies["tokens"] is JsonArray tokens && tokens.Count > 0)
        {
            var first = tokens[0];
            if (first is not null) return first.GetValue<string>();
        }
        var legacy = cookies["token"];
        if (legacy is not null)
        {
            try { return legacy.GetValue<string>(); }
            catch { return legacy.ToJsonString(); }
        }
        return null;
    }

    /// <summary>Remove the first (active) token and save. Returns true when removed.</summary>
    public bool RemoveFirstToken()
    {
        var cookies = LoadCookies();
        if (cookies["tokens"] is JsonArray tokens && tokens.Count > 0)
        {
            tokens.RemoveAt(0);
            cookies["tokens"] = tokens;
            cookies.Remove("token"); // drop legacy field to enforce new structure
            return SaveCookies(cookies);
        }
        if (cookies.ContainsKey("token"))
        {
            cookies.Remove("token");
            return SaveCookies(cookies);
        }
        return false;
    }

    /// <summary>Get the number of available tokens.</summary>
    public int GetTokenCount()
    {
        var cookies = LoadCookies();
        if (cookies["tokens"] is JsonArray tokens) return tokens.Count;
        var legacy = cookies["token"];
        if (legacy is not null)
        {
            try
            {
                if (!string.IsNullOrEmpty(legacy.GetValue<string>())) return 1;
            }
            catch { /* non-string legacy value */ }
        }
        return 0;
    }

    /// <summary>Append a token to the tokens list and save.</summary>
    public bool AddToken(string token)
    {
        var cookies = LoadCookies();
        var tokens = cookies["tokens"] as JsonArray ?? new JsonArray();
        tokens.Add(token);
        cookies["tokens"] = tokens;
        return SaveCookies(cookies);
    }

    /// <summary>
    /// Build a cookies dictionary with the active (first) token set as the
    /// 'token' field. The 'tokens' list is preserved on disk but excluded from
    /// outgoing session cookies.
    /// </summary>
    public Dictionary<string, string> BuildCookiesWithActiveToken()
    {
        var cookies = LoadCookies();
        string? active = GetActiveToken();
        var result = new Dictionary<string, string>();
        foreach (var kv in cookies)
        {
            if (kv.Key is "tokens" or "token" || kv.Value is null) continue;
            try
            {
                result[kv.Key] = kv.Value.GetValue<string>();
            }
            catch
            {
                result[kv.Key] = kv.Value.ToJsonString();
            }
        }
        if (!string.IsNullOrEmpty(active))
        {
            result["qwen-theme"] = "dark";
            result["token"] = active;
        }
        return result;
    }

    /// <summary>Build a cookies dictionary using an explicit token.</summary>
    public static Dictionary<string, string> BuildCookiesFromToken(string token) => new()
    {
        ["qwen-theme"] = "dark",
        ["token"] = token,
    };
}

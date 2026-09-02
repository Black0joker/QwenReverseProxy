using System.Globalization;

namespace QwenProxy.Config;

/// <summary>
/// Port of configData.build_headers: static Qwen request headers plus a fresh
/// X-Request-Id and Timezone string per request.
/// </summary>
public static class QwenHeaders
{
    /// <summary>Static header values that do not change between requests.</summary>
    public static readonly IReadOnlyDictionary<string, string> StaticHeaders =
        new Dictionary<string, string>
        {
            ["Pragma"] = "no-cache",
            ["Cache-Control"] = "no-cache",
            ["sec-ch-ua-platform"] = "\"Windows\"",
            ["X-Accel-Buffering"] = "no",
            ["Accept-Language"] = "en-US,en;q=0.9",
            ["sec-ch-ua"] = "\"Not;A=Brand\";v=\"8\", \"Chromium\";v=\"150\", \"Brave\";v=\"150\"",
            ["sec-ch-ua-mobile"] = "?0",
            ["source"] = "web",
            ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36",
            ["Accept"] = "application/json",
            ["Version"] = "0.2.74",
            ["bx-v"] = "2.5.36",
            ["Sec-GPC"] = "1",
            ["Origin"] = "https://chat.qwen.ai",
            ["Sec-Fetch-Site"] = "same-origin",
            ["Sec-Fetch-Mode"] = "cors",
            ["Sec-Fetch-Dest"] = "empty",
            // NOTE: 'Host' and 'Content-Type' from the Python static header set are
            // transport-level in .NET and are set automatically by HttpClient /
            // per-request content, so they are not included here.
        };

    /// <summary>
    /// Build a timezone string matching the format expected by the API.
    /// Example output: 'Thu Aug 13 2026 14:30:00 GMT+0300'
    /// </summary>
    public static string BuildTimeString()
    {
        var now = DateTimeOffset.Now;
        var offset = now.Offset;
        string sign = offset >= TimeSpan.Zero ? "+" : "-";
        var abs = offset.Duration();
        string offsetStr = $"GMT{sign}{abs.Hours:00}{abs.Minutes:00}";
        return now.ToString($"ddd MMM dd yyyy HH:mm:ss {offsetStr}", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Build a fresh headers dictionary with a unique X-Request-Id and current
    /// Timezone. Must be called per-request to avoid reusing stale request IDs.
    /// </summary>
    public static Dictionary<string, string> BuildHeaders()
    {
        var headers = new Dictionary<string, string>(StaticHeaders)
        {
            ["X-Request-Id"] = Guid.NewGuid().ToString(),
            ["Timezone"] = BuildTimeString(),
        };
        return headers;
    }

    /// <summary>Apply a header dictionary to an outgoing request message.</summary>
    public static void ApplyTo(HttpRequestMessage request, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var kv in headers)
        {
            // Some headers must live on the content; they are ignored here when
            // no content exists yet. All headers used by this proxy are request-level.
            request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }
    }
}

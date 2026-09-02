using System.Security.Cryptography;
using System.Text;
using QwenProxy.Middleware;
using QwenProxy.Models;

namespace QwenProxy.Config;

/// <summary>
/// Rate limiting settings (appsettings.json "RateLimiting" section).
/// Every policy is a sliding window keyed by the authenticated Qwen token
/// (preferred) or the client IP as a fallback.
/// </summary>
public sealed class RateLimitOptions
{
    /// <summary>Master switch; when false the rate limiting middleware is not added.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Applies to every request that reaches the middleware.</summary>
    public WindowOptions Global { get; set; } = new() { PermitLimit = 120, WindowSeconds = 60 };

    /// <summary>/api/v1/chat/completions(+/stop): expensive upstream SSE calls.</summary>
    public WindowOptions Completions { get; set; } = new() { PermitLimit = 10, WindowSeconds = 60 };

    /// <summary>/api/v1/chats/* endpoints.</summary>
    public WindowOptions Chats { get; set; } = new() { PermitLimit = 60, WindowSeconds = 60 };

    /// <summary>/api/v1/files/* upload endpoints.</summary>
    public WindowOptions Files { get; set; } = new() { PermitLimit = 20, WindowSeconds = 60 };

    public sealed class WindowOptions
    {
        public int PermitLimit { get; set; }
        public int WindowSeconds { get; set; }
    }
}

/// <summary>Policy names used by [EnableRateLimiting] and the DI registration.</summary>
public static class RateLimitPolicies
{
    public const string Completions = "completions";
    public const string Chats = "chats";
    public const string Files = "files";
}

/// <summary>Builds the limiter partition key for a request.</summary>
public static class RateLimitPartitioning
{
    /// <summary>
    /// Key = SHA-256 prefix of the decrypted Qwen token when the request is
    /// authenticated (TokenDecryptionMiddleware stores it before the rate
    /// limiter runs), otherwise the client IP. Hashing avoids retaining raw
    /// tokens in limiter diagnostics.
    /// </summary>
    public static string KeyFor(HttpContext context)
    {
        if (context.Items[TokenDecryptionMiddleware.ContextKey] is TokenClass tokenClass &&
            tokenClass.Token.Length > 0)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(tokenClass.Token));
            return "tok:" + Convert.ToHexString(hash)[..32];
        }
        return "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }
}

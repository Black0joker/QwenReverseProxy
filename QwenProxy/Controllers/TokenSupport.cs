using QwenProxy.Config;
using QwenProxy.Middleware;
using QwenProxy.Models;
using QwenProxy.Services;

namespace QwenProxy.Controllers;

/// <summary>
/// Shared helpers for controllers (port of api/dependencies.py).
/// </summary>
public static class TokenSupport
{
    /// <summary>
    /// HttpContext.Items key under which <see cref="TokenDecryptionMiddleware"/>
    /// stores the decrypted <see cref="TokenClass"/> payload.
    /// </summary>
    private const string ContextKey = "TokenClass";

    /// <summary>
    /// Retrieve the decrypted <see cref="TokenClass"/> that the
    /// <see cref="TokenDecryptionMiddleware"/> stored on the request context.
    /// </summary>
    public static TokenClass ExtractTokenClass(HttpContext context)
    {
        if (context.Items[ContextKey] is TokenClass tokenClass &&
            tokenClass.Token.Length > 0)
        {
            return tokenClass;
        }

        throw new HttpApiException(
            401, "Missing Authorization header with the Qwen token");
    }

    /// <summary>
    /// Extract the decrypted Qwen token (from the AES-decrypted Bearer payload).
    /// The extracted token is forwarded to Qwen as a 'token' cookie.
    /// </summary>
    public static string ExtractToken(HttpContext context)
    {
        return ExtractTokenClass(context).Token;
    }

    /// <summary>
    /// Create a fresh, token-authenticated QwenClient (per-request clients keep
    /// things thread-safe, mirroring the FastAPI dependency). The decrypted
    /// token is forwarded as the 'token' cookie and the s5_token as 'x5sec'.
    /// </summary>
    public static QwenClient CreateClient(
        HttpContext context,
        CookieStore cookieStore,
        string? model = null,
        string? mode = null)
    {
        TokenClass tokenClass = ExtractTokenClass(context);
        return new QwenClient(
            model: model ?? "qwen3.8-max",
            mode: mode ?? "normal",
            token: tokenClass.Token,
            s5Token: tokenClass.S5Token,
            cookieStore: cookieStore);
    }
}

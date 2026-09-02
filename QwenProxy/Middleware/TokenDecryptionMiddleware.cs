using System.Security.Cryptography;
using System.Text.Json;
using QwenProxy.Models;
using QwenProxy.Services;

namespace QwenProxy.Middleware;

/// <summary>
/// Middleware that intercepts the Authorization header, extracts the Bearer token,
/// decrypts it using AES via <see cref="ITokenEncryptionService"/>, parses the
/// resulting plaintext as JSON, and maps it to <see cref="TokenClass"/>.
/// The parsed object is stored in <c>HttpContext.Items["TokenClass"]</c> for
/// downstream consumption.
///
/// Configuration (appsettings.json):
/// <code>
/// "TokenEncryption": {
///   "Key": "base64-encoded-32-byte-key",
///   "IV": "base64-encoded-16-byte-iv"
/// }
/// </code>
/// </summary>
public sealed class TokenDecryptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ITokenEncryptionService _encryptionService;

    private const string ContextKey = "TokenClass";

    public TokenDecryptionMiddleware(RequestDelegate next, ITokenEncryptionService encryptionService)
    {
        _next = next;
        _encryptionService = encryptionService;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip paths that don't require token decryption (health checks, docs, etc.)
        if (ShouldSkip(context.Request.Path))
        {
            await _next(context);
            return;
        }

        string? authHeader = context.Request.Headers.Authorization;

        if (string.IsNullOrEmpty(authHeader) ||
            !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            // No Bearer token present — let the request continue;
            // downstream controllers can enforce auth if needed.
            await _next(context);
            return;
        }

        string encryptedToken = authHeader["Bearer ".Length..].Trim();

        if (encryptedToken.Length == 0)
        {
            await WriteError(context, 401, "Empty token provided in Authorization header.");
            return;
        }

        // Step 1: Decrypt the token using AES via the injected service
        string decryptedJson;
        try
        {
            decryptedJson = _encryptionService.Decrypt(encryptedToken);
        }
        catch (FormatException)
        {
            await WriteError(context, 401, "Token is not valid Base64.");
            return;
        }
        catch (CryptographicException)
        {
            await WriteError(context, 401, "Token decryption failed. Invalid key or corrupted token.");
            return;
        }

        // Step 2: Parse the decrypted plaintext as JSON and map to TokenClass
        TokenClass? tokenData;
        try
        {
            tokenData = JsonSerializer.Deserialize<TokenClass>(decryptedJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException)
        {
            await WriteError(context, 401, "Decrypted token is not valid JSON");
            return;
        }

        if (tokenData is null)
        {
            await WriteError(context, 401, "Decrypted token payload is null.");
            return;
        }

        // Step 3: Store the parsed TokenClass in HttpContext for downstream use
        context.Items[ContextKey] = tokenData;

        await _next(context);
    }

    /// <summary>
    /// Determines whether the middleware should skip token processing for the given path.
    /// </summary>
    private static bool ShouldSkip(PathString path)
    {
        return path.StartsWithSegments("/docs")
            || path.StartsWithSegments("/swagger")
            || path.StartsWithSegments("/scalar")
            || path.StartsWithSegments("/openapi")
            || path.Value == "/";
    }

    /// <summary>
    /// Writes a JSON error response and short-circuits the pipeline.
    /// </summary>
    private static async Task WriteError(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { detail = message });
    }
}

/// <summary>
/// Extension methods for registering the token decryption middleware.
/// </summary>
public static class TokenDecryptionMiddlewareExtensions
{
    public static IApplicationBuilder UseTokenDecryption(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<TokenDecryptionMiddleware>();
    }
}

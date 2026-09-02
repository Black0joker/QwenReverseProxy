using System.Text.Json;

namespace QwenProxy.Middleware;

/// <summary>
/// Explicit HTTP error raised by controllers (port of FastAPI HTTPException).
/// Detail may be a string or an arbitrary JSON-serializable object.
/// </summary>
public sealed class HttpApiException : Exception
{
    public int StatusCode { get; }
    public object? Detail { get; }

    public HttpApiException(int statusCode, object? detail)
        : base(detail?.ToString())
    {
        StatusCode = statusCode;
        Detail = detail;
    }
}

/// <summary>
/// Global exception handling (port of api/main.py exception handlers).
/// Guarantees the proxy ALWAYS answers with a clean JSON payload:
///  - HttpApiException      -> its status + {"detail": ...}
///  - HttpRequestException  -> 502 upstream failure
///  - anything else         -> 500 {"detail": "Internal server error: ..."}
/// </summary>
public sealed class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;

    public ErrorHandlingMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception e)
        {
            // Once response streaming has started we can no longer rewrite it.
            if (context.Response.HasStarted) throw;

            int status;
            object detail;

            switch (e)
            {
                case HttpApiException api:
                    status = api.StatusCode;
                    detail = api.Detail ?? "";
                    break;
                case HttpRequestException req:
                    status = 502;
                    detail = $"Upstream Qwen request failed: {req.Message}";
                    break;
                case OperationCanceledException:
                    // Client disconnected / shutdown: nothing useful to send.
                    context.Response.StatusCode = 499;
                    return;
                default:
                    status = 500;
                    detail = $"Internal server error: {e.Message}";
                    break;
            }

            context.Response.Clear();
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { detail });
        }
    }
}

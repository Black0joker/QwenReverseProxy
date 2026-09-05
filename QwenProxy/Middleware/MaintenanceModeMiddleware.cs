using Microsoft.Extensions.Options;
using QwenProxy.Config;

namespace QwenProxy.Middleware;

/// <summary>
/// Maintenance mode middleware.
/// When <see cref="MaintenanceOptions.Enabled"/> is true, every request that
/// is not a documentation / health-check path is short-circuited with
/// 503 Service Unavailable and a JSON detail payload.
///
/// Configuration (appsettings.json):
/// <code>
/// "MaintenanceMode": {
///   "Enabled": false,
///   "Message": "...",
///   "RetryAfterSeconds": 0
/// }
/// </code>
/// </summary>
public sealed class MaintenanceModeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly MaintenanceOptions _options;

    public MaintenanceModeMiddleware(RequestDelegate next, IOptions<MaintenanceOptions> options)
    {
        _next = next;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_options.Enabled && !ShouldSkip(context.Request.Path))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "application/json";

            if (_options.RetryAfterSeconds > 0)
            {
                context.Response.Headers.RetryAfter = _options.RetryAfterSeconds.ToString();
            }

            await context.Response.WriteAsJsonAsync(new
            {
                detail = _options.Message,
            });
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// Paths that should remain accessible even during maintenance
    /// (health checks, documentation, root info endpoint).
    /// </summary>
    private static bool ShouldSkip(PathString path)
    {
        return path.StartsWithSegments("/docs")
            || path.StartsWithSegments("/swagger")
            || path.StartsWithSegments("/scalar")
            || path.StartsWithSegments("/openapi")
            || path.StartsWithSegments("/health")
            || path.Value == "/";
    }
}

/// <summary>
/// Extension methods for registering the maintenance mode middleware.
/// </summary>
public static class MaintenanceModeMiddlewareExtensions
{
    public static IApplicationBuilder UseMaintenanceMode(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<MaintenanceModeMiddleware>();
    }
}

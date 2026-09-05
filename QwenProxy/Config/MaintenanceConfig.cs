namespace QwenProxy.Config;

/// <summary>
/// Maintenance mode settings (appsettings.json "MaintenanceMode" section).
/// When enabled the proxy short-circuits every API request with 503.
/// </summary>
public sealed class MaintenanceOptions
{
    /// <summary>Master switch; when false the middleware passes requests through.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Human-readable message returned in the JSON body.</summary>
    public string Message { get; set; } = "The service is temporarily unavailable for maintenance. Please try again later.";

    /// <summary>Optional Retry-After header value (seconds). Omit or set to 0 to skip the header.</summary>
    public int RetryAfterSeconds { get; set; } = 0;
}

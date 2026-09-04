namespace QwenProxy.Services;

/// <summary>
/// Maps public "darktech" model aliases to the upstream Qwen model identifiers.
///
/// Clients address models using the stable darktech aliases; the proxy
/// translates them to the concrete Qwen model names right before any upstream
/// call. The public endpoints accept ONLY these darktech aliases; use
/// <see cref="IsDarktechAlias"/> to enforce that at the API boundary.
/// </summary>
public static class ModelMapper
{
    /// <summary>darktech alias -> upstream Qwen model name (case-insensitive keys).</summary>
    private static readonly Dictionary<string, string> AliasToQwenModel =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["darktech_v1"] = "qwen3.7-plus",
            ["darktech_v2"] = "qwen3.7-max",
            ["darktech_v3"] = "qwen3.8-max",
        };

    /// <summary>Comma-separated list of accepted darktech aliases (for error messages/docs).</summary>
    public static string AllowedModels => string.Join(", ", AliasToQwenModel.Keys);

    /// <summary>True when the given model name is one of the accepted darktech aliases.</summary>
    public static bool IsDarktechAlias(string? model) =>
        model is not null && AliasToQwenModel.ContainsKey(model);

    /// <summary>
    /// Resolve a caller-supplied model name to the upstream Qwen model name.
    /// Returns the mapped Qwen name when the input is a known darktech alias;
    /// otherwise returns the input unchanged (null/empty -> null/empty).
    /// </summary>
    public static string Resolve(string model)
    {
        if (string.IsNullOrEmpty(model)) return model;
        return AliasToQwenModel.TryGetValue(model, out string? mapped) ? mapped : model;
    }
}

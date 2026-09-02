using System.Text.Json.Serialization;

namespace QwenProxy.Models;

/// <summary>
/// Represents the decrypted and deserialized token payload.
/// The AES-encrypted token is expected to contain a JSON object
/// that maps to this class after decryption.
/// </summary>
public sealed class TokenClass
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("s5_token")]
    public string S5Token { get; set; } = "";
}

using System.Security.Cryptography;
using System.Text;

namespace QwenProxy.Services;

/// <summary>
/// Provides AES-256-CBC encryption and decryption operations for token payloads.
/// Registered as a singleton in DI so the AES key/IV are parsed once at startup.
/// </summary>
public interface ITokenEncryptionService
{
    /// <summary>
    /// Decrypts a Base64-encoded AES-256-CBC ciphertext and returns the plaintext string.
    /// Throws <see cref="FormatException"/> if the input is not valid Base64.
    /// Throws <see cref="CryptographicException"/> if decryption fails.
    /// </summary>
    string Decrypt(string base64CipherText);

    /// <summary>
    /// Encrypts a plaintext string using AES-256-CBC and returns the Base64-encoded ciphertext.
    /// </summary>
    string Encrypt(string plainText);
}

/// <summary>
/// Default implementation of <see cref="ITokenEncryptionService"/>.
/// Reads the AES key and IV from configuration at construction time (singleton lifetime).
/// </summary>
public sealed class TokenEncryptionService : ITokenEncryptionService
{
    private readonly byte[] _aesKey;
    private readonly byte[] _aesIV;

    public TokenEncryptionService(IConfiguration configuration)
    {
        string? keyBase64 = configuration["TokenEncryption:Key"];
        string? ivBase64 = configuration["TokenEncryption:IV"];

        if (string.IsNullOrWhiteSpace(keyBase64))
            throw new InvalidOperationException(
                "TokenEncryption:Key is not configured. Provide a Base64-encoded 32-byte AES key.");

        if (string.IsNullOrWhiteSpace(ivBase64))
            throw new InvalidOperationException(
                "TokenEncryption:IV is not configured. Provide a Base64-encoded 16-byte AES IV.");

        _aesKey = Convert.FromBase64String(keyBase64);
        _aesIV = Convert.FromBase64String(ivBase64);

        if (_aesKey.Length != 32)
            throw new InvalidOperationException(
                $"TokenEncryption:Key must be 32 bytes (256-bit). Got {_aesKey.Length} bytes.");

        if (_aesIV.Length != 16)
            throw new InvalidOperationException(
                $"TokenEncryption:IV must be 16 bytes (128-bit). Got {_aesIV.Length} bytes.");
    }

    /// <inheritdoc/>
    public string Decrypt(string base64CipherText)
    {
        byte[] cipherBytes = Convert.FromBase64String(base64CipherText);

        using var aes = Aes.Create();
        aes.Key = _aesKey;
        aes.IV = _aesIV;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        byte[] plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);

        return Encoding.UTF8.GetString(plainBytes);
    }

    /// <inheritdoc/>
    public string Encrypt(string plainText)
    {
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);

        using var aes = Aes.Create();
        aes.Key = _aesKey;
        aes.IV = _aesIV;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        byte[] cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        return Convert.ToBase64String(cipherBytes);
    }
}

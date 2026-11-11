using System.Security.Cryptography;

namespace DoForYou.API.Services;

public sealed record EncryptedIdentityDocument(byte[] Ciphertext, byte[] Nonce, byte[] Tag);

public interface IIdentityDocumentEncryptionService
{
    bool IsConfigured { get; }
    EncryptedIdentityDocument Encrypt(ReadOnlySpan<byte> plaintext);
    byte[] Decrypt(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> tag);
}

/// <summary>
/// AES-256-GCM encryption for identity documents before they are persisted.
/// The 32-byte base64 key is supplied only through the deployment environment.
/// </summary>
public sealed class IdentityDocumentEncryptionService(IConfiguration configuration) : IIdentityDocumentEncryptionService
{
    private const string EnvironmentKey = "DFY_KYC_DOCUMENT_ENCRYPTION_KEY";

    private byte[]? ReadKey()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentKey)
            ?? configuration[EnvironmentKey];
        if (string.IsNullOrWhiteSpace(configured)) return null;

        try
        {
            var key = Convert.FromBase64String(configured.Trim());
            return key.Length == 32 ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public bool IsConfigured
    {
        get
        {
            var key = ReadKey();
            if (key == null) return false;
            CryptographicOperations.ZeroMemory(key);
            return true;
        }
    }

    public EncryptedIdentityDocument Encrypt(ReadOnlySpan<byte> plaintext)
    {
        var key = ReadKey() ?? throw new InvalidOperationException("DFY identity-document encryption is not configured.");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
            return new EncryptedIdentityDocument(ciphertext, nonce, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> tag)
    {
        var key = ReadKey() ?? throw new InvalidOperationException("DFY identity-document encryption is not configured.");
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}

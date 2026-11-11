using System.Security.Cryptography;
using System.Text;

namespace DoForYou.API.Services;

public interface IIdentityDocumentIdService
{
    bool IsConfigured { get; }
    string ComputeHmac(string southAfricanIdNumber);
}

/// <summary>
/// Produces a keyed, non-reversible HMAC for duplicate-ID investigation.
/// The HMAC key is independent of the document-encryption key.
/// </summary>
public sealed class IdentityDocumentIdService(IConfiguration configuration) : IIdentityDocumentIdService
{
    private const string KeyName = "DFY_KYC_ID_HMAC_KEY";

    private byte[]? ReadKey()
    {
        var value = Environment.GetEnvironmentVariable(KeyName) ?? configuration[KeyName];
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var key = Convert.FromBase64String(value.Trim());
            return key.Length >= 32 ? key : null;
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

    public string ComputeHmac(string southAfricanIdNumber)
    {
        var key = ReadKey() ?? throw new InvalidOperationException("DFY identity duplicate-detection key is not configured.");
        var normalized = new string((southAfricanIdNumber ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (normalized.Length != 13)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new ArgumentException("A 13-digit South African ID number is required.", nameof(southAfricanIdNumber));
        }

        try
        {
            var digest = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(normalized));
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}

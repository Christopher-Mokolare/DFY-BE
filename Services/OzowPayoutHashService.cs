using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DoForYou.API.Models;

namespace DoForYou.API.Services;

public class OzowPayoutHashService
{
    private static string Sha512Lower(string input)
    {
        var hash = SHA512.HashData(
            Encoding.UTF8.GetBytes(input.ToLowerInvariant()));

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool FixedTimeEqual(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);

        return CryptographicOperations.FixedTimeEquals(
            aBytes,
            bBytes);
    }

    public bool VerifyPayoutHash(
        OzowPayoutVerifyRequest req,
        string apiKey)
    {
        var cents = (long)Math.Round(req.Amount * 100);

        var input = string.Concat(
            req.PayoutId,
            req.SiteCode,
            cents,
            req.MerchantReference,
            req.CustomerBankReference,
            req.IsRtc.ToString().ToLowerInvariant(),
            req.NotifyUrl,
            req.BankingDetails?.BankGroupId,
            req.BankingDetails?.AccountNumber,
            req.BankingDetails?.BranchCode,
            apiKey);

        return FixedTimeEqual(
            Sha512Lower(input),
            req.HashCheck.ToLowerInvariant());
    }

    public bool VerifyNotificationHash(
        OzowPayoutNotificationRequest req,
        string apiKey,
        out int status,
        out int subStatus)
    {
        (status, subStatus) = ReadStatus(req);

        var input = string.Concat(
            req.PayoutId,
            req.SiteCode,
            req.MerchantReference,
            req.CustomerMerchantReference,
            status,
            subStatus,
            apiKey);

        return FixedTimeEqual(
            Sha512Lower(input),
            req.HashCheck.ToLowerInvariant());
    }

    public static (int status, int subStatus) ReadStatus(
        OzowPayoutNotificationRequest req)
    {
        var status = 0;
        var subStatus =
            req.PayoutSubStatus ??
            req.SubStatus ??
            0;

        if (req.PayoutStatus.ValueKind == JsonValueKind.Number)
        {
            status = req.PayoutStatus.GetInt32();
        }
        else if (req.PayoutStatus.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyIgnoreCase(
                    req.PayoutStatus,
                    "status",
                    out var statusProperty) &&
                statusProperty.ValueKind == JsonValueKind.Number)
            {
                status = statusProperty.GetInt32();
            }

            if (TryGetPropertyIgnoreCase(
                    req.PayoutStatus,
                    "subStatus",
                    out var subStatusProperty) &&
                subStatusProperty.ValueKind == JsonValueKind.Number)
            {
                subStatus = subStatusProperty.GetInt32();
            }
        }

        return (status, subStatus);
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(
                    property.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

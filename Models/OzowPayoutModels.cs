using System.Text.Json;
using System.Text.Json.Serialization;

namespace DoForYou.API.Models;

public class OzowBankingDetails
{
    [JsonPropertyName("bankGroupId")]
    public string BankGroupId { get; set; } = string.Empty;

    [JsonPropertyName("accountNumber")]
    public string AccountNumber { get; set; } = string.Empty;

    [JsonPropertyName("branchCode")]
    public string BranchCode { get; set; } = string.Empty;
}

public class OzowPayoutVerifyRequest
{
    [JsonPropertyName("payoutId")]
    public string PayoutId { get; set; } = string.Empty;

    [JsonPropertyName("siteCode")]
    public string SiteCode { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("merchantReference")]
    public string MerchantReference { get; set; } = string.Empty;

    [JsonPropertyName("customerBankReference")]
    public string CustomerBankReference { get; set; } = string.Empty;

    [JsonPropertyName("isRtc")]
    public bool IsRtc { get; set; }

    [JsonPropertyName("notifyUrl")]
    public string NotifyUrl { get; set; } = string.Empty;

    [JsonPropertyName("verifyUrl")]
    public string VerifyUrl { get; set; } = string.Empty;

    [JsonPropertyName("bankingDetails")]
    public OzowBankingDetails? BankingDetails { get; set; }

    [JsonPropertyName("hashCheck")]
    public string HashCheck { get; set; } = string.Empty;
}

public class OzowPayoutNotificationRequest
{
    [JsonPropertyName("payoutId")]
    public string PayoutId { get; set; } = string.Empty;

    [JsonPropertyName("siteCode")]
    public string SiteCode { get; set; } = string.Empty;

    [JsonPropertyName("merchantReference")]
    public string MerchantReference { get; set; } = string.Empty;

    [JsonPropertyName("customerMerchantReference")]
    public string CustomerMerchantReference { get; set; } = string.Empty;

    [JsonPropertyName("payoutStatus")]
    public JsonElement PayoutStatus { get; set; }

    [JsonPropertyName("payoutSubStatus")]
    public int? PayoutSubStatus { get; set; }

    [JsonPropertyName("subStatus")]
    public int? SubStatus { get; set; }

    [JsonPropertyName("hashCheck")]
    public string HashCheck { get; set; } = string.Empty;
}

public class OzowPayoutVerifyResponse
{
    [JsonPropertyName("payoutId")]
    public string PayoutId { get; set; } = string.Empty;

    [JsonPropertyName("isVerified")]
    public bool IsVerified { get; set; }

    [JsonPropertyName("accountNumberDecryptionKey")]
    public string? AccountNumberDecryptionKey { get; set; }
}

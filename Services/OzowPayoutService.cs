using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DoForYou.API.Models;

namespace DoForYou.API.Services;

public record OzowPayoutResult(
    bool Success,
    string? PayoutId,
    bool Retryable,
    string? Error);

public record OzowPayoutReconciliationResult(
    bool Success,
    bool Found,
    string? PayoutId,
    int? Status,
    int? SubStatus,
    string? ErrorMessage,
    bool RetrySafe,
    string? Error);

public interface IOzowPayoutService
{
    Task<OzowPayoutResult> RequestPayoutAsync(
        Payout payout,
        BankAccount bankAccount,
        CancellationToken cancellationToken = default);

    Task<OzowPayoutReconciliationResult> GetPayoutByReferenceAsync(
        Payout payout,
        CancellationToken cancellationToken = default);
}

public class OzowPayoutService(
    IHttpClientFactory httpFactory,
    IConfiguration config,
    ILogger<OzowPayoutService> logger)
    : IOzowPayoutService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<OzowPayoutResult> RequestPayoutAsync(
        Payout payout,
        BankAccount bankAccount,
        CancellationToken cancellationToken = default)
    {
        var siteCode = config["Ozow:SiteCode"];
        var apiKey = config["Ozow:PayoutApiKey"];
        var baseUrl =
            config["Ozow:PayoutBaseUrl"] ??
            "https://stagingpayoutsapi.ozow.com/v1";

        var encryptionKey =
            config["Ozow:AccountNumberDecryptionKey"];

        var notifyUrl = config["Ozow:NotifyUrl"];
        var verifyUrl = config["Ozow:VerifyUrl"];

        var bankGroupId = bankAccount.BankGroupId;

        if (string.IsNullOrWhiteSpace(siteCode) ||
            string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(encryptionKey) ||
            string.IsNullOrWhiteSpace(notifyUrl) ||
            string.IsNullOrWhiteSpace(verifyUrl))
        {
            return new OzowPayoutResult(
                false,
                null,
                false,
                "Ozow payout configuration is incomplete.");
        }

        if (string.IsNullOrWhiteSpace(bankGroupId))
        {
            return new OzowPayoutResult(
                false,
                null,
                false,
                "Runner bank account is missing Ozow BankGroupId.");
        }

        if (string.IsNullOrWhiteSpace(bankAccount.AccountNumber) ||
            string.IsNullOrWhiteSpace(bankAccount.BranchCode))
        {
            return new OzowPayoutResult(
                false,
                null,
                false,
                "Runner bank account is missing required banking details.");
        }

        var amountCents =
            (long)Math.Round(payout.Amount * 100);

        var isRtc =
            bool.TryParse(
                config["Ozow:PayoutIsRtc"],
                out var rtc) && rtc;

        var encryptedAccount =
            EncryptAccountNumber(
                bankAccount.AccountNumber,
                payout.MerchantReference,
                amountCents,
                encryptionKey);

        var customerBankReference =
            SanitiseBankReference(
                payout.MerchantReference);

        var hash =
            BuildHash(
                siteCode,
                amountCents,
                payout.MerchantReference,
                customerBankReference,
                isRtc,
                notifyUrl,
                bankGroupId,
                encryptedAccount,
                bankAccount.BranchCode,
                apiKey);

        var body = new
        {
            siteCode,
            amount = payout.Amount,
            merchantReference = payout.MerchantReference,
            customerBankReference,
            isRtc,
            notifyUrl,
            verifyUrl,
            bankingDetails = new
            {
                bankGroupId,
                accountNumber = encryptedAccount,
                branchCode = bankAccount.BranchCode
            },
            hashCheck = hash
        };

        try
        {
            var client =
                httpFactory.CreateClient("OzowPayout");

            using var request =
                new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{baseUrl.TrimEnd('/')}/requestpayout")
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(
                            body,
                            JsonOptions),
                        Encoding.UTF8,
                        "application/json")
                };

            request.Headers.Add("SiteCode", siteCode);
            request.Headers.Add("ApiKey", apiKey);

            using var response =
                await client.SendAsync(
                    request,
                    cancellationToken);

            var raw =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "Ozow payout request failed. HTTP={Status} Ref={Reference}",
                    (int)response.StatusCode,
                    Sanitize(payout.MerchantReference));

                var retryable =
                    (int)response.StatusCode >= 500 ||
                    (int)response.StatusCode == 408 ||
                    (int)response.StatusCode == 429;

                return new OzowPayoutResult(
                    false,
                    null,
                    retryable,
                    $"Ozow returned HTTP {(int)response.StatusCode}.");
            }

            using var document =
                JsonDocument.Parse(raw);

            var payoutId =
                document.RootElement.TryGetProperty(
                    "payoutId",
                    out var payoutIdElement)
                    ? payoutIdElement.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(payoutId))
            {
                logger.LogError(
                    "Ozow payout request returned no payoutId. Ref={Reference}",
                    Sanitize(payout.MerchantReference));

                return new OzowPayoutResult(
                    false,
                    null,
                    true,
                    "Ozow did not return a payoutId.");
            }

            logger.LogInformation(
                "Ozow payout submitted. Ref={Reference}",
                Sanitize(payout.MerchantReference));

            return new OzowPayoutResult(
                true,
                payoutId,
                false,
                null);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(
                ex,
                "Ozow payout HTTP error. Ref={Reference}",
                Sanitize(payout.MerchantReference));

            return new OzowPayoutResult(
                false,
                null,
                true,
                "Unable to reach Ozow.");
        }
        catch (TaskCanceledException ex)
        {
            logger.LogError(
                ex,
                "Ozow payout request timed out. Ref={Reference}",
                Sanitize(payout.MerchantReference));

            return new OzowPayoutResult(
                false,
                null,
                true,
                "Ozow request timed out.");
        }
        catch (JsonException ex)
        {
            logger.LogError(
                ex,
                "Invalid Ozow payout response. Ref={Reference}",
                Sanitize(payout.MerchantReference));

            return new OzowPayoutResult(
                false,
                null,
                true,
                "Invalid response from Ozow.");
        }
    }

    public async Task<OzowPayoutReconciliationResult> GetPayoutByReferenceAsync(
        Payout payout,
        CancellationToken cancellationToken = default)
    {
        var siteCode = config["Ozow:SiteCode"];
        var apiKey = config["Ozow:PayoutApiKey"];
        var baseUrl =
            config["Ozow:PayoutBaseUrl"] ??
            "https://stagingpayoutsapi.ozow.com/v1";

        if (string.IsNullOrWhiteSpace(siteCode) ||
            string.IsNullOrWhiteSpace(apiKey))
        {
            return new OzowPayoutReconciliationResult(
                false,
                false,
                null,
                null,
                null,
                null,
                false,
                "Ozow payout configuration is incomplete.");
        }

        if (string.IsNullOrWhiteSpace(payout.MerchantReference))
        {
            return new OzowPayoutReconciliationResult(
                false,
                false,
                null,
                null,
                null,
                null,
                false,
                "Payout merchant reference is missing.");
        }

        try
        {
            var client =
                httpFactory.CreateClient("OzowPayout");

            var requestBody = new
            {
                pageSize = 1,
                pageIndex = 1,

                // Ozow PayoutField 0 is the merchant-reference
                // search field used by getpayoutbyreference.
                searchFields = new[] { 0 },
                searchString = payout.MerchantReference,

                sortField = 0,

                minAmount = (decimal?)null,
                maxAmount = (decimal?)null,
                dateFrom = (DateTime?)null,
                dateTo = (DateTime?)null,

                isRtc =
                    bool.TryParse(
                        config["Ozow:PayoutIsRtc"],
                        out var rtc) && rtc,

                bulkReference = (string?)null
            };

            using var request =
                new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{baseUrl.TrimEnd('/')}/getpayoutbyreference")
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(
                            requestBody,
                            JsonOptions),
                        Encoding.UTF8,
                        "application/json")
                };

            request.Headers.Add("SiteCode", siteCode);
            request.Headers.Add("ApiKey", apiKey);

            using var response =
                await client.SendAsync(
                    request,
                    cancellationToken);

            var raw =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Ozow payout reconciliation returned HTTP {Status}. Ref={Reference}",
                    (int)response.StatusCode,
                    Sanitize(payout.MerchantReference));

                // An API error does NOT mean the payout does not exist.
                // Therefore it is never safe to submit another payout.
                return new OzowPayoutReconciliationResult(
                    false,
                    false,
                    null,
                    null,
                    null,
                    null,
                    false,
                    $"Ozow reconciliation returned HTTP {(int)response.StatusCode}.");
            }

            using var document =
                JsonDocument.Parse(raw);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new OzowPayoutReconciliationResult(
                    false,
                    false,
                    null,
                    null,
                    null,
                    null,
                    false,
                    "Unexpected Ozow reconciliation response.");
            }

            if (document.RootElement.GetArrayLength() == 0)
            {
                // Ozow explicitly confirms that no payout exists for
                // this merchant reference. This is safe to retry.
                return new OzowPayoutReconciliationResult(
                    true,
                    false,
                    null,
                    null,
                    null,
                    null,
                    true,
                    null);
            }

            var item =
                document.RootElement[0];

            string? payoutId = null;
            int? status = null;
            int? subStatus = null;
            string? errorMessage = null;

            if (item.TryGetProperty(
                    "id",
                    out var idElement))
            {
                payoutId =
                    idElement.GetString();
            }

            if (item.TryGetProperty(
                    "payoutStatus",
                    out var payoutStatus))
            {
                if (payoutStatus.TryGetProperty(
                        "status",
                        out var statusElement) &&
                    statusElement.ValueKind ==
                        JsonValueKind.Number &&
                    statusElement.TryGetInt32(
                        out var parsedStatus))
                {
                    status = parsedStatus;
                }

                if (payoutStatus.TryGetProperty(
                        "subStatus",
                        out var subStatusElement) &&
                    subStatusElement.ValueKind ==
                        JsonValueKind.Number &&
                    subStatusElement.TryGetInt32(
                        out var parsedSubStatus))
                {
                    subStatus = parsedSubStatus;
                }

                if (payoutStatus.TryGetProperty(
                        "errorMessage",
                        out var errorElement))
                {
                    errorMessage =
                        errorElement.GetString();
                }
            }

            if (string.IsNullOrWhiteSpace(payoutId))
            {
                return new OzowPayoutReconciliationResult(
                    false,
                    true,
                    null,
                    status,
                    subStatus,
                    errorMessage,
                    false,
                    "Ozow found the payout but returned no payout ID.");
            }

            logger.LogInformation(
                "Ozow payout reconciliation found existing payout. Ref={Reference}",
                Sanitize(payout.MerchantReference));

            return new OzowPayoutReconciliationResult(
                true,
                true,
                payoutId,
                status,
                subStatus,
                errorMessage,
                false,
                null);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(
                ex,
                "Ozow payout reconciliation HTTP error. Ref={Reference}",
                Sanitize(payout.MerchantReference));

            return new OzowPayoutReconciliationResult(
                false,
                false,
                null,
                null,
                null,
                null,
                false,
                "Unable to reach Ozow during reconciliation.");
        }
        catch (TaskCanceledException ex)
        {
            logger.LogWarning(
                ex,
                "Ozow payout reconciliation timed out. Ref={Reference}",
                Sanitize(payout.MerchantReference));

            return new OzowPayoutReconciliationResult(
                false,
                false,
                null,
                null,
                null,
                null,
                false,
                "Ozow reconciliation request timed out.");
        }
        catch (JsonException ex)
        {
            logger.LogWarning(
                ex,
                "Invalid Ozow reconciliation response. Ref={Reference}",
                Sanitize(payout.MerchantReference));

            return new OzowPayoutReconciliationResult(
                false,
                false,
                null,
                null,
                null,
                null,
                false,
                "Invalid response from Ozow during reconciliation.");
        }
    }

    private static string EncryptAccountNumber(
        string accountNumber,
        string merchantReference,
        long amountCents,
        string encryptionKey)
    {
        var ivInput =
            $"{merchantReference}{amountCents}{encryptionKey}";

        var ivHex =
            Convert.ToHexString(
                SHA512.HashData(
                    Encoding.UTF8.GetBytes(
                        ivInput.ToLowerInvariant())))
            .ToLowerInvariant();

        var iv =
            Encoding.UTF8.GetBytes(
                ivHex[..16]);

        var keyMaterial = encryptionKey;

        while (keyMaterial.Length < 32)
            keyMaterial += keyMaterial;

        var key =
            Encoding.UTF8.GetBytes(
                keyMaterial[..32]);

#pragma warning disable CA5358
        using var aes = Aes.Create();

        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;

        using var encryptor =
            aes.CreateEncryptor();

        var plain =
            Encoding.UTF8.GetBytes(
                accountNumber);

        var cipher =
            encryptor.TransformFinalBlock(
                plain,
                0,
                plain.Length);

        return Convert.ToBase64String(cipher);
#pragma warning restore CA5358
    }

    private static string BuildHash(
        string siteCode,
        long amountCents,
        string merchantReference,
        string customerBankReference,
        bool isRtc,
        string notifyUrl,
        string bankGroupId,
        string accountNumber,
        string branchCode,
        string apiKey)
    {
        var input = string.Concat(
            siteCode,
            amountCents,
            merchantReference,
            customerBankReference,
            isRtc.ToString().ToLowerInvariant(),
            notifyUrl,
            bankGroupId,
            accountNumber,
            branchCode,
            apiKey);

        var hash =
            SHA512.HashData(
                Encoding.UTF8.GetBytes(
                    input.ToLowerInvariant()));

        return Convert.ToHexString(hash)
            .ToLowerInvariant();
    }

    private static string SanitiseBankReference(
        string input)
    {
        return new string(
            input
                .Where(c =>
                    char.IsLetterOrDigit(c) ||
                    c == ' ' ||
                    c == '-')
                .Take(20)
                .ToArray());
    }

    private static string Sanitize(string input)
    {
        return input
            .Replace("\r", "")
            .Replace("\n", "");
    }
}

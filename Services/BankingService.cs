using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DoForYou.API.Models;

namespace DoForYou.API.Services;

public record BankVerificationResult(
    bool Success,
    string Message,
    string? ProviderReference = null);

public interface IBankingService
{
    System.Threading.Tasks.Task<bool> ValidateBankAccountAsync(
        string bankName,
        string accountNumber,
        string branchCode);

    System.Threading.Tasks.Task<BankVerificationResult> VerifyBankAccountAsync(
        User user,
        BankAccount bankAccount,
        CancellationToken cancellationToken = default);
}

public class BankingService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<BankingService> logger) : IBankingService
{
    private static readonly SemaphoreSlim TokenLock = new(1, 1);

    private static string? _accessToken;
    private static DateTime _tokenExpiresAtUtc = DateTime.MinValue;

    public System.Threading.Tasks.Task<bool> ValidateBankAccountAsync(
        string bankName,
        string accountNumber,
        string branchCode)
    {
        if (string.IsNullOrWhiteSpace(bankName) ||
            string.IsNullOrWhiteSpace(accountNumber) ||
            string.IsNullOrWhiteSpace(branchCode))
        {
            return System.Threading.Tasks.Task.FromResult(false);
        }

        if (accountNumber.Length < 9 ||
            accountNumber.Length > 11 ||
            !accountNumber.All(char.IsDigit))
        {
            return System.Threading.Tasks.Task.FromResult(false);
        }

        if (branchCode.Length != 6 ||
            !branchCode.All(char.IsDigit))
        {
            return System.Threading.Tasks.Task.FromResult(false);
        }

        return System.Threading.Tasks.Task.FromResult(true);
    }

    public async System.Threading.Tasks.Task<BankVerificationResult> VerifyBankAccountAsync(
        User user,
        BankAccount bankAccount,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(user.IdNumber))
        {
            return new BankVerificationResult(
                false,
                "Your South African ID number is required before the bank account can be verified.");
        }

        var accountType = NormalizeAccountType(bankAccount.AccountType);

        if (accountType == null)
        {
            return new BankVerificationResult(
                false,
                "Unsupported bank account type. Use Cheque, Savings, Transmission, or Bond.");
        }

        var siteCode =
            configuration["Ozow:SiteCode"];

        var clientId =
            configuration["Ozow:OneClientId"];

        var clientSecret =
            configuration["Ozow:OneClientSecret"];

        var baseUrl =
            configuration["Ozow:OneBaseUrl"] ??
            "https://stagingone.ozow.com/v1";

        if (string.IsNullOrWhiteSpace(siteCode) ||
            string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            logger.LogError(
                "Ozow One API bank verification configuration is incomplete.");

            return new BankVerificationResult(
                false,
                "Bank verification is temporarily unavailable.");
        }

        try
        {
            var accessToken = await GetAccessTokenAsync(
                baseUrl,
                clientId,
                clientSecret,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return new BankVerificationResult(
                    false,
                    "Unable to authenticate with the bank verification provider.");
            }

            var reference =
                $"DFY-BANK-{bankAccount.Id}";

            var initials =
                GetInitials(user.FirstName);

            var payload = new
            {
                siteCode,
                reference,
                region = "ZA",
                accountNumber = bankAccount.AccountNumber,
                branchCode = bankAccount.BranchCode,
                accountType,
                identification = new
                {
                    type = "said",
                    country = "ZA",
                    identifier = user.IdNumber
                },
                surname = user.LastName,
                initials,
                mobile = user.PhoneNumber,
                email = user.Email
            };

            var client =
                httpClientFactory.CreateClient("OzowOne");

            using var request =
                new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{baseUrl.TrimEnd('/')}/bankaccount/verify");

            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    accessToken);

            request.Headers.Add(
                "Idempotency-Key",
                reference);

            var correlationId =
                Guid.NewGuid().ToString();

            request.Headers.Add(
                "X-Correlation-ID",
                correlationId);

            request.Content =
                new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json");

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
                    "Ozow bank verification failed. HTTP={Status} Reference={Reference} CorrelationId={CorrelationId}",
                    (int)response.StatusCode,
                    reference,
                    correlationId);

                return new BankVerificationResult(
                    false,
                    GetProviderErrorMessage(
                        raw,
                        "Ozow could not verify the bank account."));
            }

            OzowBankVerificationResponse? result;

            try
            {
                result =
                    JsonSerializer.Deserialize<OzowBankVerificationResponse>(
                        raw,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });
            }
            catch (JsonException)
            {
                logger.LogError(
                    "Ozow returned an invalid bank verification response. Reference={Reference}",
                    reference);

                return new BankVerificationResult(
                    false,
                    "Ozow returned an invalid bank verification response.");
            }

            if (result == null)
            {
                return new BankVerificationResult(
                    false,
                    "Ozow returned an empty bank verification response.");
            }

            var verified =
                result.AccountFound &&
                result.AccountOpen &&
                result.AcceptCredits &&
                result.AccountTypeMatched &&
                result.IdentificationMatched &&
                result.SurnameMatched;

            if (!verified)
            {
                logger.LogWarning(
                    "Ozow bank verification did not pass. Reference={Reference}, AccountFound={AccountFound}, AccountOpen={AccountOpen}, AcceptCredits={AcceptCredits}, AccountTypeMatched={AccountTypeMatched}, IdentificationMatched={IdentificationMatched}, SurnameMatched={SurnameMatched}",
                    reference,
                    result.AccountFound,
                    result.AccountOpen,
                    result.AcceptCredits,
                    result.AccountTypeMatched,
                    result.IdentificationMatched,
                    result.SurnameMatched);

                return new BankVerificationResult(
                    false,
                    BuildVerificationFailureMessage(result));
            }

            return new BankVerificationResult(
                true,
                "Bank account verified successfully.",
                result.Reference ?? reference);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(
                ex,
                "Ozow bank verification HTTP error.");

            return new BankVerificationResult(
                false,
                "Unable to reach the bank verification provider.");
        }
        catch (TaskCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return new BankVerificationResult(
                false,
                "Bank verification timed out. Please try again.");
        }
    }

    private async Task<string?> GetAccessTokenAsync(
        string baseUrl,
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken) &&
            DateTime.UtcNow < _tokenExpiresAtUtc)
        {
            return _accessToken;
        }

        await TokenLock.WaitAsync(cancellationToken);

        try
        {
            if (!string.IsNullOrWhiteSpace(_accessToken) &&
                DateTime.UtcNow < _tokenExpiresAtUtc)
            {
                return _accessToken;
            }

            var client =
                httpClientFactory.CreateClient("OzowOne");

            using var request =
                new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{baseUrl.TrimEnd('/')}/token");

            request.Headers.Add(
                "X-Correlation-ID",
                Guid.NewGuid().ToString());

            request.Content =
                new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        ["client_id"] = clientId,
                        ["client_secret"] = clientSecret,
                        ["scope"] = "payments",
                        ["grant_type"] = "client_credentials"
                    });

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
                    "Ozow One API token request failed. HTTP={Status}",
                    (int)response.StatusCode);

                return null;
            }

            var token =
                JsonSerializer.Deserialize<OzowTokenResponse>(
                    raw,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

            if (token == null ||
                string.IsNullOrWhiteSpace(token.AccessToken))
            {
                return null;
            }

            _accessToken =
                token.AccessToken;

            var lifetime =
                int.TryParse(
                    token.ExpiresIn,
                    out var expiresIn)
                    ? expiresIn
                    : 3600;

            _tokenExpiresAtUtc =
                DateTime.UtcNow.AddSeconds(
                    Math.Max(60, lifetime - 60));

            return _accessToken;
        }
        finally
        {
            TokenLock.Release();
        }
    }

    private static string? NormalizeAccountType(
        string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "cheque" => "cheque",
            "checking" => "cheque",
            "savings" => "savings",
            "saving" => "savings",
            "transmission" => "transmission",
            "bond" => "bond",
            _ => null
        };
    }

    private static string? GetInitials(
        string? firstName)
    {
        if (string.IsNullOrWhiteSpace(firstName))
        {
            return null;
        }

        var parts =
            firstName
                .Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries);

        var initials =
            string.Concat(
                parts
                    .Take(5)
                    .Select(x => x[0]));

        return string.IsNullOrWhiteSpace(initials)
            ? null
            : initials.ToUpperInvariant();
    }

    private static string GetProviderErrorMessage(
        string raw,
        string fallback)
    {
        try
        {
            var error =
                JsonSerializer.Deserialize<OzowErrorResponse>(
                    raw,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

            if (!string.IsNullOrWhiteSpace(error?.Detail))
            {
                return error.Detail;
            }
        }
        catch
        {
            // Do not expose raw provider responses.
        }

        return fallback;
    }

    private static string BuildVerificationFailureMessage(
        OzowBankVerificationResponse result)
    {
        if (!result.AccountFound)
            return "The bank account could not be found.";

        if (!result.AccountOpen)
            return "The bank account is not open.";

        if (!result.AcceptCredits)
            return "The bank account cannot receive credits.";

        if (!result.AccountTypeMatched)
            return "The supplied account type does not match the bank account.";

        if (!result.IdentificationMatched)
            return "The bank account could not be matched to your ID number.";

        if (!result.SurnameMatched)
            return "The bank account could not be matched to your surname.";

        return "The bank account could not be verified.";
    }

    private sealed class OzowTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public string? ExpiresIn { get; set; }
    }

    private sealed class OzowErrorResponse
    {
        public string? Detail { get; set; }
    }

    private sealed class OzowBankVerificationResponse
    {
        public string? Reference { get; set; }
        public bool AccountFound { get; set; }
        public bool AccountOpen { get; set; }
        public bool Account3Months { get; set; }
        public bool AcceptCredits { get; set; }
        public bool AcceptDebits { get; set; }
        public bool AccountTypeMatched { get; set; }
        public bool IdentificationMatched { get; set; }
        public bool SurnameMatched { get; set; }
        public bool InitialsMatched { get; set; }
        public bool MobileMatched { get; set; }
        public bool EmailMatched { get; set; }
    }
}

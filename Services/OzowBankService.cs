using System.Text.Json;
using System.Text.Json.Serialization;

namespace DoForYou.API.Services;

public record OzowBank(
    string BankGroupId,
    string BankGroupName,
    string UniversalBranchCode);

public interface IOzowBankService
{
    Task<IReadOnlyList<OzowBank>> GetAvailableBanksAsync(
        CancellationToken cancellationToken = default);
}

public sealed class OzowBankService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<OzowBankService> logger) : IOzowBankService
{
    public async Task<IReadOnlyList<OzowBank>> GetAvailableBanksAsync(
        CancellationToken cancellationToken = default)
    {
        var apiKey = configuration["Ozow:PayoutApiKey"];
        var siteCode = configuration["Ozow:SiteCode"];

        var baseUrl =
            configuration["Ozow:PayoutBaseUrl"]
            ?? "https://stagingpayoutsapi.ozow.com/v1";

        if (string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(siteCode))
        {
            logger.LogError(
                "Ozow payout bank lookup configuration is incomplete.");

            return Array.Empty<OzowBank>();
        }

        var client =
            httpClientFactory.CreateClient("OzowPayout");

        var isRtc =
            string.Equals(
                configuration["Ozow:PayoutIsRtc"],
                "true",
                StringComparison.OrdinalIgnoreCase);

        var url =
            $"{baseUrl.TrimEnd('/')}/getavailablebanks" +
            (isRtc ? "?rtconly=true" : string.Empty);

        using var request =
            new HttpRequestMessage(
                HttpMethod.Get,
                url);

        request.Headers.Add("ApiKey", apiKey);
        request.Headers.Add("SiteCode", siteCode);

        try
        {
            using var response =
                await client.SendAsync(
                    request,
                    cancellationToken);

            var body =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "Ozow available-bank request failed with HTTP {StatusCode}.",
                    (int)response.StatusCode);

                return Array.Empty<OzowBank>();
            }

            var banks =
                JsonSerializer.Deserialize<List<OzowAvailableBankResponse>>(
                    body,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

            if (banks == null)
                return Array.Empty<OzowBank>();

            return banks
                .Where(bank =>
                    !string.IsNullOrWhiteSpace(bank.BankGroupId) &&
                    !string.IsNullOrWhiteSpace(bank.BankGroupName) &&
                    !string.IsNullOrWhiteSpace(bank.UniversalBranchCode))
                .Select(bank =>
                    new OzowBank(
                        bank.BankGroupId!,
                        bank.BankGroupName!.Trim(),
                        bank.UniversalBranchCode!.Trim()))
                .ToList();
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                "Ozow available-bank request timed out.");

            return Array.Empty<OzowBank>();
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(
                ex,
                "Ozow available-bank request failed.");

            return Array.Empty<OzowBank>();
        }
        catch (JsonException ex)
        {
            logger.LogError(
                ex,
                "Ozow available-bank response could not be parsed.");

            return Array.Empty<OzowBank>();
        }
    }

    private sealed class OzowAvailableBankResponse
    {
        [JsonPropertyName("bankGroupId")]
        public string? BankGroupId { get; set; }

        [JsonPropertyName("bankGroupName")]
        public string? BankGroupName { get; set; }

        [JsonPropertyName("universalBranchCode")]
        public string? UniversalBranchCode { get; set; }
    }
}

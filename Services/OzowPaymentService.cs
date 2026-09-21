using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DoForYou.API.Models;

namespace DoForYou.API.Services;

public record OzowPaymentResult(
    bool Success,
    string? PaymentUrl,
    string? TransactionReference,
    string? Error);

public record OzowRefundResult(
    bool Success,
    string? RefundId,
    string? TransactionId,
    decimal? Amount,
    string? Error);

public record OzowRefundLookup(
    string RefundId,
    string TransactionId,
    decimal Amount,
    int Status);

public interface IOzowPaymentService
{
    Task<OzowPaymentResult> CreatePaymentAsync(
        Models.Task task,
        User customer,
        CancellationToken cancellationToken = default);

    Task<OzowRefundResult> SubmitRefundAsync(
        string transactionId,
        decimal amount,
        string refundReason,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OzowRefundLookup>> GetRefundsByTransactionIdAsync(
        string transactionId,
        CancellationToken cancellationToken = default);

    Task<OzowTransactionResult> GetTransactionByReferenceAsync(
        string transactionReference,
        CancellationToken cancellationToken = default);

    bool VerifyNotificationHash(IReadOnlyDictionary<string, string?> fields);
    bool VerifyRefundNotificationHash(IReadOnlyDictionary<string, string?> fields);
}

public sealed class OzowPaymentService : IOzowPaymentService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OzowPaymentService> _logger;

    public OzowPaymentService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<OzowPaymentService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<OzowPaymentResult> CreatePaymentAsync(
        Models.Task task,
        User customer,
        CancellationToken cancellationToken = default)
    {
        var siteCode = Get("Ozow:SiteCode");
        var apiKey = Get("Ozow:PaymentApiKey");
        var privateKey = Get("Ozow:PaymentPrivateKey");

        if (string.IsNullOrWhiteSpace(siteCode))
            return Fail("Ozow SiteCode is not configured.");

        if (string.IsNullOrWhiteSpace(apiKey))
            return Fail("Ozow payment API key is not configured.");

        if (string.IsNullOrWhiteSpace(privateKey))
            return Fail("Ozow payment private key is not configured.");

        var baseUrl = Get("Ozow:PaymentBaseUrl")
            ?? "https://stagingapi.ozow.com";

        baseUrl = baseUrl.TrimEnd('/');

        var isTest = ParseBool(Get("Ozow:PaymentIsTest"), true);

        // Stable reference per task.
        // Reusing this reference makes payment retries idempotent at the application level.
        var transactionReference =
            string.IsNullOrWhiteSpace(task.PaymentReference)
                ? $"DFY-PAY-{task.TaskId}"
                : task.PaymentReference;

        var backendUrl =
            Environment.GetEnvironmentVariable("BACKEND_URL")
            ?? _configuration["BackendUrl"]
            ?? "https://api.doforyou.co.za";

        var frontendUrl =
            Environment.GetEnvironmentVariable("FRONTEND_URL")
            ?? _configuration["FrontendUrl"]
            ?? "https://do-for-you.vercel.app";

        var amount = task.Budget.ToString("F2", CultureInfo.InvariantCulture);

        var request = new OzowPaymentRequest
        {
            SiteCode = siteCode,
            CountryCode = "ZA",
            CurrencyCode = "ZAR",
            Amount = amount,
            TransactionReference = transactionReference,
            BankReference = BuildBankReference(task),
            Optional1 = task.TaskId,
            Optional2 = "DFY",
            Optional3 = null,
            Optional4 = null,
            Optional5 = null,
            Customer = customer.Email,
            CancelUrl = $"{frontendUrl}/payment/cancelled?taskId={Uri.EscapeDataString(task.TaskId)}",
            ErrorUrl = $"{frontendUrl}/payment/cancelled?taskId={Uri.EscapeDataString(task.TaskId)}",
            SuccessUrl = $"{frontendUrl}/payment/success?taskId={Uri.EscapeDataString(task.TaskId)}",
            NotifyUrl = $"{backendUrl.TrimEnd('/')}/api/v1/payment/notify",
            IsTest = isTest
        };

        request.HashCheck = BuildPaymentHash(request, privateKey);

        try
        {
            var client = _httpClientFactory.CreateClient("OzowPayment");

            using var message = new HttpRequestMessage(
                HttpMethod.Post,
                $"{baseUrl}/postpaymentrequest");

            message.Headers.TryAddWithoutValidation("ApiKey", apiKey);
            message.Content = JsonContent.Create(request);

            using var response = await client.SendAsync(
                message,
                cancellationToken);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Ozow payment request failed with HTTP {StatusCode}: {Body}",
                    (int)response.StatusCode,
                    body);

                return Fail($"Ozow payment request failed ({(int)response.StatusCode}).");
            }

            OzowPaymentApiResponse? result;

            try
            {
                result = JsonSerializer.Deserialize<OzowPaymentApiResponse>(
                    body,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Unable to deserialize Ozow payment response.");
                return Fail("Invalid response received from Ozow.");
            }

            if (result == null)
                return Fail("Empty response received from Ozow.");

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                return Fail(result.ErrorMessage);

            if (string.IsNullOrWhiteSpace(result.Url))
                return Fail("Ozow did not return a payment URL.");

            return new OzowPaymentResult(
                true,
                result.Url,
                transactionReference,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ozow payment request failed unexpectedly.");
            return Fail("Unable to create the Ozow payment request.");
        }
    }

    public async Task<OzowRefundResult> SubmitRefundAsync(
        string transactionId,
        decimal amount,
        string refundReason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transactionId) || amount <= 0)
            return new OzowRefundResult(false, null, transactionId, null, "Valid transaction ID and amount are required.");

        var apiKey = Get("Ozow:PaymentApiKey");
        var privateKey = Get("Ozow:PaymentPrivateKey");
        var accessToken = Get("Ozow:AccessToken");

        if (string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(privateKey) ||
            string.IsNullOrWhiteSpace(accessToken))
            return new OzowRefundResult(false, null, transactionId, null, "Ozow refund credentials are not configured.");

        var baseUrl = (Get("Ozow:RefundBaseUrl")
            ?? Get("Ozow:PaymentBaseUrl")
            ?? "https://stagingapi.ozow.com").TrimEnd('/');
        var backendUrl = Environment.GetEnvironmentVariable("BACKEND_URL")
            ?? _configuration["BackendUrl"]
            ?? "https://api.doforyou.co.za";
        var notifyUrl = $"{backendUrl.TrimEnd('/')}/api/v1/payment/refund-notify";
        var reason = refundReason.Length > 500 ? refundReason[..500] : refundReason;
        var amountText = amount.ToString("F2", CultureInfo.InvariantCulture);
        var hashInput = transactionId + amountText + reason + notifyUrl + privateKey;
        var hash = Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(hashInput))).ToLowerInvariant();

        var payload = new[]
        {
            new
            {
                transactionId,
                amount,
                refundReason = reason,
                notifyUrl,
                hashCheck = hash,
                isRtc = ParseBool(Get("Ozow:RefundIsRtc"), false)
            }
        };

        try
        {
            var client = _httpClientFactory.CreateClient("OzowPayment");
            using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/secure/refunds/submit");
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            message.Content = JsonContent.Create(payload);

            using var response = await client.SendAsync(message, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                return new OzowRefundResult(false, null, transactionId, null, $"Ozow refund request failed ({(int)response.StatusCode}).");

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var item = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                ? root[0]
                : root;

            var refundId = ReadString(item, "refundId", "RefundId");
            var responseTransactionId = ReadString(item, "transactionId", "TransactionId") ?? transactionId;
            var refundAmountText = ReadString(item, "refundAmount", "RefundAmount");
            var errors = item.TryGetProperty("errors", out var errorsElement) && errorsElement.ValueKind == JsonValueKind.Array
                ? string.Join("; ", errorsElement.EnumerateArray().Select(x => x.ToString()))
                : null;

            if (!string.IsNullOrWhiteSpace(errors))
                return new OzowRefundResult(false, refundId, responseTransactionId, null, errors);

            if (string.IsNullOrWhiteSpace(refundId))
                return new OzowRefundResult(false, null, responseTransactionId, null, "Ozow did not return a refund identifier.");

            decimal? refundAmount = decimal.TryParse(refundAmountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : amount;

            return new OzowRefundResult(true, refundId, responseTransactionId, refundAmount, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ozow refund submission failed for transaction {TransactionId}", transactionId);
            return new OzowRefundResult(false, null, transactionId, null, "Unable to submit Ozow refund.");
        }
    }

    public async Task<IReadOnlyList<OzowRefundLookup>> GetRefundsByTransactionIdAsync(
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transactionId))
            return Array.Empty<OzowRefundLookup>();

        var accessToken = Get("Ozow:AccessToken");
        if (string.IsNullOrWhiteSpace(accessToken))
            return Array.Empty<OzowRefundLookup>();

        var baseUrl = (Get("Ozow:RefundBaseUrl")
            ?? Get("Ozow:PaymentBaseUrl")
            ?? "https://api.ozow.com").TrimEnd('/');

        try
        {
            var client = _httpClientFactory.CreateClient("OzowPayment");
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{baseUrl}/secure/refunds/getrefundsbytransactionid?transactionId={Uri.EscapeDataString(transactionId)}");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Ozow refund lookup returned HTTP {StatusCode} for transaction {TransactionId}.", (int)response.StatusCode, transactionId);
                return Array.Empty<OzowRefundLookup>();
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<OzowRefundLookup>();

            var results = new List<OzowRefundLookup>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var id = ReadString(item, "id", "refundId", "RefundId");
                var tx = ReadString(item, "transactionId", "TransactionId") ?? transactionId;
                var amountText = ReadString(item, "amount", "Amount");
                var statusText = ReadString(item, "status", "Status");
                if (string.IsNullOrWhiteSpace(id) ||
                    !decimal.TryParse(amountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ||
                    !int.TryParse(statusText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var status))
                    continue;

                results.Add(new OzowRefundLookup(id, tx, amount, status));
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ozow refund lookup failed for transaction {TransactionId}.", transactionId);
            return Array.Empty<OzowRefundLookup>();
        }
    }

    public async Task<OzowTransactionResult> GetTransactionByReferenceAsync(
        string transactionReference,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transactionReference))
            return new OzowTransactionResult(
                false,
                null,
                null,
                null,
                null,
                "Transaction reference is required.");

        var siteCode = Get("Ozow:SiteCode");
        var apiKey = Get("Ozow:PaymentApiKey");

        if (string.IsNullOrWhiteSpace(siteCode) ||
            string.IsNullOrWhiteSpace(apiKey))
        {
            return new OzowTransactionResult(
                false,
                null,
                null,
                null,
                transactionReference,
                "Ozow payment credentials are not configured.");
        }

        var baseUrl = (
            Get("Ozow:PaymentBaseUrl")
            ?? "https://stagingapi.ozow.com"
        ).TrimEnd('/');

        var isTest = ParseBool(Get("Ozow:PaymentIsTest"), true);

        var url =
            $"{baseUrl}/GetTransactionByReference" +
            $"?siteCode={Uri.EscapeDataString(siteCode)}" +
            $"&transactionReference={Uri.EscapeDataString(transactionReference)}" +
            $"&isTest={isTest.ToString().ToLowerInvariant()}";

        try
        {
            var client = _httpClientFactory.CreateClient("OzowPayment");

            using var message = new HttpRequestMessage(
                HttpMethod.Get,
                url);

            message.Headers.TryAddWithoutValidation("ApiKey", apiKey);

            using var response = await client.SendAsync(
                message,
                cancellationToken);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Ozow transaction lookup returned HTTP {StatusCode}: {Body}",
                    (int)response.StatusCode,
                    body);

                return new OzowTransactionResult(
                    false,
                    null,
                    null,
                    null,
                    transactionReference,
                    $"Ozow transaction lookup failed ({(int)response.StatusCode}).");
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var transactionId = ReadString(root,
                "transactionId",
                "TransactionId");

            var status = ReadString(root,
                "status",
                "Status");

            var reference = ReadString(root,
                "transactionReference",
                "TransactionReference")
                ?? transactionReference;

            decimal? amount = null;

            var amountText = ReadString(root, "amount", "Amount");
            if (decimal.TryParse(
                    amountText,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var parsedAmount))
            {
                amount = parsedAmount;
            }

            return new OzowTransactionResult(
                true,
                transactionId,
                status,
                amount,
                reference,
                null);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Ozow transaction lookup failed for {TransactionReference}",
                transactionReference);

            return new OzowTransactionResult(
                false,
                null,
                null,
                null,
                transactionReference,
                "Unable to verify the Ozow transaction.");
        }
    }

    public bool VerifyRefundNotificationHash(IReadOnlyDictionary<string, string?> fields)
    {
        var privateKey = Get("Ozow:PaymentPrivateKey");
        if (string.IsNullOrWhiteSpace(privateKey) ||
            !fields.TryGetValue("Hash", out var suppliedHash) ||
            string.IsNullOrWhiteSpace(suppliedHash))
            return false;

        var raw = Value(fields, "RefundId") +
                  Value(fields, "TransactionId") +
                  Value(fields, "CurrencyCode") +
                  Value(fields, "Amount") +
                  Value(fields, "Status") +
                  Value(fields, "BankName") +
                  Value(fields, "AccountNumber") +
                  Value(fields, "StatusMessage") +
                  privateKey;

        var calculated = Sha512(raw);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(calculated.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(suppliedHash.Trim().ToLowerInvariant()));
    }

    public bool VerifyNotificationHash(
        IReadOnlyDictionary<string, string?> fields)
    {
        var privateKey = Get("Ozow:PaymentPrivateKey");

        if (string.IsNullOrWhiteSpace(privateKey))
            return false;

        if (!fields.TryGetValue("Hash", out var suppliedHash) ||
            string.IsNullOrWhiteSpace(suppliedHash))
            return false;

        var siteCode = Value(fields, "SiteCode");
        var transactionId = Value(fields, "TransactionId");
        var transactionReference = Value(fields, "TransactionReference");
        var amount = Value(fields, "Amount");
        var status = Value(fields, "Status");
        var optional1 = Value(fields, "Optional1");
        var optional2 = Value(fields, "Optional2");
        var optional3 = Value(fields, "Optional3");
        var optional4 = Value(fields, "Optional4");
        var optional5 = Value(fields, "Optional5");
        var currencyCode = Value(fields, "CurrencyCode");
        var isTest = Value(fields, "IsTest");

        var raw =
            siteCode +
            transactionId +
            transactionReference +
            amount +
            status +
            optional1 +
            optional2 +
            optional3 +
            optional4 +
            optional5 +
            currencyCode +
            isTest +
            privateKey;

        var calculated = Sha512(raw);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(calculated.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(suppliedHash.Trim().ToLowerInvariant()));
    }

    private string BuildPaymentHash(
        OzowPaymentRequest request,
        string privateKey)
    {
        var raw =
            request.SiteCode +
            request.CountryCode +
            request.CurrencyCode +
            request.Amount +
            request.TransactionReference +
            request.BankReference +
            (request.Optional1 ?? string.Empty) +
            (request.Optional2 ?? string.Empty) +
            (request.Optional3 ?? string.Empty) +
            (request.Optional4 ?? string.Empty) +
            (request.Optional5 ?? string.Empty) +
            request.Customer +
            request.CancelUrl +
            request.ErrorUrl +
            request.SuccessUrl +
            request.NotifyUrl +
            request.IsTest.ToString().ToLowerInvariant() +
            privateKey;

        return Sha512(raw);
    }

    private static string BuildBankReference(Models.Task task)
    {
        var reference = $"DFY {task.TaskId}";
        return reference.Length <= 20
            ? reference
            : reference[..20];
    }

    private string? Get(string key)
        => _configuration[key];

    private static bool ParseBool(string? value, bool fallback)
        => bool.TryParse(value, out var parsed)
            ? parsed
            : fallback;

    private static string Value(
        IReadOnlyDictionary<string, string?> fields,
        string key)
        => fields.TryGetValue(key, out var value)
            ? value ?? string.Empty
            : string.Empty;

    private static string? ReadString(
        JsonElement root,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.String)
                return property.GetString();

            if (property.ValueKind != JsonValueKind.Null)
                return property.ToString();
        }

        return null;
    }

    private static string Sha512(string value)
    {
        using var sha = SHA512.Create();

        return Convert.ToHexString(
            sha.ComputeHash(
                Encoding.UTF8.GetBytes(value.ToLowerInvariant())));
    }

    private static OzowPaymentResult Fail(string error)
        => new(false, null, null, error);

    private sealed class OzowPaymentRequest
    {
        public string SiteCode { get; set; } = string.Empty;
        public string CountryCode { get; set; } = "ZA";
        public string CurrencyCode { get; set; } = "ZAR";
        public string Amount { get; set; } = string.Empty;
        public string TransactionReference { get; set; } = string.Empty;
        public string BankReference { get; set; } = string.Empty;
        public string? Optional1 { get; set; }
        public string? Optional2 { get; set; }
        public string? Optional3 { get; set; }
        public string? Optional4 { get; set; }
        public string? Optional5 { get; set; }
        public string Customer { get; set; } = string.Empty;
        public string CancelUrl { get; set; } = string.Empty;
        public string ErrorUrl { get; set; } = string.Empty;
        public string SuccessUrl { get; set; } = string.Empty;
        public string NotifyUrl { get; set; } = string.Empty;
        public bool IsTest { get; set; }
        public string HashCheck { get; set; } = string.Empty;
    }

    private sealed class OzowPaymentApiResponse
    {
        public string? PaymentRequestId { get; set; }
        public string? Url { get; set; }
        public string? ErrorMessage { get; set; }
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DoForYou.API.Services;

/// <summary>
/// Didit hosted KYC adapter. API credentials and webhook signing secrets remain
/// exclusively on the backend. The legacy interface method returns the hosted URL
/// so existing handoff callers never receive a provider API key or SDK token.
/// </summary>
public sealed class DiditIdentityVerificationService(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<DiditIdentityVerificationService> logger) : IIdentityVerificationService
{
    private const string ApiBaseUrl = "https://verification.didit.me";
    private string ApiKey => ReadSetting("DIDIT_API_KEY", "Didit:ApiKey");
    private string WorkflowId => ReadSetting("DIDIT_WORKFLOW_ID", "Didit:WorkflowId");
    private string WebhookSecret => ReadSetting("DIDIT_WEBHOOK_SECRET", "Didit:WebhookSecret");
    private string PreviousWebhookSecret => ReadSetting("DIDIT_WEBHOOK_PREVIOUS_SECRET", "Didit:PreviousWebhookSecret");
    private string CallbackUrl => ReadSetting("DIDIT_WEBHOOK_URL", "Didit:WebhookUrl");
    public string ProviderName => "Didit";
    public string ConsentVersion => ReadSetting("IDENTITY_VERIFICATION_CONSENT_VERSION", "IdentityVerification:ConsentVersion");
    public string ExpectedLevelName => WorkflowId;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(WorkflowId) &&
        !string.IsNullOrWhiteSpace(WebhookSecret) &&
        !string.IsNullOrWhiteSpace(ConsentVersion);

    public async Task<string> CreateSdkAccessTokenAsync(
        string externalUserId,
        string verifiedEmail,
        CancellationToken cancellationToken)
    {
        var session = await CreateProviderSessionAsync(externalUserId, verifiedEmail, cancellationToken);
        return session.SessionId + "|" + session.VerificationUrl;
    }

    public async Task<IdentityProviderSession> CreateProviderSessionAsync(
        string externalUserId,
        string verifiedEmail,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Didit identity verification is not configured.");
        if (string.IsNullOrWhiteSpace(externalUserId))
            throw new ArgumentException("A stable DFY user reference is required.", nameof(externalUserId));

        var requestData = new Dictionary<string, object?>
        {
            ["workflow_id"] = WorkflowId,
            ["vendor_data"] = externalUserId
        };
        if (Uri.TryCreate(CallbackUrl, UriKind.Absolute, out var callback) &&
            callback.Scheme == Uri.UriSchemeHttps)
            requestData["callback"] = callback.ToString();

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(ApiBaseUrl), "/v3/session/"));
        request.Headers.TryAddWithoutValidation("x-api-key", ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(requestData), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Didit session creation failed with HTTP {StatusCode}.", (int)response.StatusCode);
            throw new InvalidOperationException("The identity-verification provider could not start a session.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var sessionId = ReadString(root, "session_id");
        var url = ReadString(root, "url");
        if (!string.IsNullOrWhiteSpace(url) && url.StartsWith("verify.didit.me/", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !Guid.TryParse(sessionId, out _) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var verificationUri) ||
            verificationUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(verificationUri.Host, "verify.didit.me", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Didit returned an invalid hosted-session response.");
            throw new InvalidOperationException("The identity-verification provider returned an invalid session response.");
        }

        // Only the hosted URL and session reference leave the provider adapter.
        // The API key and webhook secret remain server-side.
        return new IdentityProviderSession(null, sessionId, verificationUri.AbsoluteUri);
    }

    public bool VerifyWebhookSignature(ReadOnlySpan<byte> rawBody, string? digest, string? digestAlgorithm)
    {
        if (string.IsNullOrWhiteSpace(WebhookSecret) ||
            string.IsNullOrWhiteSpace(digest) ||
            string.IsNullOrWhiteSpace(digestAlgorithm))
            return false;

        try
        {
            using var document = JsonDocument.Parse(rawBody.ToArray());
            var timestamp = ReadString(document.RootElement, "timestamp");
            if (string.IsNullOrWhiteSpace(timestamp) ||
                !string.Equals(timestamp, digestAlgorithm.Trim(), StringComparison.Ordinal))
                return false;

            if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds))
                return false;
            var eventTime = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            if (Math.Abs((DateTimeOffset.UtcNow - eventTime).TotalSeconds) > 300)
                return false;

            var canonical = Canonicalize(document.RootElement);
            var payloadBytes = Encoding.UTF8.GetBytes(canonical);
            byte[] supplied;
            try { supplied = Convert.FromHexString(digest.Trim()); }
            catch (FormatException)
            {
                CryptographicOperations.ZeroMemory(payloadBytes);
                return false;
            }

            try
            {
                var secrets = new[] { WebhookSecret, PreviousWebhookSecret }
                    .Where(secret => !string.IsNullOrWhiteSpace(secret))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                foreach (var secret in secrets)
                {
                    var secretBytes = Encoding.UTF8.GetBytes(secret);
                    byte[] expected;
                    try { expected = HMACSHA256.HashData(secretBytes, payloadBytes); }
                    finally { CryptographicOperations.ZeroMemory(secretBytes); }

                    try
                    {
                        if (supplied.Length == expected.Length &&
                            CryptographicOperations.FixedTimeEquals(expected, supplied))
                            return true;
                    }
                    finally { CryptographicOperations.ZeroMemory(expected); }
                }
                return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payloadBytes);
                CryptographicOperations.ZeroMemory(supplied);
            }
        }
        catch (JsonException) { return false; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private static string Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            WriteCanonical(writer, element);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer))
                    writer.WriteNumberValue(integer);
                else if (element.TryGetDecimal(out var number))
                {
                    if (decimal.Truncate(number) == number)
                        writer.WriteRawValue(number.ToString("0", CultureInfo.InvariantCulture));
                    else
                        writer.WriteNumberValue(number);
                }
                else
                    writer.WriteNumberValue(element.GetDouble());
                break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new JsonException("Unsupported JSON value in Didit webhook.");
        }
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

    private string ReadSetting(string environmentName, string configurationName) =>
        Environment.GetEnvironmentVariable(environmentName)?.Trim()
        ?? configuration[configurationName]?.Trim()
        ?? string.Empty;
}

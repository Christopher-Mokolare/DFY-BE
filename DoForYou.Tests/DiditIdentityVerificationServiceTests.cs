using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DoForYou.API.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DoForYou.Tests;

public sealed class DiditIdentityVerificationServiceTests
{
    private const string ApiKey = "didit-test-api-key";
    private const string WorkflowId = "workflow-test-id";
    private const string WebhookSecret = "didit-test-webhook-secret";
    private const string ConsentVersion = "identity-verification-v1";

    private static IConfiguration Configuration(string? previousSecret = null) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Didit:ApiKey"] = ApiKey,
            ["Didit:WorkflowId"] = WorkflowId,
            ["Didit:WebhookSecret"] = WebhookSecret,
            ["Didit:PreviousWebhookSecret"] = previousSecret,
            ["IdentityVerification:ConsentVersion"] = ConsentVersion
        })
        .Build();

    [Fact]
    public void Provider_requires_api_key_workflow_webhook_secret_and_consent_version()
    {
        var service = CreateService(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        Assert.True(service.IsConfigured);
        Assert.Equal("Didit", service.ProviderName);
        Assert.Equal(WorkflowId, service.ExpectedLevelName);

        var incomplete = new DiditIdentityVerificationService(
            new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))),
            new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<DiditIdentityVerificationService>.Instance);
        Assert.False(incomplete.IsConfigured);
    }

    [Fact]
    public async Task Creates_hosted_session_with_server_side_api_key_and_workflow_vendor_reference()
    {
        string? seenApiKey = null;
        string? seenBody = null;
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            seenApiKey = request.Headers.GetValues("x-api-key").Single();
            seenBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    "{\"session_id\":\"4c5c7f3a-5c5c-4c5c-8f3a-4c5c7f3a5c5c\",\"url\":\"https://verify.didit.me/session/test\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var service = CreateService(handler);

        var result = await service.CreateSdkAccessTokenAsync("dfy-user-42", "runner@example.com", CancellationToken.None);

        Assert.Equal(ApiKey, seenApiKey);
        using var body = JsonDocument.Parse(seenBody!);
        Assert.Equal(WorkflowId, body.RootElement.GetProperty("workflow_id").GetString());
        Assert.Equal("dfy-user-42", body.RootElement.GetProperty("vendor_data").GetString());
        Assert.Equal("ZAF", body.RootElement.GetProperty("expected_details").GetProperty("id_country").GetString());
        Assert.Equal("4c5c7f3a-5c5c-4c5c-8f3a-4c5c7f3a5c5c|https://verify.didit.me/session/test", result);
    }

    [Fact]
    public void V2_webhook_signature_requires_matching_fresh_timestamp_and_canonical_json()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        const string raw = "{ \"timestamp\": " + "TIMESTAMP" + ", \"status\":\"Approved\",\"session_id\":\"s-1\" }";
        var json = raw.Replace("TIMESTAMP", timestamp, StringComparison.Ordinal);
        using var parsed = JsonDocument.Parse(json);
        var canonical = Canonical(parsed.RootElement);
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var service = CreateService(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));

        Assert.True(service.VerifyWebhookSignature(Encoding.UTF8.GetBytes(json), signature, timestamp));
        Assert.False(service.VerifyWebhookSignature(Encoding.UTF8.GetBytes(json), signature, "1"));
        Assert.False(service.VerifyWebhookSignature(Encoding.UTF8.GetBytes(json.Replace("Approved", "Declined", StringComparison.Ordinal)), signature, timestamp));
        Assert.False(service.VerifyWebhookSignature(Encoding.UTF8.GetBytes(json), "00", timestamp));

        const string previousSecret = "previous-didit-webhook-secret";
        var previousSignature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(previousSecret), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var rotationService = CreateService(
            new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))),
            previousSecret);
        Assert.True(rotationService.VerifyWebhookSignature(Encoding.UTF8.GetBytes(json), previousSignature, timestamp));
    }

    private static string Canonical(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
            return "{" + string.Join(",", element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}";
        if (element.ValueKind == JsonValueKind.Array)
            return "[" + string.Join(",", element.EnumerateArray().Select(Canonical)) + "]";
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var integer))
            return integer.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return element.GetRawText();
    }

    private static DiditIdentityVerificationService CreateService(HttpMessageHandler handler, string? previousSecret = null) =>
        new(new HttpClient(handler), Configuration(previousSecret), NullLogger<DiditIdentityVerificationService>.Instance);

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            callback(request, cancellationToken);
    }
}

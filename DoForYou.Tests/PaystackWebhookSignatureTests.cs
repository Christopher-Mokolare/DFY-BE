using System.Security.Cryptography;
using System.Text;
using DoForYou.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DoForYou.Tests;

public sealed class PaystackWebhookSignatureTests
{
    private const string Secret = "sk_test_webhook_signature_unit_test_secret";
    private const string Payload = """{"event":"charge.success","data":{"reference":"DFY-SIGNATURE-TEST","amount":20000,"currency":"ZAR"}}""";

    [Fact]
    public void VerifyWebhookSignature_AcceptsCorrectHmacSha512Signature()
    {
        var provider = CreateProvider(Secret);
        var signature = Sign(Payload, Secret);

        Assert.True(provider.VerifyWebhookSignature(Payload, signature));
    }

    [Fact]
    public void VerifyWebhookSignature_RejectsTamperedPayload()
    {
        var provider = CreateProvider(Secret);
        var signature = Sign(Payload, Secret);

        Assert.False(provider.VerifyWebhookSignature(Payload + " ", signature));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-signature")]
    public void VerifyWebhookSignature_RejectsMissingOrInvalidSignature(string? signature)
    {
        var provider = CreateProvider(Secret);

        Assert.False(provider.VerifyWebhookSignature(Payload, signature));
    }

    [Fact]
    public void VerifyWebhookSignature_RejectsWhenSecretIsMissing()
    {
        var provider = CreateProvider(null);

        Assert.False(provider.VerifyWebhookSignature(Payload, Sign(Payload, Secret)));
    }

    private static string Sign(string payload, string secret) =>
        Convert.ToHexString(HMACSHA512.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    [Fact]
    public void VerifyWebhookSignature_AcceptsTimestampedPaystackSignature()
    {
        const string webhookSecret = "whsec_timestamped_signature_unit_test";
        var provider = CreateProvider(Secret, webhookSecret);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = SignTimestamped(Payload, timestamp, webhookSecret);

        Assert.True(provider.VerifyWebhookSignature(Payload, signature, isTimestampedSignature: true));
    }

    [Fact]
    public void VerifyWebhookSignature_AcceptsAnyMatchingV1DuringSecretRotation()
    {
        const string oldSecret = "whsec_old_rotation_secret";
        const string currentSecret = "whsec_current_rotation_secret";
        var provider = CreateProvider(Secret, webhookSecrets: $"{oldSecret};{currentSecret}");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var oldSignature = SignTimestamped(Payload, timestamp, oldSecret);
        var currentSignature = SignTimestamped(Payload, timestamp, currentSecret);
        var signature = $"t={timestamp},{oldSignature[(oldSignature.IndexOf(',') + 1)..]},{currentSignature[(currentSignature.IndexOf(',') + 1)..]}";

        Assert.True(provider.VerifyWebhookSignature(Payload, signature, isTimestampedSignature: true));
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public void VerifyWebhookSignature_RejectsTimestampOutsideFiveMinuteWindow(long offsetSeconds)
    {
        const string webhookSecret = "whsec_timestamp_window_unit_test";
        var provider = CreateProvider(Secret, webhookSecret);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + offsetSeconds;
        var signature = SignTimestamped(Payload, timestamp, webhookSecret);

        Assert.False(provider.VerifyWebhookSignature(Payload, signature, isTimestampedSignature: true));
    }

    [Fact]
    public void VerifyWebhookSignature_RejectsTimestampedHeaderWithoutWebhookSecret()
    {
        var provider = CreateProvider(Secret);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = SignTimestamped(Payload, timestamp, "whsec_unconfigured_secret");

        Assert.False(provider.VerifyWebhookSignature(Payload, signature, isTimestampedSignature: true));
    }

    [Fact]
    public void VerifyWebhookSignature_DoesNotDowngradeTimestampedHeaderToLegacySecret()
    {
        var provider = CreateProvider(Secret);
        var legacySignature = Sign(Payload, Secret);

        Assert.False(provider.VerifyWebhookSignature(Payload, legacySignature, isTimestampedSignature: true));
    }

    private static string SignTimestamped(string payload, long timestamp, string secret)
    {
        var signedPayload = Encoding.UTF8.GetBytes($"{timestamp}.{payload}");
        var signature = Convert.ToHexString(HMACSHA512.HashData(Encoding.UTF8.GetBytes(secret), signedPayload))
            .ToLowerInvariant();
        return $"t={timestamp},v1=whk_testkey:{signature}";
    }

    private static PaystackProvider CreateProvider(
        string? secret,
        string? webhookSecret = null,
        string? webhookSecrets = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Paystack:SecretKey"] = secret,
                ["Paystack:WebhookSecret"] = webhookSecret,
                ["Paystack:WebhookSecrets"] = webhookSecrets,
                ["Paystack:BaseUrl"] = "https://api.paystack.co"
            })
            .Build();

        return new PaystackProvider(
            new StubHttpClientFactory(),
            configuration,
            NullLogger<PaystackProvider>.Instance,
            new HttpContextAccessor());
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new UnexpectedRequestHandler());
    }

    private sealed class UnexpectedRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Webhook signature validation must not make an HTTP request.");
    }
}

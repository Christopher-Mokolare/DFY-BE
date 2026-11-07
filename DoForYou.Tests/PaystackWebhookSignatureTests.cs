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

    private static PaystackProvider CreateProvider(string? secret)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Paystack:SecretKey"] = secret,
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

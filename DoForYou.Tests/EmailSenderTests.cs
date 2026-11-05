using System.Net;
using System.Text.Json;
using DoForYou.API.Services;
using Microsoft.Extensions.Configuration;

namespace DoForYou.Tests;

public class EmailSenderTests
{
    [Fact]
    public async Task Sends_with_Brevo_API_over_HTTPS_when_API_key_is_configured()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created);
        using var client = new HttpClient(handler);
        var sender = new SmtpEmailSender(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:BrevoApiKey"] = "test-api-key",
                ["Email:From"] = "verified@example.com"
            }).Build(),
            new StubHttpClientFactory(client));

        await sender.SendAsync("recipient@example.com", "Verify your email", "Verification link");

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://api.brevo.com/v3/smtp/email", handler.RequestUri?.ToString());
        Assert.Equal("test-api-key", handler.ApiKey);
        using var json = JsonDocument.Parse(handler.Body!);
        var root = json.RootElement;
        Assert.Equal("verified@example.com", root.GetProperty("sender").GetProperty("email").GetString());
        Assert.Equal("recipient@example.com", root.GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Equal("Verify your email", root.GetProperty("subject").GetString());
        Assert.Equal("Verification link", root.GetProperty("textContent").GetString());
    }

    [Fact]
    public async Task Reports_Brevo_API_rejections_without_exposing_response_body()
    {
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized);
        using var client = new HttpClient(handler);
        var sender = new SmtpEmailSender(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:BrevoApiKey"] = "test-api-key",
                ["Email:From"] = "verified@example.com"
            }).Build(),
            new StubHttpClientFactory(client));

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => sender.SendAsync("recipient@example.com", "Verify your email", "Verification link"));

        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.DoesNotContain("private", error.Message);
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? ApiKey { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            ApiKey = request.Headers.GetValues("api-key").Single();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("{\"message\":\"private provider details\"}")
            };
        }
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}

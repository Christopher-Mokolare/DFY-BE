using System.Net;
using System.Net.Mail;
using System.Net.Http.Json;

namespace DoForYou.API.Services;

public interface IEmailSender
{
    Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default);
}

/// <summary>SMTP delivery configured exclusively through environment-backed settings.</summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;

    public SmtpEmailSender(IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    public async Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default)
    {
        var brevoApiKey = Environment.GetEnvironmentVariable("BREVO_API_KEY") ?? _configuration["Email:BrevoApiKey"];
        if (!string.IsNullOrWhiteSpace(brevoApiKey))
        {
            await SendWithBrevoApiAsync(brevoApiKey, recipient, subject, body, cancellationToken);
            return;
        }

        var host = Environment.GetEnvironmentVariable("SMTP_HOST") ?? _configuration["Email:SmtpHost"];
        var portText = Environment.GetEnvironmentVariable("SMTP_PORT") ?? _configuration["Email:SmtpPort"];
        var username = Environment.GetEnvironmentVariable("SMTP_USERNAME") ?? _configuration["Email:SmtpUsername"];
        var password = Environment.GetEnvironmentVariable("SMTP_PASSWORD") ?? _configuration["Email:SmtpPassword"];
        var from = Environment.GetEnvironmentVariable("SMTP_FROM") ?? _configuration["Email:From"];
        var sslText = Environment.GetEnvironmentVariable("SMTP_ENABLE_SSL") ?? _configuration["Email:EnableSsl"];

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(from))
            throw new InvalidOperationException("SMTP is not configured. Set SMTP_HOST, SMTP_USERNAME, SMTP_PASSWORD and SMTP_FROM.");
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException("SMTP_PORT must be a valid TCP port.");

        using var message = new MailMessage
        {
            From = new MailAddress(from),
            Subject = subject,
            Body = body,
            IsBodyHtml = false
        };
        message.To.Add(new MailAddress(recipient));

        using var client = new SmtpClient(host, port)
        {
            EnableSsl = !string.Equals(sslText, "false", StringComparison.OrdinalIgnoreCase),
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(username, password),
            Timeout = 15000,
        };
        cancellationToken.ThrowIfCancellationRequested();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        await client.SendMailAsync(message, cts.Token);
    }

    private async Task SendWithBrevoApiAsync(
        string apiKey,
        string recipient,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        var from = Environment.GetEnvironmentVariable("SMTP_FROM") ?? _configuration["Email:From"];
        if (string.IsNullOrWhiteSpace(from))
            throw new InvalidOperationException("SMTP_FROM (or Email:From) must contain a verified Brevo sender address.");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email");
        request.Headers.TryAddWithoutValidation("api-key", apiKey);
        request.Content = JsonContent.Create(new
        {
            sender = new { name = "DoForYou", email = from },
            to = new[] { new { email = recipient } },
            subject,
            textContent = body
        });

        var httpClient = _httpClientFactory.CreateClient("Brevo");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Brevo transactional email request failed with HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
    }
}

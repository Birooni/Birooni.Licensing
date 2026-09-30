using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Birooni.Licensing.Api.Services;

public class ResendEmailSender : IEmailSender
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ResendEmailSender> _logger;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public ResendEmailSender(IConfiguration configuration, ILogger<ResendEmailSender> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    private string? ApiKey => Environment.GetEnvironmentVariable("RESEND_API_KEY") ?? _configuration["Resend:ApiKey"];
    private string From => Environment.GetEnvironmentVariable("RESEND_FROM")
                           ?? _configuration["Resend:From"]
                           ?? "Ibrooni <info@ibrooni.com>";

    public async Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("RESEND_API_KEY is not set.");
        }

        var verifyUrl = ExtractFirstHref(htmlBody);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        var payload = new
        {
            from = From,
            to = new[] { toEmail },
            subject,
            html = htmlBody,
            text = "Verify your Ibrooni email by opening this link:\n\n" + verifyUrl + "\n\nThis link expires in 24 hours. You can sign in only after you verify."
        };
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var res = await Http.SendAsync(req, cancellationToken);
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode)
        {
            _logger.LogError("Resend failed {Status}: {Body}", (int)res.StatusCode, body);
            throw new InvalidOperationException("Resend rejected the message: " + body);
        }

        _logger.LogInformation("Resend accepted mail to {Email}", toEmail);
    }

    private static string ExtractFirstHref(string html)
    {
        var start = html.IndexOf("href=\"", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return "https://ibrooni.com/account.html";
        }

        start += 6;
        var end = html.IndexOf('"', start);
        return end < 0 ? "https://ibrooni.com/account.html" : html[start..end];
    }
}

using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Birooni.Licensing.Api.Services;

public class SmtpEmailSender : IEmailSender
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IConfiguration configuration, ILogger<SmtpEmailSender> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(User) && !string.IsNullOrWhiteSpace(Password);

    private string? Host => First("SMTP_HOST", "Smtp:Host");
    private string? User => First("SMTP_USER", "Smtp:User");
    private string? Password => First("SMTP_PASSWORD", "Smtp:Password");
    private string? FromRaw => First("SMTP_FROM", "Smtp:From");
    private int Port
    {
        get
        {
            var raw = First("SMTP_PORT", "Smtp:Port");
            return int.TryParse(raw, out var port) ? port : 587;
        }
    }

    public async Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogError("SMTP is not configured. Set SMTP_HOST, SMTP_USER, SMTP_PASSWORD on Render. Mail to {Email} was not sent.", toEmail);
            throw new InvalidOperationException("Mail server is not configured.");
        }

        var message = new MimeMessage();
        var from = ResolveFrom();
        message.From.Add(from);
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;
        message.ReplyTo.Add(new MailboxAddress("Ibrooni", "info@ibrooni.com"));

        var builder = new BodyBuilder
        {
            HtmlBody = htmlBody,
            TextBody = "Open this link to verify your Ibrooni email: " + ExtractFirstHref(htmlBody)
        };
        message.Body = builder.ToMessageBody();

        using var client = new SmtpClient();
        var secure = Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
        await client.ConnectAsync(Host, Port, secure, cancellationToken);
        await client.AuthenticateAsync(User, Password, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);

        _logger.LogInformation("Sent '{Subject}' to {Email} from {From}", subject, toEmail, from.Address);
    }

    private MailboxAddress ResolveFrom()
    {
        // Gmail (and most SMTP hosts) will reject or silently drop mail unless
        // From matches the authenticated mailbox or a verified send-as alias.
        var user = User!.Trim();
        var raw = FromRaw?.Trim();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                var parsed = MailboxAddress.Parse(raw);
                if (string.Equals(parsed.Address, user, StringComparison.OrdinalIgnoreCase))
                {
                    return parsed;
                }
            }
            catch
            {
                // fall through to authenticated user
            }
        }

        return new MailboxAddress("Ibrooni", user);
    }

    private string? First(string env, string config) =>
        Environment.GetEnvironmentVariable(env) ?? _configuration[config];

    private static string ExtractFirstHref(string html)
    {
        var start = html.IndexOf("href=\"", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return "https://ibrooni.com/account.html";
        start += 6;
        var end = html.IndexOf('"', start);
        return end < 0 ? "https://ibrooni.com/account.html" : html[start..end];
    }
}

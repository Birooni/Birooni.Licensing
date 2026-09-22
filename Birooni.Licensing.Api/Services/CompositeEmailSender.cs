namespace Birooni.Licensing.Api.Services;

public class CompositeEmailSender : IEmailSender
{
    private readonly ResendEmailSender _resend;
    private readonly SmtpEmailSender _smtp;

    public CompositeEmailSender(ResendEmailSender resend, SmtpEmailSender smtp)
    {
        _resend = resend;
        _smtp = smtp;
    }

    public bool IsConfigured => _resend.IsConfigured || _smtp.IsConfigured;

    public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        if (_resend.IsConfigured)
        {
            return _resend.SendAsync(toEmail, subject, htmlBody, cancellationToken);
        }

        return _smtp.SendAsync(toEmail, subject, htmlBody, cancellationToken);
    }
}

using Microsoft.AspNetCore.Identity.UI.Services;

namespace Orbit.Auth;

/// <summary>
/// v1 stand-in for a real sender: account and task notification emails are written to the log.
/// Replace with an SMTP/SendGrid implementation of IEmailSender when one is chosen.
/// </summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        logger.LogInformation("Email (not sent - no sender configured) To: {To} | Subject: {Subject}\n{Body}",
            email, subject, htmlMessage);
        return Task.CompletedTask;
    }
}

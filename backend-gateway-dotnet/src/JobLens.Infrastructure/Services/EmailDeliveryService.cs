using System.Net;
using System.Net.Mail;
using JobLens.Application.Interfaces;
using JobLens.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobLens.Infrastructure.Services;

public sealed class EmailDeliveryService(
    IOptions<EmailOptions> emailOptionsAccessor,
    ILogger<EmailDeliveryService> logger) : IEmailDeliveryService
{
    private readonly EmailOptions emailOptions = emailOptionsAccessor.Value;

    public async Task SendPasswordResetAsync(string toEmail, string resetLink, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(toEmail) || string.IsNullOrWhiteSpace(resetLink))
        {
            return;
        }

        if (!emailOptions.Enabled)
        {
            logger.LogInformation("Password reset link for {Email}: {ResetLink}", toEmail, resetLink);
            return;
        }

        if (string.IsNullOrWhiteSpace(emailOptions.SmtpHost))
        {
            logger.LogWarning("Email delivery is enabled but SMTP host is not configured.");
            return;
        }

        using var client = new SmtpClient(emailOptions.SmtpHost, emailOptions.SmtpPort)
        {
            EnableSsl = emailOptions.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };

        if (!string.IsNullOrWhiteSpace(emailOptions.Username))
        {
            client.Credentials = new NetworkCredential(emailOptions.Username, emailOptions.Password);
        }

        var from = new MailAddress(emailOptions.FromAddress, emailOptions.FromName);
        var to = new MailAddress(toEmail.Trim());
        using var message = new MailMessage(from, to)
        {
            Subject = "Reset your JobLens password",
            Body = BuildBody(resetLink),
            IsBodyHtml = true,
        };

        using var registration = cancellationToken.Register(() => client.SendAsyncCancel());
        await client.SendMailAsync(message, cancellationToken);
    }

    private static string BuildBody(string resetLink)
    {
        return $"""
               <p>You requested a password reset for your JobLens account.</p>
               <p>Use the link below to choose a new password:</p>
               <p><a href=\"{WebUtility.HtmlEncode(resetLink)}\">Reset password</a></p>
               <p>If you did not request this, you can safely ignore this email.</p>
               """;
    }
}

namespace JobLens.Application.Interfaces;

public interface IEmailDeliveryService
{
    Task SendPasswordResetAsync(string toEmail, string resetLink, CancellationToken cancellationToken);
}

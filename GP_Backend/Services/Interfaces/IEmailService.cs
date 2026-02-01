using GP_Backend.Models.DTOs.Common;

namespace GP_Backend.Services.Interfaces;

public interface IEmailService
{
    Task<ApiResponse> SendEmailAsync(string toEmail, string subject, string bodyHtml, long? applicationId = null, long? userId = null);
    Task<ApiResponse> SendTemplateEmailAsync(string toEmail, string templateName, Dictionary<string, string> placeholders, long? applicationId = null, long? userId = null);
    
    // Predefined email templates
    Task<ApiResponse> SendApplicationReceivedEmailAsync(long applicationId);
    Task<ApiResponse> SendApplicationStatusUpdateEmailAsync(long applicationId);
    Task<ApiResponse> SendInterviewScheduledEmailAsync(long sessionId);
    Task<ApiResponse> SendInterviewReminderEmailAsync(long sessionId);
    Task<ApiResponse> SendInterviewCompletedEmailAsync(long sessionId);
    Task<ApiResponse> SendPasswordResetEmailAsync(string email, string resetToken);
    Task<ApiResponse> SendWelcomeEmailAsync(long userId);
}

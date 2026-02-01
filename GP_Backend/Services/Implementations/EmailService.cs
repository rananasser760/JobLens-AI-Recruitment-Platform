using GP_Backend.Data;
using GP_Backend.Models.DTOs.Common;
using GP_Backend.Models.Entities;
using GP_Backend.Models.Enums;
using GP_Backend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace GP_Backend.Services.Implementations;

public class EmailService : IEmailService
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;

    public EmailService(
        AppDbContext context,
        IConfiguration configuration,
        ILogger<EmailService> logger)
    {
        _context = context;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ApiResponse> SendEmailAsync(string toEmail, string subject, string bodyHtml, long? applicationId = null, long? userId = null)
    {
        try
        {
            // Log email in database
            var emailSend = new EmailSend
            {
                ToEmail = toEmail,
                FromEmail = _configuration["Email:FromAddress"],
                Subject = subject,
                BodyHtml = bodyHtml,
                RelatedApplicationId = applicationId,
                RelatedUserId = userId,
                Status = EmailStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };

            _context.EmailSends.Add(emailSend);
            await _context.SaveChangesAsync();

            // TODO: Implement actual email sending using SMTP or email service
            // For now, mark as sent
            emailSend.Status = EmailStatus.Sent;
            emailSend.SentAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Email sent to {ToEmail}: {Subject}", toEmail, subject);

            return ApiResponse.SuccessResponse("Email sent successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending email to {ToEmail}", toEmail);
            return ApiResponse.FailureResponse("Failed to send email");
        }
    }

    public async Task<ApiResponse> SendTemplateEmailAsync(string toEmail, string templateName, Dictionary<string, string> placeholders, long? applicationId = null, long? userId = null)
    {
        try
        {
            var (subject, bodyHtml) = GetEmailTemplate(templateName, placeholders);
            return await SendEmailAsync(toEmail, subject, bodyHtml, applicationId, userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending template email");
            return ApiResponse.FailureResponse("Failed to send email");
        }
    }

    public async Task<ApiResponse> SendApplicationReceivedEmailAsync(long applicationId)
    {
        try
        {
            var application = await _context.Applications
                .Include(a => a.Candidate).ThenInclude(c => c.User)
                .Include(a => a.Job).ThenInclude(j => j.Company)
                .FirstOrDefaultAsync(a => a.Id == applicationId);

            if (application == null)
            {
                return ApiResponse.FailureResponse("Application not found");
            }

            var placeholders = new Dictionary<string, string>
            {
                { "CandidateName", application.Candidate.FullName ?? "Candidate" },
                { "JobTitle", application.Job.Title },
                { "CompanyName", application.Job.Company?.Name ?? "the company" },
                { "ApplicationDate", application.AppliedAt.ToString("MMMM dd, yyyy") }
            };

            return await SendTemplateEmailAsync(
                application.Candidate.User.Email,
                "ApplicationReceived",
                placeholders,
                applicationId,
                application.Candidate.UserId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending application received email");
            return ApiResponse.FailureResponse("Failed to send email");
        }
    }

    public async Task<ApiResponse> SendApplicationStatusUpdateEmailAsync(long applicationId)
    {
        try
        {
            var application = await _context.Applications
                .Include(a => a.Candidate).ThenInclude(c => c.User)
                .Include(a => a.Job).ThenInclude(j => j.Company)
                .FirstOrDefaultAsync(a => a.Id == applicationId);

            if (application == null)
            {
                return ApiResponse.FailureResponse("Application not found");
            }

            var placeholders = new Dictionary<string, string>
            {
                { "CandidateName", application.Candidate.FullName ?? "Candidate" },
                { "JobTitle", application.Job.Title },
                { "CompanyName", application.Job.Company?.Name ?? "the company" },
                { "Status", application.Status.ToString() },
                { "StatusMessage", GetStatusMessage(application.Status) }
            };

            return await SendTemplateEmailAsync(
                application.Candidate.User.Email,
                "ApplicationStatusUpdate",
                placeholders,
                applicationId,
                application.Candidate.UserId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending application status update email");
            return ApiResponse.FailureResponse("Failed to send email");
        }
    }

    public async Task<ApiResponse> SendInterviewScheduledEmailAsync(long sessionId)
    {
        try
        {
            var session = await _context.InterviewSessions
                .Include(s => s.Application).ThenInclude(a => a.Candidate).ThenInclude(c => c.User)
                .Include(s => s.Application).ThenInclude(a => a.Job).ThenInclude(j => j.Company)
                .FirstOrDefaultAsync(s => s.Id == sessionId);

            if (session == null)
            {
                return ApiResponse.FailureResponse("Interview session not found");
            }

            var placeholders = new Dictionary<string, string>
            {
                { "CandidateName", session.Application.Candidate.FullName ?? "Candidate" },
                { "JobTitle", session.Application.Job.Title },
                { "CompanyName", session.Application.Job.Company?.Name ?? "the company" },
                { "InterviewDate", session.ScheduledAt?.ToString("MMMM dd, yyyy") ?? "TBD" },
                { "InterviewTime", session.ScheduledAt?.ToString("hh:mm tt") ?? "TBD" },
                { "InterviewTitle", session.InterviewTitle ?? "Interview" }
            };

            return await SendTemplateEmailAsync(
                session.Application.Candidate.User.Email,
                "InterviewScheduled",
                placeholders,
                session.ApplicationId,
                session.Application.Candidate.UserId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending interview scheduled email");
            return ApiResponse.FailureResponse("Failed to send email");
        }
    }

    public async Task<ApiResponse> SendInterviewReminderEmailAsync(long sessionId)
    {
        try
        {
            var session = await _context.InterviewSessions
                .Include(s => s.Application).ThenInclude(a => a.Candidate).ThenInclude(c => c.User)
                .Include(s => s.Application).ThenInclude(a => a.Job)
                .FirstOrDefaultAsync(s => s.Id == sessionId);

            if (session == null)
            {
                return ApiResponse.FailureResponse("Interview session not found");
            }

            var placeholders = new Dictionary<string, string>
            {
                { "CandidateName", session.Application.Candidate.FullName ?? "Candidate" },
                { "JobTitle", session.Application.Job.Title },
                { "InterviewDate", session.ScheduledAt?.ToString("MMMM dd, yyyy") ?? "TBD" },
                { "InterviewTime", session.ScheduledAt?.ToString("hh:mm tt") ?? "TBD" }
            };

            return await SendTemplateEmailAsync(
                session.Application.Candidate.User.Email,
                "InterviewReminder",
                placeholders,
                session.ApplicationId,
                session.Application.Candidate.UserId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending interview reminder email");
            return ApiResponse.FailureResponse("Failed to send email");
        }
    }

    public async Task<ApiResponse> SendInterviewCompletedEmailAsync(long sessionId)
    {
        try
        {
            var session = await _context.InterviewSessions
                .Include(s => s.Application).ThenInclude(a => a.Candidate).ThenInclude(c => c.User)
                .Include(s => s.Application).ThenInclude(a => a.Job).ThenInclude(j => j.Company)
                .FirstOrDefaultAsync(s => s.Id == sessionId);

            if (session == null)
            {
                return ApiResponse.FailureResponse("Interview session not found");
            }

            var placeholders = new Dictionary<string, string>
            {
                { "CandidateName", session.Application.Candidate.FullName ?? "Candidate" },
                { "JobTitle", session.Application.Job.Title },
                { "CompanyName", session.Application.Job.Company?.Name ?? "the company" }
            };

            return await SendTemplateEmailAsync(
                session.Application.Candidate.User.Email,
                "InterviewCompleted",
                placeholders,
                session.ApplicationId,
                session.Application.Candidate.UserId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending interview completed email");
            return ApiResponse.FailureResponse("Failed to send email");
        }
    }

    public async Task<ApiResponse> SendPasswordResetEmailAsync(string email, string resetToken)
    {
        try
        {
            var frontendUrl = _configuration["FrontendUrl"] ?? "https://localhost:3000";
            var resetLink = $"{frontendUrl}/reset-password?token={resetToken}&email={Uri.EscapeDataString(email)}";

            var placeholders = new Dictionary<string, string>
            {
                { "ResetLink", resetLink },
                { "ExpiryHours", "1" }
            };

            return await SendTemplateEmailAsync(email, "PasswordReset", placeholders);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending password reset email");
            return ApiResponse.FailureResponse("Failed to send email");
        }
    }

    public async Task<ApiResponse> SendWelcomeEmailAsync(long userId)
    {
        try
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                return ApiResponse.FailureResponse("User not found");
            }

            var placeholders = new Dictionary<string, string>
            {
                { "Username", user.Username },
                { "Role", user.Role.ToString() }
            };

            return await SendTemplateEmailAsync(user.Email, "Welcome", placeholders, null, userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending welcome email");
            return ApiResponse.FailureResponse("Failed to send email");
        }
    }

    #region Helper Methods

    private static (string subject, string bodyHtml) GetEmailTemplate(string templateName, Dictionary<string, string> placeholders)
    {
        var (subject, body) = templateName switch
        {
            "ApplicationReceived" => (
                "Application Received - {{JobTitle}}",
                @"<h2>Thank you for your application!</h2>
                <p>Dear {{CandidateName}},</p>
                <p>We have received your application for the <strong>{{JobTitle}}</strong> position at <strong>{{CompanyName}}</strong>.</p>
                <p>Application Date: {{ApplicationDate}}</p>
                <p>We will review your application and get back to you soon.</p>
                <p>Best regards,<br/>The JobLens Team</p>"
            ),
            "ApplicationStatusUpdate" => (
                "Application Update - {{JobTitle}}",
                @"<h2>Application Status Update</h2>
                <p>Dear {{CandidateName}},</p>
                <p>Your application for <strong>{{JobTitle}}</strong> at <strong>{{CompanyName}}</strong> has been updated.</p>
                <p>New Status: <strong>{{Status}}</strong></p>
                <p>{{StatusMessage}}</p>
                <p>Best regards,<br/>The JobLens Team</p>"
            ),
            "InterviewScheduled" => (
                "Interview Scheduled - {{JobTitle}}",
                @"<h2>Interview Scheduled</h2>
                <p>Dear {{CandidateName}},</p>
                <p>Congratulations! Your interview for <strong>{{JobTitle}}</strong> at <strong>{{CompanyName}}</strong> has been scheduled.</p>
                <p><strong>{{InterviewTitle}}</strong></p>
                <p>Date: {{InterviewDate}}</p>
                <p>Time: {{InterviewTime}}</p>
                <p>Please ensure you have a stable internet connection and a quiet environment for the interview.</p>
                <p>Best regards,<br/>The JobLens Team</p>"
            ),
            "InterviewReminder" => (
                "Interview Reminder - {{JobTitle}}",
                @"<h2>Interview Reminder</h2>
                <p>Dear {{CandidateName}},</p>
                <p>This is a reminder that your interview for <strong>{{JobTitle}}</strong> is coming up.</p>
                <p>Date: {{InterviewDate}}</p>
                <p>Time: {{InterviewTime}}</p>
                <p>Good luck!</p>
                <p>Best regards,<br/>The JobLens Team</p>"
            ),
            "InterviewCompleted" => (
                "Interview Completed - {{JobTitle}}",
                @"<h2>Interview Completed</h2>
                <p>Dear {{CandidateName}},</p>
                <p>Thank you for completing your interview for <strong>{{JobTitle}}</strong> at <strong>{{CompanyName}}</strong>.</p>
                <p>The hiring team will review your interview and get back to you soon.</p>
                <p>Best regards,<br/>The JobLens Team</p>"
            ),
            "PasswordReset" => (
                "Password Reset Request",
                @"<h2>Password Reset</h2>
                <p>You have requested to reset your password.</p>
                <p>Click the link below to reset your password:</p>
                <p><a href='{{ResetLink}}'>Reset Password</a></p>
                <p>This link will expire in {{ExpiryHours}} hour(s).</p>
                <p>If you did not request this, please ignore this email.</p>
                <p>Best regards,<br/>The JobLens Team</p>"
            ),
            "Welcome" => (
                "Welcome to JobLens!",
                @"<h2>Welcome to JobLens!</h2>
                <p>Dear {{Username}},</p>
                <p>Thank you for joining JobLens as a {{Role}}!</p>
                <p>We're excited to have you on board. Start exploring opportunities and make the most of our AI-powered recruitment platform.</p>
                <p>Best regards,<br/>The JobLens Team</p>"
            ),
            _ => ("Notification", "<p>You have a new notification from JobLens.</p>")
        };

        // Replace placeholders
        foreach (var (key, value) in placeholders)
        {
            subject = subject.Replace($"{{{{{key}}}}}", value);
            body = body.Replace($"{{{{{key}}}}}", value);
        }

        return (subject, body);
    }

    private static string GetStatusMessage(ApplicationStatus status)
    {
        return status switch
        {
            ApplicationStatus.UnderReview => "Your application is currently being reviewed by our team.",
            ApplicationStatus.Shortlisted => "Congratulations! You have been shortlisted for the next stage.",
            ApplicationStatus.InterviewScheduled => "An interview has been scheduled. Please check your dashboard for details.",
            ApplicationStatus.InterviewCompleted => "Thank you for completing the interview. We will get back to you soon.",
            ApplicationStatus.Hired => "Congratulations! We are pleased to inform you that you have been selected for this position!",
            ApplicationStatus.Rejected => "We appreciate your interest, but we have decided to move forward with other candidates.",
            ApplicationStatus.Withdrawn => "Your application has been withdrawn as requested.",
            _ => "Your application status has been updated."
        };
    }

    #endregion
}

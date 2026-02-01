using Microsoft.EntityFrameworkCore;
using GP_Backend.Data;
using GP_Backend.Models.DTOs.Application;
using GP_Backend.Models.DTOs.Common;
using GP_Backend.Models.Entities;
using GP_Backend.Models.Enums;
using GP_Backend.Services.Interfaces;

namespace GP_Backend.Services.Implementations;

public class ApplicationService : IApplicationService
{
    private readonly AppDbContext _context;
    private readonly IEmailService _emailService;
    private readonly IAuditService _auditService;
    private readonly ILogger<ApplicationService> _logger;

    public ApplicationService(
        AppDbContext context,
        IEmailService emailService,
        IAuditService auditService,
        ILogger<ApplicationService> logger)
    {
        _context = context;
        _emailService = emailService;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<ApiResponse<ApplicationDto>> GetApplicationAsync(long applicationId)
    {
        try
        {
            var application = await _context.Applications
                .Include(a => a.Candidate).ThenInclude(c => c.User)
                .Include(a => a.Job).ThenInclude(j => j.Company)
                .Include(a => a.Resume)
                .Include(a => a.InterviewSessions)
                .FirstOrDefaultAsync(a => a.Id == applicationId);

            if (application == null)
            {
                return ApiResponse<ApplicationDto>.FailureResponse("Application not found");
            }

            return ApiResponse<ApplicationDto>.SuccessResponse(MapToApplicationDto(application));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting application");
            return ApiResponse<ApplicationDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<ApplicationDto>> ApplyToJobAsync(long candidateId, ApplyToJobDto dto)
    {
        try
        {
            // Check if candidate exists
            var candidate = await _context.Candidates
                .Include(c => c.User)
                .FirstOrDefaultAsync(c => c.Id == candidateId);

            if (candidate == null)
            {
                return ApiResponse<ApplicationDto>.FailureResponse("Candidate not found");
            }

            // Check if job exists and is active
            var job = await _context.Jobs
                .Include(j => j.Company)
                .FirstOrDefaultAsync(j => j.Id == dto.JobId);

            if (job == null)
            {
                return ApiResponse<ApplicationDto>.FailureResponse("Job not found");
            }

            if (!job.IsActive)
            {
                return ApiResponse<ApplicationDto>.FailureResponse("This job is no longer accepting applications");
            }

            // Check if already applied
            if (await HasCandidateAppliedAsync(candidateId, dto.JobId))
            {
                return ApiResponse<ApplicationDto>.FailureResponse("You have already applied to this job");
            }

            // Validate resume if provided
            Resume? resume = null;
            if (dto.ResumeId.HasValue)
            {
                resume = await _context.Resumes.FirstOrDefaultAsync(r =>
                    r.Id == dto.ResumeId.Value && r.CandidateId == candidateId);

                if (resume == null)
                {
                    return ApiResponse<ApplicationDto>.FailureResponse("Resume not found");
                }
            }
            else
            {
                // Get default resume
                resume = await _context.Resumes.FirstOrDefaultAsync(r =>
                    r.CandidateId == candidateId && r.IsDefault);
            }

            var application = new Application
            {
                CandidateId = candidateId,
                JobId = dto.JobId,
                ResumeId = resume?.Id,
                AppliedVia = job.Source == JobSource.Internal ? "Internal" : "External",
                AppliedAt = DateTime.UtcNow,
                Status = ApplicationStatus.Pending,
                CoverLetter = dto.CoverLetter
            };

            _context.Applications.Add(application);
            await _context.SaveChangesAsync();

            // Send confirmation email
            await _emailService.SendApplicationReceivedEmailAsync(application.Id);

            await _auditService.LogAsync(candidate.UserId, "ApplyToJob", "Application", application.Id);

            // Reload with includes
            var createdApplication = await _context.Applications
                .Include(a => a.Candidate).ThenInclude(c => c.User)
                .Include(a => a.Job).ThenInclude(j => j.Company)
                .Include(a => a.Resume)
                .FirstAsync(a => a.Id == application.Id);

            return ApiResponse<ApplicationDto>.SuccessResponse(
                MapToApplicationDto(createdApplication),
                "Application submitted successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying to job");
            return ApiResponse<ApplicationDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<ApplicationDto>> UpdateStatusAsync(long applicationId, long recruiterId, UpdateApplicationStatusDto dto)
    {
        try
        {
            var application = await _context.Applications
                .Include(a => a.Candidate).ThenInclude(c => c.User)
                .Include(a => a.Job).ThenInclude(j => j.Company)
                .Include(a => a.Resume)
                .Include(a => a.InterviewSessions)
                .FirstOrDefaultAsync(a => a.Id == applicationId);

            if (application == null)
            {
                return ApiResponse<ApplicationDto>.FailureResponse("Application not found");
            }

            // Verify recruiter owns the job
            var job = await _context.Jobs.FirstOrDefaultAsync(j => j.Id == application.JobId);
            if (job == null || job.RecruiterId != recruiterId)
            {
                return ApiResponse<ApplicationDto>.FailureResponse("You don't have permission to update this application");
            }

            var oldStatus = application.Status;
            application.Status = dto.Status;
            application.RecruiterNotes = dto.Notes ?? application.RecruiterNotes;
            application.ReviewedAt = DateTime.UtcNow;
            application.ReviewedBy = recruiterId;

            await _context.SaveChangesAsync();

            // Send status update email
            await _emailService.SendApplicationStatusUpdateEmailAsync(application.Id);

            var recruiter = await _context.Recruiters.FindAsync(recruiterId);
            await _auditService.LogAsync(recruiter?.UserId, "UpdateApplicationStatus", "Application", application.Id,
                oldStatus.ToString(), dto.Status.ToString());

            return ApiResponse<ApplicationDto>.SuccessResponse(
                MapToApplicationDto(application),
                "Application status updated successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating application status");
            return ApiResponse<ApplicationDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse> WithdrawApplicationAsync(long applicationId, long candidateId)
    {
        try
        {
            var application = await _context.Applications
                .FirstOrDefaultAsync(a => a.Id == applicationId && a.CandidateId == candidateId);

            if (application == null)
            {
                return ApiResponse.FailureResponse("Application not found");
            }

            if (application.Status == ApplicationStatus.Hired)
            {
                return ApiResponse.FailureResponse("Cannot withdraw a hired application");
            }

            application.Status = ApplicationStatus.Withdrawn;
            await _context.SaveChangesAsync();

            var candidate = await _context.Candidates.FirstOrDefaultAsync(c => c.Id == candidateId);
            await _auditService.LogAsync(candidate?.UserId, "WithdrawApplication", "Application", application.Id);

            return ApiResponse.SuccessResponse("Application withdrawn successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error withdrawing application");
            return ApiResponse.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<PaginatedResponse<ApplicationListDto>>> GetCandidateApplicationsAsync(
        long candidateId, ApplicationSearchParams searchParams)
    {
        try
        {
            var query = _context.Applications
                .Include(a => a.Job).ThenInclude(j => j.Company)
                .Include(a => a.InterviewSessions)
                .Where(a => a.CandidateId == candidateId);

            // Apply filters
            if (searchParams.Status.HasValue)
            {
                query = query.Where(a => a.Status == searchParams.Status.Value);
            }

            if (searchParams.FromDate.HasValue)
            {
                query = query.Where(a => a.AppliedAt >= searchParams.FromDate.Value);
            }

            if (searchParams.ToDate.HasValue)
            {
                query = query.Where(a => a.AppliedAt <= searchParams.ToDate.Value);
            }

            var totalCount = await query.CountAsync();

            var applications = await query
                .OrderByDescending(a => a.AppliedAt)
                .Skip((searchParams.PageNumber - 1) * searchParams.PageSize)
                .Take(searchParams.PageSize)
                .Select(a => new ApplicationListDto
                {
                    Id = a.Id,
                    JobTitle = a.Job.Title,
                    CompanyName = a.Job.Company != null ? a.Job.Company.Name : null,
                    AppliedAt = a.AppliedAt,
                    Status = a.Status.ToString(),
                    AppliedVia = a.AppliedVia,
                    HasInterview = a.InterviewSessions.Any()
                })
                .ToListAsync();

            return ApiResponse<PaginatedResponse<ApplicationListDto>>.SuccessResponse(new PaginatedResponse<ApplicationListDto>
            {
                Items = applications,
                TotalCount = totalCount,
                PageNumber = searchParams.PageNumber,
                PageSize = searchParams.PageSize
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting candidate applications");
            return ApiResponse<PaginatedResponse<ApplicationListDto>>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<PaginatedResponse<CandidateApplicationDto>>> GetJobApplicationsAsync(
        long jobId, long recruiterId, ApplicationSearchParams searchParams)
    {
        try
        {
            // Verify recruiter owns the job
            var job = await _context.Jobs.FirstOrDefaultAsync(j => j.Id == jobId);
            if (job == null || job.RecruiterId != recruiterId)
            {
                return ApiResponse<PaginatedResponse<CandidateApplicationDto>>.FailureResponse("Job not found or access denied");
            }

            var query = _context.Applications
                .Include(a => a.Candidate).ThenInclude(c => c.User)
                .Include(a => a.Candidate).ThenInclude(c => c.Skills)
                .Include(a => a.Resume)
                .Include(a => a.InterviewSessions)
                .Where(a => a.JobId == jobId);

            // Apply filters
            if (searchParams.Status.HasValue)
            {
                query = query.Where(a => a.Status == searchParams.Status.Value);
            }

            if (searchParams.FromDate.HasValue)
            {
                query = query.Where(a => a.AppliedAt >= searchParams.FromDate.Value);
            }

            if (searchParams.ToDate.HasValue)
            {
                query = query.Where(a => a.AppliedAt <= searchParams.ToDate.Value);
            }

            var totalCount = await query.CountAsync();

            // Get rankings for this job
            var rankings = await _context.CandidateRankings
                .Where(r => r.JobId == jobId)
                .ToDictionaryAsync(r => r.CandidateId, r => new { r.Score, r.Rank });

            var applications = await query
                .OrderByDescending(a => a.AppliedAt)
                .Skip((searchParams.PageNumber - 1) * searchParams.PageSize)
                .Take(searchParams.PageSize)
                .ToListAsync();

            var result = applications.Select(a =>
            {
                var ranking = rankings.GetValueOrDefault(a.CandidateId);
                var latestInterview = a.InterviewSessions.OrderByDescending(i => i.StartedAt).FirstOrDefault();

                return new CandidateApplicationDto
                {
                    ApplicationId = a.Id,
                    CandidateId = a.CandidateId,
                    CandidateName = a.Candidate.FullName ?? "Unknown",
                    CandidateEmail = a.Candidate.User.Email,
                    CurrentTitle = a.Candidate.CurrentTitle,
                    YearsOfExperience = a.Candidate.YearsOfExperience,
                    ProfileImage = a.Candidate.ProfileImagePath,
                    AppliedAt = a.AppliedAt,
                    Status = a.Status.ToString(),
                    AtsScore = a.Resume?.AtsScore,
                    RankingScore = ranking?.Score,
                    Rank = ranking?.Rank,
                    Skills = a.Candidate.Skills.Take(5).Select(s => s.SkillName).ToList(),
                    HasInterview = a.InterviewSessions.Any(),
                    InterviewScore = latestInterview?.OverallScore
                };
            }).ToList();

            return ApiResponse<PaginatedResponse<CandidateApplicationDto>>.SuccessResponse(new PaginatedResponse<CandidateApplicationDto>
            {
                Items = result,
                TotalCount = totalCount,
                PageNumber = searchParams.PageNumber,
                PageSize = searchParams.PageSize
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting job applications");
            return ApiResponse<PaginatedResponse<CandidateApplicationDto>>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<List<CandidateApplicationDto>>> GetRankedCandidatesAsync(long jobId, long recruiterId)
    {
        try
        {
            // Verify recruiter owns the job
            var job = await _context.Jobs.FirstOrDefaultAsync(j => j.Id == jobId);
            if (job == null || job.RecruiterId != recruiterId)
            {
                return ApiResponse<List<CandidateApplicationDto>>.FailureResponse("Job not found or access denied");
            }

            var applications = await _context.Applications
                .Include(a => a.Candidate).ThenInclude(c => c.User)
                .Include(a => a.Candidate).ThenInclude(c => c.Skills)
                .Include(a => a.Resume)
                .Include(a => a.InterviewSessions)
                .Where(a => a.JobId == jobId && a.Status != ApplicationStatus.Withdrawn && a.Status != ApplicationStatus.Rejected)
                .ToListAsync();

            // Get rankings
            var rankings = await _context.CandidateRankings
                .Where(r => r.JobId == jobId)
                .ToDictionaryAsync(r => r.CandidateId, r => new { r.Score, r.Rank });

            var rankedCandidates = applications
                .Select(a =>
                {
                    var ranking = rankings.GetValueOrDefault(a.CandidateId);
                    var latestInterview = a.InterviewSessions.OrderByDescending(i => i.StartedAt).FirstOrDefault();

                    return new CandidateApplicationDto
                    {
                        ApplicationId = a.Id,
                        CandidateId = a.CandidateId,
                        CandidateName = a.Candidate.FullName ?? "Unknown",
                        CandidateEmail = a.Candidate.User.Email,
                        CurrentTitle = a.Candidate.CurrentTitle,
                        YearsOfExperience = a.Candidate.YearsOfExperience,
                        ProfileImage = a.Candidate.ProfileImagePath,
                        AppliedAt = a.AppliedAt,
                        Status = a.Status.ToString(),
                        AtsScore = a.Resume?.AtsScore,
                        RankingScore = ranking?.Score ?? 0,
                        Rank = ranking?.Rank ?? int.MaxValue,
                        Skills = a.Candidate.Skills.Take(5).Select(s => s.SkillName).ToList(),
                        HasInterview = a.InterviewSessions.Any(),
                        InterviewScore = latestInterview?.OverallScore
                    };
                })
                .OrderBy(c => c.Rank)
                .ThenByDescending(c => c.RankingScore)
                .ToList();

            return ApiResponse<List<CandidateApplicationDto>>.SuccessResponse(rankedCandidates);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting ranked candidates");
            return ApiResponse<List<CandidateApplicationDto>>.FailureResponse("An error occurred");
        }
    }

    public async Task<bool> HasCandidateAppliedAsync(long candidateId, long jobId)
    {
        return await _context.Applications.AnyAsync(a =>
            a.CandidateId == candidateId && a.JobId == jobId);
    }

    #region Helper Methods

    private static ApplicationDto MapToApplicationDto(Application application)
    {
        var latestInterview = application.InterviewSessions?.OrderByDescending(i => i.StartedAt).FirstOrDefault();

        return new ApplicationDto
        {
            Id = application.Id,
            CandidateId = application.CandidateId,
            JobId = application.JobId,
            ResumeId = application.ResumeId,
            AppliedVia = application.AppliedVia,
            AppliedAt = application.AppliedAt,
            Status = application.Status.ToString(),
            CoverLetter = application.CoverLetter,
            RecruiterNotes = application.RecruiterNotes,
            ReviewedAt = application.ReviewedAt,
            CandidateName = application.Candidate.FullName ?? "Unknown",
            CandidateEmail = application.Candidate.User.Email,
            CandidatePhone = application.Candidate.Phone,
            JobTitle = application.Job.Title,
            CompanyName = application.Job.Company?.Name,
            ResumeName = application.Resume?.FileName,
            AtsScore = application.Resume?.AtsScore,
            HasInterview = application.InterviewSessions?.Any() ?? false,
            InterviewScore = latestInterview?.OverallScore
        };
    }

    #endregion
}

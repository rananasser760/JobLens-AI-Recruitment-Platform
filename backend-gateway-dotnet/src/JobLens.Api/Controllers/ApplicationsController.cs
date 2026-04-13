using JobLens.Api.Compatibility;
using JobLens.Api.Contracts;
using JobLens.Application.Common;
using JobLens.Application.DTOs.Applications;
using JobLens.Application.Interfaces;
using JobLens.Infrastructure.Persistence;
using JobLens.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JobLens.Api.Controllers;

[Route("api/applications")]
public sealed class ApplicationsController(IApplicationService applicationService, JobLensDbContext dbContext) : AppControllerBase
{
    [Authorize(Roles = "Candidate")]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateApplicationRequest request, CancellationToken cancellationToken)
    {
        var result = await applicationService.CreateAsync(GetRequiredUserId(), request, cancellationToken);
        if (!result.Success || result.Data is null)
        {
            return BadRequest(new ApiResponse<ApplicationViewDto>(false, null, result.Message, result.Errors));
        }

        var application = await LoadApplicationAsync(result.Data.ApplicationId, cancellationToken);
        if (application is null)
        {
            return Ok(new ApiResponse<ApplicationViewDto>(true, null, result.Message));
        }

        return Ok(new ApiResponse<ApplicationViewDto>(true, ToApplicationView(application), result.Message));
    }

    [Authorize(Roles = "Candidate")]
    [HttpGet("mine")]
    public async Task<IActionResult> GetMine(CancellationToken cancellationToken)
    {
        var data = await applicationService.GetCandidateApplicationsAsync(GetRequiredUserId(), cancellationToken);
        return Ok(data);
    }

    [Authorize(Roles = "Candidate")]
    [HttpGet("my-applications")]
    public async Task<IActionResult> GetMyApplications(
        [FromQuery] string? status,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var candidate = await dbContext.CandidateProfiles.FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (candidate is null)
        {
            return NotFound(new ApiResponse<PaginatedResponseDto<ApplicationViewDto>>(false, null, "Candidate profile not found.", ["not_found"]));
        }

        var query = dbContext.Applications
            .Include(x => x.CandidateProfile)
            .ThenInclude(x => x.User)
            .Include(x => x.JobPosting)
            .ThenInclude(x => x.Company)
            .Include(x => x.Resume)
            .Include(x => x.AtsAssessments)
            .Include(x => x.InterviewSessions)
            .Where(x => x.CandidateProfileId == candidate.Id);

        if (!string.IsNullOrWhiteSpace(status))
        {
            var target = FrontendStatusMapper.FromFrontend(status);
            query = query.Where(x => x.Status == target);
        }

        var total = await query.CountAsync(cancellationToken);
        var safePage = Math.Max(1, pageNumber);
        var safeSize = Math.Clamp(pageSize, 1, 200);

        var items = await query
            .OrderByDescending(x => x.SubmittedAtUtc)
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .ToListAsync(cancellationToken);

        var page = FrontendStatusMapper.ToPage(items.Select(ToApplicationView).ToList(), safePage, safeSize, total);
        return Ok(new ApiResponse<PaginatedResponseDto<ApplicationViewDto>>(true, page));
    }

    [Authorize(Roles = "Recruiter,Admin")]
    [HttpGet("jobs/{jobId:long}")]
    public async Task<IActionResult> GetApplicants(long jobId, CancellationToken cancellationToken) =>
        Ok(await applicationService.GetApplicantsForJobAsync(GetRequiredUserId(), jobId, cancellationToken));

    [Authorize]
    [HttpGet("{applicationId:long}")]
    public async Task<IActionResult> GetById(long applicationId, CancellationToken cancellationToken)
    {
        var application = await LoadApplicationAsync(applicationId, cancellationToken);
        if (application is null)
        {
            return NotFound(new ApiResponse<ApplicationViewDto>(false, null, "Application not found.", ["not_found"]));
        }

        if (!await CanAccessApplicationAsync(application, cancellationToken))
        {
            return Forbid();
        }

        return Ok(new ApiResponse<ApplicationViewDto>(true, ToApplicationView(application)));
    }

    [Authorize(Roles = "Recruiter,Admin")]
    [HttpGet("job/{jobId:long}")]
    public async Task<IActionResult> GetJobApplications(
        long jobId,
        [FromQuery] string? status,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (!await CanManageJobAsync(jobId, cancellationToken))
        {
            return Forbid();
        }

        var query = dbContext.Applications
            .Include(x => x.CandidateProfile)
            .ThenInclude(x => x.User)
            .Include(x => x.JobPosting)
            .Include(x => x.AtsAssessments)
            .Include(x => x.InterviewSessions)
            .Where(x => x.JobPostingId == jobId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            var target = FrontendStatusMapper.FromFrontend(status);
            query = query.Where(x => x.Status == target);
        }

        var total = await query.CountAsync(cancellationToken);
        var safePage = Math.Max(1, pageNumber);
        var safeSize = Math.Clamp(pageSize, 1, 200);

        var items = await query
            .OrderByDescending(x => x.SubmittedAtUtc)
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .ToListAsync(cancellationToken);

        var dtos = items.Select(ToCandidateApplicationView).ToList();
        var page = FrontendStatusMapper.ToPage(dtos, safePage, safeSize, total);
        return Ok(new ApiResponse<PaginatedResponseDto<CandidateApplicationViewDto>>(true, page));
    }

    [Authorize(Roles = "Recruiter,Admin")]
    [HttpGet("job/{jobId:long}/ranked")]
    public async Task<IActionResult> GetRankedCandidates(long jobId, CancellationToken cancellationToken)
    {
        if (!await CanManageJobAsync(jobId, cancellationToken))
        {
            return Forbid();
        }

        var items = await dbContext.Applications
            .Include(x => x.CandidateProfile)
            .ThenInclude(x => x.User)
            .Include(x => x.AtsAssessments)
            .Include(x => x.InterviewSessions)
            .Where(x => x.JobPostingId == jobId)
            .ToListAsync(cancellationToken);

        var ranked = items
            .Select(ToCandidateApplicationView)
            .Select(x =>
            {
                var ats = x.AtsScore ?? 0d;
                var interview = x.InterviewScore ?? 0d;
                var ranking = Math.Round((ats * 0.4) + (interview * 0.6), 2);
                return x with { RankingScore = ranking };
            })
            .OrderByDescending(x => x.RankingScore ?? 0d)
            .Select((item, idx) => item with { Rank = idx + 1 })
            .ToList();

        return Ok(new ApiResponse<IReadOnlyList<CandidateApplicationViewDto>>(true, ranked));
    }

    [Authorize(Roles = "Candidate")]
    [HttpGet("check/{jobId:long}")]
    public async Task<IActionResult> CheckIfApplied(long jobId, CancellationToken cancellationToken)
    {
        var userId = GetRequiredUserId();
        var hasApplied = await dbContext.Applications.AnyAsync(
            x => x.JobPostingId == jobId && x.CandidateProfile.UserId == userId,
            cancellationToken);

        return Ok(new ApiResponse<ApplicationCheckDto>(true, new ApplicationCheckDto(hasApplied)));
    }

    [Authorize(Roles = "Recruiter,Admin")]
    [HttpPut("{applicationId:long}/status")]
    public async Task<IActionResult> UpdateStatus(long applicationId, [FromBody] UpdateApplicationStatusRequest request, CancellationToken cancellationToken)
    {
        var application = await dbContext.Applications
            .Include(x => x.JobPosting)
            .FirstOrDefaultAsync(x => x.Id == applicationId, cancellationToken);

        if (application is null)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Application not found.", ["not_found"]));
        }

        if (!await CanManageJobAsync(application.JobPostingId, cancellationToken))
        {
            return Forbid();
        }

        application.Status = FrontendStatusMapper.FromFrontend(request.Status);
        application.RecruiterNotes = request.Notes?.Trim() ?? string.Empty;
        application.ReviewedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new ApiResponse<bool>(true, true, "Application status updated."));
    }

    [Authorize(Roles = "Recruiter,Admin")]
    [HttpPut("bulk-status")]
    public async Task<IActionResult> BulkUpdateStatus([FromBody] BulkUpdateApplicationStatusRequest request, CancellationToken cancellationToken)
    {
        var ids = request.ApplicationIds?.Distinct().Where(x => x > 0).ToArray() ?? [];
        if (ids.Length == 0)
        {
            return BadRequest(new ApiResponse<BulkUpdateApplicationStatusResultDto>(false, null, "No application IDs provided.", ["validation_error"]));
        }

        var apps = await dbContext.Applications
            .Include(x => x.JobPosting)
            .Where(x => ids.Contains(x.Id))
            .ToListAsync(cancellationToken);

        var foundIds = apps.Select(x => x.Id).ToHashSet();
        var notFoundIds = ids.Where(x => !foundIds.Contains(x)).ToArray();
        var unauthorizedIds = new List<long>();
        var updated = 0;

        foreach (var app in apps)
        {
            if (!await CanManageJobAsync(app.JobPostingId, cancellationToken))
            {
                unauthorizedIds.Add(app.Id);
                continue;
            }

            app.Status = FrontendStatusMapper.FromFrontend(request.Status);
            app.RecruiterNotes = request.Notes?.Trim() ?? string.Empty;
            app.ReviewedAtUtc = DateTime.UtcNow;
            updated++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var result = new BulkUpdateApplicationStatusResultDto(
            ids.Length,
            updated,
            ids.Length - updated,
            notFoundIds,
            unauthorizedIds);

        return Ok(new ApiResponse<BulkUpdateApplicationStatusResultDto>(true, result, "Bulk status update completed."));
    }

    [Authorize(Roles = "Candidate")]
    [HttpPost("{applicationId:long}/withdraw")]
    public async Task<IActionResult> Withdraw(long applicationId, CancellationToken cancellationToken)
    {
        var application = await dbContext.Applications
            .Include(x => x.CandidateProfile)
            .FirstOrDefaultAsync(x => x.Id == applicationId, cancellationToken);

        if (application is null)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Application not found.", ["not_found"]));
        }

        if (application.CandidateProfile.UserId != GetRequiredUserId())
        {
            return Forbid();
        }

        application.Status = Domain.Enums.ApplicationStatus.Withdrawn;
        application.ReviewedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new ApiResponse<bool>(true, true, "Application withdrawn."));
    }

    private async Task<Domain.Entities.JobApplication?> LoadApplicationAsync(long applicationId, CancellationToken cancellationToken)
    {
        return await dbContext.Applications
            .Include(x => x.CandidateProfile)
            .ThenInclude(x => x.User)
            .Include(x => x.JobPosting)
            .ThenInclude(x => x.Company)
            .Include(x => x.Resume)
            .Include(x => x.AtsAssessments)
            .Include(x => x.InterviewSessions)
            .FirstOrDefaultAsync(x => x.Id == applicationId, cancellationToken);
    }

    private async Task<bool> CanAccessApplicationAsync(Domain.Entities.JobApplication application, CancellationToken cancellationToken)
    {
        if (User.IsInRole("Admin"))
        {
            return true;
        }

        var userId = GetRequiredUserId();
        if (User.IsInRole("Candidate"))
        {
            return application.CandidateProfile.UserId == userId;
        }

        if (User.IsInRole("Recruiter"))
        {
            var recruiter = await dbContext.RecruiterProfiles.FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);
            return recruiter is not null && application.JobPosting.CompanyId == recruiter.CompanyId;
        }

        return false;
    }

    private async Task<bool> CanManageJobAsync(long jobId, CancellationToken cancellationToken)
    {
        if (User.IsInRole("Admin"))
        {
            return true;
        }

        var userId = GetRequiredUserId();
        var recruiter = await dbContext.RecruiterProfiles.FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (recruiter is null)
        {
            return false;
        }

        var job = await dbContext.Jobs.FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        return job is not null && job.CompanyId == recruiter.CompanyId;
    }

    private static ApplicationViewDto ToApplicationView(Domain.Entities.JobApplication application)
    {
        var latestAts = application.AtsAssessments.OrderByDescending(x => x.EvaluatedAtUtc ?? x.CreatedAtUtc).FirstOrDefault();
        var latestInterview = application.InterviewSessions.OrderByDescending(x => x.UpdatedAtUtc).FirstOrDefault();

        return new ApplicationViewDto(
            application.Id,
            application.CandidateProfileId,
            application.JobPostingId,
            application.ResumeId,
            string.IsNullOrWhiteSpace(application.AppliedVia) ? "Portal" : application.AppliedVia,
            application.SubmittedAtUtc,
            FrontendStatusMapper.ToFrontend(application.Status),
            string.IsNullOrWhiteSpace(application.CoverLetter) ? null : application.CoverLetter,
            string.IsNullOrWhiteSpace(application.RecruiterNotes) ? null : application.RecruiterNotes,
            application.ReviewedAtUtc,
            application.CandidateProfile.User.DisplayName,
            application.CandidateProfile.User.Email,
            string.IsNullOrWhiteSpace(application.CandidateProfile.Phone) ? null : application.CandidateProfile.Phone,
            application.JobPosting.Title,
            application.JobPosting.Company?.Name,
            application.Resume.FileName,
            latestAts?.Score,
            latestInterview is not null,
            latestInterview?.FinalScore);
    }

    private static CandidateApplicationViewDto ToCandidateApplicationView(Domain.Entities.JobApplication application)
    {
        var latestAts = application.AtsAssessments.OrderByDescending(x => x.EvaluatedAtUtc ?? x.CreatedAtUtc).FirstOrDefault();
        var latestInterview = application.InterviewSessions.OrderByDescending(x => x.UpdatedAtUtc).FirstOrDefault();

        return new CandidateApplicationViewDto(
            application.Id,
            application.CandidateProfileId,
            application.CandidateProfile.User.DisplayName,
            application.CandidateProfile.User.Email,
            string.IsNullOrWhiteSpace(application.CandidateProfile.Headline) ? null : application.CandidateProfile.Headline,
            application.CandidateProfile.YearsExperience,
            string.IsNullOrWhiteSpace(application.CandidateProfile.ProfileImagePath) ? null : application.CandidateProfile.ProfileImagePath,
            application.SubmittedAtUtc,
            FrontendStatusMapper.ToFrontend(application.Status),
            latestAts?.Score,
            null,
            null,
            ServiceJson.DeserializeStringList(application.CandidateProfile.SkillsJson),
            latestInterview is not null,
            latestInterview?.FinalScore);
    }
}

using JobLens.Api.Compatibility;
using JobLens.Api.Contracts;
using JobLens.Application.Common;
using JobLens.Application.Interfaces;
using JobLens.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JobLens.Api.Controllers;

[Authorize(Roles = "Recruiter,Admin")]
[Route("api/recruiters")]
public sealed class RecruitersController(IRecruiterService recruiterService, JobLensDbContext dbContext) : AppControllerBase
{
    private long CurrentUserId => GetRequiredUserId();

    [HttpGet("profile")]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken)
    {
        var profile = await dbContext.RecruiterProfiles
            .Include(x => x.User)
            .Include(x => x.Company)
            .FirstOrDefaultAsync(x => x.UserId == CurrentUserId, cancellationToken);

        if (profile is null)
        {
            if (!User.IsInRole("Admin"))
            {
                return NotFound(new ApiResponse<RecruiterProfileViewDto>(false, null, "Recruiter profile not found.", ["not_found"]));
            }

            var adminUser = await dbContext.Users.FirstOrDefaultAsync(x => x.Id == CurrentUserId && x.IsActive, cancellationToken);
            if (adminUser is null)
            {
                return NotFound(new ApiResponse<RecruiterProfileViewDto>(false, null, "User not found.", ["not_found"]));
            }

            var adminDto = new RecruiterProfileViewDto(
                0,
                adminUser.Id,
                adminUser.Email,
                adminUser.DisplayName,
                null,
                "Administrator",
                adminUser.CreatedAtUtc,
                null);

            return Ok(new ApiResponse<RecruiterProfileViewDto>(true, adminDto));
        }

        var dto = new RecruiterProfileViewDto(
            profile.Id,
            profile.UserId,
            profile.User.Email,
            profile.User.DisplayName,
            string.IsNullOrWhiteSpace(profile.Phone) ? null : profile.Phone,
            string.IsNullOrWhiteSpace(profile.JobTitle) ? null : profile.JobTitle,
            profile.CreatedAtUtc,
            profile.Company is null
                ? null
                : new CompanyBasicDto(
                    profile.Company.Id,
                    profile.Company.Name,
                    string.IsNullOrWhiteSpace(profile.Company.WebsiteUrl) ? null : profile.Company.WebsiteUrl,
                    string.IsNullOrWhiteSpace(profile.Company.Industry) ? null : profile.Company.Industry,
                    string.IsNullOrWhiteSpace(profile.Company.LogoUrl) ? null : profile.Company.LogoUrl));

        return Ok(new ApiResponse<RecruiterProfileViewDto>(true, dto));
    }

    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateRecruiterProfileCompatRequest request, CancellationToken cancellationToken)
    {
        var profile = await dbContext.RecruiterProfiles
            .Include(x => x.User)
            .Include(x => x.Company)
            .FirstOrDefaultAsync(x => x.UserId == CurrentUserId, cancellationToken);

        if (profile is null)
        {
            if (User.IsInRole("Admin"))
            {
                return BadRequest(new ApiResponse<RecruiterProfileViewDto>(false, null, "Admin profile is read-only in recruiter profile settings.", ["admin_read_only"]));
            }

            return NotFound(new ApiResponse<RecruiterProfileViewDto>(false, null, "Recruiter profile not found.", ["not_found"]));
        }

        if (!string.IsNullOrWhiteSpace(request.FullName))
        {
            profile.User.DisplayName = request.FullName.Trim();
        }

        if (request.Phone is not null)
        {
            profile.Phone = request.Phone.Trim();
        }

        if (request.Position is not null)
        {
            profile.JobTitle = request.Position.Trim();
        }

        if (request.CompanyId.HasValue)
        {
            var company = await dbContext.Companies.FirstOrDefaultAsync(x => x.Id == request.CompanyId.Value, cancellationToken);
            if (company is null)
            {
                return NotFound(new ApiResponse<RecruiterProfileViewDto>(false, null, "Company not found.", ["not_found"]));
            }

            profile.CompanyId = company.Id;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetProfile(cancellationToken);
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard(CancellationToken cancellationToken)
    {
        var profile = await dbContext.RecruiterProfiles.FirstOrDefaultAsync(x => x.UserId == CurrentUserId, cancellationToken);
        if (profile is null && !User.IsInRole("Admin"))
        {
            return NotFound(new ApiResponse<RecruiterDashboardDto>(false, null, "Recruiter profile not found.", ["not_found"]));
        }

        var jobQuery = dbContext.Jobs.AsQueryable();
        if (profile is not null)
        {
            jobQuery = jobQuery.Where(x => x.CompanyId == profile.CompanyId);
        }

        var totalJobs = await jobQuery.CountAsync(cancellationToken);
        var activeJobs = await jobQuery.CountAsync(x => x.IsActive, cancellationToken);

        var applicationsQuery = dbContext.Applications
            .Include(x => x.CandidateProfile)
            .ThenInclude(x => x.User)
            .Include(x => x.JobPosting)
            .AsQueryable();

        if (profile is not null)
        {
            applicationsQuery = applicationsQuery.Where(x => x.JobPosting.CompanyId == profile.CompanyId);
        }

        var allApplications = await applicationsQuery.ToListAsync(cancellationToken);
        var totalApplications = allApplications.Count;
        var pendingApplications = allApplications.Count(x => x.Status is Domain.Enums.ApplicationStatus.Submitted or Domain.Enums.ApplicationStatus.AtsPending);

        var interviewsQuery = dbContext.InterviewSessions
            .Include(x => x.Application)
            .ThenInclude(x => x.JobPosting)
            .AsQueryable();

        if (profile is not null)
        {
            interviewsQuery = interviewsQuery.Where(x => x.Application.JobPosting.CompanyId == profile.CompanyId);
        }

        var interviews = await interviewsQuery.ToListAsync(cancellationToken);

        var interviewsScheduled = interviews.Count(x => x.Status == Domain.Enums.InterviewSessionStatus.Scheduled);
        var interviewsCompleted = interviews.Count(x => x.Status == Domain.Enums.InterviewSessionStatus.Completed);

        var recent = allApplications
            .OrderByDescending(x => x.SubmittedAtUtc)
            .Take(5)
            .Select(x => new RecentApplicationDto(
                x.Id,
                x.CandidateProfile.User.DisplayName,
                x.JobPosting.Title,
                x.SubmittedAtUtc,
                FrontendStatusMapper.ToFrontend(x.Status)))
            .ToList();

        var topJobs = allApplications
            .GroupBy(x => new { x.JobPostingId, x.JobPosting.Title })
            .Select(x => new JobStatsDto(
                x.Key.JobPostingId,
                x.Key.Title,
                x.Count(),
                x.Count(item => item.Status == Domain.Enums.ApplicationStatus.AtsQualified)))
            .OrderByDescending(x => x.ApplicationCount)
            .Take(5)
            .ToList();

        var dto = new RecruiterDashboardDto(
            totalJobs,
            activeJobs,
            totalApplications,
            pendingApplications,
            interviewsScheduled,
            interviewsCompleted,
            recent,
            topJobs);

        return Ok(new ApiResponse<RecruiterDashboardDto>(true, dto));
    }
}

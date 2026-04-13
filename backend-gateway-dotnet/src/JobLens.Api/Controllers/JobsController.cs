using JobLens.Api.Compatibility;
using JobLens.Api.Contracts;
using JobLens.Application.Common;
using JobLens.Application.DTOs.Jobs;
using JobLens.Application.Interfaces;
using JobLens.Infrastructure.Persistence;
using JobLens.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JobLens.Api.Controllers;

[Route("api/jobs")]
public sealed class JobsController(IJobService jobService, IAdminService adminService, IAiBackendClient aiBackendClient, JobLensDbContext dbContext) : AppControllerBase
{
    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> GetJobs(
        [FromQuery] string? keyword,
        [FromQuery] string? location,
        [FromQuery] string? skills,
        [FromQuery] string? source,
        [FromQuery] bool? isActive,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.Jobs.Include(x => x.Company).AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var term = keyword.Trim().ToLowerInvariant();
            query = query.Where(x =>
                x.Title.ToLower().Contains(term) ||
                x.Description.ToLower().Contains(term) ||
                x.Requirements.ToLower().Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(location))
        {
            var term = location.Trim().ToLowerInvariant();
            query = query.Where(x => x.Location.ToLower().Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(skills))
        {
            var terms = skills.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.ToLowerInvariant())
                .ToArray();
            if (terms.Length > 0)
            {
                query = query.Where(x => terms.Any(skill => x.SkillsJson.ToLower().Contains(skill)));
            }
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            query = source.Equals("Scraped", StringComparison.OrdinalIgnoreCase)
                ? query.Where(x => x.SourceType == Domain.Enums.JobSourceType.External)
                : query.Where(x => x.SourceType == Domain.Enums.JobSourceType.Internal);
        }

        if (isActive.HasValue)
        {
            query = query.Where(x => x.IsActive == isActive.Value);
        }

        var total = await query.CountAsync(cancellationToken);
        var safePage = Math.Max(1, pageNumber);
        var safeSize = Math.Clamp(pageSize, 1, 200);

        var jobs = await query
            .OrderByDescending(x => x.PostedAtUtc ?? x.CreatedAtUtc)
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .Select(x => new JobListItemDto(
                x.Id,
                x.Title,
                string.IsNullOrWhiteSpace(x.Location) ? null : x.Location,
                string.IsNullOrWhiteSpace(x.EmploymentType) ? "FullTime" : x.EmploymentType,
                string.IsNullOrWhiteSpace(x.SalaryRange) ? null : x.SalaryRange,
                x.PostedAtUtc ?? x.CreatedAtUtc,
                x.Company != null ? x.Company.Name : null,
                x.Company != null && !string.IsNullOrWhiteSpace(x.Company.LogoUrl) ? x.Company.LogoUrl : null,
                FrontendStatusMapper.ToFrontend(x.SourceType),
                ServiceJson.DeserializeStringList(x.SkillsJson).Take(6).ToArray(),
                null))
            .ToListAsync(cancellationToken);

        var page = FrontendStatusMapper.ToPage(jobs, safePage, safeSize, total);
        return Ok(new ApiResponse<PaginatedResponseDto<JobListItemDto>>(true, page));
    }

    [AllowAnonymous]
    [HttpGet("{jobId:long}")]
    public async Task<IActionResult> GetJob(long jobId, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs
            .Include(x => x.Company)
            .Include(x => x.Applications)
            .FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);

        if (job is null)
        {
            return NotFound(new ApiResponse<JobViewDto>(false, null, "Job not found.", ["not_found"]));
        }

        return Ok(new ApiResponse<JobViewDto>(true, ToJobView(job)));
    }

    [Authorize(Roles = "Recruiter,Admin")]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateJobCompatRequest request, CancellationToken cancellationToken)
    {
        var create = new CreateJobRequest(
            request.Title,
            request.Description,
            request.Location ?? string.Empty,
            request.RequiredSkills?.Select(x => x.SkillName).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() ?? [],
            request.ExpiresAt);

        var result = await jobService.CreateJobAsync(GetRequiredUserId(), create, cancellationToken);
        if (!result.Success || result.Data is null)
        {
            return BadRequest(new ApiResponse<JobViewDto>(false, null, result.Message, result.Errors));
        }

        var job = await dbContext.Jobs.Include(x => x.Company).Include(x => x.Applications).FirstOrDefaultAsync(x => x.Id == result.Data.JobId, cancellationToken);
        if (job is null)
        {
            return Ok(new ApiResponse<JobViewDto>(true, null, result.Message));
        }

        job.Requirements = request.Requirements?.Trim() ?? job.Requirements;
        job.Responsibilities = request.Responsibilities?.Trim() ?? job.Responsibilities;
        job.EmploymentType = string.IsNullOrWhiteSpace(request.EmploymentType) ? job.EmploymentType : request.EmploymentType.Trim();
        job.SalaryRange = request.SalaryRange?.Trim() ?? job.SalaryRange;
        job.SalaryMin = request.SalaryMin;
        job.SalaryMax = request.SalaryMax;
        job.Currency = request.Currency?.Trim() ?? job.Currency;
        job.ExperienceLevel = request.ExperienceLevel?.Trim() ?? job.ExperienceLevel;
        await dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new ApiResponse<JobViewDto>(true, ToJobView(job), result.Message));
    }

    [Authorize(Roles = "Recruiter,Admin")]
    [HttpPut("{jobId:long}")]
    public async Task<IActionResult> Update(long jobId, [FromBody] UpdateJobCompatRequest request, CancellationToken cancellationToken)
    {
        var current = await dbContext.Jobs.FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (current is null)
        {
            return NotFound(new ApiResponse<JobViewDto>(false, null, "Job not found.", ["not_found"]));
        }

        var update = new UpdateJobRequest(
            request.Title?.Trim() ?? current.Title,
            request.Description?.Trim() ?? current.Description,
            request.Location?.Trim() ?? current.Location,
            ServiceJson.DeserializeStringList(current.SkillsJson),
            request.IsActive ?? current.IsActive,
            request.ExpiresAt ?? current.ExpiresAtUtc);

        var result = await jobService.UpdateJobAsync(GetRequiredUserId(), jobId, update, cancellationToken);
        if (!result.Success)
        {
            return BadRequest(new ApiResponse<JobViewDto>(false, null, result.Message, result.Errors));
        }

        current = await dbContext.Jobs.Include(x => x.Company).Include(x => x.Applications).FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (current is null)
        {
            return NotFound(new ApiResponse<JobViewDto>(false, null, "Job not found.", ["not_found"]));
        }

        if (request.Requirements is not null)
        {
            current.Requirements = request.Requirements.Trim();
        }

        if (request.Responsibilities is not null)
        {
            current.Responsibilities = request.Responsibilities.Trim();
        }

        if (request.EmploymentType is not null)
        {
            current.EmploymentType = request.EmploymentType.Trim();
        }

        if (request.SalaryRange is not null)
        {
            current.SalaryRange = request.SalaryRange.Trim();
        }

        current.SalaryMin = request.SalaryMin ?? current.SalaryMin;
        current.SalaryMax = request.SalaryMax ?? current.SalaryMax;

        if (request.ExperienceLevel is not null)
        {
            current.ExperienceLevel = request.ExperienceLevel.Trim();
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new ApiResponse<JobViewDto>(true, ToJobView(current), result.Message));
    }

    [Authorize(Roles = "Recruiter,Admin")]
    [HttpDelete("{jobId:long}")]
    public async Task<IActionResult> Delete(long jobId, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (job is null)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Job not found.", ["not_found"]));
        }

        var vectorEntry = await dbContext.VectorIndexEntries
            .FirstOrDefaultAsync(x => x.EntityType == "job" && x.EntityId == jobId, cancellationToken);
        if (vectorEntry is not null)
        {
            dbContext.VectorIndexEntries.Remove(vectorEntry);
        }

        var recommendationEntries = await dbContext.RecommendationCacheEntries
            .Where(x =>
                (x.SubjectType == Domain.Enums.RecommendationSubjectType.Job && x.SubjectId == jobId) ||
                (x.TargetType == Domain.Enums.RecommendationTargetType.Job && x.TargetId == jobId))
            .ToListAsync(cancellationToken);
        if (recommendationEntries.Count > 0)
        {
            dbContext.RecommendationCacheEntries.RemoveRange(recommendationEntries);
        }

        dbContext.Jobs.Remove(job);
        await dbContext.SaveChangesAsync(cancellationToken);

        _ = await aiBackendClient.DeleteJobVectorAsync(jobId, cancellationToken);

        return Ok(new ApiResponse<bool>(true, true, "Job deleted."));
    }

    [Authorize(Roles = "Recruiter,Admin")]
    [HttpPost("{jobId:long}/toggle-status")]
    public async Task<IActionResult> ToggleStatus(long jobId, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (job is null)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Job not found.", ["not_found"]));
        }

        job.IsActive = !job.IsActive;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new ApiResponse<bool>(true, true, "Job status updated."));
    }

    [Authorize(Roles = "Recruiter,Admin")]
    [HttpPost("{jobId:long}/skills")]
    public async Task<IActionResult> AddSkill(long jobId, [FromBody] CreateJobSkillRequest request, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (job is null)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Job not found.", ["not_found"]));
        }

        if (string.IsNullOrWhiteSpace(request.SkillName))
        {
            return BadRequest(new ApiResponse<bool>(false, false, "Skill name is required.", ["validation_error"]));
        }

        var skills = ServiceJson.DeserializeStringList(job.SkillsJson);
        if (!skills.Any(x => string.Equals(x, request.SkillName, StringComparison.OrdinalIgnoreCase)))
        {
            skills.Add(request.SkillName.Trim());
            job.SkillsJson = ServiceJson.Serialize(skills);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Ok(new ApiResponse<bool>(true, true, "Skill added."));
    }

    [Authorize(Roles = "Recruiter,Admin")]
    [HttpDelete("{jobId:long}/skills/{skillId:long}")]
    public async Task<IActionResult> RemoveSkill(long jobId, long skillId, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (job is null)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Job not found.", ["not_found"]));
        }

        var skills = ServiceJson.DeserializeStringList(job.SkillsJson);
        var index = (int)skillId - 1;
        if (index < 0 || index >= skills.Count)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Skill not found.", ["not_found"]));
        }

        skills.RemoveAt(index);
        job.SkillsJson = ServiceJson.Serialize(skills);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new ApiResponse<bool>(true, true, "Skill removed."));
    }

    [Authorize(Roles = "Candidate")]
    [HttpGet("recommendations")]
    public async Task<IActionResult> GetCandidateRecommendations([FromQuery] int limit = 10, [FromQuery] bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var recs = await jobService.GetRecommendationsForCandidateAsync(GetRequiredUserId(), limit, cancellationToken, forceRefresh);
        if (!recs.Success || recs.Data is null)
        {
            return BadRequest(new ApiResponse<IReadOnlyList<JobRecommendationDto>>(false, null, recs.Message, recs.Errors));
        }

        var ids = recs.Data.Select(x => x.TargetId).Distinct().ToList();
        var jobs = await dbContext.Jobs.Include(x => x.Company).Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);

        var mapped = recs.Data.Select(x =>
        {
            jobs.TryGetValue(x.TargetId, out var job);
            return new JobRecommendationDto(
                x.TargetId,
                job?.Title ?? "Recommended Job",
                job?.Company?.Name,
                job?.Location,
                x.Score,
                ServiceJson.DeserializeStringList(job?.SkillsJson ?? "[]").Take(6).ToArray(),
                x.Reason);
        }).ToList();

        return Ok(new ApiResponse<IReadOnlyList<JobRecommendationDto>>(true, mapped));
    }

    [Authorize(Roles = "Candidate")]
    [HttpPost("recommendations/match-from-text")]
    public async Task<IActionResult> MatchFromText([FromBody] MatchJobsFromTextRequest request, [FromQuery] int limit = 5, CancellationToken cancellationToken = default)
    {
        var resumeText = request.ResumeText?.Trim() ?? string.Empty;
        if (resumeText.Length == 0)
        {
            return BadRequest(new ApiResponse<IReadOnlyList<JobRecommendationDto>>(false, null, "Resume text is required.", ["validation_error"]));
        }

        var result = await jobService.MatchJobsFromTextAsync(resumeText, limit, cancellationToken);
        if (!result.Success || result.Data is null)
        {
            return BadRequest(new ApiResponse<IReadOnlyList<JobRecommendationDto>>(false, null, result.Message, result.Errors));
        }

        var ids = result.Data.Select(x => x.TargetId).Distinct().Where(x => x > 0).ToList();
        var jobsById = await dbContext.Jobs
            .Include(x => x.Company)
            .Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        var mapped = result.Data
            .Select(x =>
            {
                jobsById.TryGetValue(x.TargetId, out var job);
                return new JobRecommendationDto(
                    x.TargetId,
                    job?.Title ?? "Recommended Job",
                    job?.Company?.Name,
                    job?.Location,
                    x.Score,
                    ServiceJson.DeserializeStringList(job?.SkillsJson ?? "[]").Take(6).ToArray(),
                    x.Reason);
            })
            .ToList();

        return Ok(new ApiResponse<IReadOnlyList<JobRecommendationDto>>(true, mapped));
    }

    [Authorize(Roles = "Recruiter,Admin")]
    [HttpGet("{jobId:long}/candidate-recommendations")]
    public async Task<IActionResult> GetCandidateRecommendationsForJob(long jobId, [FromQuery] int limit = 10, CancellationToken cancellationToken = default) =>
        Ok(await jobService.GetRecommendationsForJobAsync(GetRequiredUserId(), jobId, limit, cancellationToken));

    [Authorize(Roles = "Recruiter,Admin")]
    [HttpGet("my-jobs")]
    public async Task<IActionResult> GetMyJobs(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var recruiter = await dbContext.RecruiterProfiles.FirstOrDefaultAsync(x => x.UserId == GetRequiredUserId(), cancellationToken);
        if (recruiter is null && !User.IsInRole("Admin"))
        {
            return NotFound(new ApiResponse<PaginatedResponseDto<JobListItemDto>>(false, null, "Recruiter profile not found.", ["not_found"]));
        }

        var query = dbContext.Jobs.Include(x => x.Company).AsQueryable();
        if (recruiter is not null)
        {
            query = query.Where(x => x.CompanyId == recruiter.CompanyId);
        }

        var total = await query.CountAsync(cancellationToken);
        var safePage = Math.Max(1, pageNumber);
        var safeSize = Math.Clamp(pageSize, 1, 200);

        var jobs = await query
            .OrderByDescending(x => x.PostedAtUtc ?? x.CreatedAtUtc)
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .Select(x => new JobListItemDto(
                x.Id,
                x.Title,
                string.IsNullOrWhiteSpace(x.Location) ? null : x.Location,
                string.IsNullOrWhiteSpace(x.EmploymentType) ? "FullTime" : x.EmploymentType,
                string.IsNullOrWhiteSpace(x.SalaryRange) ? null : x.SalaryRange,
                x.PostedAtUtc ?? x.CreatedAtUtc,
                x.Company != null ? x.Company.Name : null,
                x.Company != null && !string.IsNullOrWhiteSpace(x.Company.LogoUrl) ? x.Company.LogoUrl : null,
                FrontendStatusMapper.ToFrontend(x.SourceType),
                ServiceJson.DeserializeStringList(x.SkillsJson).Take(6).ToArray(),
                null))
            .ToListAsync(cancellationToken);

        var page = FrontendStatusMapper.ToPage(jobs, safePage, safeSize, total);
        return Ok(new ApiResponse<PaginatedResponseDto<JobListItemDto>>(true, page));
    }

    [Authorize]
    [HttpGet("scraping/jobs")]
    public async Task<IActionResult> GetScrapedJobs([FromQuery] string? keyword, [FromQuery] string? location, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Jobs.Where(x => x.SourceType == Domain.Enums.JobSourceType.External);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var term = keyword.Trim().ToLowerInvariant();
            query = query.Where(x => x.Title.ToLower().Contains(term) || x.Description.ToLower().Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(location))
        {
            var term = location.Trim().ToLowerInvariant();
            query = query.Where(x => x.Location.ToLower().Contains(term));
        }

        var items = await query
            .OrderByDescending(x => x.PostedAtUtc ?? x.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(x => new ScrapedJobDto(
                string.IsNullOrWhiteSpace(x.ExternalJobId) ? $"job_{x.Id}" : x.ExternalJobId,
                x.Title,
                x.Description,
                string.IsNullOrWhiteSpace(x.Requirements) ? null : x.Requirements,
                string.IsNullOrWhiteSpace(x.Location) ? null : x.Location,
                string.IsNullOrWhiteSpace(x.SalaryRange) ? null : x.SalaryRange,
                string.IsNullOrWhiteSpace(x.EmploymentType) ? null : x.EmploymentType,
                string.IsNullOrWhiteSpace(x.RedirectUrl) ? x.SourceUrl : x.RedirectUrl,
                "internal-cache",
                null,
                x.PostedAtUtc ?? x.CreatedAtUtc,
                ServiceJson.DeserializeStringList(x.SkillsJson)))
            .ToListAsync(cancellationToken);

        return Ok(new ApiResponse<IReadOnlyList<ScrapedJobDto>>(true, items));
    }

    [Authorize]
    [HttpGet("scraping/status")]
    public async Task<IActionResult> GetScrapingStatus(CancellationToken cancellationToken)
    {
        var queued = await dbContext.BackgroundJobStates
            .Where(x => x.JobType.Contains("scrap", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return Ok(new ApiResponse<object>(true, new
        {
            running = queued is not null && string.Equals(queued.Status, "Running", StringComparison.OrdinalIgnoreCase),
            lastStatus = queued?.Status ?? "Idle",
            updatedAt = queued?.UpdatedAtUtc,
        }));
    }

    [Authorize(Roles = "Recruiter,Admin")]
    [HttpPost("scraping/trigger")]
    public async Task<IActionResult> TriggerScraping([FromQuery] int? maxCategories, CancellationToken cancellationToken)
    {
        var result = await adminService.TriggerScrapeAsync(new JobLens.Application.DTOs.Admin.TriggerScrapeRequest(maxCategories), cancellationToken);
        return Ok(new ApiResponse<bool>(result.Success, result.Data, result.Message, result.Errors));
    }

    [Authorize]
    [HttpGet("recruitment/status")]
    public IActionResult RecruitmentStatus()
    {
        return Ok(new ApiResponse<object>(true, new
        {
            vectorsEnabled = true,
            recommendationsEnabled = true,
            scrapingEnabled = true,
            timestampUtc = DateTime.UtcNow,
        }));
    }

    private static JobViewDto ToJobView(Domain.Entities.JobPosting job)
    {
        return new JobViewDto(
            job.Id,
            job.Title,
            job.Description,
            string.IsNullOrWhiteSpace(job.Requirements) ? null : job.Requirements,
            string.IsNullOrWhiteSpace(job.Responsibilities) ? null : job.Responsibilities,
            string.IsNullOrWhiteSpace(job.Location) ? null : job.Location,
            string.IsNullOrWhiteSpace(job.EmploymentType) ? "FullTime" : job.EmploymentType,
            string.IsNullOrWhiteSpace(job.SalaryRange) ? null : job.SalaryRange,
            job.SalaryMin,
            job.SalaryMax,
            string.IsNullOrWhiteSpace(job.Currency) ? null : job.Currency,
            string.IsNullOrWhiteSpace(job.ExperienceLevel) ? null : job.ExperienceLevel,
            job.PostedAtUtc ?? job.CreatedAtUtc,
            job.ExpiresAtUtc,
            job.IsActive,
            FrontendStatusMapper.ToFrontend(job.SourceType),
            string.IsNullOrWhiteSpace(job.RedirectUrl) ? null : job.RedirectUrl,
            job.Company?.Name,
            job.Company is not null && !string.IsNullOrWhiteSpace(job.Company.LogoUrl) ? job.Company.LogoUrl : null,
            ServiceJson.DeserializeStringList(job.SkillsJson),
            job.Applications.Count);
    }
}

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
using System.Text.Json;

namespace JobLens.Api.Controllers;

[Route("api/jobs")]
public sealed class JobsController(IJobService jobService, IAdminService adminService, IAiBackendClient aiBackendClient, JobLensDbContext dbContext) : AppControllerBase
{
    private static readonly string[] EgyptLocationTokens =
    [
        "egypt", "cairo", "giza", "alexandria", "aswan", "luxor", "mansoura", "tanta",
        "suez", "ismailia", "zagazig", "port said", "new cairo", "maadi", "nasr city",
        "october", "sheikh zayed", "smart village"
    ];

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

        var rows = await query
            .OrderByDescending(x => x.PostedAtUtc ?? x.CreatedAtUtc)
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .Select(x => new
            {
                x.Id,
                x.Title,
                x.Location,
                x.EmploymentType,
                x.SalaryRange,
                PostedAt = x.PostedAtUtc ?? x.CreatedAtUtc,
                CompanyName = x.Company != null ? x.Company.Name : null,
                CompanyLogo = x.Company != null && !string.IsNullOrWhiteSpace(x.Company.LogoUrl) ? x.Company.LogoUrl : null,
                x.SourceType,
                x.SkillsJson,
                x.MetadataJson,
                x.RedirectUrl,
                x.SourceUrl,
            })
            .ToListAsync(cancellationToken);

        var jobs = rows
            .Select(x => new JobListItemDto(
                x.Id,
                x.Title,
                string.IsNullOrWhiteSpace(x.Location) ? null : x.Location,
                string.IsNullOrWhiteSpace(x.EmploymentType) ? "FullTime" : x.EmploymentType,
                string.IsNullOrWhiteSpace(x.SalaryRange) ? null : x.SalaryRange,
                x.PostedAt,
                CoalesceText(x.CompanyName, ReadMetadataString(x.MetadataJson, "company")),
                x.CompanyLogo,
                FrontendStatusMapper.ToFrontend(x.SourceType),
                ServiceJson.DeserializeStringList(x.SkillsJson).Take(6).ToArray(),
                null,
                ResolveExternalUrl(x.SourceType, x.RedirectUrl, x.SourceUrl)))
            .ToList();

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
        if (request.InterviewDefaults is null)
        {
            return BadRequest(new ApiResponse<JobViewDto>(false, null, "Interview defaults are required.", ["validation_error"]));
        }

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
        job.InterviewDefaultsJson = SerializeInterviewDefaults(request.InterviewDefaults);
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

        if (request.InterviewDefaults is not null)
        {
            current.InterviewDefaultsJson = SerializeInterviewDefaults(request.InterviewDefaults);
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
    public async Task<IActionResult> GetCandidateRecommendationsForJob(long jobId, [FromQuery] int limit = 10, [FromQuery] bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var recs = await jobService.GetRecommendationsForJobAsync(GetRequiredUserId(), jobId, limit, cancellationToken, forceRefresh);
        if (!recs.Success || recs.Data is null)
        {
            return BadRequest(new ApiResponse<IReadOnlyList<CandidateRecommendationDto>>(false, null, recs.Message, recs.Errors));
        }

        var candidateIds = recs.Data.Select(x => x.TargetId).Distinct().Where(x => x > 0).ToList();
        var candidates = await dbContext.CandidateProfiles
            .Include(x => x.User)
            .Where(x => candidateIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        var mapped = recs.Data
            .Select(x =>
            {
                candidates.TryGetValue(x.TargetId, out var candidate);
                return new CandidateRecommendationDto(
                    x.TargetId,
                    candidate?.User?.DisplayName ?? "Candidate",
                    string.IsNullOrWhiteSpace(candidate?.Headline) ? null : candidate.Headline,
                    string.IsNullOrWhiteSpace(candidate?.Location) ? null : candidate.Location,
                    string.IsNullOrWhiteSpace(candidate?.ProfileImagePath) ? null : candidate.ProfileImagePath,
                    candidate?.YearsExperience,
                    ServiceJson.DeserializeStringList(candidate?.SkillsJson ?? "[]").Take(8).ToArray(),
                    NormalizeRecommendationScore(x.Score),
                    string.IsNullOrWhiteSpace(x.Reason) ? null : x.Reason);
            })
            .ToList();

        return Ok(new ApiResponse<IReadOnlyList<CandidateRecommendationDto>>(true, mapped, recs.Message, recs.Errors));
    }

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

        var rows = await query
            .OrderByDescending(x => x.PostedAtUtc ?? x.CreatedAtUtc)
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .Select(x => new
            {
                x.Id,
                x.Title,
                x.Location,
                x.EmploymentType,
                x.SalaryRange,
                PostedAt = x.PostedAtUtc ?? x.CreatedAtUtc,
                CompanyName = x.Company != null ? x.Company.Name : null,
                CompanyLogo = x.Company != null && !string.IsNullOrWhiteSpace(x.Company.LogoUrl) ? x.Company.LogoUrl : null,
                x.SourceType,
                x.SkillsJson,
                x.MetadataJson,
                x.RedirectUrl,
                x.SourceUrl,
            })
            .ToListAsync(cancellationToken);

        var jobs = rows
            .Select(x => new JobListItemDto(
                x.Id,
                x.Title,
                string.IsNullOrWhiteSpace(x.Location) ? null : x.Location,
                string.IsNullOrWhiteSpace(x.EmploymentType) ? "FullTime" : x.EmploymentType,
                string.IsNullOrWhiteSpace(x.SalaryRange) ? null : x.SalaryRange,
                x.PostedAt,
                CoalesceText(x.CompanyName, ReadMetadataString(x.MetadataJson, "company")),
                x.CompanyLogo,
                FrontendStatusMapper.ToFrontend(x.SourceType),
                ServiceJson.DeserializeStringList(x.SkillsJson).Take(6).ToArray(),
                null,
                ResolveExternalUrl(x.SourceType, x.RedirectUrl, x.SourceUrl)))
            .ToList();

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

        var rows = await query
            .OrderByDescending(x => x.PostedAtUtc ?? x.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(x => new
            {
                x.Id,
                x.ExternalJobId,
                x.Title,
                x.Description,
                x.Requirements,
                x.Responsibilities,
                x.Location,
                x.SalaryRange,
                x.EmploymentType,
                x.ExperienceLevel,
                x.RedirectUrl,
                x.SourceUrl,
                x.MetadataJson,
                PostedAt = x.PostedAtUtc ?? x.CreatedAtUtc,
                x.SkillsJson,
            })
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(x => new ScrapedJobDto(
                string.IsNullOrWhiteSpace(x.ExternalJobId) ? $"job_{x.Id}" : x.ExternalJobId,
                x.Title,
                x.Description,
                string.IsNullOrWhiteSpace(x.Requirements) ? null : x.Requirements,
                string.IsNullOrWhiteSpace(x.Responsibilities) ? ReadMetadataString(x.MetadataJson, "responsibilities") : x.Responsibilities,
                string.IsNullOrWhiteSpace(x.Location) ? null : x.Location,
                ReadMetadataString(x.MetadataJson, "city"),
                ReadMetadataString(x.MetadataJson, "country"),
                string.IsNullOrWhiteSpace(x.SalaryRange) ? null : x.SalaryRange,
                string.IsNullOrWhiteSpace(x.EmploymentType) ? null : x.EmploymentType,
                string.IsNullOrWhiteSpace(x.ExperienceLevel) ? ReadMetadataString(x.MetadataJson, "experience_level") : x.ExperienceLevel,
                ResolveExternalUrl(Domain.Enums.JobSourceType.External, x.RedirectUrl, x.SourceUrl) ?? string.Empty,
                CoalesceText(ReadMetadataString(x.MetadataJson, "source"), "internal-cache") ?? "internal-cache",
                ReadMetadataString(x.MetadataJson, "company"),
                x.PostedAt,
                ServiceJson.DeserializeStringList(x.SkillsJson)))
            .ToList();

        return Ok(new ApiResponse<IReadOnlyList<ScrapedJobDto>>(true, items));
    }

    [Authorize]
    [HttpGet("scraping/status")]
    public async Task<IActionResult> GetScrapingStatus(CancellationToken cancellationToken)
    {
        var queued = await dbContext.BackgroundJobStates
            .Where(x => x.JobType != null && EF.Functions.Like(x.JobType.ToLower(), "%scrap%"))
            .OrderByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var running = queued is not null && string.Equals(queued.Status, "Running", StringComparison.OrdinalIgnoreCase);
        var payload = ParseScrapePayload(queued?.PayloadJson);
        var status = queued?.Status ?? "Idle";

        return Ok(new ApiResponse<object>(true, new
        {
            running,
            lastStatus = status,
            updatedAt = queued?.UpdatedAtUtc,
            phase = payload.Phase ?? InferScrapePhase(status, running),
            message = payload.Message,
            progressPercent = payload.ProgressPercent ?? InferScrapeProgress(status, running),
            processedJobs = payload.ProcessedJobs,
            totalJobs = payload.TotalJobs,
            insertedJobs = payload.InsertedJobs,
            updatedJobs = payload.UpdatedJobs,
            processedCategories = payload.ProcessedCategories,
            upsertedJobs = payload.UpsertedJobs,
            requestedMaxCategories = payload.RequestedMaxCategories,
        }));
    }

    [Authorize(Roles = "Recruiter,Admin")]
    [HttpGet("scraping/diagnostics")]
    public async Task<IActionResult> GetScrapingDiagnostics(CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.Jobs
            .Where(x => x.SourceType == Domain.Enums.JobSourceType.External)
            .Select(x => new
            {
                x.Id,
                x.Title,
                x.Description,
                x.Requirements,
                x.Responsibilities,
                x.Location,
                x.EmploymentType,
                x.ExperienceLevel,
                x.RedirectUrl,
                x.SourceUrl,
                x.MetadataJson,
                x.SkillsJson,
            })
            .ToListAsync(cancellationToken);

        var total = rows.Count;
        var lastScrape = await dbContext.BackgroundJobStates
            .Where(x => x.JobType != null && EF.Functions.Like(x.JobType.ToLower(), "%scrap%"))
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Select(x => new
            {
                x.Status,
                x.UpdatedAtUtc,
                x.Error,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (total == 0)
        {
            return Ok(new ApiResponse<object>(true, new
            {
                generatedAtUtc = DateTime.UtcNow,
                totalExternalJobs = 0,
                coverage = new
                {
                    applyLinkCoveragePct = 0d,
                    externalApplyLinkCoveragePct = 0d,
                    egyptOnlyRatioPct = 0d,
                },
                fillRates = new
                {
                    descriptionPct = 0d,
                    requirementsPct = 0d,
                    responsibilitiesPct = 0d,
                    skillsPct = 0d,
                    employmentTypePct = 0d,
                    experienceLevelPct = 0d,
                    cityPct = 0d,
                    countryPct = 0d,
                },
                enrichment = new
                {
                    withTagPct = 0d,
                    llmPct = 0d,
                },
                scrapeStats = new
                {
                    running = lastScrape is not null && string.Equals(lastScrape.Status, "Running", StringComparison.OrdinalIgnoreCase),
                    status = lastScrape?.Status ?? "Idle",
                    updatedAtUtc = lastScrape?.UpdatedAtUtc,
                    error = lastScrape?.Error,
                },
                topNonEgyptLocations = Array.Empty<object>(),
            }));
        }

        var applyLinkCount = 0;
        var externalApplyLinkCount = 0;
        var egyptCount = 0;
        var descriptionCount = 0;
        var requirementsCount = 0;
        var responsibilitiesCount = 0;
        var skillsCount = 0;
        var employmentTypeCount = 0;
        var experienceLevelCount = 0;
        var cityCount = 0;
        var countryCount = 0;
        var enrichmentTaggedCount = 0;
        var llmEnrichedCount = 0;
        var nonEgyptLocations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var externalUrl = ResolveExternalUrl(Domain.Enums.JobSourceType.External, row.RedirectUrl, row.SourceUrl);
            if (!string.IsNullOrWhiteSpace(externalUrl))
            {
                applyLinkCount += 1;

                if (!string.IsNullOrWhiteSpace(row.SourceUrl)
                    && !string.Equals(externalUrl.Trim(), row.SourceUrl.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    externalApplyLinkCount += 1;
                }
            }

            var location = CoalesceText(row.Location, ReadMetadataString(row.MetadataJson, "location"));
            var city = ReadMetadataString(row.MetadataJson, "city");
            var country = ReadMetadataString(row.MetadataJson, "country");
            var isEgypt = IsEgyptLocation(location, city, country);
            if (isEgypt)
            {
                egyptCount += 1;
            }
            else
            {
                var key = CoalesceText(location, city, country, "Unknown") ?? "Unknown";
                nonEgyptLocations[key] = nonEgyptLocations.TryGetValue(key, out var count) ? count + 1 : 1;
            }

            var description = CoalesceText(row.Description, ReadMetadataString(row.MetadataJson, "description"));
            var requirements = CoalesceText(row.Requirements, ReadMetadataString(row.MetadataJson, "requirements"));
            var responsibilities = CoalesceText(row.Responsibilities, ReadMetadataString(row.MetadataJson, "responsibilities"));
            var employmentType = CoalesceText(row.EmploymentType, ReadMetadataString(row.MetadataJson, "employment_type"));
            var experienceLevel = CoalesceText(row.ExperienceLevel, ReadMetadataString(row.MetadataJson, "experience_level"));
            var enrichmentSource = ReadMetadataString(row.MetadataJson, "enrichment_source");
            var skills = ServiceJson.DeserializeStringList(row.SkillsJson);

            if (!string.IsNullOrWhiteSpace(description))
            {
                descriptionCount += 1;
            }

            if (!string.IsNullOrWhiteSpace(requirements))
            {
                requirementsCount += 1;
            }

            if (!string.IsNullOrWhiteSpace(responsibilities))
            {
                responsibilitiesCount += 1;
            }

            if (skills.Count > 0)
            {
                skillsCount += 1;
            }

            if (!string.IsNullOrWhiteSpace(employmentType)
                && !string.Equals(employmentType, "Not Specified", StringComparison.OrdinalIgnoreCase))
            {
                employmentTypeCount += 1;
            }

            if (!string.IsNullOrWhiteSpace(experienceLevel)
                && !string.Equals(experienceLevel, "Not Specified", StringComparison.OrdinalIgnoreCase))
            {
                experienceLevelCount += 1;
            }

            if (!string.IsNullOrWhiteSpace(city))
            {
                cityCount += 1;
            }

            if (!string.IsNullOrWhiteSpace(country))
            {
                countryCount += 1;
            }

            if (!string.IsNullOrWhiteSpace(enrichmentSource))
            {
                enrichmentTaggedCount += 1;
                if (string.Equals(enrichmentSource, "llm", StringComparison.OrdinalIgnoreCase))
                {
                    llmEnrichedCount += 1;
                }
            }
        }

        var topNonEgyptLocations = nonEgyptLocations
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key)
            .Take(10)
            .Select(x => new { location = x.Key, count = x.Value })
            .ToArray();

        return Ok(new ApiResponse<object>(true, new
        {
            generatedAtUtc = DateTime.UtcNow,
            totalExternalJobs = total,
            coverage = new
            {
                applyLinkCoveragePct = Percentage(applyLinkCount, total),
                externalApplyLinkCoveragePct = Percentage(externalApplyLinkCount, total),
                egyptOnlyRatioPct = Percentage(egyptCount, total),
            },
            fillRates = new
            {
                descriptionPct = Percentage(descriptionCount, total),
                requirementsPct = Percentage(requirementsCount, total),
                responsibilitiesPct = Percentage(responsibilitiesCount, total),
                skillsPct = Percentage(skillsCount, total),
                employmentTypePct = Percentage(employmentTypeCount, total),
                experienceLevelPct = Percentage(experienceLevelCount, total),
                cityPct = Percentage(cityCount, total),
                countryPct = Percentage(countryCount, total),
            },
            enrichment = new
            {
                withTagPct = Percentage(enrichmentTaggedCount, total),
                llmPct = Percentage(llmEnrichedCount, total),
            },
            scrapeStats = new
            {
                running = lastScrape is not null && string.Equals(lastScrape.Status, "Running", StringComparison.OrdinalIgnoreCase),
                status = lastScrape?.Status ?? "Idle",
                updatedAtUtc = lastScrape?.UpdatedAtUtc,
                error = lastScrape?.Error,
            },
            topNonEgyptLocations,
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
        var companyName = CoalesceText(job.Company?.Name, ReadMetadataString(job.MetadataJson, "company"));
        var description = CoalesceText(job.Description, ReadMetadataString(job.MetadataJson, "description")) ?? string.Empty;
        var requirements = CoalesceText(job.Requirements, ReadMetadataString(job.MetadataJson, "requirements"));
        var responsibilities = CoalesceText(job.Responsibilities, ReadMetadataString(job.MetadataJson, "responsibilities"));
        var location = CoalesceText(job.Location, ReadMetadataString(job.MetadataJson, "location"));
        var employmentType = CoalesceText(job.EmploymentType, ReadMetadataString(job.MetadataJson, "employment_type")) ?? "FullTime";
        var experienceLevel = CoalesceText(job.ExperienceLevel, ReadMetadataString(job.MetadataJson, "experience_level"));

        return new JobViewDto(
            job.Id,
            job.Title,
            description,
            requirements,
            responsibilities,
            location,
            employmentType,
            string.IsNullOrWhiteSpace(job.SalaryRange) ? null : job.SalaryRange,
            job.SalaryMin,
            job.SalaryMax,
            string.IsNullOrWhiteSpace(job.Currency) ? null : job.Currency,
            experienceLevel,
            job.PostedAtUtc ?? job.CreatedAtUtc,
            job.ExpiresAtUtc,
            job.IsActive,
            FrontendStatusMapper.ToFrontend(job.SourceType),
            ResolveExternalUrl(job.SourceType, job.RedirectUrl, job.SourceUrl),
            companyName,
            job.Company is not null && !string.IsNullOrWhiteSpace(job.Company.LogoUrl) ? job.Company.LogoUrl : null,
            ServiceJson.DeserializeStringList(job.SkillsJson),
            job.Applications.Count,
            DeserializeInterviewDefaults(job.InterviewDefaultsJson));
    }

    private static string SerializeInterviewDefaults(InterviewDefaultsRequest? input)
    {
        if (input is null)
        {
            return "{}";
        }

        return ServiceJson.Serialize(new
        {
            agentType = string.IsNullOrWhiteSpace(input.AgentType) ? "Mixed" : input.AgentType.Trim(),
            maxQuestions = Math.Clamp(input.MaxQuestions, 1, 20),
            evaluationCriteria = input.EvaluationCriteria?.Trim() ?? string.Empty,
            focusSkills = input.FocusSkills?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToArray() ?? Array.Empty<string>(),
            questionTimeLimitSeconds = input.QuestionTimeLimitSeconds,
            totalInterviewDurationMinutes = input.TotalInterviewDurationMinutes,
            proctoringStrictness = string.IsNullOrWhiteSpace(input.ProctoringStrictness) ? "Medium" : input.ProctoringStrictness.Trim()
        });
    }

    private static InterviewDefaultsRequest? DeserializeInterviewDefaults(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var focusSkills = root.TryGetProperty("focusSkills", out var focusEl) && focusEl.ValueKind == JsonValueKind.Array
                ? focusEl.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
                : Array.Empty<string>();

            return new InterviewDefaultsRequest(
                root.TryGetProperty("agentType", out var agentEl) && agentEl.ValueKind == JsonValueKind.String ? agentEl.GetString() ?? "Mixed" : "Mixed",
                root.TryGetProperty("maxQuestions", out var maxEl) && maxEl.ValueKind == JsonValueKind.Number ? maxEl.GetInt32() : 5,
                root.TryGetProperty("evaluationCriteria", out var evalEl) && evalEl.ValueKind == JsonValueKind.String ? evalEl.GetString() ?? string.Empty : string.Empty,
                focusSkills,
                root.TryGetProperty("questionTimeLimitSeconds", out var qlEl) && qlEl.ValueKind == JsonValueKind.Number ? qlEl.GetInt32() : null,
                root.TryGetProperty("totalInterviewDurationMinutes", out var tdEl) && tdEl.ValueKind == JsonValueKind.Number ? tdEl.GetInt32() : null,
                root.TryGetProperty("proctoringStrictness", out var psEl) && psEl.ValueKind == JsonValueKind.String ? psEl.GetString() : null);
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveExternalUrl(Domain.Enums.JobSourceType sourceType, string? redirectUrl, string? sourceUrl)
    {
        if (sourceType != Domain.Enums.JobSourceType.External)
        {
            return null;
        }

        return CoalesceText(redirectUrl, sourceUrl);
    }

    private static string? CoalesceText(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? ReadMetadataString(string? metadataJson, string key)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            if (!document.RootElement.TryGetProperty(key, out var element))
            {
                return null;
            }

            var value = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };

            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsEgyptLocation(string? location, string? city, string? country)
    {
        var combined = string.Join(" ",
        [
            location?.Trim().ToLowerInvariant() ?? string.Empty,
            city?.Trim().ToLowerInvariant() ?? string.Empty,
            country?.Trim().ToLowerInvariant() ?? string.Empty,
        ]);

        return EgyptLocationTokens.Any(token => combined.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static double NormalizeRecommendationScore(double score)
    {
        if (!double.IsFinite(score) || score <= 0)
        {
            return 0;
        }

        var normalized = score <= 1 ? score * 100 : score;
        return Math.Round(Math.Min(normalized, 100), 2);
    }

    private static double Percentage(int part, int total)
    {
        if (total <= 0)
        {
            return 0;
        }

        return Math.Round((double)part / total * 100, 2);
    }

    private static ScrapePayload ParseScrapePayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new ScrapePayload();
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;

            return new ScrapePayload
            {
                Phase = ReadPayloadString(root, "phase"),
                Message = ReadPayloadString(root, "message"),
                ProgressPercent = ReadPayloadInt(root, "progressPercent"),
                ProcessedJobs = ReadPayloadInt(root, "processedJobs"),
                TotalJobs = ReadPayloadInt(root, "totalJobs"),
                InsertedJobs = ReadPayloadInt(root, "insertedJobs"),
                UpdatedJobs = ReadPayloadInt(root, "updatedJobs"),
                ProcessedCategories = ReadPayloadInt(root, "processedCategories"),
                UpsertedJobs = ReadPayloadInt(root, "upsertedJobs"),
                RequestedMaxCategories = ReadPayloadInt(root, "requestedMaxCategories"),
            };
        }
        catch
        {
            return new ScrapePayload();
        }
    }

    private static string InferScrapePhase(string? status, bool running)
    {
        if (running)
        {
            return "running";
        }

        if (string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase))
        {
            return "completed";
        }

        if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            return "failed";
        }

        if (string.Equals(status, "Queued", StringComparison.OrdinalIgnoreCase))
        {
            return "queued";
        }

        return "idle";
    }

    private static int InferScrapeProgress(string? status, bool running)
    {
        if (running)
        {
            return 35;
        }

        if (string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        return 0;
    }

    private static int? ReadPayloadInt(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
        {
            return number;
        }

        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out number))
        {
            return number;
        }

        return null;
    }

    private static string? ReadPayloadString(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed class ScrapePayload
    {
        public string? Phase { get; init; }

        public string? Message { get; init; }

        public int? ProgressPercent { get; init; }

        public int? ProcessedJobs { get; init; }

        public int? TotalJobs { get; init; }

        public int? InsertedJobs { get; init; }

        public int? UpdatedJobs { get; init; }

        public int? ProcessedCategories { get; init; }

        public int? UpsertedJobs { get; init; }

        public int? RequestedMaxCategories { get; init; }
    }
}

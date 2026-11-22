using System.Text.Json;
using Hangfire;
using JobLens.Api.Compatibility;
using JobLens.Api.Contracts;
using JobLens.Application.Common;
using JobLens.Application.Interfaces;
using JobLens.Infrastructure.BackgroundJobs;
using JobLens.Infrastructure.Persistence;
using JobLens.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JobLens.Api.Controllers;

[Route("api/candidates")]
public sealed class CandidatesController(
    ICandidateService candidateService,
    JobLensDbContext dbContext,
    IAiBackendClient aiBackendClient,
    IFileStorageService fileStorageService,
    IBackgroundJobClient backgroundJobs) : AppControllerBase
{
    [Authorize(Roles = "Candidate")]
    [HttpGet("profile")]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken)
    {
        var profile = await dbContext.CandidateProfiles
            .Include(x => x.User)
            .Include(x => x.Resumes)
            .ThenInclude(x => x.ParsedResumeResult)
            .FirstOrDefaultAsync(x => x.UserId == GetRequiredUserId(), cancellationToken);

        if (profile is null)
        {
            return NotFound(new ApiResponse<CandidateProfileViewDto>(false, null, "Candidate profile not found.", ["not_found"]));
        }

        var dto = await MapCandidateProfileAsync(profile, cancellationToken);
        return Ok(new ApiResponse<CandidateProfileViewDto>(true, dto));
    }

    [Authorize(Roles = "Candidate")]
    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateCandidateProfileCompatRequest request, CancellationToken cancellationToken)
    {
        var profile = await dbContext.CandidateProfiles
            .Include(x => x.User)
            .Include(x => x.Resumes)
            .FirstOrDefaultAsync(x => x.UserId == GetRequiredUserId(), cancellationToken);

        if (profile is null)
        {
            return NotFound(new ApiResponse<CandidateProfileViewDto>(false, null, "Candidate profile not found.", ["not_found"]));
        }

        if (!string.IsNullOrWhiteSpace(request.FullName))
        {
            profile.User.DisplayName = request.FullName.Trim();
        }

        if (request.Phone is not null)
        {
            profile.Phone = request.Phone.Trim();
        }

        if (request.Location is not null)
        {
            profile.Location = request.Location.Trim();
        }

        if (request.CurrentTitle is not null)
        {
            profile.Headline = request.CurrentTitle.Trim();
        }

        if (request.Summary is not null)
        {
            profile.Summary = request.Summary.Trim();
        }

        if (request.LinkedInUrl is not null)
        {
            profile.LinkedInUrl = request.LinkedInUrl.Trim();
        }

        if (request.PortfolioUrl is not null)
        {
            profile.PortfolioUrl = request.PortfolioUrl.Trim();
        }

        if (request.YearsOfExperience.HasValue)
        {
            profile.YearsExperience = Math.Max(0, request.YearsOfExperience.Value);
        }

        profile.UpdatedAtUtc = DateTime.UtcNow;
        profile.User.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        backgroundJobs.Enqueue<RecommendationRefreshJob>(j => j.RefreshCandidateAsync(profile.Id));

        var dto = await MapCandidateProfileAsync(profile, cancellationToken);
        return Ok(new ApiResponse<CandidateProfileViewDto>(true, dto, "Candidate profile updated."));
    }

    [Authorize(Roles = "Candidate")]
    [HttpPost("profile/image")]
    [RequestSizeLimit(8_000_000)]
    public async Task<IActionResult> UpdateProfileImage([FromForm] IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new ApiResponse<bool>(false, false, "Invalid image file."));
        }

        if (!UploadRules.HasAllowedExtension(file.FileName, UploadRules.ImageExtensions))
        {
            return BadRequest(new ApiResponse<bool>(false, false, "Profile image must be a PNG, JPG, WEBP or GIF file.", ["invalid_file_type"]));
        }

        var profile = await dbContext.CandidateProfiles.FirstOrDefaultAsync(x => x.UserId == GetRequiredUserId(), cancellationToken);
        if (profile is null)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Candidate profile not found.", ["not_found"]));
        }

        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);

        var oldImagePath = profile.ProfileImagePath;
        var newStoragePath = await fileStorageService.SaveAsync(file.FileName, memory.ToArray(), cancellationToken);

        try
        {
            profile.ProfileImagePath = newStoragePath;
            profile.UpdatedAtUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(oldImagePath))
            {
                await fileStorageService.DeleteAsync(oldImagePath, cancellationToken);
            }

            return Ok(new ApiResponse<bool>(true, true, "Profile image updated."));
        }
        catch
        {
            await fileStorageService.DeleteAsync(newStoragePath, CancellationToken.None);
            throw;
        }
    }

    [AllowAnonymous]
    [HttpGet("{candidateId:long}/image")]
    public async Task<IActionResult> GetProfileImage(long candidateId, CancellationToken cancellationToken)
    {
        var path = await dbContext.CandidateProfiles
            .Where(x => x.Id == candidateId)
            .Select(x => x.ProfileImagePath)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(path))
        {
            return NotFound();
        }

        var bytes = await fileStorageService.ReadAsync(path, cancellationToken);
        if (bytes is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=86400";
        return File(bytes, UploadRules.ContentTypeFor(path));
    }

    [Authorize(Roles = "Candidate")]
    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard(CancellationToken cancellationToken)
    {
        var userId = GetRequiredUserId();
        var candidate = await dbContext.CandidateProfiles.FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (candidate is null)
        {
            return NotFound(new ApiResponse<CandidateDashboardDto>(false, null, "Candidate profile not found.", ["not_found"]));
        }

        var applications = await dbContext.Applications
            .Include(x => x.JobPosting)
            .ThenInclude(x => x.Company)
            .Include(x => x.AtsAssessments)
            .Where(x => x.CandidateProfileId == candidate.Id)
            .OrderByDescending(x => x.SubmittedAtUtc)
            .ToListAsync(cancellationToken);

        var interviews = await dbContext.InterviewSessions
            .Include(x => x.Application)
            .ThenInclude(x => x.JobPosting)
            .Include(x => x.ProctoringEvents)
            .Where(x => x.Application.CandidateProfileId == candidate.Id)
            .ToListAsync(cancellationToken);

        var recentApplications = applications
            .Take(5)
            .Select(x => new CandidateRecentApplicationDto(
                x.Id,
                x.JobPostingId,
                x.JobPosting.Title,
                x.JobPosting.Company != null ? x.JobPosting.Company.Name : null,
                x.SubmittedAtUtc,
                FrontendStatusMapper.ToFrontend(x.Status),
                x.AtsAssessments.OrderByDescending(a => a.EvaluatedAtUtc ?? a.CreatedAtUtc).FirstOrDefault()?.Score))
            .ToList();

        var upcomingInterviews = interviews
            .Where(x => x.ScheduledAtUtc.HasValue && x.ScheduledAtUtc.Value >= DateTime.UtcNow)
            .OrderBy(x => x.ScheduledAtUtc)
            .Take(5)
            .Select(x => new CandidateUpcomingInterviewDto(
                x.Id,
                x.ApplicationId,
                x.Application.JobPosting.Title,
                string.IsNullOrWhiteSpace(x.InterviewTitle) ? null : x.InterviewTitle,
                x.ScheduledAtUtc,
                FrontendStatusMapper.ToFrontend(x.Status),
                x.ProctoringEvents.Any(),
                x.FinalScore))
            .ToList();

        var highestAts = applications
            .SelectMany(x => x.AtsAssessments)
            .Select(x => x.Score)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .DefaultIfEmpty(0)
            .Max();

        var activeCount = applications.Count(x => x.Status is not Domain.Enums.ApplicationStatus.Rejected and not Domain.Enums.ApplicationStatus.Withdrawn and not Domain.Enums.ApplicationStatus.Offered);
        var scheduledCount = interviews.Count(x => x.Status == Domain.Enums.InterviewSessionStatus.Scheduled);
        var completedCount = interviews.Count(x => x.Status == Domain.Enums.InterviewSessionStatus.Completed);

        var dto = new CandidateDashboardDto(
            applications.Count,
            activeCount,
            scheduledCount,
            completedCount,
            highestAts,
            recentApplications,
            upcomingInterviews);

        return Ok(new ApiResponse<CandidateDashboardDto>(true, dto));
    }

    [Authorize(Roles = "Recruiter,Admin")]
    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string? keyword,
        [FromQuery] string? location,
        [FromQuery] string? skills,
        [FromQuery] int? minExperience,
        [FromQuery] int? maxExperience,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.CandidateProfiles.Include(x => x.User).AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var term = keyword.Trim().ToLowerInvariant();
            query = query.Where(x =>
                x.User.DisplayName.ToLower().Contains(term) ||
                x.User.Email.ToLower().Contains(term) ||
                x.Headline.ToLower().Contains(term) ||
                x.Summary.ToLower().Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(location))
        {
            var term = location.Trim().ToLowerInvariant();
            query = query.Where(x => x.Location.ToLower().Contains(term));
        }

        if (minExperience.HasValue)
        {
            query = query.Where(x => x.YearsExperience >= minExperience.Value);
        }

        if (maxExperience.HasValue)
        {
            query = query.Where(x => x.YearsExperience <= maxExperience.Value);
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

        var total = await query.CountAsync(cancellationToken);
        var safePage = Math.Max(1, pageNumber);
        var safeSize = Math.Clamp(pageSize, 1, 200);

        var items = await query
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .Select(x => new CandidateListDto(
                x.Id,
                x.User.DisplayName,
                x.User.Email,
                string.IsNullOrWhiteSpace(x.Headline) ? null : x.Headline,
                string.IsNullOrWhiteSpace(x.Location) ? null : x.Location,
                x.YearsExperience,
                MediaUrls.CandidateImage(x.Id, x.ProfileImagePath),
                ServiceJson.DeserializeStringList(x.SkillsJson).Take(5).ToArray()))
            .ToListAsync(cancellationToken);

        var page = FrontendStatusMapper.ToPage(items, safePage, safeSize, total);
        return Ok(new ApiResponse<PaginatedResponseDto<CandidateListDto>>(true, page));
    }

    [Authorize(Roles = "Recruiter,Admin,Candidate")]
    [HttpGet("{candidateId:long}")]
    public async Task<IActionResult> GetById(long candidateId, CancellationToken cancellationToken)
    {
        var profile = await dbContext.CandidateProfiles
            .Include(x => x.User)
            .Include(x => x.Resumes)
            .ThenInclude(x => x.ParsedResumeResult)
            .FirstOrDefaultAsync(x => x.Id == candidateId, cancellationToken);

        if (profile is null)
        {
            return NotFound(new ApiResponse<CandidateProfileViewDto>(false, null, "Candidate profile not found.", ["not_found"]));
        }

        if (User.IsInRole("Candidate") && profile.UserId != GetRequiredUserId())
        {
            return Forbid();
        }

        var dto = await MapCandidateProfileAsync(profile, cancellationToken);
        return Ok(new ApiResponse<CandidateProfileViewDto>(true, dto));
    }

    [Authorize(Roles = "Recruiter,Admin,Candidate")]
    [HttpGet("{candidateId:long}/skills")]
    public async Task<IActionResult> GetSkills(long candidateId, CancellationToken cancellationToken)
    {
        var profile = await dbContext.CandidateProfiles.FirstOrDefaultAsync(x => x.Id == candidateId, cancellationToken);
        if (profile is null)
        {
            return NotFound(new ApiResponse<IReadOnlyList<CandidateSkillDto>>(false, null, "Candidate not found.", ["not_found"]));
        }

        if (User.IsInRole("Candidate") && profile.UserId != GetRequiredUserId())
        {
            return Forbid();
        }

        var skills = ServiceJson.DeserializeStringList(profile.SkillsJson)
            .Select((skill, idx) => new CandidateSkillDto(idx + 1, skill, null, null, null, false))
            .ToList();

        return Ok(new ApiResponse<IReadOnlyList<CandidateSkillDto>>(true, skills));
    }

    [Authorize(Roles = "Candidate")]
    [HttpPost("skills")]
    public async Task<IActionResult> AddSkill([FromBody] AddSkillRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SkillName))
        {
            return BadRequest(new ApiResponse<CandidateSkillDto>(false, null, "Skill name is required.", ["validation_error"]));
        }

        var profile = await dbContext.CandidateProfiles.FirstOrDefaultAsync(x => x.UserId == GetRequiredUserId(), cancellationToken);
        if (profile is null)
        {
            return NotFound(new ApiResponse<CandidateSkillDto>(false, null, "Candidate profile not found.", ["not_found"]));
        }

        var skills = ServiceJson.DeserializeStringList(profile.SkillsJson);
        var normalized = request.SkillName.Trim();
        if (!skills.Any(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            skills.Add(normalized);
        }

        profile.SkillsJson = ServiceJson.Serialize(skills);
        profile.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        backgroundJobs.Enqueue<RecommendationRefreshJob>(j => j.RefreshCandidateAsync(profile.Id));

        var dto = new CandidateSkillDto(skills.Count, normalized, request.ExperienceYears, null, request.ProficiencyLevel, false);
        return Ok(new ApiResponse<CandidateSkillDto>(true, dto, "Skill added."));
    }

    [Authorize(Roles = "Candidate")]
    [HttpDelete("skills/{skillId:int}")]
    public async Task<IActionResult> RemoveSkill(int skillId, CancellationToken cancellationToken)
    {
        var profile = await dbContext.CandidateProfiles.FirstOrDefaultAsync(x => x.UserId == GetRequiredUserId(), cancellationToken);
        if (profile is null)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Candidate profile not found.", ["not_found"]));
        }

        var skills = ServiceJson.DeserializeStringList(profile.SkillsJson);
        var index = skillId - 1;
        if (index < 0 || index >= skills.Count)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Skill not found.", ["not_found"]));
        }

        skills.RemoveAt(index);
        profile.SkillsJson = ServiceJson.Serialize(skills);
        profile.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        backgroundJobs.Enqueue<RecommendationRefreshJob>(j => j.RefreshCandidateAsync(profile.Id));

        return Ok(new ApiResponse<bool>(true, true, "Skill removed."));
    }

    [Authorize(Roles = "Candidate")]
    [HttpPost("fill-from-resume/{resumeId:long}")]
    public async Task<IActionResult> FillFromResume(long resumeId, CancellationToken cancellationToken)
    {
        var profile = await dbContext.CandidateProfiles
            .Include(x => x.User)
            .FirstOrDefaultAsync(x => x.UserId == GetRequiredUserId(), cancellationToken);

        if (profile is null)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Candidate profile not found.", ["not_found"]));
        }

        var resume = await dbContext.Resumes
            .Include(x => x.ParsedResumeResult)
            .FirstOrDefaultAsync(x => x.Id == resumeId && x.CandidateProfileId == profile.Id, cancellationToken);

        if (resume is null)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Resume not found.", ["not_found"]));
        }

        string? structuredJson = resume.ParsedResumeResult?.StructuredJson;
        List<string> extractedSkills = ServiceJson.DeserializeStringList(resume.ParsedResumeResult?.SkillsJson).ToList();

        if (string.IsNullOrWhiteSpace(structuredJson))
        {
            var parse = await aiBackendClient.ParseResumeTextAsync(resume.RawText, cancellationToken);
            if (!parse.Success || parse.Data is null)
            {
                return BadRequest(new ApiResponse<bool>(false, false, parse.Error?.Message ?? "Could not parse resume."));
            }

            structuredJson = parse.Data.StructuredJson;
            if (parse.Data.Skills is { Count: > 0 })
            {
                extractedSkills = parse.Data.Skills.ToList();
            }

            if (!string.IsNullOrWhiteSpace(parse.Data.FullName))
            {
                profile.User.DisplayName = parse.Data.FullName.Trim();
            }
            if (!string.IsNullOrWhiteSpace(parse.Data.Phone))
            {
                profile.Phone = parse.Data.Phone.Trim();
            }

            if (resume.ParsedResumeResult is null)
            {
                dbContext.ParsedResumeResults.Add(new Domain.Entities.ParsedResumeResult
                {
                    ResumeId = resume.Id,
                    FullName = parse.Data.FullName ?? string.Empty,
                    Email = parse.Data.Email ?? string.Empty,
                    Phone = parse.Data.Phone ?? string.Empty,
                    SkillsJson = ServiceJson.Serialize(extractedSkills),
                    StructuredJson = structuredJson ?? "{}",
                    ParsedAtUtc = DateTime.UtcNow,
                });
            }
            else
            {
                resume.ParsedResumeResult.FullName = parse.Data.FullName ?? string.Empty;
                resume.ParsedResumeResult.Email = parse.Data.Email ?? string.Empty;
                resume.ParsedResumeResult.Phone = parse.Data.Phone ?? string.Empty;
                resume.ParsedResumeResult.SkillsJson = ServiceJson.Serialize(extractedSkills);
                resume.ParsedResumeResult.StructuredJson = structuredJson ?? "{}";
                resume.ParsedResumeResult.ParsedAtUtc = DateTime.UtcNow;
            }

            resume.ParseStatus = "Completed";
            resume.UpdatedAtUtc = DateTime.UtcNow;
        }

        if (!string.IsNullOrWhiteSpace(structuredJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(structuredJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("full_name", out var nameEl) && !string.IsNullOrWhiteSpace(nameEl.GetString()))
                {
                    profile.User.DisplayName = nameEl.GetString()!.Trim();
                }

                if (root.TryGetProperty("phone", out var phoneEl) && !string.IsNullOrWhiteSpace(phoneEl.GetString()))
                {
                    profile.Phone = phoneEl.GetString()!.Trim();
                }

                if (root.TryGetProperty("job_title", out var titleEl) && !string.IsNullOrWhiteSpace(titleEl.GetString()))
                {
                    profile.Headline = titleEl.GetString()!.Trim();
                }

                if (root.TryGetProperty("summary", out var summaryEl) && !string.IsNullOrWhiteSpace(summaryEl.GetString()))
                {
                    profile.Summary = summaryEl.GetString()!.Trim();
                }

                if (root.TryGetProperty("location", out var locationEl) && !string.IsNullOrWhiteSpace(locationEl.GetString()))
                {
                    profile.Location = locationEl.GetString()!.Trim();
                }

                if (root.TryGetProperty("linkedin", out var linkedInEl) && !string.IsNullOrWhiteSpace(linkedInEl.GetString()))
                {
                    profile.LinkedInUrl = linkedInEl.GetString()!.Trim();
                }

                if (root.TryGetProperty("github", out var githubEl) && !string.IsNullOrWhiteSpace(githubEl.GetString()))
                {
                    profile.PortfolioUrl = githubEl.GetString()!.Trim();
                }

                if (root.TryGetProperty("skills", out var skillsEl))
                {
                    var skillsList = new List<string>();
                    if (skillsEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in skillsEl.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in prop.Value.EnumerateArray())
                                {
                                    var val = item.GetString()?.Trim();
                                    if (!string.IsNullOrWhiteSpace(val)) skillsList.Add(val);
                                }
                            }
                        }
                    }
                    else if (skillsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in skillsEl.EnumerateArray())
                        {
                            var val = item.GetString()?.Trim();
                            if (!string.IsNullOrWhiteSpace(val)) skillsList.Add(val);
                        }
                    }

                    if (skillsList.Count > 0)
                    {
                        extractedSkills = skillsList.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    }
                }

                if (root.TryGetProperty("experience", out var expEl) && expEl.ValueKind == JsonValueKind.Array)
                {
                    var expCount = expEl.GetArrayLength();
                    if (expCount > 0)
                    {
                        if (string.IsNullOrWhiteSpace(profile.Location))
                        {
                            var firstExp = expEl[0];
                            if (firstExp.TryGetProperty("location", out var expLoc) && !string.IsNullOrWhiteSpace(expLoc.GetString()))
                            {
                                profile.Location = expLoc.GetString()!.Trim();
                            }
                        }
                    }
                }
            }
            catch
            {
                // Gracefully preserve direct assignments if JSON structure varies
            }
        }

        if (extractedSkills.Count > 0)
        {
            profile.SkillsJson = ServiceJson.Serialize(extractedSkills);
        }

        profile.UpdatedAtUtc = DateTime.UtcNow;
        profile.User.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        backgroundJobs.Enqueue<RecommendationRefreshJob>(j => j.RefreshCandidateAsync(profile.Id));

        return Ok(new ApiResponse<bool>(true, true, "Profile fields were filled from resume."));
    }

    private async Task<CandidateProfileViewDto> MapCandidateProfileAsync(Domain.Entities.CandidateProfile profile, CancellationToken cancellationToken)
    {
        var atsByResume = await dbContext.AtsAssessments
            .Where(x => x.Application.CandidateProfileId == profile.Id)
            .GroupBy(x => x.Application.ResumeId)
            .Select(x => new 
            { 
                ResumeId = x.Key, 
                Score = x.OrderByDescending(y => y.EvaluatedAtUtc ?? y.CreatedAtUtc).Select(y => y.Score).FirstOrDefault() 
            })
            .ToListAsync(cancellationToken);

        var scoreLookup = atsByResume.ToDictionary(x => x.ResumeId, x => x.Score);
        var skills = ServiceJson.DeserializeStringList(profile.SkillsJson)
            .Select((skill, idx) => new CandidateSkillDto(idx + 1, skill, null, null, null, false))
            .ToList();

        var resumes = profile.Resumes
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new ResumeBasicDto(
                x.Id,
                x.FileName,
                string.IsNullOrWhiteSpace(x.ContentType) ? null : x.ContentType,
                x.ParsedResumeResult is not null,
                scoreLookup.GetValueOrDefault(x.Id),
                x.IsDefault,
                x.CreatedAtUtc))
            .ToList();

        return new CandidateProfileViewDto(
            profile.Id,
            profile.UserId,
            profile.User.DisplayName,
            profile.User.Email,
            string.IsNullOrWhiteSpace(profile.Phone) ? null : profile.Phone,
            string.IsNullOrWhiteSpace(profile.Location) ? null : profile.Location,
            string.IsNullOrWhiteSpace(profile.Headline) ? null : profile.Headline,
            string.IsNullOrWhiteSpace(profile.Summary) ? null : profile.Summary,
            string.IsNullOrWhiteSpace(profile.LinkedInUrl) ? null : profile.LinkedInUrl,
            string.IsNullOrWhiteSpace(profile.PortfolioUrl) ? null : profile.PortfolioUrl,
            MediaUrls.CandidateImage(profile.Id, profile.ProfileImagePath),
            profile.YearsExperience,
            profile.CreatedAtUtc,
            profile.UpdatedAtUtc,
            skills,
            resumes);
    }
}
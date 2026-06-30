using Hangfire;
using JobLens.Application.Common;
using JobLens.Application.Contracts;
using JobLens.Application.DTOs.Jobs;
using JobLens.Application.Interfaces;
using JobLens.Domain.Entities;
using JobLens.Domain.Enums;
using JobLens.Infrastructure.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace JobLens.Infrastructure.Services;

public sealed class JobService(
    Persistence.JobLensDbContext dbContext,
    IContentHashService contentHashService,
    IBackgroundJobClient backgroundJobs,
    IAiBackendClient aiBackendClient) : IJobService
{
    public async Task<ApiResponse<IReadOnlyList<JobPostingDto>>> GetJobsAsync(string? search, bool includeExternal, CancellationToken cancellationToken)
    {
        var query = dbContext.Jobs.Include(x => x.Company).Where(x => x.IsActive);
        if (!includeExternal)
        {
            query = query.Where(x => x.SourceType == JobSourceType.Internal);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.Title.Contains(term) || x.Description.Contains(term) || x.Location.Contains(term));
        }

        var jobs = await query
            .OrderByDescending(x => x.PostedAtUtc ?? x.CreatedAtUtc)
            .Select(x => ToDto(x))
            .ToListAsync(cancellationToken);

        return new ApiResponse<IReadOnlyList<JobPostingDto>>(true, jobs);
    }

    public async Task<ApiResponse<JobPostingDto>> GetJobAsync(long jobId, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.Include(x => x.Company).FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        return job is null
            ? new ApiResponse<JobPostingDto>(false, null, "Job not found.", ["not_found"])
            : new ApiResponse<JobPostingDto>(true, ToDto(job));
    }

    public async Task<ApiResponse<JobPostingDto>> CreateJobAsync(long recruiterUserId, CreateJobRequest request, CancellationToken cancellationToken)
    {
        var recruiter = await dbContext.RecruiterProfiles.Include(x => x.Company).FirstOrDefaultAsync(x => x.UserId == recruiterUserId, cancellationToken);
        if (recruiter is null)
        {
            return new ApiResponse<JobPostingDto>(false, null, "Recruiter profile not found.", ["not_found"]);
        }

        var normalized = $"{request.Title}\n{request.Description}\n{request.Location}\n{string.Join(',', request.Skills)}";
        var job = new JobPosting
        {
            CompanyId = recruiter.CompanyId,
            Title = request.Title.Trim(),
            Description = request.Description.Trim(),
            Location = request.Location.Trim(),
            SkillsJson = ServiceJson.Serialize(request.Skills),
            ContentHash = contentHashService.Compute(normalized),
            ExpiresAtUtc = request.ExpiresAtUtc,
            PostedAtUtc = DateTime.UtcNow,
            SourceType = JobSourceType.Internal,
        };

        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken);

        backgroundJobs.Enqueue<RecommendationRefreshJob>(jobRunner => jobRunner.RefreshJobAsync(job.Id));

        return new ApiResponse<JobPostingDto>(true, ToDto(job, recruiter.Company?.Name), "Job created.");
    }

    public async Task<ApiResponse<JobPostingDto>> UpdateJobAsync(long recruiterUserId, long jobId, UpdateJobRequest request, CancellationToken cancellationToken)
    {
        var recruiter = await dbContext.RecruiterProfiles.FirstOrDefaultAsync(x => x.UserId == recruiterUserId, cancellationToken);
        var job = await dbContext.Jobs.Include(x => x.Company).FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);

        if (recruiter is null || job is null)
        {
            return new ApiResponse<JobPostingDto>(false, null, "Job or recruiter not found.", ["not_found"]);
        }

        job.Title = request.Title.Trim();
        job.Description = request.Description.Trim();
        job.Location = request.Location.Trim();
        job.SkillsJson = ServiceJson.Serialize(request.Skills);
        job.IsActive = request.IsActive;
        job.ExpiresAtUtc = request.ExpiresAtUtc;
        job.ContentHash = contentHashService.Compute($"{job.Title}\n{job.Description}\n{job.Location}\n{string.Join(',', request.Skills)}");

        await dbContext.SaveChangesAsync(cancellationToken);
        backgroundJobs.Enqueue<RecommendationRefreshJob>(jobRunner => jobRunner.RefreshJobAsync(job.Id));

        return new ApiResponse<JobPostingDto>(true, ToDto(job), "Job updated.");
    }

    public async Task<ApiResponse<IReadOnlyList<RecommendationDto>>> MatchJobsFromTextAsync(string resumeText, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resumeText))
        {
            return new ApiResponse<IReadOnlyList<RecommendationDto>>(false, null, "Resume text is required.", ["validation_error"]);
        }

        var safeLimit = Math.Clamp(limit, 1, 50);
        var aiResult = await aiBackendClient.RecommendJobsAsync(
            new JobRecommendationRequest(0, resumeText, safeLimit),
            cancellationToken);

        if (!aiResult.Success || aiResult.Data is null)
        {
            return new ApiResponse<IReadOnlyList<RecommendationDto>>(false, null, aiResult.Error?.Message ?? "Recommendation request failed.");
        }

        var sanitized = SanitizeRecommendations(aiResult.Data, safeLimit);

        var mapped = sanitized
            .Select(x => new RecommendationDto(x.TargetId, x.TargetType, x.Score, x.Reason, x.PreviewJson))
            .ToList();

        return new ApiResponse<IReadOnlyList<RecommendationDto>>(true, mapped);
    }

    public async Task<ApiResponse<IReadOnlyList<RecommendationDto>>> GetRecommendationsForCandidateAsync(long candidateUserId, int limit, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        var safeLimit = Math.Clamp(limit, 1, 50);
        var now = DateTime.UtcNow;

        var candidate = await dbContext.CandidateProfiles.FirstOrDefaultAsync(x => x.UserId == candidateUserId, cancellationToken);
        if (candidate is null)
        {
            return new ApiResponse<IReadOnlyList<RecommendationDto>>(false, null, "Candidate profile not found.", ["not_found"]);
        }

        if (!forceRefresh)
        {
            var cached = await dbContext.RecommendationCacheEntries
                .Where(x => x.SubjectType == RecommendationSubjectType.Candidate && x.SubjectId == candidate.Id && x.TargetType == RecommendationTargetType.Job && x.ExpiresAtUtc > now)
                .OrderBy(x => x.Rank)
                .Take(safeLimit)
                .Select(x => new RecommendationDto(x.TargetId, "Job", x.Score, x.Reason, string.Empty))
                .ToListAsync(cancellationToken);

            if (cached.Count > 0)
            {
                return new ApiResponse<IReadOnlyList<RecommendationDto>>(true, cached);
            }

            var staleCached = await dbContext.RecommendationCacheEntries
                .Where(x => x.SubjectType == RecommendationSubjectType.Candidate && x.SubjectId == candidate.Id && x.TargetType == RecommendationTargetType.Job)
                .OrderBy(x => x.Rank)
                .Take(safeLimit)
                .Select(x => new RecommendationDto(x.TargetId, "Job", x.Score, x.Reason, string.Empty))
                .ToListAsync(cancellationToken);

            backgroundJobs.Enqueue<RecommendationRefreshJob>(jobRunner => jobRunner.RefreshCandidateAsync(candidate.Id));

            if (staleCached.Count > 0)
            {
                return new ApiResponse<IReadOnlyList<RecommendationDto>>(true, staleCached, "Showing cached recommendations while fresh results are generated.");
            }

            var hasReadyJobVectors = await dbContext.VectorIndexEntries.AnyAsync(
                x => x.EntityType == "job" && x.Status == VectorIndexStatus.Ready,
                cancellationToken);
            if (!hasReadyJobVectors)
            {
                backgroundJobs.Enqueue<RecommendationRefreshJob>(jobRunner => jobRunner.RefreshAllAsync());
                return new ApiResponse<IReadOnlyList<RecommendationDto>>(true, Array.Empty<RecommendationDto>(), "Recommendations are being prepared. Job vectors are syncing now.");
            }

            return new ApiResponse<IReadOnlyList<RecommendationDto>>(true, Array.Empty<RecommendationDto>(), "Recommendations are being generated. Please refresh shortly.");
        }

        var resume = await dbContext.Resumes
            .Where(x => x.CandidateProfileId == candidate.Id)
            .OrderByDescending(x => x.IsDefault)
            .ThenByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (resume is null || string.IsNullOrWhiteSpace(resume.RawText))
        {
            return new ApiResponse<IReadOnlyList<RecommendationDto>>(false, null, "No resume with extractable text found for recommendations.", ["missing_resume"]);
        }

        var aiResult = await aiBackendClient.RecommendJobsAsync(new JobRecommendationRequest(candidate.Id, resume.RawText, safeLimit), cancellationToken);
        if (!aiResult.Success || aiResult.Data is null)
        {
            var staleCached = await dbContext.RecommendationCacheEntries
                .Where(x => x.SubjectType == RecommendationSubjectType.Candidate && x.SubjectId == candidate.Id && x.TargetType == RecommendationTargetType.Job)
                .OrderBy(x => x.Rank)
                .Take(safeLimit)
                .Select(x => new RecommendationDto(x.TargetId, "Job", x.Score, x.Reason, string.Empty))
                .ToListAsync(cancellationToken);

            if (staleCached.Count > 0)
            {
                return new ApiResponse<IReadOnlyList<RecommendationDto>>(true, staleCached, "Showing cached recommendations because refresh failed.");
            }

            return new ApiResponse<IReadOnlyList<RecommendationDto>>(false, null, aiResult.Error?.Message ?? "Recommendation request failed.");
        }

        var sanitized = SanitizeRecommendations(aiResult.Data, safeLimit);
        if (sanitized.Count == 0)
        {
            backgroundJobs.Enqueue<RecommendationRefreshJob>(jobRunner => jobRunner.RefreshAllAsync());

            var staleCached = await dbContext.RecommendationCacheEntries
                .Where(x => x.SubjectType == RecommendationSubjectType.Candidate && x.SubjectId == candidate.Id && x.TargetType == RecommendationTargetType.Job)
                .OrderBy(x => x.Rank)
                .Take(safeLimit)
                .Select(x => new RecommendationDto(x.TargetId, "Job", x.Score, x.Reason, string.Empty))
                .ToListAsync(cancellationToken);

            if (staleCached.Count > 0)
            {
                return new ApiResponse<IReadOnlyList<RecommendationDto>>(true, staleCached, "Showing cached recommendations while fresh results are generated.");
            }

            return new ApiResponse<IReadOnlyList<RecommendationDto>>(true, Array.Empty<RecommendationDto>(), "No matches returned yet. Recommendation vectors are being refreshed.");
        }

        await UpsertRecommendationCacheAsync(candidate.Id, RecommendationSubjectType.Candidate, RecommendationTargetType.Job, sanitized, cancellationToken);
        return new ApiResponse<IReadOnlyList<RecommendationDto>>(true, sanitized.Select(x => new RecommendationDto(x.TargetId, x.TargetType, x.Score, x.Reason, x.PreviewJson)).ToList());
    }

    public async Task<ApiResponse<IReadOnlyList<RecommendationDto>>> GetRecommendationsForJobAsync(long recruiterUserId, long jobId, int limit, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        var safeLimit = Math.Clamp(limit, 1, 50);
        var now = DateTime.UtcNow;
        var recruiter = await dbContext.RecruiterProfiles.FirstOrDefaultAsync(x => x.UserId == recruiterUserId, cancellationToken);
        var isAdmin = await dbContext.Users.AnyAsync(x => x.Id == recruiterUserId && x.Role == AppRole.Admin, cancellationToken);
        var job = await dbContext.Jobs.FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (job is null || (recruiter is null && !isAdmin))
        {
            return new ApiResponse<IReadOnlyList<RecommendationDto>>(false, null, "Job or recruiter not found.", ["not_found"]);
        }

        if (!forceRefresh)
        {
            var cached = await dbContext.RecommendationCacheEntries
                .Where(x => x.SubjectType == RecommendationSubjectType.Job && x.SubjectId == job.Id && x.TargetType == RecommendationTargetType.Candidate && x.ExpiresAtUtc > now)
                .OrderBy(x => x.Rank)
                .Take(safeLimit)
                .Select(x => new RecommendationDto(x.TargetId, "Candidate", x.Score, x.Reason, string.Empty))
                .ToListAsync(cancellationToken);

            if (cached.Count > 0)
            {
                return new ApiResponse<IReadOnlyList<RecommendationDto>>(true, cached);
            }

            var staleCached = await dbContext.RecommendationCacheEntries
                .Where(x => x.SubjectType == RecommendationSubjectType.Job && x.SubjectId == job.Id && x.TargetType == RecommendationTargetType.Candidate)
                .OrderBy(x => x.Rank)
                .Take(safeLimit)
                .Select(x => new RecommendationDto(x.TargetId, "Candidate", x.Score, x.Reason, string.Empty))
                .ToListAsync(cancellationToken);

            backgroundJobs.Enqueue<RecommendationRefreshJob>(jobRunner => jobRunner.RefreshJobAsync(job.Id));

            if (staleCached.Count > 0)
            {
                return new ApiResponse<IReadOnlyList<RecommendationDto>>(true, staleCached, "Showing cached recommendations while fresh results are generated.");
            }

            return new ApiResponse<IReadOnlyList<RecommendationDto>>(true, Array.Empty<RecommendationDto>(), "Recommendations are being generated. Please refresh shortly.");
        }

        var staleCachedForForceRefresh = await dbContext.RecommendationCacheEntries
            .Where(x => x.SubjectType == RecommendationSubjectType.Job && x.SubjectId == job.Id && x.TargetType == RecommendationTargetType.Candidate)
            .OrderBy(x => x.Rank)
            .Take(safeLimit)
            .Select(x => new RecommendationDto(x.TargetId, "Candidate", x.Score, x.Reason, string.Empty))
            .ToListAsync(cancellationToken);

        var aiResult = await aiBackendClient.RecommendCandidatesAsync(
            new CandidateRecommendationRequest(job.Id, job.Description ?? string.Empty, safeLimit),
            cancellationToken);

        if (!aiResult.Success || aiResult.Data is null)
        {
            if (staleCachedForForceRefresh.Count > 0)
            {
                return new ApiResponse<IReadOnlyList<RecommendationDto>>(true, staleCachedForForceRefresh, "Showing cached recommendations because refresh failed.");
            }

            return new ApiResponse<IReadOnlyList<RecommendationDto>>(false, null, aiResult.Error?.Message ?? "Recommendation request failed.");
        }

        var sanitized = SanitizeRecommendations(aiResult.Data, safeLimit);
        await UpsertRecommendationCacheAsync(job.Id, RecommendationSubjectType.Job, RecommendationTargetType.Candidate, sanitized, cancellationToken);
        return new ApiResponse<IReadOnlyList<RecommendationDto>>(true, sanitized.Select(x => new RecommendationDto(x.TargetId, x.TargetType, x.Score, x.Reason, x.PreviewJson)).ToList());
    }

    private async Task UpsertRecommendationCacheAsync(
        long subjectId,
        RecommendationSubjectType subjectType,
        RecommendationTargetType targetType,
        IReadOnlyList<RecommendationResultDto> data,
        CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

            await dbContext.RecommendationCacheEntries
                .Where(x => x.SubjectId == subjectId && x.SubjectType == subjectType && x.TargetType == targetType)
                .ExecuteDeleteAsync(cancellationToken);

            if (data.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            var refreshedAt = DateTime.UtcNow;
            for (var i = 0; i < data.Count; i++)
            {
                dbContext.RecommendationCacheEntries.Add(new RecommendationCacheEntry
                {
                    SubjectId = subjectId,
                    SubjectType = subjectType,
                    TargetType = targetType,
                    TargetId = data[i].TargetId,
                    Rank = i + 1,
                    Score = data[i].Score,
                    Reason = data[i].Reason,
                    SourceSnapshotHash = data[i].PreviewJson,
                    RefreshedAtUtc = refreshedAt,
                    ExpiresAtUtc = refreshedAt.AddHours(6),
                });
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private static IReadOnlyList<RecommendationResultDto> SanitizeRecommendations(
        IReadOnlyList<RecommendationResultDto> data,
        int maxCount)
    {
        var safeLimit = Math.Max(1, maxCount);
        var seenTargetIds = new HashSet<long>();
        var sanitized = new List<RecommendationResultDto>(Math.Min(data.Count, safeLimit));

        foreach (var item in data)
        {
            if (item.TargetId <= 0 || !seenTargetIds.Add(item.TargetId))
            {
                continue;
            }

            sanitized.Add(item with
            {
                Score = double.IsFinite(item.Score) ? item.Score : 0,
                Reason = item.Reason ?? string.Empty,
                PreviewJson = item.PreviewJson ?? string.Empty,
            });

            if (sanitized.Count >= safeLimit)
            {
                break;
            }
        }

        return sanitized;
    }

    private static JobPostingDto ToDto(JobPosting job, string? companyName = null) =>
        new(job.Id, job.SourceType, job.Title, job.Description, job.Location, companyName ?? job.Company?.Name ?? "External", job.IsActive, job.RedirectUrl, job.PostedAtUtc, ServiceJson.DeserializeStringList(job.SkillsJson));
}

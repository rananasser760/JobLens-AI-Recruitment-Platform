using JobLens.Application.Contracts;
using JobLens.Application.Interfaces;
using JobLens.Domain.Entities;
using JobLens.Domain.Enums;
using JobLens.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace JobLens.Infrastructure.BackgroundJobs;

public sealed class RecommendationRefreshJob(Persistence.JobLensDbContext dbContext, IAiBackendClient aiBackendClient)
{
    public async Task RefreshAllAsync()
    {
        var candidateIds = await dbContext.CandidateProfiles
            .Select(x => x.Id)
            .ToListAsync();

        foreach (var candidateId in candidateIds)
        {
            await RefreshCandidateAsync(candidateId);
        }

        var jobIds = await dbContext.Jobs
            .Where(x => x.IsActive)
            .Select(x => x.Id)
            .ToListAsync();

        foreach (var jobId in jobIds)
        {
            await RefreshJobAsync(jobId);
        }
    }

    public async Task RefreshCandidateAsync(long candidateId)
    {
        var candidate = await dbContext.CandidateProfiles.Include(x => x.Resumes).FirstOrDefaultAsync(x => x.Id == candidateId);
        var resume = candidate?.Resumes.OrderByDescending(x => x.IsDefault).ThenByDescending(x => x.CreatedAtUtc).FirstOrDefault();
        if (candidate is null || resume is null || string.IsNullOrWhiteSpace(resume.RawText))
        {
            return;
        }

        var results = await aiBackendClient.RecommendJobsAsync(new JobRecommendationRequest(candidateId, resume.RawText, 10), CancellationToken.None);
        if (!results.Success || results.Data is null)
        {
            return;
        }

        var sanitized = SanitizeRecommendations(results.Data, 10);
        await ReplaceRecommendationCacheAsync(candidateId, RecommendationSubjectType.Candidate, RecommendationTargetType.Job, sanitized, CancellationToken.None);
    }

    public async Task RefreshJobAsync(long jobId)
    {
        var job = await dbContext.Jobs.FirstOrDefaultAsync(x => x.Id == jobId);
        if (job is null)
        {
            return;
        }

        var vectorEntry = await dbContext.VectorIndexEntries
            .FirstOrDefaultAsync(x => x.EntityType == "job" && x.EntityId == job.Id);

        var shouldUpsertVector = vectorEntry is null
            || vectorEntry.Status != VectorIndexStatus.Ready
            || !string.Equals(vectorEntry.ContentHash, job.ContentHash, StringComparison.Ordinal);

        if (shouldUpsertVector)
        {
            var vectorResult = await aiBackendClient.UpsertJobVectorAsync(
                new JobVectorSyncRequest(
                    job.Id,
                    new Dictionary<string, object?>
                    {
                        ["jobId"] = job.Id,
                        ["title"] = job.Title,
                        ["description"] = job.Description,
                        ["location"] = job.Location,
                        ["skills"] = ServiceJson.DeserializeStringList(job.SkillsJson),
                    },
                    job.ContentHash),
                CancellationToken.None);

            if (vectorEntry is null)
            {
                vectorEntry = new VectorIndexEntry
                {
                    EntityType = "job",
                    EntityId = job.Id,
                };
                dbContext.VectorIndexEntries.Add(vectorEntry);
            }

            vectorEntry.ContentHash = job.ContentHash;
            vectorEntry.EmbeddedAtUtc = DateTime.UtcNow;

            if (vectorResult.Success && vectorResult.Data is not null)
            {
                vectorEntry.Collection = vectorResult.Data.Collection;
                vectorEntry.VectorId = vectorResult.Data.VectorId;
                vectorEntry.Model = vectorResult.Data.Model;
                vectorEntry.Status = VectorIndexStatus.Ready;
                vectorEntry.LastError = string.Empty;
            }
            else
            {
                vectorEntry.Status = VectorIndexStatus.Failed;
                vectorEntry.LastError = vectorResult.Error?.Message ?? "Job vector upsert failed.";
            }

            await dbContext.SaveChangesAsync();
        }

        var results = await aiBackendClient.RecommendCandidatesAsync(new CandidateRecommendationRequest(job.Id, job.Description, 10), CancellationToken.None);
        if (!results.Success || results.Data is null)
        {
            return;
        }

        var sanitized = SanitizeRecommendations(results.Data, 10);
        await ReplaceRecommendationCacheAsync(jobId, RecommendationSubjectType.Job, RecommendationTargetType.Candidate, sanitized, CancellationToken.None);
    }

    private async Task ReplaceRecommendationCacheAsync(
        long subjectId,
        RecommendationSubjectType subjectType,
        RecommendationTargetType targetType,
        IReadOnlyList<RecommendationResultDto> recommendations,
        CancellationToken cancellationToken)
    {
        await dbContext.RecommendationCacheEntries
            .Where(x => x.SubjectType == subjectType && x.SubjectId == subjectId && x.TargetType == targetType)
            .ExecuteDeleteAsync(cancellationToken);

        if (recommendations.Count == 0)
        {
            return;
        }

        var refreshedAt = DateTime.UtcNow;
        for (var i = 0; i < recommendations.Count; i++)
        {
            var item = recommendations[i];
            dbContext.RecommendationCacheEntries.Add(new RecommendationCacheEntry
            {
                SubjectType = subjectType,
                SubjectId = subjectId,
                TargetType = targetType,
                TargetId = item.TargetId,
                Rank = i + 1,
                Score = item.Score,
                Reason = item.Reason,
                SourceSnapshotHash = item.PreviewJson,
                RefreshedAtUtc = refreshedAt,
                ExpiresAtUtc = refreshedAt.AddHours(6),
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static IReadOnlyList<RecommendationResultDto> SanitizeRecommendations(
        IReadOnlyList<RecommendationResultDto> recommendations,
        int maxCount)
    {
        var safeLimit = Math.Max(1, maxCount);
        var seenTargetIds = new HashSet<long>();
        var sanitized = new List<RecommendationResultDto>(Math.Min(recommendations.Count, safeLimit));

        foreach (var item in recommendations)
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
}

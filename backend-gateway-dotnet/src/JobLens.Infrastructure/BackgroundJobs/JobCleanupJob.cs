using JobLens.Application.Interfaces;
using JobLens.Domain.Enums;
using JobLens.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace JobLens.Infrastructure.BackgroundJobs;

public sealed class JobCleanupJob(
    Persistence.JobLensDbContext dbContext,
    IAiBackendClient aiBackendClient)
{
    public async Task CleanupAsync(int staleAfterDays)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Abs(staleAfterDays));
        var now = DateTime.UtcNow;

        var staleJobs = await dbContext.Jobs
            .Where(x => x.IsActive && ((x.ExpiresAtUtc.HasValue && x.ExpiresAtUtc < now) || (x.PostedAtUtc.HasValue && x.PostedAtUtc < cutoff)))
            .ToListAsync();

        if (staleJobs.Count == 0)
        {
            dbContext.BackgroundJobStates.Add(new Domain.Entities.BackgroundJobState
            {
                JobType = "CleanupJobs",
                CorrelationId = Guid.NewGuid().ToString("N"),
                Status = "Completed",
                LastRunAtUtc = now,
                PayloadJson = ServiceJson.Serialize(new { staleAfterDays, affected = 0 }),
            });
            await dbContext.SaveChangesAsync();
            return;
        }

        var staleJobIds = staleJobs.Select(j => j.Id).ToList();

        foreach (var job in staleJobs)
        {
            job.IsActive = false;
            job.UpdatedAtUtc = now;
        }

        // 1. Remove deactivated jobs from the recommendation cache
        var cachedRecommendations = await dbContext.RecommendationCacheEntries
            .Where(x => (x.SubjectType == RecommendationSubjectType.Job && staleJobIds.Contains(x.SubjectId)) ||
                        (x.TargetType == RecommendationTargetType.Job && staleJobIds.Contains(x.TargetId)))
            .ToListAsync();

        if (cachedRecommendations.Count > 0)
        {
            dbContext.RecommendationCacheEntries.RemoveRange(cachedRecommendations);
        }

        // 2. Mark vector index entries as Deleted
        var vectorEntries = await dbContext.VectorIndexEntries
            .Where(x => x.EntityType == "job" && staleJobIds.Contains(x.EntityId))
            .ToListAsync();

        foreach (var entry in vectorEntries)
        {
            entry.Status = VectorIndexStatus.Deleted;
            entry.UpdatedAtUtc = now;
        }

        dbContext.BackgroundJobStates.Add(new Domain.Entities.BackgroundJobState
        {
            JobType = "CleanupJobs",
            CorrelationId = Guid.NewGuid().ToString("N"),
            Status = "Completed",
            LastRunAtUtc = now,
            PayloadJson = ServiceJson.Serialize(new 
            { 
                staleAfterDays, 
                deactivatedJobs = staleJobs.Count,
                evictedRecommendations = cachedRecommendations.Count 
            }),
        });

        await dbContext.SaveChangesAsync();

        // 3. Remove from ChromaDB vector store
        foreach (var jobId in staleJobIds)
        {
            try
            {
                await aiBackendClient.DeleteJobVectorAsync(jobId, CancellationToken.None);
            }
            catch
            {
                // Silently continue if external vector deletion fails during bulk cleanup
            }
        }
    }
}
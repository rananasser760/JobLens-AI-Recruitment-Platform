using JobLens.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace JobLens.Infrastructure.BackgroundJobs;

public sealed class JobCleanupJob(Persistence.JobLensDbContext dbContext)
{
    public async Task CleanupAsync(int staleAfterDays)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Abs(staleAfterDays));
        var staleJobs = await dbContext.Jobs
            .Where(x => x.IsActive && ((x.ExpiresAtUtc.HasValue && x.ExpiresAtUtc < DateTime.UtcNow) || (x.PostedAtUtc.HasValue && x.PostedAtUtc < cutoff)))
            .ToListAsync();

        foreach (var job in staleJobs)
        {
            job.IsActive = false;
        }

        dbContext.BackgroundJobStates.Add(new Domain.Entities.BackgroundJobState
        {
            JobType = "CleanupJobs",
            CorrelationId = Guid.NewGuid().ToString("N"),
            Status = "Completed",
            LastRunAtUtc = DateTime.UtcNow,
            PayloadJson = ServiceJson.Serialize(new { staleAfterDays, affected = staleJobs.Count }),
        });

        await dbContext.SaveChangesAsync();
    }
}

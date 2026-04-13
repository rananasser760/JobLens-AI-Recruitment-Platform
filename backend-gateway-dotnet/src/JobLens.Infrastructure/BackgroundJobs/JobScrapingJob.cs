using JobLens.Application.Contracts;
using JobLens.Application.Interfaces;
using JobLens.Domain.Entities;
using JobLens.Domain.Enums;
using JobLens.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace JobLens.Infrastructure.BackgroundJobs;

public sealed class JobScrapingJob(Persistence.JobLensDbContext dbContext, IAiBackendClient aiBackendClient, IContentHashService contentHashService)
{
    public async Task RunAsync(int? maxCategories)
    {
        var state = new BackgroundJobState { JobType = "ScrapeJobs", CorrelationId = Guid.NewGuid().ToString("N"), Status = "Running", LastRunAtUtc = DateTime.UtcNow };
        dbContext.BackgroundJobStates.Add(state);
        await dbContext.SaveChangesAsync();

        try
        {
            var result = await aiBackendClient.ScrapeJobsAsync(new ScrapeJobsRequest(maxCategories), CancellationToken.None);
            if (!result.Success || result.Data is null)
            {
                state.Status = "Failed";
                state.Error = result.Error?.Message ?? "Scrape failed";
                await dbContext.SaveChangesAsync();
                return;
            }

            foreach (var item in result.Data.Jobs)
            {
                var existing = await dbContext.Jobs.FirstOrDefaultAsync(x =>
                    x.SourceType == JobSourceType.External &&
                    x.SourceUrl == item.SourceUrl &&
                    x.ExternalJobId == item.ExternalJobId);

                var contentHash = contentHashService.Compute($"{item.Title}\n{item.Description}\n{item.Location}\n{string.Join(',', item.Skills)}");
                if (existing is null)
                {
                    existing = new JobPosting
                    {
                        SourceType = JobSourceType.External,
                        ExternalJobId = item.ExternalJobId,
                        SourceUrl = item.SourceUrl,
                    };
                    dbContext.Jobs.Add(existing);
                }

                existing.RedirectUrl = item.RedirectUrl;
                existing.Title = item.Title;
                existing.Description = item.Description;
                existing.Location = item.Location;
                existing.MetadataJson = ServiceJson.Serialize(item.Metadata);
                existing.SkillsJson = ServiceJson.Serialize(item.Skills);
                existing.ContentHash = contentHash;
                existing.PostedAtUtc = item.PostedAtUtc;
                existing.IsActive = true;
            }

            state.Status = "Completed";
            state.LastRunAtUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            state.Status = "Failed";
            state.Error = ex.Message;
            state.Attempts += 1;
            state.LastRunAtUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync();
        }
    }
}

using Hangfire;
using JobLens.Application.Common;
using JobLens.Application.DTOs.Admin;
using JobLens.Application.Interfaces;
using JobLens.Infrastructure.BackgroundJobs;
using Microsoft.EntityFrameworkCore;

namespace JobLens.Infrastructure.Services;

public sealed class AdminService(
    Persistence.JobLensDbContext dbContext,
    IBackgroundJobClient backgroundJobs) : IAdminService
{
    public Task<ApiResponse<bool>> TriggerScrapeAsync(TriggerScrapeRequest? request, CancellationToken cancellationToken)
    {
        var maxCategories = request?.MaxCategories;
        backgroundJobs.Enqueue<JobScrapingJob>(job => job.RunAsync(maxCategories));
        return Task.FromResult(new ApiResponse<bool>(true, true, "Scraping job queued."));
    }

    public Task<ApiResponse<bool>> CleanupJobsAsync(CleanupJobsRequest? request, CancellationToken cancellationToken)
    {
        var staleDays = request?.StaleAfterDays > 0 ? request.StaleAfterDays : 45;
        backgroundJobs.Enqueue<JobCleanupJob>(job => job.CleanupAsync(staleDays));
        return Task.FromResult(new ApiResponse<bool>(true, true, "Job cleanup queued."));
    }

    public Task<ApiResponse<bool>> RefreshRecommendationsAsync(CancellationToken cancellationToken)
    {
        backgroundJobs.Enqueue<RecommendationRefreshJob>(job => job.RefreshAllAsync());
        return Task.FromResult(new ApiResponse<bool>(true, true, "Recommendation refresh queued."));
    }

    public async Task<ApiResponse<IReadOnlyList<BackgroundJobDto>>> GetBackgroundJobsAsync(CancellationToken cancellationToken)
    {
        var jobs = await dbContext.BackgroundJobStates
            .AsNoTracking()
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Take(100)
            .Select(x => new BackgroundJobDto(
                x.Id, 
                x.JobType, 
                x.Status, 
                x.Attempts, 
                x.LastRunAtUtc, 
                x.NextRunAtUtc, 
                x.Error))
            .ToListAsync(cancellationToken);

        return new ApiResponse<IReadOnlyList<BackgroundJobDto>>(true, jobs);
    }
}
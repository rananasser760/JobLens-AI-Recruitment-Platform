using JobLens.Application.Common;
using JobLens.Application.DTOs.Admin;

namespace JobLens.Application.Interfaces;

public interface IAdminService
{
    Task<ApiResponse<bool>> TriggerScrapeAsync(TriggerScrapeRequest request, CancellationToken cancellationToken);
    Task<ApiResponse<bool>> CleanupJobsAsync(CleanupJobsRequest request, CancellationToken cancellationToken);
    Task<ApiResponse<bool>> RefreshRecommendationsAsync(CancellationToken cancellationToken);
    Task<ApiResponse<IReadOnlyList<BackgroundJobDto>>> GetBackgroundJobsAsync(CancellationToken cancellationToken);
}

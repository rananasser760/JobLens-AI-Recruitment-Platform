using JobLens.Application.Common;
using JobLens.Application.DTOs.Jobs;

namespace JobLens.Application.Interfaces;

public interface IJobService
{
    Task<ApiResponse<IReadOnlyList<JobPostingDto>>> GetJobsAsync(string? search, bool includeExternal, CancellationToken cancellationToken);
    Task<ApiResponse<JobPostingDto>> GetJobAsync(long jobId, CancellationToken cancellationToken);
    Task<ApiResponse<JobPostingDto>> CreateJobAsync(long recruiterUserId, CreateJobRequest request, CancellationToken cancellationToken);
    Task<ApiResponse<JobPostingDto>> UpdateJobAsync(long recruiterUserId, long jobId, UpdateJobRequest request, CancellationToken cancellationToken);
    Task<ApiResponse<IReadOnlyList<RecommendationDto>>> MatchJobsFromTextAsync(string resumeText, int limit, CancellationToken cancellationToken);
    Task<ApiResponse<IReadOnlyList<RecommendationDto>>> GetRecommendationsForCandidateAsync(long candidateUserId, int limit, CancellationToken cancellationToken, bool forceRefresh = false);
    Task<ApiResponse<IReadOnlyList<RecommendationDto>>> GetRecommendationsForJobAsync(long recruiterUserId, long jobId, int limit, CancellationToken cancellationToken);
}

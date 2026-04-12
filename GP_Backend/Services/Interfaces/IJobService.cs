using GP_Backend.Models.DTOs.Common;
using GP_Backend.Models.DTOs.Job;
using System.Text.Json;

namespace GP_Backend.Services.Interfaces;

public interface IJobService
{
    Task<ApiResponse<JobDto>> GetJobAsync(long jobId);
    Task<ApiResponse<PaginatedResponse<JobListDto>>> SearchJobsAsync(JobSearchParams searchParams);
    Task<ApiResponse<JobDto>> CreateJobAsync(long recruiterId, CreateJobDto dto);
    Task<ApiResponse<JobDto>> UpdateJobAsync(long jobId, long recruiterId, UpdateJobDto dto);
    Task<ApiResponse> DeleteJobAsync(long jobId, long recruiterId);
    Task<ApiResponse> ToggleJobStatusAsync(long jobId, long recruiterId);
    
    // Job skills
    Task<ApiResponse> AddJobSkillAsync(long jobId, CreateJobSkillDto dto);
    Task<ApiResponse> RemoveJobSkillAsync(long jobId, long skillId);
    
    // Job recommendations for candidates
    Task<ApiResponse<List<JobRecommendationDto>>> GetRecommendedJobsAsync(long candidateId, int limit = 10);
    Task<ApiResponse<List<JobRecommendationDto>>> MatchJobsFromTextAsync(string resumeText, int limit = 5);
    
    // Scraped jobs management
    Task<ApiResponse<int>> ImportScrapedJobsAsync(List<ScrapedJobDto> jobs);
    Task<ApiResponse<int>> CleanupExpiredScrapedJobsAsync(int daysOld = 30);
    Task<ApiResponse<List<ScrapedJobDto>>> GetScrapedJobsFromAiAsync(string? keyword = null, string? location = null, int limit = 50);
    Task<ApiResponse<JsonElement>> GetScrapingStatusAsync();
    Task<ApiResponse<JsonElement>> GetRecruitmentStatusAsync();
    Task<ApiResponse> TriggerScrapingAsync(int? maxCategories = null);
    
    // For recruiters - get their jobs
    Task<ApiResponse<PaginatedResponse<JobListDto>>> GetRecruiterJobsAsync(long recruiterId, JobSearchParams searchParams);
}

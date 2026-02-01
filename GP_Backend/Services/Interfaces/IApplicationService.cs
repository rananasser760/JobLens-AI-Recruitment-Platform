using GP_Backend.Models.DTOs.Application;
using GP_Backend.Models.DTOs.Common;

namespace GP_Backend.Services.Interfaces;

public interface IApplicationService
{
    Task<ApiResponse<ApplicationDto>> GetApplicationAsync(long applicationId);
    Task<ApiResponse<ApplicationDto>> ApplyToJobAsync(long candidateId, ApplyToJobDto dto);
    Task<ApiResponse<ApplicationDto>> UpdateStatusAsync(long applicationId, long recruiterId, UpdateApplicationStatusDto dto);
    Task<ApiResponse> WithdrawApplicationAsync(long applicationId, long candidateId);
    
    // For candidates - get their applications
    Task<ApiResponse<PaginatedResponse<ApplicationListDto>>> GetCandidateApplicationsAsync(long candidateId, ApplicationSearchParams searchParams);
    
    // For recruiters - get applications for their jobs
    Task<ApiResponse<PaginatedResponse<CandidateApplicationDto>>> GetJobApplicationsAsync(long jobId, long recruiterId, ApplicationSearchParams searchParams);
    
    // Ranking candidates for a job
    Task<ApiResponse<List<CandidateApplicationDto>>> GetRankedCandidatesAsync(long jobId, long recruiterId);
    
    // Check if candidate already applied
    Task<bool> HasCandidateAppliedAsync(long candidateId, long jobId);
}

using JobLens.Application.Common;
using JobLens.Application.DTOs.Applications;

namespace JobLens.Application.Interfaces;

public interface IApplicationService
{
    Task<ApiResponse<ApplicationDto>> CreateAsync(long candidateUserId, CreateApplicationRequest request, CancellationToken cancellationToken);
    Task<ApiResponse<IReadOnlyList<ApplicationDto>>> GetCandidateApplicationsAsync(long candidateUserId, CancellationToken cancellationToken);
    Task<ApiResponse<IReadOnlyList<ApplicationDto>>> GetApplicantsForJobAsync(long recruiterUserId, long jobId, CancellationToken cancellationToken);
}

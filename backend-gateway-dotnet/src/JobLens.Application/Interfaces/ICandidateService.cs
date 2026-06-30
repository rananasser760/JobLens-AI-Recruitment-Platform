using JobLens.Application.Common;
using JobLens.Application.DTOs.Candidates;

namespace JobLens.Application.Interfaces;

public interface ICandidateService
{
    Task<ApiResponse<CandidateProfileDto>> GetProfileAsync(long userId, CancellationToken cancellationToken);
    Task<ApiResponse<CandidateProfileDto>> UpdateProfileAsync(long userId, UpdateCandidateProfileRequest request, CancellationToken cancellationToken);
}

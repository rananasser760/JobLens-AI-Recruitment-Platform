using JobLens.Application.Common;
using JobLens.Application.DTOs.Recruiters;

namespace JobLens.Application.Interfaces;

public interface IRecruiterService
{
    Task<ApiResponse<RecruiterProfileDto>> GetProfileAsync(long userId, CancellationToken cancellationToken);
    Task<ApiResponse<RecruiterProfileDto>> UpdateProfileAsync(long userId, UpdateRecruiterProfileRequest request, CancellationToken cancellationToken);
}

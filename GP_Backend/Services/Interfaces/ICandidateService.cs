using GP_Backend.Models.DTOs.Candidate;
using GP_Backend.Models.DTOs.Common;

namespace GP_Backend.Services.Interfaces;

public interface ICandidateService
{
    Task<ApiResponse<CandidateProfileDto>> GetProfileAsync(long userId);
    Task<ApiResponse<CandidateDashboardDto>> GetDashboardAsync(long userId);
    Task<ApiResponse<CandidateProfileDto>> GetProfileByIdAsync(long candidateId);
    Task<ApiResponse<CandidateProfileDto>> UpdateProfileAsync(long userId, UpdateCandidateProfileDto dto);
    Task<ApiResponse> UpdateProfileImageAsync(long userId, Stream imageStream, string fileName);
    Task<ApiResponse<List<CandidateSkillDto>>> GetSkillsAsync(long candidateId);
    Task<ApiResponse<CandidateSkillDto>> AddSkillAsync(long userId, AddSkillDto dto);
    Task<ApiResponse> RemoveSkillAsync(long userId, long skillId);
    Task<ApiResponse<PaginatedResponse<CandidateListDto>>> SearchCandidatesAsync(CandidateSearchParams searchParams);
    Task<ApiResponse> FillProfileFromResumeAsync(long userId, long resumeId);
}

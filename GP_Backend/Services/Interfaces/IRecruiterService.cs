using GP_Backend.Models.DTOs.Common;
using GP_Backend.Models.DTOs.Recruiter;

namespace GP_Backend.Services.Interfaces;

public interface IRecruiterService
{
    Task<ApiResponse<RecruiterProfileDto>> GetProfileAsync(long userId);
    Task<ApiResponse<RecruiterProfileDto>> UpdateProfileAsync(long userId, UpdateRecruiterProfileDto dto);
    Task<ApiResponse<RecruiterDashboardDto>> GetDashboardAsync(long userId);
    
    // Company operations
    Task<ApiResponse<CompanyDto>> GetCompanyAsync(long companyId);
    Task<ApiResponse<List<CompanyDto>>> GetAllCompaniesAsync();
    Task<ApiResponse<CompanyDto>> CreateCompanyAsync(CreateCompanyDto dto);
    Task<ApiResponse<CompanyDto>> UpdateCompanyAsync(long companyId, UpdateCompanyDto dto);
    Task<ApiResponse> UpdateCompanyLogoAsync(long companyId, Stream imageStream, string fileName);
}

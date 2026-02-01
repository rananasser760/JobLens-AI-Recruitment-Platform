using Microsoft.EntityFrameworkCore;
using GP_Backend.Data;
using GP_Backend.Models.DTOs.Common;
using GP_Backend.Models.DTOs.Recruiter;
using GP_Backend.Models.Entities;
using GP_Backend.Models.Enums;
using GP_Backend.Services.Interfaces;

namespace GP_Backend.Services.Implementations;

public class RecruiterService : IRecruiterService
{
    private readonly AppDbContext _context;
    private readonly IFileStorageService _fileStorage;
    private readonly IAuditService _auditService;
    private readonly ILogger<RecruiterService> _logger;

    public RecruiterService(
        AppDbContext context,
        IFileStorageService fileStorage,
        IAuditService auditService,
        ILogger<RecruiterService> logger)
    {
        _context = context;
        _fileStorage = fileStorage;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<ApiResponse<RecruiterProfileDto>> GetProfileAsync(long userId)
    {
        try
        {
            var recruiter = await _context.Recruiters
                .Include(r => r.User)
                .Include(r => r.Company)
                .FirstOrDefaultAsync(r => r.UserId == userId);

            if (recruiter == null)
            {
                return ApiResponse<RecruiterProfileDto>.FailureResponse("Recruiter profile not found");
            }

            return ApiResponse<RecruiterProfileDto>.SuccessResponse(MapToProfileDto(recruiter));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting recruiter profile");
            return ApiResponse<RecruiterProfileDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<RecruiterProfileDto>> UpdateProfileAsync(long userId, UpdateRecruiterProfileDto dto)
    {
        try
        {
            var recruiter = await _context.Recruiters
                .Include(r => r.User)
                .Include(r => r.Company)
                .FirstOrDefaultAsync(r => r.UserId == userId);

            if (recruiter == null)
            {
                return ApiResponse<RecruiterProfileDto>.FailureResponse("Recruiter profile not found");
            }

            if (dto.FullName != null) recruiter.FullName = dto.FullName;
            if (dto.Phone != null) recruiter.Phone = dto.Phone;
            if (dto.Position != null) recruiter.Position = dto.Position;
            if (dto.CompanyId.HasValue)
            {
                var company = await _context.Companies.FindAsync(dto.CompanyId.Value);
                if (company != null)
                {
                    recruiter.CompanyId = dto.CompanyId;
                }
            }

            await _context.SaveChangesAsync();

            // Reload company if changed
            if (dto.CompanyId.HasValue)
            {
                await _context.Entry(recruiter).Reference(r => r.Company).LoadAsync();
            }

            await _auditService.LogAsync(userId, "UpdateProfile", "Recruiter", recruiter.Id);

            return ApiResponse<RecruiterProfileDto>.SuccessResponse(MapToProfileDto(recruiter), "Profile updated successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating recruiter profile");
            return ApiResponse<RecruiterProfileDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<RecruiterDashboardDto>> GetDashboardAsync(long userId)
    {
        try
        {
            var recruiter = await _context.Recruiters.FirstOrDefaultAsync(r => r.UserId == userId);
            if (recruiter == null)
            {
                return ApiResponse<RecruiterDashboardDto>.FailureResponse("Recruiter profile not found");
            }

            var jobIds = await _context.Jobs
                .Where(j => j.RecruiterId == recruiter.Id)
                .Select(j => j.Id)
                .ToListAsync();

            var totalJobs = jobIds.Count;
            var activeJobs = await _context.Jobs.CountAsync(j => j.RecruiterId == recruiter.Id && j.IsActive);

            var applications = await _context.Applications
                .Where(a => jobIds.Contains(a.JobId))
                .ToListAsync();

            var totalApplications = applications.Count;
            var pendingApplications = applications.Count(a => a.Status == ApplicationStatus.Pending);

            var interviews = await _context.InterviewSessions
                .Where(i => applications.Select(a => a.Id).Contains(i.ApplicationId))
                .ToListAsync();

            var interviewsScheduled = interviews.Count(i => i.Status == "Scheduled");
            var interviewsCompleted = interviews.Count(i => i.Status == "Completed");

            // Recent applications
            var recentApplications = await _context.Applications
                .Include(a => a.Candidate)
                .Include(a => a.Job)
                .Where(a => jobIds.Contains(a.JobId))
                .OrderByDescending(a => a.AppliedAt)
                .Take(10)
                .Select(a => new RecentApplicationDto
                {
                    ApplicationId = a.Id,
                    CandidateName = a.Candidate.FullName ?? "Unknown",
                    JobTitle = a.Job.Title,
                    AppliedAt = a.AppliedAt,
                    Status = a.Status.ToString()
                })
                .ToListAsync();

            // Top jobs by application count
            var topJobs = await _context.Jobs
                .Where(j => j.RecruiterId == recruiter.Id)
                .Select(j => new JobStatsDto
                {
                    JobId = j.Id,
                    JobTitle = j.Title,
                    ApplicationCount = j.Applications.Count,
                    ShortlistedCount = j.Applications.Count(a => a.Status == ApplicationStatus.Shortlisted)
                })
                .OrderByDescending(j => j.ApplicationCount)
                .Take(5)
                .ToListAsync();

            return ApiResponse<RecruiterDashboardDto>.SuccessResponse(new RecruiterDashboardDto
            {
                TotalJobsPosted = totalJobs,
                ActiveJobs = activeJobs,
                TotalApplications = totalApplications,
                PendingApplications = pendingApplications,
                InterviewsScheduled = interviewsScheduled,
                InterviewsCompleted = interviewsCompleted,
                RecentApplications = recentApplications,
                TopJobs = topJobs
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting recruiter dashboard");
            return ApiResponse<RecruiterDashboardDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<CompanyDto>> GetCompanyAsync(long companyId)
    {
        try
        {
            var company = await _context.Companies
                .Include(c => c.Jobs)
                .FirstOrDefaultAsync(c => c.Id == companyId);

            if (company == null)
            {
                return ApiResponse<CompanyDto>.FailureResponse("Company not found");
            }

            return ApiResponse<CompanyDto>.SuccessResponse(MapToCompanyDto(company));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting company");
            return ApiResponse<CompanyDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<List<CompanyDto>>> GetAllCompaniesAsync()
    {
        try
        {
            var companies = await _context.Companies
                .Include(c => c.Jobs)
                .OrderBy(c => c.Name)
                .Select(c => new CompanyDto
                {
                    Id = c.Id,
                    Name = c.Name,
                    Website = c.Website,
                    Industry = c.Industry,
                    Size = c.Size,
                    LogoPath = c.LogoPath,
                    Description = c.Description,
                    Location = c.Location,
                    CreatedAt = c.CreatedAt,
                    TotalJobs = c.Jobs.Count,
                    ActiveJobs = c.Jobs.Count(j => j.IsActive)
                })
                .ToListAsync();

            return ApiResponse<List<CompanyDto>>.SuccessResponse(companies);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all companies");
            return ApiResponse<List<CompanyDto>>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<CompanyDto>> CreateCompanyAsync(CreateCompanyDto dto)
    {
        try
        {
            var company = new Company
            {
                Name = dto.Name,
                Website = dto.Website,
                Industry = dto.Industry,
                Size = dto.Size,
                Description = dto.Description,
                Location = dto.Location,
                CreatedAt = DateTime.UtcNow
            };

            _context.Companies.Add(company);
            await _context.SaveChangesAsync();

            return ApiResponse<CompanyDto>.SuccessResponse(new CompanyDto
            {
                Id = company.Id,
                Name = company.Name,
                Website = company.Website,
                Industry = company.Industry,
                Size = company.Size,
                Description = company.Description,
                Location = company.Location,
                CreatedAt = company.CreatedAt,
                TotalJobs = 0,
                ActiveJobs = 0
            }, "Company created successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating company");
            return ApiResponse<CompanyDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<CompanyDto>> UpdateCompanyAsync(long companyId, UpdateCompanyDto dto)
    {
        try
        {
            var company = await _context.Companies
                .Include(c => c.Jobs)
                .FirstOrDefaultAsync(c => c.Id == companyId);

            if (company == null)
            {
                return ApiResponse<CompanyDto>.FailureResponse("Company not found");
            }

            if (dto.Name != null) company.Name = dto.Name;
            if (dto.Website != null) company.Website = dto.Website;
            if (dto.Industry != null) company.Industry = dto.Industry;
            if (dto.Size.HasValue) company.Size = dto.Size;
            if (dto.Description != null) company.Description = dto.Description;
            if (dto.Location != null) company.Location = dto.Location;

            await _context.SaveChangesAsync();

            return ApiResponse<CompanyDto>.SuccessResponse(MapToCompanyDto(company), "Company updated successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating company");
            return ApiResponse<CompanyDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse> UpdateCompanyLogoAsync(long companyId, Stream imageStream, string fileName)
    {
        try
        {
            var company = await _context.Companies.FindAsync(companyId);
            if (company == null)
            {
                return ApiResponse.FailureResponse("Company not found");
            }

            // Delete old logo if exists
            if (!string.IsNullOrEmpty(company.LogoPath))
            {
                await _fileStorage.DeleteFileAsync(company.LogoPath);
            }

            // Save new logo
            var logoPath = await _fileStorage.SaveFileAsync(imageStream, fileName, "company-logos");
            company.LogoPath = logoPath;

            await _context.SaveChangesAsync();

            return ApiResponse.SuccessResponse("Company logo updated successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating company logo");
            return ApiResponse.FailureResponse("An error occurred");
        }
    }

    #region Helper Methods

    private static RecruiterProfileDto MapToProfileDto(Recruiter recruiter)
    {
        return new RecruiterProfileDto
        {
            Id = recruiter.Id,
            UserId = recruiter.UserId,
            Email = recruiter.User.Email,
            FullName = recruiter.FullName,
            Phone = recruiter.Phone,
            Position = recruiter.Position,
            CreatedAt = recruiter.CreatedAt,
            Company = recruiter.Company != null ? new CompanyBasicDto
            {
                Id = recruiter.Company.Id,
                Name = recruiter.Company.Name,
                Website = recruiter.Company.Website,
                Industry = recruiter.Company.Industry,
                LogoPath = recruiter.Company.LogoPath
            } : null
        };
    }

    private static CompanyDto MapToCompanyDto(Company company)
    {
        return new CompanyDto
        {
            Id = company.Id,
            Name = company.Name,
            Website = company.Website,
            Industry = company.Industry,
            Size = company.Size,
            LogoPath = company.LogoPath,
            Description = company.Description,
            Location = company.Location,
            CreatedAt = company.CreatedAt,
            TotalJobs = company.Jobs.Count,
            ActiveJobs = company.Jobs.Count(j => j.IsActive)
        };
    }

    #endregion
}

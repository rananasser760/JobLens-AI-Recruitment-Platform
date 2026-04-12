using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using GP_Backend.Data;
using GP_Backend.Models.DTOs.Candidate;
using GP_Backend.Models.DTOs.Common;
using GP_Backend.Models.Entities;
using GP_Backend.Models.Enums;
using GP_Backend.Services.Interfaces;
using GP_Backend.Services.AI;

namespace GP_Backend.Services.Implementations;

public class CandidateService : ICandidateService
{
    private readonly AppDbContext _context;
    private readonly IFileStorageService _fileStorage;
    private readonly IAiService _aiBackend;
    private readonly IAuditService _auditService;
    private readonly ILogger<CandidateService> _logger;

    public CandidateService(
        AppDbContext context,
        IFileStorageService fileStorage,
        IAiService aiBackend,
        IAuditService auditService,
        ILogger<CandidateService> logger)
    {
        _context = context;
        _fileStorage = fileStorage;
        _aiBackend = aiBackend;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<ApiResponse<CandidateProfileDto>> GetProfileAsync(long userId)
    {
        try
        {
            var candidate = await _context.Candidates
                .Include(c => c.User)
                .Include(c => c.Skills)
                .Include(c => c.Resumes)
                .FirstOrDefaultAsync(c => c.UserId == userId);

            if (candidate == null)
            {
                return ApiResponse<CandidateProfileDto>.FailureResponse("Candidate profile not found");
            }

            return ApiResponse<CandidateProfileDto>.SuccessResponse(MapToProfileDto(candidate));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting candidate profile");
            return ApiResponse<CandidateProfileDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<CandidateDashboardDto>> GetDashboardAsync(long userId)
    {
        try
        {
            var candidate = await _context.Candidates
                .Include(c => c.Resumes)
                .FirstOrDefaultAsync(c => c.UserId == userId);

            if (candidate == null)
            {
                return ApiResponse<CandidateDashboardDto>.FailureResponse("Candidate profile not found");
            }

            var applications = await _context.Applications
                .Where(a => a.CandidateId == candidate.Id)
                .Select(a => new
                {
                    a.Id,
                    a.JobId,
                    a.AppliedAt,
                    a.Status,
                    JobTitle = a.Job.Title,
                    CompanyName = a.Job.Company != null ? a.Job.Company.Name : null,
                    AtsScore = a.Resume != null ? a.Resume.AtsScore : null
                })
                .ToListAsync();

            var interviews = await _context.InterviewSessions
                .Where(i => i.Application.CandidateId == candidate.Id)
                .Select(i => new
                {
                    i.Id,
                    i.ApplicationId,
                    JobTitle = i.Application.Job.Title,
                    i.InterviewTitle,
                    i.ScheduledAt,
                    i.Status,
                    i.CheatingDetected,
                    i.OverallScore
                })
                .ToListAsync();

            var activeStatuses = new[]
            {
                ApplicationStatus.Pending,
                ApplicationStatus.UnderReview,
                ApplicationStatus.Shortlisted,
                ApplicationStatus.InterviewScheduled,
                ApplicationStatus.InterviewCompleted
            };

            var totalApplications = applications.Count;
            var activeApplications = applications.Count(a => activeStatuses.Contains(a.Status));

            var interviewsCompleted = interviews.Count(i =>
                string.Equals(i.Status, "Completed", StringComparison.OrdinalIgnoreCase));

            var interviewsScheduled = interviews.Count(i =>
                !string.Equals(i.Status, "Completed", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(i.Status, "Cancelled", StringComparison.OrdinalIgnoreCase));

            var dashboard = new CandidateDashboardDto
            {
                TotalApplications = totalApplications,
                ActiveApplications = activeApplications,
                InterviewsCompleted = interviewsCompleted,
                InterviewsScheduled = interviewsScheduled,
                HighestAtsScore = candidate.Resumes.Count == 0
                    ? 0
                    : candidate.Resumes.Max(r => r.AtsScore ?? 0),
                RecentApplications = applications
                    .OrderByDescending(a => a.AppliedAt)
                    .Take(5)
                    .Select(a => new CandidateRecentApplicationDto
                    {
                        ApplicationId = a.Id,
                        JobId = a.JobId,
                        JobTitle = a.JobTitle,
                        CompanyName = a.CompanyName,
                        AppliedAt = a.AppliedAt,
                        Status = a.Status.ToString(),
                        AtsScore = a.AtsScore
                    })
                    .ToList(),
                UpcomingInterviews = interviews
                    .Where(i =>
                        !string.Equals(i.Status, "Completed", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(i.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(i => i.ScheduledAt ?? DateTime.MaxValue)
                    .Take(5)
                    .Select(i => new CandidateUpcomingInterviewDto
                    {
                        SessionId = i.Id,
                        ApplicationId = i.ApplicationId,
                        JobTitle = i.JobTitle,
                        InterviewTitle = i.InterviewTitle,
                        ScheduledAt = i.ScheduledAt,
                        Status = i.Status ?? "Unknown",
                        CheatingDetected = i.CheatingDetected,
                        OverallScore = i.OverallScore
                    })
                    .ToList()
            };

            return ApiResponse<CandidateDashboardDto>.SuccessResponse(dashboard);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting candidate dashboard");
            return ApiResponse<CandidateDashboardDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<CandidateProfileDto>> GetProfileByIdAsync(long candidateId)
    {
        try
        {
            var candidate = await _context.Candidates
                .Include(c => c.User)
                .Include(c => c.Skills)
                .Include(c => c.Resumes)
                .FirstOrDefaultAsync(c => c.Id == candidateId);

            if (candidate == null)
            {
                return ApiResponse<CandidateProfileDto>.FailureResponse("Candidate not found");
            }

            return ApiResponse<CandidateProfileDto>.SuccessResponse(MapToProfileDto(candidate));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting candidate profile by ID");
            return ApiResponse<CandidateProfileDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<CandidateProfileDto>> UpdateProfileAsync(long userId, UpdateCandidateProfileDto dto)
    {
        try
        {
            var candidate = await _context.Candidates
                .Include(c => c.User)
                .Include(c => c.Skills)
                .Include(c => c.Resumes)
                .FirstOrDefaultAsync(c => c.UserId == userId);

            if (candidate == null)
            {
                return ApiResponse<CandidateProfileDto>.FailureResponse("Candidate profile not found");
            }

            // Update fields
            if (dto.FullName != null) candidate.FullName = dto.FullName;
            if (dto.Phone != null) candidate.Phone = dto.Phone;
            if (dto.Location != null) candidate.Location = dto.Location;
            if (dto.CurrentTitle != null) candidate.CurrentTitle = dto.CurrentTitle;
            if (dto.Summary != null) candidate.Summary = dto.Summary;
            if (dto.LinkedInUrl != null) candidate.LinkedInUrl = dto.LinkedInUrl;
            if (dto.PortfolioUrl != null) candidate.PortfolioUrl = dto.PortfolioUrl;
            if (dto.YearsOfExperience.HasValue) candidate.YearsOfExperience = dto.YearsOfExperience;

            candidate.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            var embeddingPayload = BuildProfileData(candidate);
            var embeddingResult = await _aiBackend.UpdateCandidateEmbeddingAsync(candidate.Id, embeddingPayload);
            if (!embeddingResult.Success)
            {
                _logger.LogWarning(
                    "Candidate embedding update failed for candidate {CandidateId}: {Message}",
                    candidate.Id,
                    embeddingResult.Message);
            }

            await _auditService.LogAsync(userId, "UpdateProfile", "Candidate", candidate.Id);

            return ApiResponse<CandidateProfileDto>.SuccessResponse(MapToProfileDto(candidate), "Profile updated successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating candidate profile");
            return ApiResponse<CandidateProfileDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse> UpdateProfileImageAsync(long userId, Stream imageStream, string fileName)
    {
        try
        {
            var candidate = await _context.Candidates.FirstOrDefaultAsync(c => c.UserId == userId);
            if (candidate == null)
            {
                return ApiResponse.FailureResponse("Candidate profile not found");
            }

            // Delete old image if exists
            if (!string.IsNullOrEmpty(candidate.ProfileImagePath))
            {
                await _fileStorage.DeleteFileAsync(candidate.ProfileImagePath);
            }

            // Save new image
            var imagePath = await _fileStorage.SaveFileAsync(imageStream, fileName, "profile-images");
            candidate.ProfileImagePath = imagePath;
            candidate.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return ApiResponse.SuccessResponse("Profile image updated successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating profile image");
            return ApiResponse.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<List<CandidateSkillDto>>> GetSkillsAsync(long candidateId)
    {
        try
        {
            var skills = await _context.CandidateSkills
                .Where(s => s.CandidateId == candidateId)
                .Select(s => new CandidateSkillDto
                {
                    Id = s.Id,
                    SkillName = s.SkillName,
                    ExperienceYears = s.ExperienceYears,
                    SkillConfidence = s.SkillConfidence,
                    ProficiencyLevel = s.ProficiencyLevel,
                    IsVerified = s.IsVerified
                })
                .ToListAsync();

            return ApiResponse<List<CandidateSkillDto>>.SuccessResponse(skills);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting candidate skills");
            return ApiResponse<List<CandidateSkillDto>>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<CandidateSkillDto>> AddSkillAsync(long userId, AddSkillDto dto)
    {
        try
        {
            var candidate = await _context.Candidates.FirstOrDefaultAsync(c => c.UserId == userId);
            if (candidate == null)
            {
                return ApiResponse<CandidateSkillDto>.FailureResponse("Candidate profile not found");
            }

            // Check if skill already exists
            var existingSkill = await _context.CandidateSkills
                .FirstOrDefaultAsync(s => s.CandidateId == candidate.Id && s.SkillName.ToLower() == dto.SkillName.ToLower());

            if (existingSkill != null)
            {
                return ApiResponse<CandidateSkillDto>.FailureResponse("Skill already exists");
            }

            var skill = new CandidateSkill
            {
                CandidateId = candidate.Id,
                SkillName = dto.SkillName,
                ExperienceYears = dto.ExperienceYears,
                ProficiencyLevel = dto.ProficiencyLevel,
                IsVerified = false
            };

            _context.CandidateSkills.Add(skill);
            await _context.SaveChangesAsync();

            return ApiResponse<CandidateSkillDto>.SuccessResponse(new CandidateSkillDto
            {
                Id = skill.Id,
                SkillName = skill.SkillName,
                ExperienceYears = skill.ExperienceYears,
                ProficiencyLevel = skill.ProficiencyLevel,
                IsVerified = skill.IsVerified
            }, "Skill added successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding skill");
            return ApiResponse<CandidateSkillDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse> RemoveSkillAsync(long userId, long skillId)
    {
        try
        {
            var candidate = await _context.Candidates.FirstOrDefaultAsync(c => c.UserId == userId);
            if (candidate == null)
            {
                return ApiResponse.FailureResponse("Candidate profile not found");
            }

            var skill = await _context.CandidateSkills
                .FirstOrDefaultAsync(s => s.Id == skillId && s.CandidateId == candidate.Id);

            if (skill == null)
            {
                return ApiResponse.FailureResponse("Skill not found");
            }

            _context.CandidateSkills.Remove(skill);
            await _context.SaveChangesAsync();

            return ApiResponse.SuccessResponse("Skill removed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing skill");
            return ApiResponse.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<PaginatedResponse<CandidateListDto>>> SearchCandidatesAsync(CandidateSearchParams searchParams)
    {
        try
        {
            var query = _context.Candidates
                .Include(c => c.User)
                .Include(c => c.Skills)
                .Where(c => c.User.IsActive)
                .AsQueryable();

            // Apply filters
            if (!string.IsNullOrEmpty(searchParams.Keyword))
            {
                var keyword = searchParams.Keyword.ToLower();
                query = query.Where(c =>
                    (c.FullName != null && c.FullName.ToLower().Contains(keyword)) ||
                    (c.CurrentTitle != null && c.CurrentTitle.ToLower().Contains(keyword)) ||
                    (c.Summary != null && c.Summary.ToLower().Contains(keyword)));
            }

            if (!string.IsNullOrEmpty(searchParams.Location))
            {
                query = query.Where(c => c.Location != null && c.Location.ToLower().Contains(searchParams.Location.ToLower()));
            }

            if (!string.IsNullOrEmpty(searchParams.Skills))
            {
                var skillList = searchParams.Skills.Split(',').Select(s => s.Trim().ToLower()).ToList();
                query = query.Where(c => c.Skills.Any(s => skillList.Contains(s.SkillName.ToLower())));
            }

            if (searchParams.MinExperience.HasValue)
            {
                query = query.Where(c => c.YearsOfExperience >= searchParams.MinExperience);
            }

            if (searchParams.MaxExperience.HasValue)
            {
                query = query.Where(c => c.YearsOfExperience <= searchParams.MaxExperience);
            }

            // Get total count
            var totalCount = await query.CountAsync();

            // Apply sorting
            query = searchParams.SortBy?.ToLower() switch
            {
                "name" => searchParams.SortDescending ? query.OrderByDescending(c => c.FullName) : query.OrderBy(c => c.FullName),
                "experience" => searchParams.SortDescending ? query.OrderByDescending(c => c.YearsOfExperience) : query.OrderBy(c => c.YearsOfExperience),
                _ => query.OrderByDescending(c => c.UpdatedAt)
            };

            // Apply pagination
            var candidates = await query
                .Skip((searchParams.PageNumber - 1) * searchParams.PageSize)
                .Take(searchParams.PageSize)
                .Select(c => new CandidateListDto
                {
                    Id = c.Id,
                    FullName = c.FullName,
                    Email = c.User.Email,
                    CurrentTitle = c.CurrentTitle,
                    Location = c.Location,
                    YearsOfExperience = c.YearsOfExperience,
                    ProfileImagePath = c.ProfileImagePath,
                    TopSkills = c.Skills.Take(5).Select(s => s.SkillName).ToList()
                })
                .ToListAsync();

            return ApiResponse<PaginatedResponse<CandidateListDto>>.SuccessResponse(new PaginatedResponse<CandidateListDto>
            {
                Items = candidates,
                TotalCount = totalCount,
                PageNumber = searchParams.PageNumber,
                PageSize = searchParams.PageSize
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching candidates");
            return ApiResponse<PaginatedResponse<CandidateListDto>>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse> FillProfileFromResumeAsync(long userId, long resumeId)
    {
        try
        {
            var candidate = await _context.Candidates
                .Include(c => c.Skills)
                .FirstOrDefaultAsync(c => c.UserId == userId);

            if (candidate == null)
            {
                return ApiResponse.FailureResponse("Candidate profile not found");
            }

            var resume = await _context.Resumes
                .Include(r => r.ParsingResult)
                .FirstOrDefaultAsync(r => r.Id == resumeId && r.CandidateId == candidate.Id);

            if (resume == null)
            {
                return ApiResponse.FailureResponse("Resume not found");
            }

            if (resume.ParsingResult == null)
            {
                return ApiResponse.FailureResponse("Resume has not been parsed yet");
            }

            var parsingResult = resume.ParsingResult;

            // Update profile with parsed data
            if (!string.IsNullOrEmpty(parsingResult.ExtractedName) && string.IsNullOrEmpty(candidate.FullName))
            {
                candidate.FullName = parsingResult.ExtractedName;
            }

            if (!string.IsNullOrEmpty(parsingResult.ExtractedPhone) && string.IsNullOrEmpty(candidate.Phone))
            {
                candidate.Phone = parsingResult.ExtractedPhone;
            }

            if (!string.IsNullOrEmpty(parsingResult.Summary) && string.IsNullOrEmpty(candidate.Summary))
            {
                candidate.Summary = parsingResult.Summary;
            }

            // Add skills from parsed data
            if (!string.IsNullOrEmpty(parsingResult.ExtractedSkills))
            {
                var parsedSkills = System.Text.Json.JsonSerializer.Deserialize<List<string>>(parsingResult.ExtractedSkills);
                if (parsedSkills != null)
                {
                    var existingSkills = candidate.Skills.Select(s => s.SkillName.ToLower()).ToHashSet();
                    foreach (var skillName in parsedSkills.Where(s => !existingSkills.Contains(s.ToLower())))
                    {
                        _context.CandidateSkills.Add(new CandidateSkill
                        {
                            CandidateId = candidate.Id,
                            SkillName = skillName,
                            SkillConfidence = (int)((parsingResult.Confidence ?? 0f) * 100)
                        });
                    }
                }
            }

            candidate.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var embeddingPayload = BuildProfileData(candidate);
            var embeddingResult = await _aiBackend.UpdateCandidateEmbeddingAsync(candidate.Id, embeddingPayload);
            if (!embeddingResult.Success)
            {
                _logger.LogWarning(
                    "Candidate embedding update failed after fill-from-resume for candidate {CandidateId}: {Message}",
                    candidate.Id,
                    embeddingResult.Message);
            }

            await _auditService.LogAsync(userId, "FillProfileFromResume", "Candidate", candidate.Id);

            return ApiResponse.SuccessResponse("Profile updated from resume");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error filling profile from resume");
            return ApiResponse.FailureResponse("An error occurred");
        }
    }

    #region Helper Methods

    private static string BuildProfileData(Candidate candidate)
    {
        var payload = new Dictionary<string, object?>
        {
            ["full_name"] = candidate.FullName,
            ["current_title"] = candidate.CurrentTitle,
            ["summary"] = candidate.Summary,
            ["skills"] = candidate.Skills
                .Select(s => s.SkillName)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ["experience_years"] = candidate.YearsOfExperience,
            ["location"] = candidate.Location,
            ["linkedin_url"] = candidate.LinkedInUrl,
            ["portfolio_url"] = candidate.PortfolioUrl
        };

        return JsonSerializer.Serialize(payload);
    }

    private static CandidateProfileDto MapToProfileDto(Candidate candidate)
    {
        return new CandidateProfileDto
        {
            Id = candidate.Id,
            UserId = candidate.UserId,
            FullName = candidate.FullName,
            Email = candidate.User.Email,
            Phone = candidate.Phone,
            Location = candidate.Location,
            CurrentTitle = candidate.CurrentTitle,
            Summary = candidate.Summary,
            LinkedInUrl = candidate.LinkedInUrl,
            PortfolioUrl = candidate.PortfolioUrl,
            ProfileImagePath = candidate.ProfileImagePath,
            YearsOfExperience = candidate.YearsOfExperience,
            CreatedAt = candidate.CreatedAt,
            UpdatedAt = candidate.UpdatedAt,
            Skills = candidate.Skills.Select(s => new CandidateSkillDto
            {
                Id = s.Id,
                SkillName = s.SkillName,
                ExperienceYears = s.ExperienceYears,
                SkillConfidence = s.SkillConfidence,
                ProficiencyLevel = s.ProficiencyLevel,
                IsVerified = s.IsVerified
            }).ToList(),
            Resumes = candidate.Resumes.Select(r => new ResumeBasicDto
            {
                Id = r.Id,
                FileName = r.FileName,
                FileType = r.FileType,
                IsParsed = r.IsParsed,
                AtsScore = r.AtsScore,
                IsDefault = r.IsDefault,
                UploadedAt = r.UploadedAt
            }).ToList()
        };
    }

    #endregion
}

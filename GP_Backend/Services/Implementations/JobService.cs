using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using GP_Backend.Data;
using GP_Backend.Models.DTOs.Common;
using GP_Backend.Models.DTOs.Job;
using GP_Backend.Models.Entities;
using GP_Backend.Models.Enums;
using GP_Backend.Services.Interfaces;
using GP_Backend.Services.AI;

namespace GP_Backend.Services.Implementations;

public class JobService : IJobService
{
    private readonly AppDbContext _context;
    private readonly IAiService _aiBackend;
    private readonly IAuditService _auditService;
    private readonly ILogger<JobService> _logger;

    public JobService(
        AppDbContext context,
        IAiService aiBackend,
        IAuditService auditService,
        ILogger<JobService> logger)
    {
        _context = context;
        _aiBackend = aiBackend;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<ApiResponse<JobDto>> GetJobAsync(long jobId)
    {
        try
        {
            var job = await _context.Jobs
                .Include(j => j.Company)
                .Include(j => j.RequiredSkills)
                .Include(j => j.Applications)
                .FirstOrDefaultAsync(j => j.Id == jobId);

            if (job == null)
            {
                return ApiResponse<JobDto>.FailureResponse("Job not found");
            }

            return ApiResponse<JobDto>.SuccessResponse(MapToJobDto(job));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting job");
            return ApiResponse<JobDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<PaginatedResponse<JobListDto>>> SearchJobsAsync(JobSearchParams searchParams)
    {
        try
        {
            var query = _context.Jobs
                .Include(j => j.Company)
                .Include(j => j.RequiredSkills)
                .AsQueryable();

            // Apply filters
            if (searchParams.IsActive.HasValue)
            {
                query = query.Where(j => j.IsActive == searchParams.IsActive.Value);
            }

            if (!string.IsNullOrEmpty(searchParams.Keyword))
            {
                var keyword = searchParams.Keyword.ToLower();
                query = query.Where(j =>
                    j.Title.ToLower().Contains(keyword) ||
                    j.Description.ToLower().Contains(keyword) ||
                    (j.Company != null && j.Company.Name.ToLower().Contains(keyword)));
            }

            if (!string.IsNullOrEmpty(searchParams.Location))
            {
                query = query.Where(j => j.Location != null && j.Location.ToLower().Contains(searchParams.Location.ToLower()));
            }

            if (!string.IsNullOrEmpty(searchParams.Skills))
            {
                var skillList = searchParams.Skills.Split(',').Select(s => s.Trim().ToLower()).ToList();
                query = query.Where(j => j.RequiredSkills.Any(s => skillList.Contains(s.SkillName.ToLower())));
            }

            if (searchParams.EmploymentType.HasValue)
            {
                query = query.Where(j => j.EmploymentType == searchParams.EmploymentType.Value);
            }

            if (!string.IsNullOrEmpty(searchParams.ExperienceLevel))
            {
                query = query.Where(j => j.ExperienceLevel == searchParams.ExperienceLevel);
            }

            if (searchParams.MinSalary.HasValue)
            {
                query = query.Where(j => j.SalaryMin >= searchParams.MinSalary.Value);
            }

            if (searchParams.MaxSalary.HasValue)
            {
                query = query.Where(j => j.SalaryMax <= searchParams.MaxSalary.Value);
            }

            if (searchParams.Source.HasValue)
            {
                query = query.Where(j => j.Source == searchParams.Source.Value);
            }

            if (searchParams.CompanyId.HasValue)
            {
                query = query.Where(j => j.CompanyId == searchParams.CompanyId.Value);
            }

            var totalCount = await query.CountAsync();

            // Apply sorting
            query = searchParams.SortBy?.ToLower() switch
            {
                "title" => searchParams.SortDescending ? query.OrderByDescending(j => j.Title) : query.OrderBy(j => j.Title),
                "salary" => searchParams.SortDescending ? query.OrderByDescending(j => j.SalaryMax) : query.OrderBy(j => j.SalaryMax),
                "company" => searchParams.SortDescending ? query.OrderByDescending(j => j.Company!.Name) : query.OrderBy(j => j.Company!.Name),
                _ => query.OrderByDescending(j => j.PostedAt)
            };

            var jobs = await query
                .Skip((searchParams.PageNumber - 1) * searchParams.PageSize)
                .Take(searchParams.PageSize)
                .Select(j => new JobListDto
                {
                    Id = j.Id,
                    Title = j.Title,
                    Location = j.Location,
                    EmploymentType = j.EmploymentType.ToString(),
                    SalaryRange = j.SalaryRange,
                    PostedAt = j.PostedAt,
                    CompanyName = j.Company != null ? j.Company.Name : null,
                    CompanyLogo = j.Company != null ? j.Company.LogoPath : null,
                    Source = j.Source.ToString(),
                    TopSkills = j.RequiredSkills.Take(5).Select(s => s.SkillName).ToList()
                })
                .ToListAsync();

            return ApiResponse<PaginatedResponse<JobListDto>>.SuccessResponse(new PaginatedResponse<JobListDto>
            {
                Items = jobs,
                TotalCount = totalCount,
                PageNumber = searchParams.PageNumber,
                PageSize = searchParams.PageSize
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching jobs");
            return ApiResponse<PaginatedResponse<JobListDto>>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<JobDto>> CreateJobAsync(long recruiterId, CreateJobDto dto)
    {
        try
        {
            var recruiter = await _context.Recruiters.FindAsync(recruiterId);
            if (recruiter == null)
            {
                return ApiResponse<JobDto>.FailureResponse("Recruiter not found");
            }

            var job = new Job
            {
                RecruiterId = recruiterId,
                CompanyId = recruiter.CompanyId,
                Title = dto.Title,
                Description = dto.Description,
                Requirements = dto.Requirements,
                Responsibilities = dto.Responsibilities,
                Location = dto.Location,
                EmploymentType = dto.EmploymentType,
                SalaryRange = dto.SalaryRange,
                SalaryMin = dto.SalaryMin,
                SalaryMax = dto.SalaryMax,
                Currency = dto.Currency,
                ExperienceLevel = dto.ExperienceLevel,
                PostedAt = DateTime.UtcNow,
                ExpiresAt = dto.ExpiresAt,
                IsActive = true,
                Source = JobSource.Internal
            };

            _context.Jobs.Add(job);
            await _context.SaveChangesAsync();

            // Add required skills
            foreach (var skillDto in dto.RequiredSkills)
            {
                _context.JobSkills.Add(new JobSkill
                {
                    JobId = job.Id,
                    SkillName = skillDto.SkillName,
                    Importance = skillDto.Importance,
                    IsRequired = skillDto.IsRequired
                });
            }
            await _context.SaveChangesAsync();

            string? companyName = null;
            if (job.CompanyId.HasValue)
            {
                companyName = await _context.Companies
                    .Where(c => c.Id == job.CompanyId.Value)
                    .Select(c => c.Name)
                    .FirstOrDefaultAsync();
            }

            var embeddingPayload = BuildJobData(job, dto.RequiredSkills.Select(s => s.SkillName), companyName);
            var embeddingResult = await _aiBackend.CreateJobEmbeddingAsync(job.Id, embeddingPayload);
            if (!embeddingResult.Success)
            {
                _logger.LogWarning(
                    "Job embedding creation failed for job {JobId}: {Message}",
                    job.Id,
                    embeddingResult.Message);
            }

            await _auditService.LogAsync(recruiter.UserId, "CreateJob", "Job", job.Id);

            // Reload with includes
            var createdJob = await _context.Jobs
                .Include(j => j.Company)
                .Include(j => j.RequiredSkills)
                .FirstAsync(j => j.Id == job.Id);

            return ApiResponse<JobDto>.SuccessResponse(MapToJobDto(createdJob), "Job created successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating job");
            return ApiResponse<JobDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<JobDto>> UpdateJobAsync(long jobId, long recruiterId, UpdateJobDto dto)
    {
        try
        {
            var job = await _context.Jobs
                .Include(j => j.Company)
                .Include(j => j.RequiredSkills)
                .FirstOrDefaultAsync(j => j.Id == jobId);

            if (job == null)
            {
                return ApiResponse<JobDto>.FailureResponse("Job not found");
            }

            if (job.RecruiterId != recruiterId)
            {
                return ApiResponse<JobDto>.FailureResponse("You don't have permission to update this job");
            }

            // Update fields
            if (dto.Title != null) job.Title = dto.Title;
            if (dto.Description != null) job.Description = dto.Description;
            if (dto.Requirements != null) job.Requirements = dto.Requirements;
            if (dto.Responsibilities != null) job.Responsibilities = dto.Responsibilities;
            if (dto.Location != null) job.Location = dto.Location;
            if (dto.EmploymentType.HasValue) job.EmploymentType = dto.EmploymentType.Value;
            if (dto.SalaryRange != null) job.SalaryRange = dto.SalaryRange;
            if (dto.SalaryMin.HasValue) job.SalaryMin = dto.SalaryMin;
            if (dto.SalaryMax.HasValue) job.SalaryMax = dto.SalaryMax;
            if (dto.ExperienceLevel != null) job.ExperienceLevel = dto.ExperienceLevel;
            if (dto.ExpiresAt.HasValue) job.ExpiresAt = dto.ExpiresAt;
            if (dto.IsActive.HasValue) job.IsActive = dto.IsActive.Value;

            await _context.SaveChangesAsync();

            var embeddingPayload = BuildJobData(job, job.RequiredSkills.Select(s => s.SkillName), job.Company?.Name);
            var embeddingResult = await _aiBackend.UpdateJobEmbeddingAsync(job.Id, embeddingPayload);
            if (!embeddingResult.Success)
            {
                _logger.LogWarning(
                    "Job embedding update failed for job {JobId}: {Message}",
                    job.Id,
                    embeddingResult.Message);
            }

            var recruiter = await _context.Recruiters.FindAsync(recruiterId);
            await _auditService.LogAsync(recruiter?.UserId, "UpdateJob", "Job", job.Id);

            return ApiResponse<JobDto>.SuccessResponse(MapToJobDto(job), "Job updated successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating job");
            return ApiResponse<JobDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse> DeleteJobAsync(long jobId, long recruiterId)
    {
        try
        {
            var job = await _context.Jobs.FindAsync(jobId);
            if (job == null)
            {
                return ApiResponse.FailureResponse("Job not found");
            }

            if (job.RecruiterId != recruiterId)
            {
                return ApiResponse.FailureResponse("You don't have permission to delete this job");
            }

            // Soft delete - just deactivate
            job.IsActive = false;
            await _context.SaveChangesAsync();

            var embeddingResult = await _aiBackend.DeleteJobEmbeddingAsync(job.Id);
            if (!embeddingResult.Success)
            {
                _logger.LogWarning(
                    "Job embedding deletion failed for job {JobId}: {Message}",
                    job.Id,
                    embeddingResult.Message);
            }

            var recruiter = await _context.Recruiters.FindAsync(recruiterId);
            await _auditService.LogAsync(recruiter?.UserId, "DeleteJob", "Job", job.Id);

            return ApiResponse.SuccessResponse("Job deleted successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting job");
            return ApiResponse.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse> ToggleJobStatusAsync(long jobId, long recruiterId)
    {
        try
        {
            var job = await _context.Jobs.FindAsync(jobId);
            if (job == null)
            {
                return ApiResponse.FailureResponse("Job not found");
            }

            if (job.RecruiterId != recruiterId)
            {
                return ApiResponse.FailureResponse("You don't have permission to modify this job");
            }

            job.IsActive = !job.IsActive;
            await _context.SaveChangesAsync();

            return ApiResponse.SuccessResponse($"Job {(job.IsActive ? "activated" : "deactivated")} successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling job status");
            return ApiResponse.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse> AddJobSkillAsync(long jobId, CreateJobSkillDto dto)
    {
        try
        {
            var job = await _context.Jobs.FindAsync(jobId);
            if (job == null)
            {
                return ApiResponse.FailureResponse("Job not found");
            }

            var existingSkill = await _context.JobSkills
                .FirstOrDefaultAsync(s => s.JobId == jobId && s.SkillName.ToLower() == dto.SkillName.ToLower());

            if (existingSkill != null)
            {
                return ApiResponse.FailureResponse("Skill already exists for this job");
            }

            _context.JobSkills.Add(new JobSkill
            {
                JobId = jobId,
                SkillName = dto.SkillName,
                Importance = dto.Importance,
                IsRequired = dto.IsRequired
            });

            await _context.SaveChangesAsync();

            return ApiResponse.SuccessResponse("Skill added successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding job skill");
            return ApiResponse.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse> RemoveJobSkillAsync(long jobId, long skillId)
    {
        try
        {
            var skill = await _context.JobSkills.FirstOrDefaultAsync(s => s.Id == skillId && s.JobId == jobId);
            if (skill == null)
            {
                return ApiResponse.FailureResponse("Skill not found");
            }

            _context.JobSkills.Remove(skill);
            await _context.SaveChangesAsync();

            return ApiResponse.SuccessResponse("Skill removed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing job skill");
            return ApiResponse.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<List<JobRecommendationDto>>> GetRecommendedJobsAsync(long candidateId, int limit = 10)
    {
        try
        {
            var aiRecommendations = await _aiBackend.GetJobRecommendationsForCandidateAsync(candidateId, limit);
            if (aiRecommendations.Success
                && aiRecommendations.Data != null
                && aiRecommendations.Data.Any())
            {
                return aiRecommendations;
            }

            if (!aiRecommendations.Success)
            {
                _logger.LogWarning(
                    "AI job recommendations failed for candidate {CandidateId}: {Message}. Falling back to skill matching.",
                    candidateId,
                    aiRecommendations.Message);
            }

            // For now, return jobs matching candidate skills
            var candidate = await _context.Candidates
                .Include(c => c.Skills)
                .FirstOrDefaultAsync(c => c.Id == candidateId);

            if (candidate == null)
            {
                return ApiResponse<List<JobRecommendationDto>>.FailureResponse("Candidate not found");
            }

            var candidateSkills = candidate.Skills.Select(s => s.SkillName.ToLower()).ToList();

            var recommendedJobs = await _context.Jobs
                .Include(j => j.Company)
                .Include(j => j.RequiredSkills)
                .Where(j => j.IsActive)
                .Select(j => new
                {
                    Job = j,
                    MatchingSkills = j.RequiredSkills.Where(s => candidateSkills.Contains(s.SkillName.ToLower())).ToList()
                })
                .Where(x => x.MatchingSkills.Any())
                .OrderByDescending(x => x.MatchingSkills.Count)
                .Take(limit)
                .ToListAsync();

            var recommendations = recommendedJobs.Select(x => new JobRecommendationDto
            {
                JobId = x.Job.Id,
                Title = x.Job.Title,
                CompanyName = x.Job.Company?.Name,
                Location = x.Job.Location,
                MatchScore = (float)x.MatchingSkills.Count / Math.Max(x.Job.RequiredSkills.Count, 1),
                MatchingSkills = x.MatchingSkills.Select(s => s.SkillName).ToList(),
                MatchReason = $"Matches {x.MatchingSkills.Count} of your skills"
            }).ToList();

            return ApiResponse<List<JobRecommendationDto>>.SuccessResponse(recommendations);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting job recommendations");
            return ApiResponse<List<JobRecommendationDto>>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<List<JobRecommendationDto>>> MatchJobsFromTextAsync(string resumeText, int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(resumeText))
        {
            return ApiResponse<List<JobRecommendationDto>>.FailureResponse("Resume text is required");
        }

        return await _aiBackend.MatchJobsFromTextAsync(resumeText, limit);
    }

    public async Task<ApiResponse<int>> ImportScrapedJobsAsync(List<ScrapedJobDto> jobs)
    {
        try
        {
            var importedCount = 0;

            foreach (var jobDto in jobs)
            {
                // Check if job already exists
                var exists = await _context.Jobs.AnyAsync(j =>
                    j.ExternalJobId == jobDto.ExternalJobId &&
                    j.ExternalSource == jobDto.ExternalSource);

                if (exists) continue;

                // Try to find or create company
                long? companyId = null;
                if (!string.IsNullOrEmpty(jobDto.CompanyName))
                {
                    var company = await _context.Companies.FirstOrDefaultAsync(c =>
                        c.Name.ToLower() == jobDto.CompanyName.ToLower());

                    if (company == null)
                    {
                        company = new Company { Name = jobDto.CompanyName, CreatedAt = DateTime.UtcNow };
                        _context.Companies.Add(company);
                        await _context.SaveChangesAsync();
                    }

                    companyId = company.Id;
                }

                var job = new Job
                {
                    CompanyId = companyId,
                    Title = jobDto.Title,
                    Description = jobDto.Description,
                    Requirements = jobDto.Requirements,
                    Location = jobDto.Location,
                    SalaryRange = jobDto.SalaryRange,
                    EmploymentType = ParseEmploymentType(jobDto.EmploymentType),
                    PostedAt = jobDto.PostedAt,
                    IsActive = true,
                    Source = JobSource.Scraped,
                    ExternalJobId = jobDto.ExternalJobId,
                    ExternalUrl = jobDto.ExternalUrl,
                    ExternalSource = jobDto.ExternalSource,
                    ScrapedAt = DateTime.UtcNow
                };

                _context.Jobs.Add(job);
                await _context.SaveChangesAsync();

                // Add skills
                if (jobDto.Skills != null)
                {
                    foreach (var skill in jobDto.Skills)
                    {
                        _context.JobSkills.Add(new JobSkill
                        {
                            JobId = job.Id,
                            SkillName = skill,
                            Importance = 5
                        });
                    }
                    await _context.SaveChangesAsync();
                }

                var scrapedEmbeddingPayload = BuildJobData(job, jobDto.Skills, jobDto.CompanyName);
                var embeddingResult = await _aiBackend.CreateJobEmbeddingAsync(job.Id, scrapedEmbeddingPayload);
                if (!embeddingResult.Success)
                {
                    _logger.LogWarning(
                        "Job embedding creation failed for imported scraped job {JobId}: {Message}",
                        job.Id,
                        embeddingResult.Message);
                }

                importedCount++;
            }

            return ApiResponse<int>.SuccessResponse(importedCount, $"{importedCount} jobs imported successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing scraped jobs");
            return ApiResponse<int>.FailureResponse("An error occurred during import");
        }
    }

    public async Task<ApiResponse<List<ScrapedJobDto>>> GetScrapedJobsFromAiAsync(string? keyword = null, string? location = null, int limit = 50)
    {
        try
        {
            return await _aiBackend.GetScrapedJobsAsync(keyword, location, limit);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting scraped jobs from AI backend");
            return ApiResponse<List<ScrapedJobDto>>.FailureResponse("Failed to get scraped jobs from AI backend");
        }
    }

    public async Task<ApiResponse<JsonElement>> GetScrapingStatusAsync()
    {
        try
        {
            return await _aiBackend.GetScrapingStatusAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting scraping status from AI backend");
            return ApiResponse<JsonElement>.FailureResponse("Failed to get scraping status");
        }
    }

    public async Task<ApiResponse<JsonElement>> GetRecruitmentStatusAsync()
    {
        try
        {
            return await _aiBackend.GetRecruitmentStatusAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting recruitment status from AI backend");
            return ApiResponse<JsonElement>.FailureResponse("Failed to get recruitment status");
        }
    }

    public async Task<ApiResponse> TriggerScrapingAsync(int? maxCategories = null)
    {
        try
        {
            return await _aiBackend.TriggerScrapingAsync(maxCategories);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error triggering scraping in AI backend");
            return ApiResponse.FailureResponse("Failed to trigger scraping");
        }
    }

    public async Task<ApiResponse<int>> CleanupExpiredScrapedJobsAsync(int daysOld = 30)
    {
        try
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-daysOld);

            var expiredJobs = await _context.Jobs
                .Where(j => j.Source == JobSource.Scraped && j.ScrapedAt < cutoffDate)
                .ToListAsync();

            foreach (var job in expiredJobs)
            {
                var embeddingResult = await _aiBackend.DeleteJobEmbeddingAsync(job.Id);
                if (!embeddingResult.Success)
                {
                    _logger.LogWarning(
                        "Job embedding deletion failed for expired scraped job {JobId}: {Message}",
                        job.Id,
                        embeddingResult.Message);
                }
            }

            _context.Jobs.RemoveRange(expiredJobs);
            var deletedCount = await _context.SaveChangesAsync();

            return ApiResponse<int>.SuccessResponse(deletedCount, $"{deletedCount} expired jobs cleaned up");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up expired jobs");
            return ApiResponse<int>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<PaginatedResponse<JobListDto>>> GetRecruiterJobsAsync(long recruiterId, JobSearchParams searchParams)
    {
        try
        {
            var query = _context.Jobs
                .Include(j => j.Company)
                .Include(j => j.RequiredSkills)
                .Where(j => j.RecruiterId == recruiterId);

            if (searchParams.IsActive.HasValue)
            {
                query = query.Where(j => j.IsActive == searchParams.IsActive.Value);
            }

            if (!string.IsNullOrEmpty(searchParams.Keyword))
            {
                var keyword = searchParams.Keyword.ToLower();
                query = query.Where(j => j.Title.ToLower().Contains(keyword));
            }

            var totalCount = await query.CountAsync();

            var jobs = await query
                .OrderByDescending(j => j.PostedAt)
                .Skip((searchParams.PageNumber - 1) * searchParams.PageSize)
                .Take(searchParams.PageSize)
                .Select(j => new JobListDto
                {
                    Id = j.Id,
                    Title = j.Title,
                    Location = j.Location,
                    EmploymentType = j.EmploymentType.ToString(),
                    SalaryRange = j.SalaryRange,
                    PostedAt = j.PostedAt,
                    CompanyName = j.Company != null ? j.Company.Name : null,
                    CompanyLogo = j.Company != null ? j.Company.LogoPath : null,
                    Source = j.Source.ToString(),
                    TopSkills = j.RequiredSkills.Take(5).Select(s => s.SkillName).ToList()
                })
                .ToListAsync();

            return ApiResponse<PaginatedResponse<JobListDto>>.SuccessResponse(new PaginatedResponse<JobListDto>
            {
                Items = jobs,
                TotalCount = totalCount,
                PageNumber = searchParams.PageNumber,
                PageSize = searchParams.PageSize
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting recruiter jobs");
            return ApiResponse<PaginatedResponse<JobListDto>>.FailureResponse("An error occurred");
        }
    }

    #region Helper Methods

    private static string BuildJobData(Job job, IEnumerable<string>? skills, string? companyName)
    {
        var normalizedSkills = (skills ?? Enumerable.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var payload = new Dictionary<string, object?>
        {
            ["title"] = job.Title,
            ["description"] = job.Description,
            ["requirements"] = job.Requirements,
            ["responsibilities"] = job.Responsibilities,
            ["skills"] = normalizedSkills,
            ["location"] = job.Location,
            ["experience_level"] = job.ExperienceLevel,
            ["employment_type"] = job.EmploymentType.ToString(),
            ["salary_range"] = job.SalaryRange,
            ["salary_min"] = job.SalaryMin,
            ["salary_max"] = job.SalaryMax,
            ["currency"] = job.Currency,
            ["company"] = companyName,
            ["external_source"] = job.ExternalSource,
            ["external_url"] = job.ExternalUrl
        };

        return JsonSerializer.Serialize(payload);
    }

    private static JobDto MapToJobDto(Job job)
    {
        return new JobDto
        {
            Id = job.Id,
            Title = job.Title,
            Description = job.Description,
            Requirements = job.Requirements,
            Responsibilities = job.Responsibilities,
            Location = job.Location,
            EmploymentType = job.EmploymentType.ToString(),
            SalaryRange = job.SalaryRange,
            SalaryMin = job.SalaryMin,
            SalaryMax = job.SalaryMax,
            Currency = job.Currency,
            ExperienceLevel = job.ExperienceLevel,
            PostedAt = job.PostedAt,
            ExpiresAt = job.ExpiresAt,
            IsActive = job.IsActive,
            Source = job.Source.ToString(),
            ExternalUrl = job.ExternalUrl,
            CompanyName = job.Company?.Name,
            CompanyLogo = job.Company?.LogoPath,
            RequiredSkills = job.RequiredSkills.Select(s => s.SkillName).ToList(),
            ApplicationCount = job.Applications.Count
        };
    }

    private static EmploymentType ParseEmploymentType(string? type)
    {
        if (string.IsNullOrEmpty(type)) return EmploymentType.FullTime;

        return type.ToLower() switch
        {
            "full-time" or "fulltime" or "full time" => EmploymentType.FullTime,
            "part-time" or "parttime" or "part time" => EmploymentType.PartTime,
            "contract" => EmploymentType.Contract,
            "internship" or "intern" => EmploymentType.Internship,
            "freelance" => EmploymentType.Freelance,
            "remote" => EmploymentType.Remote,
            _ => EmploymentType.FullTime
        };
    }

    #endregion
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using GP_Backend.Data;
using GP_Backend.Models.DTOs.Common;
using GP_Backend.Models.DTOs.Resume;
using GP_Backend.Models.Entities;
using GP_Backend.Services.Interfaces;

namespace GP_Backend.Services.Implementations;

public class ResumeService : IResumeService
{
    private readonly AppDbContext _context;
    private readonly IFileStorageService _fileStorage;
    private readonly IAIBackendService _aiBackend;
    private readonly IAuditService _auditService;
    private readonly ILogger<ResumeService> _logger;

    public ResumeService(
        AppDbContext context,
        IFileStorageService fileStorage,
        IAIBackendService aiBackend,
        IAuditService auditService,
        ILogger<ResumeService> logger)
    {
        _context = context;
        _fileStorage = fileStorage;
        _aiBackend = aiBackend;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<ApiResponse<ResumeDto>> GetResumeAsync(long resumeId)
    {
        try
        {
            var resume = await _context.Resumes
                .Include(r => r.ParsingResult)
                .FirstOrDefaultAsync(r => r.Id == resumeId);

            if (resume == null)
            {
                return ApiResponse<ResumeDto>.FailureResponse("Resume not found");
            }

            return ApiResponse<ResumeDto>.SuccessResponse(MapToResumeDto(resume));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting resume");
            return ApiResponse<ResumeDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<List<ResumeDto>>> GetCandidateResumesAsync(long candidateId)
    {
        try
        {
            var resumes = await _context.Resumes
                .Include(r => r.ParsingResult)
                .Where(r => r.CandidateId == candidateId)
                .OrderByDescending(r => r.UploadedAt)
                .ToListAsync();

            return ApiResponse<List<ResumeDto>>.SuccessResponse(resumes.Select(MapToResumeDto).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting candidate resumes");
            return ApiResponse<List<ResumeDto>>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<ResumeDto>> UploadResumeAsync(long candidateId, Stream fileStream, string fileName, UploadResumeDto dto)
    {
        try
        {
            var candidate = await _context.Candidates.FindAsync(candidateId);
            if (candidate == null)
            {
                return ApiResponse<ResumeDto>.FailureResponse("Candidate not found");
            }

            // Validate file type
            var extension = Path.GetExtension(fileName).ToLower();
            var allowedExtensions = new[] { ".pdf", ".doc", ".docx" };
            if (!allowedExtensions.Contains(extension))
            {
                return ApiResponse<ResumeDto>.FailureResponse("Only PDF and Word documents are allowed");
            }

            // Save file
            var filePath = await _fileStorage.SaveFileAsync(fileStream, fileName, "resumes");

            // Get file size
            fileStream.Position = 0;
            var fileSize = fileStream.Length;

            // If this is set as default, unset other defaults
            if (dto.IsDefault)
            {
                var existingDefaults = await _context.Resumes
                    .Where(r => r.CandidateId == candidateId && r.IsDefault)
                    .ToListAsync();

                foreach (var existingDefault in existingDefaults)
                {
                    existingDefault.IsDefault = false;
                }
            }

            var resume = new Resume
            {
                CandidateId = candidateId,
                FilePath = filePath,
                FileName = fileName,
                FileType = extension.TrimStart('.'),
                FileSize = fileSize,
                IsDefault = dto.IsDefault,
                IsParsed = false,
                UploadedAt = DateTime.UtcNow
            };

            _context.Resumes.Add(resume);
            await _context.SaveChangesAsync();

            await _auditService.LogAsync(candidate.UserId, "UploadResume", "Resume", resume.Id);

            // Parse resume if requested
            if (dto.ParseNow)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ParseResumeInternalAsync(resume.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error parsing resume in background");
                    }
                });
            }

            return ApiResponse<ResumeDto>.SuccessResponse(MapToResumeDto(resume), "Resume uploaded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading resume");
            return ApiResponse<ResumeDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse> DeleteResumeAsync(long resumeId, long candidateId)
    {
        try
        {
            var resume = await _context.Resumes.FirstOrDefaultAsync(r =>
                r.Id == resumeId && r.CandidateId == candidateId);

            if (resume == null)
            {
                return ApiResponse.FailureResponse("Resume not found");
            }

            // Check if resume is used in any active application
            var hasActiveApplications = await _context.Applications
                .AnyAsync(a => a.ResumeId == resumeId &&
                    a.Status != Models.Enums.ApplicationStatus.Withdrawn &&
                    a.Status != Models.Enums.ApplicationStatus.Rejected);

            if (hasActiveApplications)
            {
                return ApiResponse.FailureResponse("Cannot delete resume that is used in active applications");
            }

            // Delete file
            await _fileStorage.DeleteFileAsync(resume.FilePath);

            // Delete from database
            _context.Resumes.Remove(resume);
            await _context.SaveChangesAsync();

            var candidate = await _context.Candidates.FindAsync(candidateId);
            await _auditService.LogAsync(candidate?.UserId, "DeleteResume", "Resume", resumeId);

            return ApiResponse.SuccessResponse("Resume deleted successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting resume");
            return ApiResponse.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse> SetDefaultResumeAsync(long resumeId, long candidateId)
    {
        try
        {
            var resume = await _context.Resumes.FirstOrDefaultAsync(r =>
                r.Id == resumeId && r.CandidateId == candidateId);

            if (resume == null)
            {
                return ApiResponse.FailureResponse("Resume not found");
            }

            // Unset other defaults
            var existingDefaults = await _context.Resumes
                .Where(r => r.CandidateId == candidateId && r.IsDefault && r.Id != resumeId)
                .ToListAsync();

            foreach (var existingDefault in existingDefaults)
            {
                existingDefault.IsDefault = false;
            }

            resume.IsDefault = true;
            await _context.SaveChangesAsync();

            return ApiResponse.SuccessResponse("Default resume updated");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting default resume");
            return ApiResponse.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<ResumeParsingResultDto>> ParseResumeAsync(long resumeId)
    {
        try
        {
            var result = await ParseResumeInternalAsync(resumeId);
            if (result == null)
            {
                return ApiResponse<ResumeParsingResultDto>.FailureResponse("Failed to parse resume");
            }

            return ApiResponse<ResumeParsingResultDto>.SuccessResponse(result, "Resume parsed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing resume");
            return ApiResponse<ResumeParsingResultDto>.FailureResponse("An error occurred");
        }
    }

    private async Task<ResumeParsingResultDto?> ParseResumeInternalAsync(long resumeId)
    {
        var resume = await _context.Resumes
            .Include(r => r.ParsingResult)
            .Include(r => r.Candidate)
            .FirstOrDefaultAsync(r => r.Id == resumeId);

        if (resume == null) return null;

        // Get file stream
        var fileStream = await _fileStorage.GetFileAsync(resume.FilePath);
        if (fileStream == null) return null;

        // TODO: Call FastAPI to parse CV
        // var parseResult = await _aiBackend.ParseCvAsync(fileStream, resume.FileName);

        // For now, create a placeholder parsing result
        // This would be replaced with actual FastAPI call
        var parsedResponse = new ParsedCvResponseDto
        {
            FullName = "Parsed Name",
            Email = "parsed@email.com",
            Phone = "+1234567890",
            Summary = "Professional summary extracted from CV",
            Skills = new List<string> { "C#", ".NET", "SQL", "Azure" },
            Experience = new List<ParsedExperienceDto>
            {
                new() { JobTitle = "Software Engineer", Company = "Tech Corp", StartDate = "2020", EndDate = "Present" }
            },
            Education = new List<ParsedEducationDto>
            {
                new() { Degree = "BSc Computer Science", Institution = "University", GraduationYear = "2020" }
            },
            Confidence = 0.85f
        };

        // Delete existing parsing result if any
        if (resume.ParsingResult != null)
        {
            _context.ResumeParsingResults.Remove(resume.ParsingResult);
        }

        // Create parsing result
        var parsingResult = new ResumeParsingResult
        {
            ResumeId = resumeId,
            ParsedJson = JsonSerializer.Serialize(parsedResponse),
            Confidence = parsedResponse.Confidence,
            Summary = parsedResponse.Summary,
            ExtractedName = parsedResponse.FullName,
            ExtractedEmail = parsedResponse.Email,
            ExtractedPhone = parsedResponse.Phone,
            ExtractedSkills = JsonSerializer.Serialize(parsedResponse.Skills),
            ExtractedExperience = JsonSerializer.Serialize(parsedResponse.Experience),
            ExtractedEducation = JsonSerializer.Serialize(parsedResponse.Education),
            CreatedAt = DateTime.UtcNow
        };

        _context.ResumeParsingResults.Add(parsingResult);

        resume.IsParsed = true;
        resume.ResumeText = "Extracted text from resume"; // TODO: Get from FastAPI

        await _context.SaveChangesAsync();

        // Update candidate embedding
        // TODO: Call AI backend to update candidate embedding

        return MapToParsingResultDto(parsingResult);
    }

    public async Task<ApiResponse<AtsScoreDto>> GetAtsScoreAsync(long resumeId, long? jobId = null)
    {
        try
        {
            var resume = await _context.Resumes.FindAsync(resumeId);
            if (resume == null)
            {
                return ApiResponse<AtsScoreDto>.FailureResponse("Resume not found");
            }

            string? jobDescription = null;
            if (jobId.HasValue)
            {
                var job = await _context.Jobs.FindAsync(jobId.Value);
                jobDescription = job?.Description;
            }

            // TODO: Call FastAPI to get ATS score
            // var atsResult = await _aiBackend.GetAtsScoreAsync(resume.ResumeText ?? "", jobDescription);

            // For now, calculate a simple score
            var score = new Random().Next(60, 95);
            var recommendations = new List<string>
            {
                "Add more quantifiable achievements",
                "Include relevant keywords from job description",
                "Improve formatting for ATS compatibility"
            };

            resume.AtsScore = score;
            resume.AtsFriendly = score >= 70;
            resume.AtsRecommendations = JsonSerializer.Serialize(recommendations);
            await _context.SaveChangesAsync();

            return ApiResponse<AtsScoreDto>.SuccessResponse(new AtsScoreDto
            {
                ResumeId = resumeId,
                Score = score,
                IsFriendly = score >= 70,
                Recommendations = recommendations,
                CategoryScores = new Dictionary<string, int>
                {
                    { "Format", new Random().Next(60, 100) },
                    { "Keywords", new Random().Next(60, 100) },
                    { "Experience", new Random().Next(60, 100) },
                    { "Education", new Random().Next(60, 100) }
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting ATS score");
            return ApiResponse<AtsScoreDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<(byte[] content, string contentType, string fileName)?> DownloadResumeAsync(long resumeId)
    {
        try
        {
            var resume = await _context.Resumes.FindAsync(resumeId);
            if (resume == null) return null;

            var fileStream = await _fileStorage.GetFileAsync(resume.FilePath);
            if (fileStream == null) return null;

            using var memoryStream = new MemoryStream();
            await fileStream.CopyToAsync(memoryStream);

            var contentType = _fileStorage.GetContentType(resume.FileName);

            return (memoryStream.ToArray(), contentType, resume.FileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading resume");
            return null;
        }
    }

    #region Helper Methods

    private static ResumeDto MapToResumeDto(Resume resume)
    {
        return new ResumeDto
        {
            Id = resume.Id,
            CandidateId = resume.CandidateId,
            FileName = resume.FileName,
            FileType = resume.FileType,
            FileSize = resume.FileSize,
            ResumeText = resume.ResumeText,
            IsParsed = resume.IsParsed,
            AtsScore = resume.AtsScore,
            AtsFriendly = resume.AtsFriendly,
            AtsRecommendations = resume.AtsRecommendations,
            IsDefault = resume.IsDefault,
            UploadedAt = resume.UploadedAt,
            ParsingResult = resume.ParsingResult != null ? MapToParsingResultDto(resume.ParsingResult) : null
        };
    }

    private static ResumeParsingResultDto MapToParsingResultDto(ResumeParsingResult result)
    {
        var dto = new ResumeParsingResultDto
        {
            Id = result.Id,
            ParsedJson = result.ParsedJson,
            Confidence = result.Confidence,
            Summary = result.Summary,
            ExtractedName = result.ExtractedName,
            ExtractedEmail = result.ExtractedEmail,
            ExtractedPhone = result.ExtractedPhone
        };

        if (!string.IsNullOrEmpty(result.ExtractedSkills))
        {
            try
            {
                dto.ExtractedSkills = JsonSerializer.Deserialize<List<string>>(result.ExtractedSkills) ?? new();
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(result.ExtractedExperience))
        {
            try
            {
                var experiences = JsonSerializer.Deserialize<List<ParsedExperienceDto>>(result.ExtractedExperience);
                dto.ExtractedExperience = experiences?.Select(e => new ExperienceDto
                {
                    Title = e.JobTitle,
                    Company = e.Company,
                    Duration = $"{e.StartDate} - {e.EndDate}",
                    Description = e.Description
                }).ToList() ?? new();
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(result.ExtractedEducation))
        {
            try
            {
                var education = JsonSerializer.Deserialize<List<ParsedEducationDto>>(result.ExtractedEducation);
                dto.ExtractedEducation = education?.Select(e => new EducationDto
                {
                    Degree = e.Degree,
                    Institution = e.Institution,
                    Year = e.GraduationYear,
                    Field = e.FieldOfStudy
                }).ToList() ?? new();
            }
            catch { }
        }

        return dto;
    }

    #endregion
}

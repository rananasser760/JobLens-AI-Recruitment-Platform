using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using GP_Backend.Data;
using GP_Backend.Models.DTOs.Common;
using GP_Backend.Models.DTOs.Resume;
using GP_Backend.Models.Entities;
using GP_Backend.Services.Interfaces;
using GP_Backend.Services.AI;

namespace GP_Backend.Services.Implementations;

public class ResumeService : IResumeService
{
    private readonly AppDbContext _context;
    private readonly IFileStorageService _fileStorage;
    private readonly IAiService _aiBackend;
    private readonly IAuditService _auditService;
    private readonly ILogger<ResumeService> _logger;

    public ResumeService(
        AppDbContext context,
        IFileStorageService fileStorage,
        IAiService aiBackend,
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
        using var fileStream = await _fileStorage.GetFileAsync(resume.FilePath);
        if (fileStream == null) return null;

        var parseResult = await _aiBackend.ParseCvAsync(fileStream, resume.FileName);
        if (!parseResult.Success || parseResult.Data == null)
        {
            _logger.LogWarning(
                "AI parse failed for resume {ResumeId}: {Message}",
                resumeId,
                parseResult.Message);
            return null;
        }

        var parsedResponse = parseResult.Data;

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
        resume.ResumeText = BuildResumeText(parsedResponse);

        await _context.SaveChangesAsync();

        var candidateEmbeddingPayload = JsonSerializer.Serialize(new
        {
            full_name = parsedResponse.FullName ?? resume.Candidate?.FullName,
            summary = parsedResponse.Summary,
            skills = parsedResponse.Skills ?? new List<string>(),
            location = parsedResponse.Location ?? resume.Candidate?.Location,
            resume_text = resume.ResumeText
        });

        var embeddingResult = await _aiBackend.UpdateCandidateEmbeddingAsync(resume.CandidateId, candidateEmbeddingPayload);
        if (!embeddingResult.Success)
        {
            _logger.LogWarning(
                "Candidate embedding update failed after resume parse for candidate {CandidateId}: {Message}",
                resume.CandidateId,
                embeddingResult.Message);
        }

        return MapToParsingResultDto(parsingResult);
    }

    public async Task<ApiResponse<AtsScoreDto>> GetAtsScoreAsync(long resumeId, long? jobId = null)
    {
        try
        {
            var resume = await _context.Resumes
                .Include(r => r.ParsingResult)
                .FirstOrDefaultAsync(r => r.Id == resumeId);

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

            var resumeText = resume.ResumeText;
            if (string.IsNullOrWhiteSpace(resumeText)
                && resume.ParsingResult != null
                && !string.IsNullOrWhiteSpace(resume.ParsingResult.ParsedJson))
            {
                try
                {
                    var parsedCv = JsonSerializer.Deserialize<ParsedCvResponseDto>(resume.ParsingResult.ParsedJson);
                    if (parsedCv != null)
                    {
                        resumeText = BuildResumeText(parsedCv);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to rebuild resume text from parsed JSON for resume {ResumeId}", resumeId);
                }
            }

            if (string.IsNullOrWhiteSpace(resumeText))
            {
                return ApiResponse<AtsScoreDto>.FailureResponse("Resume text is not available. Parse the resume first.");
            }

            var atsResult = await _aiBackend.GetAtsScoreAsync(resumeText, jobDescription);
            if (!atsResult.Success || atsResult.Data == null)
            {
                var message = string.IsNullOrWhiteSpace(atsResult.Message)
                    ? "Failed to get ATS score from AI backend"
                    : atsResult.Message;
                return ApiResponse<AtsScoreDto>.FailureResponse(message);
            }

            var atsData = atsResult.Data;
            var score = atsData.OverallScore;
            var recommendations = atsData.Recommendations ?? new List<string>();

            resume.AtsScore = score;
            resume.AtsFriendly = atsData.IsFriendly;
            resume.AtsRecommendations = JsonSerializer.Serialize(recommendations);
            await _context.SaveChangesAsync();

            return ApiResponse<AtsScoreDto>.SuccessResponse(new AtsScoreDto
            {
                ResumeId = resumeId,
                Score = score,
                IsFriendly = atsData.IsFriendly,
                Recommendations = recommendations,
                CategoryScores = atsData.CategoryScores
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting ATS score");
            return ApiResponse<AtsScoreDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<ParsedCvResponseDto>> ParseResumeTextAsync(string resumeText)
    {
        if (string.IsNullOrWhiteSpace(resumeText))
        {
            return ApiResponse<ParsedCvResponseDto>.FailureResponse("Resume text is required");
        }

        return await _aiBackend.ParseCvFromTextAsync(resumeText);
    }

    public async Task<ApiResponse<AtsScoreDto>> GetAtsScoreFromTextAsync(string resumeText, string? jobDescription = null)
    {
        if (string.IsNullOrWhiteSpace(resumeText))
        {
            return ApiResponse<AtsScoreDto>.FailureResponse("Resume text is required");
        }

        var atsResult = await _aiBackend.GetAtsScoreAsync(resumeText, jobDescription);
        if (!atsResult.Success || atsResult.Data == null)
        {
            var message = string.IsNullOrWhiteSpace(atsResult.Message)
                ? "Failed to get ATS score from AI backend"
                : atsResult.Message;
            return ApiResponse<AtsScoreDto>.FailureResponse(message);
        }

        return ApiResponse<AtsScoreDto>.SuccessResponse(new AtsScoreDto
        {
            ResumeId = 0,
            Score = atsResult.Data.OverallScore,
            IsFriendly = atsResult.Data.IsFriendly,
            Recommendations = atsResult.Data.Recommendations,
            CategoryScores = atsResult.Data.CategoryScores
        });
    }

    public async Task<ApiResponse<JsonElement>> GetResumeImprovementsAsync(string resumeText)
    {
        if (string.IsNullOrWhiteSpace(resumeText))
        {
            return ApiResponse<JsonElement>.FailureResponse("Resume text is required");
        }

        return await _aiBackend.GetCvImprovementsAsync(resumeText);
    }

    public async Task<ApiResponse<JsonElement>> GetFullResumeAnalysisAsync(string resumeText, bool includeImprovements = true, int jobMatchLimit = 5)
    {
        if (string.IsNullOrWhiteSpace(resumeText))
        {
            return ApiResponse<JsonElement>.FailureResponse("Resume text is required");
        }

        return await _aiBackend.GetFullCvAnalysisAsync(resumeText, includeImprovements, jobMatchLimit);
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

    private static string BuildResumeText(ParsedCvResponseDto parsed)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(parsed.FullName))
        {
            builder.AppendLine(parsed.FullName);
        }

        if (!string.IsNullOrWhiteSpace(parsed.Email))
        {
            builder.AppendLine($"Email: {parsed.Email}");
        }

        if (!string.IsNullOrWhiteSpace(parsed.Phone))
        {
            builder.AppendLine($"Phone: {parsed.Phone}");
        }

        if (!string.IsNullOrWhiteSpace(parsed.Location))
        {
            builder.AppendLine($"Location: {parsed.Location}");
        }

        if (!string.IsNullOrWhiteSpace(parsed.Summary))
        {
            builder.AppendLine();
            builder.AppendLine(parsed.Summary);
        }

        if (parsed.Skills != null && parsed.Skills.Any())
        {
            builder.AppendLine();
            builder.AppendLine("Skills:");
            builder.AppendLine(string.Join(", ", parsed.Skills));
        }

        if (parsed.Experience != null && parsed.Experience.Any())
        {
            builder.AppendLine();
            builder.AppendLine("Experience:");
            foreach (var experience in parsed.Experience)
            {
                var title = experience.JobTitle ?? "";
                var company = experience.Company ?? "";
                var period = $"{experience.StartDate} - {experience.EndDate}".Trim();
                builder.AppendLine($"- {title} {(!string.IsNullOrWhiteSpace(company) ? $"at {company}" : "")}".Trim());
                if (!string.IsNullOrWhiteSpace(period) && period != "-")
                {
                    builder.AppendLine($"  {period}");
                }
                if (!string.IsNullOrWhiteSpace(experience.Description))
                {
                    builder.AppendLine($"  {experience.Description}");
                }
            }
        }

        if (parsed.Education != null && parsed.Education.Any())
        {
            builder.AppendLine();
            builder.AppendLine("Education:");
            foreach (var education in parsed.Education)
            {
                builder.AppendLine(
                    $"- {education.Degree} {(!string.IsNullOrWhiteSpace(education.Institution) ? $"- {education.Institution}" : "")}".Trim());
                if (!string.IsNullOrWhiteSpace(education.GraduationYear))
                {
                    builder.AppendLine($"  {education.GraduationYear}");
                }
            }
        }

        return builder.ToString().Trim();
    }

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

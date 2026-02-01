using System.ComponentModel.DataAnnotations;

namespace GP_Backend.Models.DTOs.Resume;

public class ResumeDto
{
    public long Id { get; set; }
    public long CandidateId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? FileType { get; set; }
    public long? FileSize { get; set; }
    public string? ResumeText { get; set; }
    public bool IsParsed { get; set; }
    public int? AtsScore { get; set; }
    public bool AtsFriendly { get; set; }
    public string? AtsRecommendations { get; set; }
    public bool IsDefault { get; set; }
    public DateTime UploadedAt { get; set; }
    public ResumeParsingResultDto? ParsingResult { get; set; }
}

public class ResumeParsingResultDto
{
    public long Id { get; set; }
    public string ParsedJson { get; set; } = string.Empty;
    public float? Confidence { get; set; }
    public string? Summary { get; set; }
    public string? ExtractedName { get; set; }
    public string? ExtractedEmail { get; set; }
    public string? ExtractedPhone { get; set; }
    public List<string> ExtractedSkills { get; set; } = new();
    public List<ExperienceDto> ExtractedExperience { get; set; } = new();
    public List<EducationDto> ExtractedEducation { get; set; } = new();
}

public class ExperienceDto
{
    public string? Title { get; set; }
    public string? Company { get; set; }
    public string? Duration { get; set; }
    public string? Description { get; set; }
}

public class EducationDto
{
    public string? Degree { get; set; }
    public string? Institution { get; set; }
    public string? Year { get; set; }
    public string? Field { get; set; }
}

public class UploadResumeDto
{
    public bool IsDefault { get; set; } = false;
    public bool ParseNow { get; set; } = true;
}

public class AtsScoreDto
{
    public long ResumeId { get; set; }
    public int Score { get; set; }
    public bool IsFriendly { get; set; }
    public List<string> Recommendations { get; set; } = new();
    public Dictionary<string, int> CategoryScores { get; set; } = new();
}

// Response from FastAPI for CV parsing
public class ParsedCvResponseDto
{
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Location { get; set; }
    public string? Summary { get; set; }
    public List<string>? Skills { get; set; }
    public List<ParsedExperienceDto>? Experience { get; set; }
    public List<ParsedEducationDto>? Education { get; set; }
    public float Confidence { get; set; }
}

public class ParsedExperienceDto
{
    public string? JobTitle { get; set; }
    public string? Company { get; set; }
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }
    public string? Description { get; set; }
}

public class ParsedEducationDto
{
    public string? Degree { get; set; }
    public string? Institution { get; set; }
    public string? GraduationYear { get; set; }
    public string? FieldOfStudy { get; set; }
}

// Request to FastAPI for ATS scoring
public class AtsScoreRequestDto
{
    public string ResumeText { get; set; } = string.Empty;
    public string? JobDescription { get; set; }
}

// Response from FastAPI for ATS scoring
public class AtsScoreResponseDto
{
    public int OverallScore { get; set; }
    public bool IsFriendly { get; set; }
    public List<string> Recommendations { get; set; } = new();
    public Dictionary<string, int> CategoryScores { get; set; } = new();
}

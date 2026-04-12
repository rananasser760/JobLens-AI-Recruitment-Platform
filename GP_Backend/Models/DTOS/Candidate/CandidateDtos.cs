using System.ComponentModel.DataAnnotations;

namespace GP_Backend.Models.DTOs.Candidate;

public class CandidateProfileDto
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string? FullName { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Location { get; set; }
    public string? CurrentTitle { get; set; }
    public string? Summary { get; set; }
    public string? LinkedInUrl { get; set; }
    public string? PortfolioUrl { get; set; }
    public string? ProfileImagePath { get; set; }
    public int? YearsOfExperience { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<CandidateSkillDto> Skills { get; set; } = new();
    public List<ResumeBasicDto> Resumes { get; set; } = new();
}

public class UpdateCandidateProfileDto
{
    [MaxLength(200)]
    public string? FullName { get; set; }

    [MaxLength(20)]
    public string? Phone { get; set; }

    [MaxLength(255)]
    public string? Location { get; set; }

    [MaxLength(200)]
    public string? CurrentTitle { get; set; }

    [MaxLength(500)]
    public string? Summary { get; set; }

    [MaxLength(255)]
    public string? LinkedInUrl { get; set; }

    [MaxLength(255)]
    public string? PortfolioUrl { get; set; }

    public int? YearsOfExperience { get; set; }
}

public class CandidateSkillDto
{
    public long Id { get; set; }
    public string SkillName { get; set; } = string.Empty;
    public int? ExperienceYears { get; set; }
    public int? SkillConfidence { get; set; }
    public string? ProficiencyLevel { get; set; }
    public bool IsVerified { get; set; }
}

public class AddSkillDto
{
    [Required]
    [MaxLength(100)]
    public string SkillName { get; set; } = string.Empty;

    public int? ExperienceYears { get; set; }

    [MaxLength(50)]
    public string? ProficiencyLevel { get; set; }
}

public class ResumeBasicDto
{
    public long Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? FileType { get; set; }
    public bool IsParsed { get; set; }
    public int? AtsScore { get; set; }
    public bool IsDefault { get; set; }
    public DateTime UploadedAt { get; set; }
}

public class CandidateListDto
{
    public long Id { get; set; }
    public string? FullName { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? CurrentTitle { get; set; }
    public string? Location { get; set; }
    public int? YearsOfExperience { get; set; }
    public string? ProfileImagePath { get; set; }
    public List<string> TopSkills { get; set; } = new();
}

public class CandidateSearchParams : Models.DTOs.Common.PaginationParams
{
    public string? Keyword { get; set; }
    public string? Location { get; set; }
    public string? Skills { get; set; }
    public int? MinExperience { get; set; }
    public int? MaxExperience { get; set; }
}

public class CandidateDashboardDto
{
    public int TotalApplications { get; set; }
    public int ActiveApplications { get; set; }
    public int InterviewsScheduled { get; set; }
    public int InterviewsCompleted { get; set; }
    public int HighestAtsScore { get; set; }
    public List<CandidateRecentApplicationDto> RecentApplications { get; set; } = new();
    public List<CandidateUpcomingInterviewDto> UpcomingInterviews { get; set; } = new();
}

public class CandidateRecentApplicationDto
{
    public long ApplicationId { get; set; }
    public long JobId { get; set; }
    public string JobTitle { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
    public DateTime AppliedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public int? AtsScore { get; set; }
}

public class CandidateUpcomingInterviewDto
{
    public long SessionId { get; set; }
    public long ApplicationId { get; set; }
    public string JobTitle { get; set; } = string.Empty;
    public string? InterviewTitle { get; set; }
    public DateTime? ScheduledAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool CheatingDetected { get; set; }
    public float? OverallScore { get; set; }
}

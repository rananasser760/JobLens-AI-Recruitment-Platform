using System.ComponentModel.DataAnnotations;
using GP_Backend.Models.Enums;

namespace GP_Backend.Models.DTOs.Application;

public class ApplicationDto
{
    public long Id { get; set; }
    public long CandidateId { get; set; }
    public long JobId { get; set; }
    public long? ResumeId { get; set; }
    public string AppliedVia { get; set; } = string.Empty;
    public DateTime AppliedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? CoverLetter { get; set; }
    public string? RecruiterNotes { get; set; }
    public DateTime? ReviewedAt { get; set; }

    // Related info
    public string CandidateName { get; set; } = string.Empty;
    public string CandidateEmail { get; set; } = string.Empty;
    public string? CandidatePhone { get; set; }
    public string JobTitle { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
    public string? ResumeName { get; set; }
    public int? AtsScore { get; set; }
    
    // Interview info
    public bool HasInterview { get; set; }
    public float? InterviewScore { get; set; }
}

public class ApplicationListDto
{
    public long Id { get; set; }
    public string JobTitle { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
    public DateTime AppliedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public string AppliedVia { get; set; } = string.Empty;
    public bool HasInterview { get; set; }
}

public class ApplyToJobDto
{
    [Required]
    public long JobId { get; set; }

    public long? ResumeId { get; set; }

    [MaxLength(1000)]
    public string? CoverLetter { get; set; }
}

public class UpdateApplicationStatusDto
{
    [Required]
    public ApplicationStatus Status { get; set; }

    public string? Notes { get; set; }
}

public class BulkUpdateApplicationStatusDto
{
    [Required]
    [MinLength(1)]
    public List<long> ApplicationIds { get; set; } = new();

    [Required]
    public ApplicationStatus Status { get; set; }

    public string? Notes { get; set; }
}

public class BulkUpdateApplicationStatusResultDto
{
    public int RequestedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int SkippedCount { get; set; }
    public List<long> NotFoundIds { get; set; } = new();
    public List<long> UnauthorizedIds { get; set; } = new();
}

public class ApplicationSearchParams : Models.DTOs.Common.PaginationParams
{
    public long? JobId { get; set; }
    public long? CandidateId { get; set; }
    public ApplicationStatus? Status { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
}

// For recruiters - viewing candidates who applied
public class CandidateApplicationDto
{
    public long ApplicationId { get; set; }
    public long CandidateId { get; set; }
    public string CandidateName { get; set; } = string.Empty;
    public string CandidateEmail { get; set; } = string.Empty;
    public string? CurrentTitle { get; set; }
    public int? YearsOfExperience { get; set; }
    public string? ProfileImage { get; set; }
    public DateTime AppliedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public int? AtsScore { get; set; }
    public float? RankingScore { get; set; }
    public int? Rank { get; set; }
    public List<string> Skills { get; set; } = new();
    public bool HasInterview { get; set; }
    public float? InterviewScore { get; set; }
}

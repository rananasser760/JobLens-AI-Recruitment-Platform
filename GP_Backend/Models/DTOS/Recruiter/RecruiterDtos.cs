using System.ComponentModel.DataAnnotations;

namespace GP_Backend.Models.DTOs.Recruiter;

public class RecruiterProfileDto
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public string? Phone { get; set; }
    public string? Position { get; set; }
    public DateTime CreatedAt { get; set; }
    public CompanyBasicDto? Company { get; set; }
}

public class UpdateRecruiterProfileDto
{
    [MaxLength(200)]
    public string? FullName { get; set; }

    [MaxLength(20)]
    public string? Phone { get; set; }

    [MaxLength(100)]
    public string? Position { get; set; }

    public long? CompanyId { get; set; }
}

public class CompanyBasicDto
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Website { get; set; }
    public string? Industry { get; set; }
    public string? LogoPath { get; set; }
}

public class CompanyDto
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Website { get; set; }
    public string? Industry { get; set; }
    public int? Size { get; set; }
    public string? LogoPath { get; set; }
    public string? Description { get; set; }
    public string? Location { get; set; }
    public DateTime CreatedAt { get; set; }
    public int TotalJobs { get; set; }
    public int ActiveJobs { get; set; }
}

public class CreateCompanyDto
{
    [Required]
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? Website { get; set; }

    [MaxLength(100)]
    public string? Industry { get; set; }

    public int? Size { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    [MaxLength(255)]
    public string? Location { get; set; }
}

public class UpdateCompanyDto
{
    [MaxLength(255)]
    public string? Name { get; set; }

    [MaxLength(255)]
    public string? Website { get; set; }

    [MaxLength(100)]
    public string? Industry { get; set; }

    public int? Size { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    [MaxLength(255)]
    public string? Location { get; set; }
}

public class RecruiterDashboardDto
{
    public int TotalJobsPosted { get; set; }
    public int ActiveJobs { get; set; }
    public int TotalApplications { get; set; }
    public int PendingApplications { get; set; }
    public int InterviewsScheduled { get; set; }
    public int InterviewsCompleted { get; set; }
    public List<RecentApplicationDto> RecentApplications { get; set; } = new();
    public List<JobStatsDto> TopJobs { get; set; } = new();
}

public class RecentApplicationDto
{
    public long ApplicationId { get; set; }
    public string CandidateName { get; set; } = string.Empty;
    public string JobTitle { get; set; } = string.Empty;
    public DateTime AppliedAt { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class JobStatsDto
{
    public long JobId { get; set; }
    public string JobTitle { get; set; } = string.Empty;
    public int ApplicationCount { get; set; }
    public int ShortlistedCount { get; set; }
}

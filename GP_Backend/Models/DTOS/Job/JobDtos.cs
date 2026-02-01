using System.ComponentModel.DataAnnotations;
using GP_Backend.Models.Enums;

namespace GP_Backend.Models.DTOs.Job;

public class JobDto
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Requirements { get; set; }
    public string? Responsibilities { get; set; }
    public string? Location { get; set; }
    public string EmploymentType { get; set; } = string.Empty;
    public string? SalaryRange { get; set; }
    public decimal? SalaryMin { get; set; }
    public decimal? SalaryMax { get; set; }
    public string? Currency { get; set; }
    public string? ExperienceLevel { get; set; }
    public DateTime PostedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool IsActive { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? ExternalUrl { get; set; }
    public string? CompanyName { get; set; }
    public string? CompanyLogo { get; set; }
    public List<string> RequiredSkills { get; set; } = new();
    public int ApplicationCount { get; set; }
}

public class JobListDto
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Location { get; set; }
    public string EmploymentType { get; set; } = string.Empty;
    public string? SalaryRange { get; set; }
    public DateTime PostedAt { get; set; }
    public string? CompanyName { get; set; }
    public string? CompanyLogo { get; set; }
    public string Source { get; set; } = string.Empty;
    public List<string> TopSkills { get; set; } = new();
    public float? MatchScore { get; set; }
}

public class CreateJobDto
{
    [Required]
    [MaxLength(255)]
    public string Title { get; set; } = string.Empty;

    [Required]
    public string Description { get; set; } = string.Empty;

    public string? Requirements { get; set; }

    public string? Responsibilities { get; set; }

    [MaxLength(255)]
    public string? Location { get; set; }

    public EmploymentType EmploymentType { get; set; }

    [MaxLength(100)]
    public string? SalaryRange { get; set; }

    public decimal? SalaryMin { get; set; }

    public decimal? SalaryMax { get; set; }

    [MaxLength(50)]
    public string? Currency { get; set; }

    [MaxLength(100)]
    public string? ExperienceLevel { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public List<CreateJobSkillDto> RequiredSkills { get; set; } = new();
}

public class UpdateJobDto
{
    [MaxLength(255)]
    public string? Title { get; set; }

    public string? Description { get; set; }

    public string? Requirements { get; set; }

    public string? Responsibilities { get; set; }

    [MaxLength(255)]
    public string? Location { get; set; }

    public EmploymentType? EmploymentType { get; set; }

    [MaxLength(100)]
    public string? SalaryRange { get; set; }

    public decimal? SalaryMin { get; set; }

    public decimal? SalaryMax { get; set; }

    [MaxLength(100)]
    public string? ExperienceLevel { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public bool? IsActive { get; set; }
}

public class CreateJobSkillDto
{
    [Required]
    [MaxLength(100)]
    public string SkillName { get; set; } = string.Empty;

    public int Importance { get; set; } = 5;

    public bool IsRequired { get; set; } = false;
}

public class JobSearchParams : Models.DTOs.Common.PaginationParams
{
    public string? Keyword { get; set; }
    public string? Location { get; set; }
    public string? Skills { get; set; }
    public EmploymentType? EmploymentType { get; set; }
    public string? ExperienceLevel { get; set; }
    public decimal? MinSalary { get; set; }
    public decimal? MaxSalary { get; set; }
    public JobSource? Source { get; set; }
    public bool? IsActive { get; set; } = true;
    public long? CompanyId { get; set; }
}

public class JobRecommendationDto
{
    public long JobId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
    public string? Location { get; set; }
    public float MatchScore { get; set; }
    public List<string> MatchingSkills { get; set; } = new();
    public string? MatchReason { get; set; }
}

// DTO for scraped jobs from FastAPI
public class ScrapedJobDto
{
    public string ExternalJobId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Requirements { get; set; }
    public string? Location { get; set; }
    public string? SalaryRange { get; set; }
    public string? EmploymentType { get; set; }
    public string ExternalUrl { get; set; } = string.Empty;
    public string ExternalSource { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
    public DateTime PostedAt { get; set; }
    public List<string>? Skills { get; set; }
}

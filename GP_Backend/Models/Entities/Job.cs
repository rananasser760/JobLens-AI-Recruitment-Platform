using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using GP_Backend.Models.Enums;

namespace GP_Backend.Models.Entities;

public class Job
{
    [Key]
    public long Id { get; set; }

    public long? RecruiterId { get; set; }

    public long? CompanyId { get; set; }

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

    public DateTime PostedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ExpiresAt { get; set; }

    public bool IsActive { get; set; } = true;

    public JobSource Source { get; set; }

    [MaxLength(255)]
    public string? ExternalJobId { get; set; }

    [MaxLength(500)]
    public string? ExternalUrl { get; set; }

    [MaxLength(100)]
    public string? ExternalSource { get; set; }

    // For scraped jobs - when to delete
    public DateTime? ScrapedAt { get; set; }

    // Navigation properties
    [ForeignKey(nameof(RecruiterId))]
    public virtual Recruiter? Recruiter { get; set; }

    [ForeignKey(nameof(CompanyId))]
    public virtual Company? Company { get; set; }

    public virtual ICollection<Application> Applications { get; set; } = new List<Application>();
    public virtual ICollection<JobSkill> RequiredSkills { get; set; } = new List<JobSkill>();
    public virtual ICollection<CandidateRanking> CandidateRankings { get; set; } = new List<CandidateRanking>();
}

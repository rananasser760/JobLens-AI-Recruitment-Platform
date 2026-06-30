using JobLens.Domain.Common;
using JobLens.Domain.Enums;

namespace JobLens.Domain.Entities;

public sealed class JobPosting : BaseEntity
{
    public long? CompanyId { get; set; }
    public JobSourceType SourceType { get; set; } = JobSourceType.Internal;
    public string ExternalJobId { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string RedirectUrl { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Requirements { get; set; } = string.Empty;
    public string Responsibilities { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string EmploymentType { get; set; } = "FullTime";
    public string SalaryRange { get; set; } = string.Empty;
    public decimal? SalaryMin { get; set; }
    public decimal? SalaryMax { get; set; }
    public string Currency { get; set; } = "USD";
    public string ExperienceLevel { get; set; } = string.Empty;
    public string SkillsJson { get; set; } = "[]";
    public string InterviewDefaultsJson { get; set; } = "{}";
    public string MetadataJson { get; set; } = "{}";
    public string ContentHash { get; set; } = string.Empty;
    public DateTime? PostedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public bool IsActive { get; set; } = true;

    public Company? Company { get; set; }
    public ICollection<JobApplication> Applications { get; set; } = new List<JobApplication>();
}

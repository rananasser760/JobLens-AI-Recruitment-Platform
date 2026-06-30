using JobLens.Domain.Common;

namespace JobLens.Domain.Entities;

public sealed class CandidateProfile : BaseEntity
{
    public long UserId { get; set; }
    public string Headline { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string LinkedInUrl { get; set; } = string.Empty;
    public string PortfolioUrl { get; set; } = string.Empty;
    public string ProfileImagePath { get; set; } = string.Empty;
    public string SkillsJson { get; set; } = "[]";
    public int YearsExperience { get; set; }

    public User User { get; set; } = null!;
    public ICollection<Resume> Resumes { get; set; } = new List<Resume>();
    public ICollection<JobApplication> Applications { get; set; } = new List<JobApplication>();
}

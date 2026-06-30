using JobLens.Domain.Common;

namespace JobLens.Domain.Entities;

public sealed class Company : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string WebsiteUrl { get; set; } = string.Empty;
    public string LogoUrl { get; set; } = string.Empty;
    public string Industry { get; set; } = string.Empty;
    public int? Size { get; set; }
    public string Location { get; set; } = string.Empty;

    public ICollection<RecruiterProfile> Recruiters { get; set; } = new List<RecruiterProfile>();
    public ICollection<JobPosting> Jobs { get; set; } = new List<JobPosting>();
}

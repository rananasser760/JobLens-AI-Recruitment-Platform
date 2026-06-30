using JobLens.Domain.Common;

namespace JobLens.Domain.Entities;

public sealed class ParsedResumeResult : BaseEntity
{
    public long ResumeId { get; set; }
    public string StructuredJson { get; set; } = "{}";
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string SkillsJson { get; set; } = "[]";
    public DateTime ParsedAtUtc { get; set; } = DateTime.UtcNow;

    public Resume Resume { get; set; } = null!;
}

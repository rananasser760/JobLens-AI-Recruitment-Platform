using JobLens.Domain.Common;
using JobLens.Domain.Enums;

namespace JobLens.Domain.Entities;

public sealed class User : BaseEntity
{
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public AppRole Role { get; set; }
    public bool IsActive { get; set; } = true;

    public CandidateProfile? CandidateProfile { get; set; }
    public RecruiterProfile? RecruiterProfile { get; set; }
    public ICollection<AuditLog> AuditLogs { get; set; } = new List<AuditLog>();
}

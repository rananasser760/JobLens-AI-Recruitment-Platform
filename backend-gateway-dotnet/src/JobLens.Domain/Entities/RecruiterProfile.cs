using JobLens.Domain.Common;

namespace JobLens.Domain.Entities;

public sealed class RecruiterProfile : BaseEntity
{
    public long UserId { get; set; }
    public long? CompanyId { get; set; }
    public string JobTitle { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;

    public User User { get; set; } = null!;
    public Company? Company { get; set; }
}

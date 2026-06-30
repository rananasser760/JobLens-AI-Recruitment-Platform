using JobLens.Domain.Common;
using JobLens.Domain.Enums;

namespace JobLens.Domain.Entities;

public sealed class RecommendationCacheEntry : BaseEntity
{
    public RecommendationSubjectType SubjectType { get; set; }
    public long SubjectId { get; set; }
    public RecommendationTargetType TargetType { get; set; }
    public long TargetId { get; set; }
    public int Rank { get; set; }
    public double Score { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string SourceSnapshotHash { get; set; } = string.Empty;
    public DateTime RefreshedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddHours(6);
}

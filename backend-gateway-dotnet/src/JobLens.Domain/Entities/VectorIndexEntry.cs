using JobLens.Domain.Common;
using JobLens.Domain.Enums;

namespace JobLens.Domain.Entities;

public sealed class VectorIndexEntry : BaseEntity
{
    public string EntityType { get; set; } = string.Empty;
    public long EntityId { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public string Collection { get; set; } = string.Empty;
    public string VectorId { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public VectorIndexStatus Status { get; set; } = VectorIndexStatus.Pending;
    public DateTime? EmbeddedAtUtc { get; set; }
    public string LastError { get; set; } = string.Empty;
}

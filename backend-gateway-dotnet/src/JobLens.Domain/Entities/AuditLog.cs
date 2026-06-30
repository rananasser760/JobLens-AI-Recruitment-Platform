using JobLens.Domain.Common;

namespace JobLens.Domain.Entities;

public sealed class AuditLog : BaseEntity
{
    public long? ActorUserId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = "{}";

    public User? ActorUser { get; set; }
}

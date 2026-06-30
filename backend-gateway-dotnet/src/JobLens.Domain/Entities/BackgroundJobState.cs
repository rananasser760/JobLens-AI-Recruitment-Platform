using JobLens.Domain.Common;

namespace JobLens.Domain.Entities;

public sealed class BackgroundJobState : BaseEntity
{
    public string JobType { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string Status { get; set; } = "Queued";
    public string PayloadJson { get; set; } = "{}";
    public string Error { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public DateTime? LastRunAtUtc { get; set; }
    public DateTime? NextRunAtUtc { get; set; }
}

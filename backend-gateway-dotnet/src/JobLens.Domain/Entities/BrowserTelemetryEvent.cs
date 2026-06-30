using JobLens.Domain.Common;

namespace JobLens.Domain.Entities;

public sealed class BrowserTelemetryEvent : BaseEntity
{
    public long InterviewSessionId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;

    public InterviewSession InterviewSession { get; set; } = null!;
}

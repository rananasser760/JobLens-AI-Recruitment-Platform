using JobLens.Domain.Common;

namespace JobLens.Domain.Entities;

public sealed class InterviewTranscriptSegment : BaseEntity
{
    public long InterviewSessionId { get; set; }
    public int Sequence { get; set; }
    public string Speaker { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;

    public InterviewSession InterviewSession { get; set; } = null!;
}

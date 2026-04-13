using JobLens.Domain.Common;

namespace JobLens.Domain.Entities;

public sealed class InterviewReport : BaseEntity
{
    public long InterviewSessionId { get; set; }
    public string RecruiterReportJson { get; set; } = "{}";
    public string CandidateFeedbackJson { get; set; } = "{}";
    public double? FinalScore { get; set; }
    public string Verdict { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

    public InterviewSession InterviewSession { get; set; } = null!;
}

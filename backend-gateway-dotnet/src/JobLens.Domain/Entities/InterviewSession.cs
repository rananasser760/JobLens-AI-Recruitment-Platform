using JobLens.Domain.Common;
using JobLens.Domain.Enums;

namespace JobLens.Domain.Entities;

public sealed class InterviewSession : BaseEntity
{
    public long ApplicationId { get; set; }
    public string AgentType { get; set; } = "Mixed";
    public string InterviewTitle { get; set; } = string.Empty;
    public int MaxQuestions { get; set; } = 5;
    public InterviewSessionStatus Status { get; set; } = InterviewSessionStatus.Draft;
    public DateTime? ScheduledAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public DateTime? ConsentCapturedAtUtc { get; set; }
    public string InterviewBackendSessionId { get; set; } = string.Empty;
    public string IntegrityBackendSessionId { get; set; } = string.Empty;
    public string CriteriaSnapshot { get; set; } = string.Empty;
    public double? FinalScore { get; set; }
    public string FinalVerdict { get; set; } = string.Empty;

    public JobApplication Application { get; set; } = null!;
    public ICollection<InterviewTranscriptSegment> TranscriptSegments { get; set; } = new List<InterviewTranscriptSegment>();
    public ICollection<BrowserTelemetryEvent> BrowserEvents { get; set; } = new List<BrowserTelemetryEvent>();
    public ICollection<ProctoringEvent> ProctoringEvents { get; set; } = new List<ProctoringEvent>();
    public ICollection<InterviewReport> Reports { get; set; } = new List<InterviewReport>();
}

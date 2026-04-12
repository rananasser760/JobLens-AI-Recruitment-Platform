namespace GP_Backend.Services.AI;

public class IntegritySessionStartRequestDto
{
    public string? CandidateName { get; set; }
    public string? CandidateId { get; set; }
    public string? InterviewSessionId { get; set; }
}

public class IntegritySessionStartResponseDto
{
    public long SessionId { get; set; }
    public string? StartedAt { get; set; }
    public string? CandidateName { get; set; }
    public string? CandidateId { get; set; }
}

public class IntegritySessionEndResponseDto
{
    public long SessionId { get; set; }
    public float? FinalScore { get; set; }
    public string? Recommendation { get; set; }
    public string? Reason { get; set; }
    public float? DurationSeconds { get; set; }
}

public class UnifiedSessionReportDto
{
    public long SessionId { get; set; }
    public string? StartedAt { get; set; }
    public string? EndedAt { get; set; }
    public float? DurationSeconds { get; set; }
    public float? FinalScore { get; set; }
    public string? Recommendation { get; set; }
    public int TotalAlerts { get; set; }
    public int TotalYoloAlerts { get; set; }
    public Dictionary<string, int> AlertBreakdown { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> YoloAlertBreakdown { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? InterviewSessionId { get; set; }
    public float? InterviewScore { get; set; }
    public string? InterviewSummaryJson { get; set; }
    public string? CombinedVerdict { get; set; }
    public string? CombinedReason { get; set; }
    public string? RawJson { get; set; }
}
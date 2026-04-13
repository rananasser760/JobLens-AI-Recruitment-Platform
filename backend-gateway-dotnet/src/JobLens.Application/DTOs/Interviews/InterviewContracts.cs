using JobLens.Application.Contracts;
using JobLens.Domain.Enums;

namespace JobLens.Application.DTOs.Interviews;

public sealed record ScheduleInterviewRequest(long ApplicationId, DateTime ScheduledAtUtc, string EvaluationCriteria, int MaxQuestions = 5);
public sealed record StartInterviewRequest(long InterviewSessionId, bool ConsentCaptured);
public sealed record BrowserEventRequest(long InterviewSessionId, string EventType, string Severity, string PayloadJson);
public sealed record TranscriptEntryDto(int Sequence, string Speaker, string Content, DateTime OccurredAtUtc);

public sealed record InterviewSessionDto(
    long InterviewSessionId,
    InterviewSessionStatus Status,
    DateTime? ScheduledAtUtc,
    DateTime? StartedAtUtc,
    DateTime? EndedAtUtc,
    string InterviewBackendSessionId,
    string IntegrityBackendSessionId,
    string EvaluationCriteria,
    double? FinalScore,
    string FinalVerdict);

public sealed record InterviewRealtimeResultDto(
    string Transcript,
    string Reply,
    bool IsComplete,
    double? Score,
    IReadOnlyList<VideoFlagDto> ProctoringEvents,
    string? ReplyAudioBase64,
    string? ReplyAudioMimeType);

public sealed record InterviewReportDto(
    long InterviewSessionId,
    double? FinalScore,
    string Verdict,
    string RecruiterReportJson,
    string CandidateFeedbackJson);

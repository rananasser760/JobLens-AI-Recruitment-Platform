using JobLens.Application.DTOs.Interviews;

namespace JobLens.Application.Contracts;

public sealed record CandidateVectorSyncRequest(long CandidateId, IReadOnlyDictionary<string, object?> ProfileData, string? ContentHash = null);
public sealed record JobVectorSyncRequest(long JobId, IReadOnlyDictionary<string, object?> JobData, string? ContentHash = null);
public sealed record VectorUpsertResponseDto(string VectorId, string Collection, string Model);

public sealed record JobRecommendationRequest(long CandidateId, string ResumeText, int Limit);
public sealed record CandidateRecommendationRequest(long JobId, string JobDescription, int Limit);
public sealed record RecommendationResultDto(long TargetId, string TargetType, double Score, string Reason, string PreviewJson);

public sealed record ScrapeJobsRequest(int? MaxCategories = null);

public sealed record ScrapedJobItemDto(
    string Source,
    string ExternalJobId,
    string SourceUrl,
    string RedirectUrl,
    string Title,
    string Company,
    string Location,
    string? City,
    string? Country,
    string Description,
    string? Requirements,
    string? Responsibilities,
    string? EmploymentType,
    string? ExperienceLevel,
    string? EnrichmentSource,
    IReadOnlyList<string> Skills,
    DateTime? PostedAtUtc,
    IReadOnlyDictionary<string, object?> Metadata);

public sealed record ScrapeJobsResponseDto(int ProcessedCategories, int UpsertedJobs, IReadOnlyList<ScrapedJobItemDto> Jobs);

public sealed record StartInterviewAiRequest(
    string CandidateName,
    string CandidateId,
    string ResumeText,
    string JobDescription,
    string EvaluationCriteria,
    int MaxQuestions);

public sealed record InterviewSessionInitResponseDto(string InterviewSessionId, string IntegritySessionId, int MaxQuestions, string WelcomeMessage);
public sealed record AudioAnalysisRequest(string InterviewSessionId, string Base64Audio, int Sequence);
public sealed record AudioAnalysisResponseDto(
    string Transcript,
    string Reply,
    bool IsComplete,
    double? Score,
    IReadOnlyList<string> Flags,
    string? ReplyAudioBase64,
    string? ReplyAudioMimeType);
public sealed record VideoAnalysisRequest(string InterviewSessionId, string Base64Frame, int Sequence);
public sealed record VideoFlagDto(string EventType, string Severity, string Source, string Description, string? MediaReference = null);
public sealed record VideoAnalysisResponseDto(IReadOnlyList<VideoFlagDto> Events);
public sealed record FinalizeInterviewRequest(string InterviewSessionId, string IntegritySessionId, IReadOnlyList<TranscriptEntryDto> Transcript);
public sealed record InterviewFinalizationResponseDto(double FinalScore, string Verdict, string RecruiterReportJson, string CandidateFeedbackJson);

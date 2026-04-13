using JobLens.Domain.Enums;

namespace JobLens.Api.Contracts;

public sealed record PaginatedResponseDto<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int PageNumber,
    int PageSize,
    int TotalPages,
    bool HasPreviousPage,
    bool HasNextPage);

public sealed record RegisterCompatRequest(
    string Username,
    string Email,
    string Password,
    string ConfirmPassword,
    string Role,
    string? FullName = null,
    string? Phone = null,
    long? CompanyId = null);

public sealed record RefreshTokenRequest(string AccessToken, string RefreshToken);

public sealed record AuthSessionDto(
    long UserId,
    string Username,
    string Email,
    string Role,
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresAt,
    long? ProfileId,
    string? FullName);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword, string ConfirmNewPassword);
public sealed record ForgotPasswordRequest(string Email);
public sealed record ResetPasswordRequest(string Token, string Email, string NewPassword, string ConfirmNewPassword);

public sealed record UpdateApplicationStatusRequest(string Status, string? Notes);
public sealed record BulkUpdateApplicationStatusRequest(IReadOnlyList<long> ApplicationIds, string Status, string? Notes);

public sealed record BulkUpdateApplicationStatusResultDto(
    int RequestedCount,
    int UpdatedCount,
    int SkippedCount,
    IReadOnlyList<long> NotFoundIds,
    IReadOnlyList<long> UnauthorizedIds);

public sealed record ApplicationViewDto(
    long Id,
    long CandidateId,
    long JobId,
    long? ResumeId,
    string AppliedVia,
    DateTime AppliedAt,
    string Status,
    string? CoverLetter,
    string? RecruiterNotes,
    DateTime? ReviewedAt,
    string CandidateName,
    string CandidateEmail,
    string? CandidatePhone,
    string JobTitle,
    string? CompanyName,
    string? ResumeName,
    double? AtsScore,
    bool HasInterview,
    double? InterviewScore);

public sealed record CandidateApplicationViewDto(
    long ApplicationId,
    long CandidateId,
    string CandidateName,
    string CandidateEmail,
    string? CurrentTitle,
    int? YearsOfExperience,
    string? ProfileImage,
    DateTime AppliedAt,
    string Status,
    double? AtsScore,
    double? RankingScore,
    int? Rank,
    IReadOnlyList<string> Skills,
    bool HasInterview,
    double? InterviewScore);

public sealed record ApplicationCheckDto(bool HasApplied);

public sealed record CandidateSkillDto(int Id, string SkillName, int? ExperienceYears, double? SkillConfidence, string? ProficiencyLevel, bool IsVerified);

public sealed record ResumeBasicDto(long Id, string FileName, string? FileType, bool IsParsed, double? AtsScore, bool IsDefault, DateTime UploadedAt);

public sealed record CandidateProfileViewDto(
    long Id,
    long UserId,
    string? FullName,
    string Email,
    string? Phone,
    string? Location,
    string? CurrentTitle,
    string? Summary,
    string? LinkedInUrl,
    string? PortfolioUrl,
    string? ProfileImagePath,
    int? YearsOfExperience,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<CandidateSkillDto> Skills,
    IReadOnlyList<ResumeBasicDto> Resumes);

public sealed record UpdateCandidateProfileCompatRequest(
    string? FullName,
    string? Phone,
    string? Location,
    string? CurrentTitle,
    string? Summary,
    string? LinkedInUrl,
    string? PortfolioUrl,
    int? YearsOfExperience);

public sealed record AddSkillRequest(string SkillName, int? ExperienceYears, string? ProficiencyLevel);

public sealed record CandidateListDto(
    long Id,
    string? FullName,
    string Email,
    string? CurrentTitle,
    string? Location,
    int? YearsOfExperience,
    string? ProfileImagePath,
    IReadOnlyList<string> TopSkills);

public sealed record CandidateRecentApplicationDto(
    long ApplicationId,
    long JobId,
    string JobTitle,
    string? CompanyName,
    DateTime AppliedAt,
    string Status,
    double? AtsScore);

public sealed record CandidateUpcomingInterviewDto(
    long SessionId,
    long ApplicationId,
    string JobTitle,
    string? InterviewTitle,
    DateTime? ScheduledAt,
    string Status,
    bool CheatingDetected,
    double? OverallScore);

public sealed record CandidateDashboardDto(
    int TotalApplications,
    int ActiveApplications,
    int InterviewsScheduled,
    int InterviewsCompleted,
    double HighestAtsScore,
    IReadOnlyList<CandidateRecentApplicationDto> RecentApplications,
    IReadOnlyList<CandidateUpcomingInterviewDto> UpcomingInterviews);

public sealed record CompanyDto(
    long Id,
    string Name,
    string? Website,
    string? Industry,
    int? Size,
    string? LogoPath,
    string? Description,
    string? Location,
    DateTime CreatedAt,
    int TotalJobs,
    int ActiveJobs);

public sealed record CreateCompanyRequest(
    string Name,
    string? Website,
    string? Industry,
    int? Size,
    string? Description,
    string? Location);

public sealed record UpdateCompanyRequest(
    string? Name,
    string? Website,
    string? Industry,
    int? Size,
    string? Description,
    string? Location);

public sealed record CompanyBasicDto(long Id, string Name, string? Website, string? Industry, string? LogoPath);

public sealed record RecruiterProfileViewDto(
    long Id,
    long UserId,
    string Email,
    string? FullName,
    string? Phone,
    string? Position,
    DateTime CreatedAt,
    CompanyBasicDto? Company);

public sealed record UpdateRecruiterProfileCompatRequest(
    string? FullName,
    string? Phone,
    string? Position,
    long? CompanyId);

public sealed record RecentApplicationDto(
    long ApplicationId,
    string CandidateName,
    string JobTitle,
    DateTime AppliedAt,
    string Status);

public sealed record JobStatsDto(long JobId, string JobTitle, int ApplicationCount, int ShortlistedCount);

public sealed record RecruiterDashboardDto(
    int TotalJobsPosted,
    int ActiveJobs,
    int TotalApplications,
    int PendingApplications,
    int InterviewsScheduled,
    int InterviewsCompleted,
    IReadOnlyList<RecentApplicationDto> RecentApplications,
    IReadOnlyList<JobStatsDto> TopJobs);

public sealed record CreateJobSkillRequest(string SkillName, int? Importance, bool? IsRequired);

public sealed record CreateJobCompatRequest(
    string Title,
    string Description,
    string? Requirements,
    string? Responsibilities,
    string? Location,
    string EmploymentType,
    string? SalaryRange,
    decimal? SalaryMin,
    decimal? SalaryMax,
    string? Currency,
    string? ExperienceLevel,
    DateTime? ExpiresAt,
    IReadOnlyList<CreateJobSkillRequest>? RequiredSkills);

public sealed record UpdateJobCompatRequest(
    string? Title,
    string? Description,
    string? Requirements,
    string? Responsibilities,
    string? Location,
    string? EmploymentType,
    string? SalaryRange,
    decimal? SalaryMin,
    decimal? SalaryMax,
    string? ExperienceLevel,
    DateTime? ExpiresAt,
    bool? IsActive);

public sealed record JobViewDto(
    long Id,
    string Title,
    string Description,
    string? Requirements,
    string? Responsibilities,
    string? Location,
    string EmploymentType,
    string? SalaryRange,
    decimal? SalaryMin,
    decimal? SalaryMax,
    string? Currency,
    string? ExperienceLevel,
    DateTime PostedAt,
    DateTime? ExpiresAt,
    bool IsActive,
    string Source,
    string? ExternalUrl,
    string? CompanyName,
    string? CompanyLogo,
    IReadOnlyList<string> RequiredSkills,
    int ApplicationCount);

public sealed record JobListItemDto(
    long Id,
    string Title,
    string? Location,
    string EmploymentType,
    string? SalaryRange,
    DateTime PostedAt,
    string? CompanyName,
    string? CompanyLogo,
    string Source,
    IReadOnlyList<string> TopSkills,
    double? MatchScore);

public sealed record JobRecommendationDto(
    long JobId,
    string Title,
    string? CompanyName,
    string? Location,
    double MatchScore,
    IReadOnlyList<string> MatchingSkills,
    string? MatchReason);

public sealed record ScrapedJobDto(
    string ExternalJobId,
    string Title,
    string Description,
    string? Requirements,
    string? Location,
    string? SalaryRange,
    string? EmploymentType,
    string ExternalUrl,
    string ExternalSource,
    string? CompanyName,
    DateTime PostedAt,
    IReadOnlyList<string>? Skills);

public sealed record MatchJobsFromTextRequest(string ResumeText);

public sealed record ResumeParsingResultDto(
    long Id,
    string ParsedJson,
    double? Confidence,
    string? Summary,
    string? ExtractedName,
    string? ExtractedEmail,
    string? ExtractedPhone,
    IReadOnlyList<string> ExtractedSkills,
    IReadOnlyList<Dictionary<string, object?>> ExtractedExperience,
    IReadOnlyList<Dictionary<string, object?>> ExtractedEducation);

public sealed record ResumeViewDto(
    long Id,
    long CandidateId,
    string FileName,
    string? FileType,
    long? FileSize,
    string? ResumeText,
    bool IsParsed,
    double? AtsScore,
    bool AtsFriendly,
    string? AtsRecommendations,
    bool IsDefault,
    DateTime UploadedAt,
    ResumeParsingResultDto? ParsingResult);

public sealed record ResumeTextRequest(string ResumeText);
public sealed record ResumeTextAtsRequest(string ResumeText, string? JobDescription);
public sealed record ResumeFullAnalysisRequest(string ResumeText, bool IncludeImprovements = true, int JobMatchLimit = 5);

public sealed record AtsScoreDto(
    long ResumeId,
    double Score,
    bool IsFriendly,
    IReadOnlyList<string> Recommendations,
    IReadOnlyDictionary<string, double> CategoryScores);

public sealed record ScheduleInterviewCompatRequest(
    long ApplicationId,
    DateTime ScheduledAt,
    string? AgentType,
    string? InterviewTitle);

public sealed record SubmitAnswerCompatRequest(
    long QuestionId,
    string? AnswerText,
    int? ResponseDurationSeconds);

public sealed record ReportCheatingEventCompatRequest(
    long SessionId,
    string EventType,
    double? Confidence,
    string? Details,
    int? TimestampSeconds);

public sealed record ReportBrowserEventCompatRequest(
    long SessionId,
    int? TabSwitchCount,
    int? FocusLossCount,
    int? CopyPasteCount,
    int? RightClickCount,
    string? DetailsJson);

public sealed record InterviewAnswerDto(
    long Id,
    long QuestionId,
    string? AnswerText,
    int? ResponseDurationSeconds,
    double? AiScore,
    string? AiFeedback,
    DateTime AnsweredAt);

public sealed record InterviewQuestionDto(
    long Id,
    long SessionId,
    string QuestionText,
    int OrderIndex,
    string? Category,
    string? Difficulty,
    int? MaxDurationSeconds,
    bool IsAnswered,
    InterviewAnswerDto? Answer);

public sealed record CheatingEventDto(
    long Id,
    long SessionId,
    string EventType,
    double? Confidence,
    DateTime DetectedAt,
    string? Details,
    int? TimestampSeconds);

public sealed record InterviewSessionDto(
    long Id,
    long ApplicationId,
    string AgentType,
    string? InterviewTitle,
    DateTime? ScheduledAt,
    DateTime? StartedAt,
    DateTime? EndedAt,
    double? OverallScore,
    bool CheatingDetected,
    int TotalQuestions,
    int AnsweredQuestions,
    string Status,
    long? IntegritySessionId,
    string? InterviewBackendSessionId,
    string? FinalReport,
    string? AiFeedback,
    string CandidateName,
    string JobTitle,
    int CheatingEventsCount);

public sealed record InterviewSessionListDto(
    long Id,
    string? InterviewTitle,
    DateTime? ScheduledAt,
    string Status,
    long? IntegritySessionId,
    string? InterviewBackendSessionId,
    double? OverallScore,
    bool CheatingDetected,
    string CandidateName,
    string JobTitle);

public sealed record QuestionScoreDto(string QuestionText, string? Category, double? Score, string? Feedback);

public sealed record CheatingReportDto(
    bool CheatingDetected,
    int TotalEvents,
    IReadOnlyDictionary<string, int> EventsByType,
    int TotalTabSwitches,
    int TotalFocusLosses);

public sealed record InterviewReportViewDto(
    long SessionId,
    string CandidateName,
    string JobTitle,
    DateTime? StartedAt,
    DateTime? EndedAt,
    int DurationMinutes,
    double? OverallScore,
    IReadOnlyList<QuestionScoreDto> QuestionScores,
    CheatingReportDto CheatingReport,
    string? AiFeedback,
    string? Recommendation);

public sealed record InterviewRankingDto(
    long ApplicationId,
    long CandidateId,
    string CandidateName,
    double InterviewScore,
    double? AtsScore,
    double? RankingScore,
    int Rank,
    bool CheatingDetected,
    string Status);

public sealed record EnumOptionDto(int Value, string Name);
public sealed record EnumMetadataDto(IReadOnlyDictionary<string, IReadOnlyList<EnumOptionDto>> Enums);

public sealed record CancelInterviewRequest(string? Reason);
public sealed record RescheduleInterviewRequest(DateTime ScheduledAt);

public sealed record InternalRequestEnvelope<T>(string RequestId, T Payload);
public sealed record InternalErrorDto(string Code, string Message, string? Details);
public sealed record InternalResponseEnvelope<T>(string RequestId, bool Success, T? Data, InternalErrorDto? Error = null);

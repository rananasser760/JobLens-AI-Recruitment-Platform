using System.ComponentModel.DataAnnotations;
using GP_Backend.Models.Enums;

namespace GP_Backend.Models.DTOs.Interview;

public class InterviewSessionDto
{
    public long Id { get; set; }
    public long ApplicationId { get; set; }
    public string AgentType { get; set; } = string.Empty;
    public string? InterviewTitle { get; set; }
    public DateTime? ScheduledAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public float? OverallScore { get; set; }
    public bool CheatingDetected { get; set; }
    public int TotalQuestions { get; set; }
    public int AnsweredQuestions { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? FinalReport { get; set; }
    public string? AiFeedback { get; set; }

    // Related info
    public string CandidateName { get; set; } = string.Empty;
    public string JobTitle { get; set; } = string.Empty;
    public int CheatingEventsCount { get; set; }
}

public class InterviewSessionListDto
{
    public long Id { get; set; }
    public string? InterviewTitle { get; set; }
    public DateTime? ScheduledAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public float? OverallScore { get; set; }
    public bool CheatingDetected { get; set; }
    public string CandidateName { get; set; } = string.Empty;
    public string JobTitle { get; set; } = string.Empty;
}

public class ScheduleInterviewDto
{
    [Required]
    public long ApplicationId { get; set; }

    [Required]
    public DateTime ScheduledAt { get; set; }

    public InterviewAgentType AgentType { get; set; } = InterviewAgentType.Mixed;

    [MaxLength(100)]
    public string? InterviewTitle { get; set; }
}

public class StartInterviewDto
{
    [Required]
    public long SessionId { get; set; }
}

public class InterviewQuestionDto
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public string QuestionText { get; set; } = string.Empty;
    public int OrderIndex { get; set; }
    public string? Category { get; set; }
    public string? Difficulty { get; set; }
    public int? MaxDurationSeconds { get; set; }
    public bool IsAnswered { get; set; }
    public InterviewAnswerDto? Answer { get; set; }
}

public class InterviewAnswerDto
{
    public long Id { get; set; }
    public long QuestionId { get; set; }
    public string? AnswerText { get; set; }
    public int? ResponseDurationSeconds { get; set; }
    public float? AiScore { get; set; }
    public string? AiFeedback { get; set; }
    public DateTime AnsweredAt { get; set; }
}

public class SubmitAnswerDto
{
    [Required]
    public long QuestionId { get; set; }

    public string? AnswerText { get; set; }

    public int? ResponseDurationSeconds { get; set; }

    // Audio file will be sent as form data
}

public class CheatingEventDto
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public float? Confidence { get; set; }
    public DateTime DetectedAt { get; set; }
    public string? Details { get; set; }
    public int? TimestampSeconds { get; set; }
}

public class ReportCheatingEventDto
{
    [Required]
    public long SessionId { get; set; }

    [Required]
    public CheatingEventType EventType { get; set; }

    public float? Confidence { get; set; }

    public string? Details { get; set; }

    public int? TimestampSeconds { get; set; }

    // Frame image will be sent as form data
}

public class BrowserEventDto
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public int TabSwitchCount { get; set; }
    public int FocusLossCount { get; set; }
    public int CopyPasteCount { get; set; }
    public int RightClickCount { get; set; }
    public DateTime RecordedAt { get; set; }
}

public class ReportBrowserEventDto
{
    [Required]
    public long SessionId { get; set; }

    public int TabSwitchCount { get; set; }
    public int FocusLossCount { get; set; }
    public int CopyPasteCount { get; set; }
    public int RightClickCount { get; set; }
    public string? DetailsJson { get; set; }
}

public class InterviewReportDto
{
    public long SessionId { get; set; }
    public string CandidateName { get; set; } = string.Empty;
    public string JobTitle { get; set; } = string.Empty;
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int DurationMinutes { get; set; }
    public float? OverallScore { get; set; }
    public List<QuestionScoreDto> QuestionScores { get; set; } = new();
    public CheatingReportDto CheatingReport { get; set; } = new();
    public string? AiFeedback { get; set; }
    public string? Recommendation { get; set; }
}

public class QuestionScoreDto
{
    public string QuestionText { get; set; } = string.Empty;
    public string? Category { get; set; }
    public float? Score { get; set; }
    public string? Feedback { get; set; }
}

public class CheatingReportDto
{
    public bool CheatingDetected { get; set; }
    public int TotalEvents { get; set; }
    public Dictionary<string, int> EventsByType { get; set; } = new();
    public int TotalTabSwitches { get; set; }
    public int TotalFocusLosses { get; set; }
}

public class InterviewRankingDto
{
    public long ApplicationId { get; set; }
    public long CandidateId { get; set; }
    public string CandidateName { get; set; } = string.Empty;
    public float InterviewScore { get; set; }
    public float? AtsScore { get; set; }
    public float? RankingScore { get; set; }
    public int Rank { get; set; }
    public bool CheatingDetected { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class InterviewSearchParams : Models.DTOs.Common.PaginationParams
{
    public long? JobId { get; set; }
    public long? ApplicationId { get; set; }
    public string? Status { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public bool? CheatingDetected { get; set; }
}

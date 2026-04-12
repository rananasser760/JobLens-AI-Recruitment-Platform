namespace GP_Backend.Services.AI;

public class GeneratedQuestionDto
{
    public string QuestionText { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? Difficulty { get; set; }
    public string? ExpectedAnswer { get; set; }
    public int? MaxDurationSeconds { get; set; }
}

public class AnswerEvaluationDto
{
    public float Score { get; set; }
    public string Feedback { get; set; } = string.Empty;
    public List<string>? StrongPoints { get; set; }
    public List<string>? ImprovementAreas { get; set; }
}

public class QuestionAnswerPairDto
{
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public float Score { get; set; }
    public string? Category { get; set; }
}

public class InterviewSessionStartRequestDto
{
    public string CvText { get; set; } = string.Empty;
    public string JobDescription { get; set; } = string.Empty;
    public string EvaluationCriteria { get; set; } = string.Empty;
    public int MaxQuestions { get; set; } = 5;
    public string? CandidateName { get; set; }
    public string? CandidateId { get; set; }
    public long? IntegrityDbSessionId { get; set; }
}

public class InterviewSessionStartResponseDto
{
    public string InterviewSessionId { get; set; } = string.Empty;
    public int MaxQuestions { get; set; }
    public string? Message { get; set; }
}

public class InterviewSessionSummaryDto
{
    public string? Status { get; set; }
    public string? SummaryJson { get; set; }
}
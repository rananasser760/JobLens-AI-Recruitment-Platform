using GP_Backend.Models.DTOs.Common;
using GP_Backend.Models.DTOs.Job;
using GP_Backend.Models.DTOs.Resume;

namespace GP_Backend.Services.Interfaces;

/// <summary>
/// Service for communicating with the FastAPI AI backend
/// </summary>
public interface IAIBackendService
{
    // CV Parsing
    Task<ApiResponse<ParsedCvResponseDto>> ParseCvAsync(Stream fileStream, string fileName);
    Task<ApiResponse<ParsedCvResponseDto>> ParseCvFromTextAsync(string resumeText);
    
    // ATS Scoring
    Task<ApiResponse<AtsScoreResponseDto>> GetAtsScoreAsync(string resumeText, string? jobDescription = null);
    
    // Embeddings & Recommendations
    Task<ApiResponse> CreateCandidateEmbeddingAsync(long candidateId, string profileData);
    Task<ApiResponse> UpdateCandidateEmbeddingAsync(long candidateId, string profileData);
    Task<ApiResponse> DeleteCandidateEmbeddingAsync(long candidateId);
    
    Task<ApiResponse> CreateJobEmbeddingAsync(long jobId, string jobData);
    Task<ApiResponse> UpdateJobEmbeddingAsync(long jobId, string jobData);
    Task<ApiResponse> DeleteJobEmbeddingAsync(long jobId);
    
    // Recommendations
    Task<ApiResponse<List<JobRecommendationDto>>> GetJobRecommendationsForCandidateAsync(long candidateId, int limit = 10);
    Task<ApiResponse<List<CandidateRankingResultDto>>> GetCandidateRankingsForJobAsync(long jobId, int limit = 50);
    
    // Job Scraping
    Task<ApiResponse<List<ScrapedJobDto>>> GetScrapedJobsAsync(string? keyword = null, string? location = null);
    
    // Interview AI
    Task<ApiResponse<List<GeneratedQuestionDto>>> GenerateInterviewQuestionsAsync(long jobId, string agentType, int questionCount = 10);
    Task<ApiResponse<AnswerEvaluationDto>> EvaluateAnswerAsync(string question, string answer, string? expectedAnswer = null);
    Task<ApiResponse<string>> TranscribeAudioAsync(Stream audioStream);
    Task<ApiResponse<Stream>> TextToSpeechAsync(string text);
    
    // Interview Report
    Task<ApiResponse<string>> GenerateInterviewReportAsync(long sessionId, List<QuestionAnswerPairDto> qaList, float overallScore);
}

public class CandidateRankingResultDto
{
    public long CandidateId { get; set; }
    public float Score { get; set; }
    public string? Reason { get; set; }
}

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

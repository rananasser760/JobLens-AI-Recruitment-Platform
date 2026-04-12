using GP_Backend.Models.DTOs.Common;
using GP_Backend.Models.DTOs.Job;
using GP_Backend.Models.DTOs.Resume;
using System.Text.Json;

namespace GP_Backend.Services.AI;

/// <summary>
/// Service for communicating with the FastAPI AI backend
/// </summary>
public interface IAiService
{
    // CV Parsing
    Task<ApiResponse<ParsedCvResponseDto>> ParseCvAsync(Stream fileStream, string fileName);
    Task<ApiResponse<ParsedCvResponseDto>> ParseCvFromTextAsync(string resumeText);
    
    // ATS Scoring
    Task<ApiResponse<AtsScoreResponseDto>> GetAtsScoreAsync(string resumeText, string? jobDescription = null);
    Task<ApiResponse<JsonElement>> GetCvImprovementsAsync(string resumeText);
    Task<ApiResponse<JsonElement>> GetFullCvAnalysisAsync(string resumeText, bool includeImprovements = true, int jobMatchLimit = 5);
    
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
    Task<ApiResponse<List<JobRecommendationDto>>> MatchJobsFromTextAsync(string resumeText, int limit = 5);
    
    // Job Scraping
    Task<ApiResponse<List<ScrapedJobDto>>> GetScrapedJobsAsync(string? keyword = null, string? location = null, int limit = 50);
    Task<ApiResponse<JsonElement>> GetScrapingStatusAsync();
    Task<ApiResponse> TriggerScrapingAsync(int? maxCategories = null);
    Task<ApiResponse<JsonElement>> GetRecruitmentStatusAsync();
    
    // Interview AI
    Task<ApiResponse<List<GeneratedQuestionDto>>> GenerateInterviewQuestionsAsync(long jobId, string agentType, int questionCount = 10);
    Task<ApiResponse<AnswerEvaluationDto>> EvaluateAnswerAsync(string question, string answer, string? expectedAnswer = null);
    Task<ApiResponse<string>> TranscribeAudioAsync(Stream audioStream);
    Task<ApiResponse<Stream>> TextToSpeechAsync(string text);
    
    // Interview Report
    Task<ApiResponse<string>> GenerateInterviewReportAsync(long sessionId, List<QuestionAnswerPairDto> qaList, float overallScore);

    // Integrity + Interview Gateway Composition
    Task<ApiResponse<IntegritySessionStartResponseDto>> StartIntegritySessionAsync(IntegritySessionStartRequestDto request);
    Task<ApiResponse<IntegritySessionEndResponseDto>> EndIntegritySessionAsync(long integritySessionId);
    Task<ApiResponse<InterviewSessionStartResponseDto>> StartInterviewSessionAsync(InterviewSessionStartRequestDto request);
    Task<ApiResponse<InterviewSessionSummaryDto>> GetInterviewSessionSummaryAsync(string interviewSessionId);
    Task<ApiResponse<JsonElement>> GetInterviewSessionHistoryAsync(string interviewSessionId);
    Task<ApiResponse<UnifiedSessionReportDto>> GetUnifiedSessionReportAsync(long integritySessionId);
}

using GP_Backend.Models.DTOs.Common;
using GP_Backend.Models.DTOs.Interview;

namespace GP_Backend.Services.Interfaces;

public interface IInterviewService
{
    Task<ApiResponse<InterviewSessionDto>> GetSessionAsync(long sessionId);
    Task<ApiResponse<InterviewSessionDto>> ScheduleInterviewAsync(long recruiterId, ScheduleInterviewDto dto);
    Task<ApiResponse<InterviewSessionDto>> StartInterviewAsync(long sessionId, long candidateId);
    Task<ApiResponse<InterviewSessionDto>> EndInterviewAsync(long sessionId);
    
    // Questions
    Task<ApiResponse<List<InterviewQuestionDto>>> GetSessionQuestionsAsync(long sessionId);
    Task<ApiResponse<InterviewQuestionDto>> GetNextQuestionAsync(long sessionId);
    
    // Answers
    Task<ApiResponse<InterviewAnswerDto>> SubmitAnswerAsync(long candidateId, SubmitAnswerDto dto, Stream? audioStream = null);
    
    // Cheating detection
    Task<ApiResponse> ReportCheatingEventAsync(ReportCheatingEventDto dto, Stream? frameImage = null);
    Task<ApiResponse> ReportBrowserEventAsync(ReportBrowserEventDto dto);
    Task<ApiResponse<List<CheatingEventDto>>> GetCheatingEventsAsync(long sessionId);
    
    // Report & Ranking
    Task<ApiResponse<InterviewReportDto>> GetInterviewReportAsync(
        long sessionId,
        long? recruiterId = null,
        long? candidateId = null);
    Task<ApiResponse<List<InterviewRankingDto>>> GetInterviewRankingsAsync(long jobId, long recruiterId);
    
    // List interviews
    Task<ApiResponse<PaginatedResponse<InterviewSessionListDto>>> GetRecruiterInterviewsAsync(long recruiterId, InterviewSearchParams searchParams);
    Task<ApiResponse<PaginatedResponse<InterviewSessionListDto>>> GetCandidateInterviewsAsync(long candidateId, InterviewSearchParams searchParams);
    
    // Video recording
    Task<ApiResponse> UploadVideoRecordingAsync(long sessionId, Stream videoStream, string fileName);
}

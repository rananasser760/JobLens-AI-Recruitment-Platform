using JobLens.Application.Common;
using JobLens.Application.DTOs.Interviews;

namespace JobLens.Application.Interfaces;

public interface IInterviewService
{
    Task<ApiResponse<InterviewSessionDto>> ScheduleAsync(long recruiterUserId, ScheduleInterviewRequest request, CancellationToken cancellationToken);
    Task<ApiResponse<InterviewSessionDto>> StartAsync(long candidateUserId, StartInterviewRequest request, CancellationToken cancellationToken);
    Task<bool> CanUserAccessSessionAsync(long interviewSessionId, long userId, bool isAdmin, bool isRecruiter, bool isCandidate, CancellationToken cancellationToken);
    Task<ApiResponse<InterviewRealtimeResultDto>> ProcessAudioAsync(long interviewSessionId, string base64Audio, int sequence, CancellationToken cancellationToken);
    Task<ApiResponse<IReadOnlyList<string>>> ProcessVideoFrameAsync(long interviewSessionId, string base64Frame, int sequence, CancellationToken cancellationToken);
    Task<ApiResponse<bool>> RecordBrowserEventAsync(BrowserEventRequest request, CancellationToken cancellationToken);
    Task<ApiResponse<InterviewReportDto>> CompleteAsync(long interviewSessionId, CancellationToken cancellationToken);
    Task<ApiResponse<InterviewReportDto>> GetReportAsync(long interviewSessionId, CancellationToken cancellationToken);
}

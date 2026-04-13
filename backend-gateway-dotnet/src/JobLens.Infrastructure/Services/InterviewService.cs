using JobLens.Application.Common;
using JobLens.Application.Contracts;
using JobLens.Application.DTOs.Interviews;
using JobLens.Application.Interfaces;
using JobLens.Domain.Entities;
using JobLens.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace JobLens.Infrastructure.Services;

public sealed class InterviewService(
    Persistence.JobLensDbContext dbContext,
    IAiBackendClient aiBackendClient) : IInterviewService
{
    private static readonly TimeSpan StartInterviewAiTimeout = TimeSpan.FromSeconds(20);

    public async Task<bool> CanUserAccessSessionAsync(long interviewSessionId, long userId, bool isAdmin, bool isRecruiter, bool isCandidate, CancellationToken cancellationToken)
    {
        if (isAdmin)
        {
            return await dbContext.InterviewSessions.AnyAsync(x => x.Id == interviewSessionId, cancellationToken);
        }

        if (isCandidate)
        {
            return await dbContext.InterviewSessions.AnyAsync(
                x => x.Id == interviewSessionId && x.Application.CandidateProfile.UserId == userId,
                cancellationToken);
        }

        if (isRecruiter)
        {
            var recruiterCompanyId = await dbContext.RecruiterProfiles
                .Where(x => x.UserId == userId)
                .Select(x => (long?)x.CompanyId)
                .FirstOrDefaultAsync(cancellationToken);

            if (!recruiterCompanyId.HasValue)
            {
                return false;
            }

            return await dbContext.InterviewSessions.AnyAsync(
                x => x.Id == interviewSessionId && x.Application.JobPosting.CompanyId == recruiterCompanyId.Value,
                cancellationToken);
        }

        return false;
    }

    public async Task<ApiResponse<InterviewSessionDto>> ScheduleAsync(long recruiterUserId, ScheduleInterviewRequest request, CancellationToken cancellationToken)
    {
        var recruiter = await dbContext.RecruiterProfiles.FirstOrDefaultAsync(x => x.UserId == recruiterUserId, cancellationToken);
        var application = await dbContext.Applications.Include(x => x.JobPosting).FirstOrDefaultAsync(x => x.Id == request.ApplicationId, cancellationToken);

        if (recruiter is null || application is null)
        {
            return new ApiResponse<InterviewSessionDto>(false, null, "Recruiter or application not found.", ["not_found"]);
        }

        var session = new InterviewSession
        {
            ApplicationId = application.Id,
            ScheduledAtUtc = request.ScheduledAtUtc,
            CriteriaSnapshot = request.EvaluationCriteria.Trim(),
            Status = InterviewSessionStatus.Scheduled,
        };

        application.Status = ApplicationStatus.InterviewScheduled;
        dbContext.InterviewSessions.Add(session);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ApiResponse<InterviewSessionDto>(true, ToDto(session), "Interview scheduled.");
    }

    public async Task<ApiResponse<InterviewSessionDto>> StartAsync(long candidateUserId, StartInterviewRequest request, CancellationToken cancellationToken)
    {
        var session = await dbContext.InterviewSessions
            .Include(x => x.Application)
                .ThenInclude(x => x.CandidateProfile)
                    .ThenInclude(x => x.User)
            .Include(x => x.Application)
                .ThenInclude(x => x.Resume)
            .Include(x => x.Application)
                .ThenInclude(x => x.JobPosting)
            .FirstOrDefaultAsync(x => x.Id == request.InterviewSessionId, cancellationToken);

        if (session is null || session.Application.CandidateProfile.UserId != candidateUserId)
        {
            return new ApiResponse<InterviewSessionDto>(false, null, "Interview session not found.", ["not_found"]);
        }

        if (!request.ConsentCaptured)
        {
            return new ApiResponse<InterviewSessionDto>(false, null, "Consent must be captured before starting the interview.", ["consent_required"]);
        }

        if (session.Status == InterviewSessionStatus.Live && !string.IsNullOrWhiteSpace(session.InterviewBackendSessionId))
        {
            return new ApiResponse<InterviewSessionDto>(true, ToDto(session), "Interview is already started.");
        }

        if (session.Status is InterviewSessionStatus.Completed or InterviewSessionStatus.Cancelled)
        {
            return new ApiResponse<InterviewSessionDto>(false, null, "This interview session can no longer be started.", ["invalid_state"]);
        }

        var resumeText = session.Application.Resume?.RawText;
        if (string.IsNullOrWhiteSpace(resumeText))
        {
            return new ApiResponse<InterviewSessionDto>(false, null, "No default resume text found for this interview.", ["missing_resume"]);
        }

        var jobDescription = session.Application.JobPosting?.Description;
        if (string.IsNullOrWhiteSpace(jobDescription))
        {
            return new ApiResponse<InterviewSessionDto>(false, null, "Job description is missing for this interview.", ["missing_job_description"]);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(StartInterviewAiTimeout);

        var ai = await aiBackendClient.StartInterviewSessionAsync(
            new StartInterviewAiRequest(
                session.Application.CandidateProfile.User.DisplayName,
                session.Application.CandidateProfile.Id.ToString(),
                resumeText,
                jobDescription,
                session.CriteriaSnapshot,
                5),
            timeoutCts.Token);

        if (!ai.Success || ai.Data is null)
        {
            if (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                return new ApiResponse<InterviewSessionDto>(false, null, "Interview startup is taking too long. Please try again.", ["timeout"]);
            }

            return new ApiResponse<InterviewSessionDto>(false, null, ai.Error?.Message ?? "Could not start AI interview.");
        }

        session.Status = InterviewSessionStatus.Live;
        session.StartedAtUtc = DateTime.UtcNow;
        session.ConsentCapturedAtUtc = DateTime.UtcNow;
        session.InterviewBackendSessionId = ai.Data.InterviewSessionId;
        session.IntegrityBackendSessionId = ai.Data.IntegritySessionId;

        await dbContext.SaveChangesAsync(cancellationToken);
        return new ApiResponse<InterviewSessionDto>(true, ToDto(session), "Interview started.");
    }

    public async Task<ApiResponse<InterviewRealtimeResultDto>> ProcessAudioAsync(long interviewSessionId, string base64Audio, int sequence, CancellationToken cancellationToken)
    {
        var session = await dbContext.InterviewSessions.FirstOrDefaultAsync(x => x.Id == interviewSessionId, cancellationToken);
        if (session is null || string.IsNullOrWhiteSpace(session.InterviewBackendSessionId))
        {
            return new ApiResponse<InterviewRealtimeResultDto>(false, null, "Interview session is not active.", ["not_active"]);
        }

        var ai = await aiBackendClient.AnalyzeAudioAsync(new AudioAnalysisRequest(session.InterviewBackendSessionId, base64Audio, sequence), cancellationToken);
        if (!ai.Success || ai.Data is null)
        {
            return new ApiResponse<InterviewRealtimeResultDto>(false, null, ai.Error?.Message ?? "Audio analysis failed.");
        }

        if (!string.IsNullOrWhiteSpace(ai.Data.Transcript))
        {
            dbContext.InterviewTranscriptSegments.Add(new InterviewTranscriptSegment
            {
                InterviewSessionId = session.Id,
                Sequence = sequence * 2 - 1,
                Speaker = "candidate",
                Source = "stt",
                Content = ai.Data.Transcript,
                OccurredAtUtc = DateTime.UtcNow,
            });
        }

        dbContext.InterviewTranscriptSegments.Add(new InterviewTranscriptSegment
        {
            InterviewSessionId = session.Id,
            Sequence = sequence * 2,
            Speaker = "assistant",
            Source = "llm",
            Content = ai.Data.Reply,
            OccurredAtUtc = DateTime.UtcNow,
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return new ApiResponse<InterviewRealtimeResultDto>(
            true,
            new InterviewRealtimeResultDto(ai.Data.Transcript, ai.Data.Reply, ai.Data.IsComplete, ai.Data.Score, []));
    }

    public async Task<ApiResponse<IReadOnlyList<string>>> ProcessVideoFrameAsync(long interviewSessionId, string base64Frame, int sequence, CancellationToken cancellationToken)
    {
        var session = await dbContext.InterviewSessions.FirstOrDefaultAsync(x => x.Id == interviewSessionId, cancellationToken);
        if (session is null || string.IsNullOrWhiteSpace(session.InterviewBackendSessionId))
        {
            return new ApiResponse<IReadOnlyList<string>>(false, null, "Interview session is not active.", ["not_active"]);
        }

        var ai = await aiBackendClient.AnalyzeVideoAsync(new VideoAnalysisRequest(session.InterviewBackendSessionId, base64Frame, sequence), cancellationToken);
        if (!ai.Success || ai.Data is null)
        {
            return new ApiResponse<IReadOnlyList<string>>(false, null, ai.Error?.Message ?? "Video analysis failed.");
        }

        foreach (var evt in ai.Data.Events)
        {
            dbContext.ProctoringEvents.Add(new ProctoringEvent
            {
                InterviewSessionId = session.Id,
                EventType = evt.EventType,
                Severity = evt.Severity,
                Source = evt.Source,
                PayloadJson = ServiceJson.Serialize(evt),
                MediaReference = evt.MediaReference ?? string.Empty,
                OccurredAtUtc = DateTime.UtcNow,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new ApiResponse<IReadOnlyList<string>>(true, ai.Data.Events.Select(x => x.Description).ToList());
    }

    public async Task<ApiResponse<bool>> RecordBrowserEventAsync(BrowserEventRequest request, CancellationToken cancellationToken)
    {
        var sessionExists = await dbContext.InterviewSessions.AnyAsync(x => x.Id == request.InterviewSessionId, cancellationToken);
        if (!sessionExists)
        {
            return new ApiResponse<bool>(false, false, "Interview session not found.", ["not_found"]);
        }

        dbContext.BrowserTelemetryEvents.Add(new BrowserTelemetryEvent
        {
            InterviewSessionId = request.InterviewSessionId,
            EventType = request.EventType,
            Severity = request.Severity,
            PayloadJson = request.PayloadJson,
            OccurredAtUtc = DateTime.UtcNow,
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return new ApiResponse<bool>(true, true, "Browser event recorded.");
    }

    public async Task<ApiResponse<InterviewReportDto>> CompleteAsync(long interviewSessionId, CancellationToken cancellationToken)
    {
        var session = await dbContext.InterviewSessions
            .Include(x => x.TranscriptSegments)
            .Include(x => x.Reports)
            .FirstOrDefaultAsync(x => x.Id == interviewSessionId, cancellationToken);

        if (session is null)
        {
            return new ApiResponse<InterviewReportDto>(false, null, "Interview session not found.", ["not_found"]);
        }

        var transcript = session.TranscriptSegments
            .OrderBy(x => x.Sequence)
            .Select(x => new TranscriptEntryDto(x.Sequence, x.Speaker, x.Content, x.OccurredAtUtc))
            .ToList();

        var ai = await aiBackendClient.FinalizeInterviewAsync(new FinalizeInterviewRequest(session.InterviewBackendSessionId, session.IntegrityBackendSessionId, transcript), cancellationToken);
        if (!ai.Success || ai.Data is null)
        {
            return new ApiResponse<InterviewReportDto>(false, null, ai.Error?.Message ?? "Could not finalize interview.");
        }

        session.Status = InterviewSessionStatus.Completed;
        session.EndedAtUtc = DateTime.UtcNow;
        session.FinalScore = ai.Data.FinalScore;
        session.FinalVerdict = ai.Data.Verdict;

        var report = new InterviewReport
        {
            InterviewSessionId = session.Id,
            RecruiterReportJson = ai.Data.RecruiterReportJson,
            CandidateFeedbackJson = ai.Data.CandidateFeedbackJson,
            FinalScore = ai.Data.FinalScore,
            Verdict = ai.Data.Verdict,
            GeneratedAtUtc = DateTime.UtcNow,
        };

        dbContext.InterviewReports.Add(report);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ApiResponse<InterviewReportDto>(true, new InterviewReportDto(session.Id, report.FinalScore, report.Verdict, report.RecruiterReportJson, report.CandidateFeedbackJson), "Interview completed.");
    }

    public async Task<ApiResponse<InterviewReportDto>> GetReportAsync(long interviewSessionId, CancellationToken cancellationToken)
    {
        var report = await dbContext.InterviewReports
            .Where(x => x.InterviewSessionId == interviewSessionId)
            .OrderByDescending(x => x.GeneratedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (report is null)
        {
            return new ApiResponse<InterviewReportDto>(false, null, "Report not found.", ["not_found"]);
        }

        return new ApiResponse<InterviewReportDto>(true, new InterviewReportDto(interviewSessionId, report.FinalScore, report.Verdict, report.RecruiterReportJson, report.CandidateFeedbackJson));
    }

    private static InterviewSessionDto ToDto(InterviewSession session) =>
        new(session.Id, session.Status, session.ScheduledAtUtc, session.StartedAtUtc, session.EndedAtUtc, session.InterviewBackendSessionId, session.IntegrityBackendSessionId, session.CriteriaSnapshot, session.FinalScore, session.FinalVerdict);
}

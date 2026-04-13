using JobLens.Api.Compatibility;
using JobLens.Api.Contracts;
using JobLens.Application.Common;
using JobLens.Application.Interfaces;
using JobLens.Infrastructure.Persistence;
using JobLens.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using InterviewContracts = JobLens.Application.DTOs.Interviews;

namespace JobLens.Api.Controllers;

[Authorize]
[Route("api/interviews")]
public sealed class InterviewsController(IInterviewService interviewService, JobLensDbContext dbContext) : AppControllerBase
{
    [Authorize(Roles = "Recruiter,Admin")]
    [HttpPost("schedule")]
    public async Task<IActionResult> Schedule([FromBody] ScheduleInterviewCompatRequest request, CancellationToken cancellationToken)
    {
        var application = await dbContext.Applications
            .Include(x => x.JobPosting)
            .FirstOrDefaultAsync(x => x.Id == request.ApplicationId, cancellationToken);

        if (application is null)
        {
            return NotFound(new ApiResponse<InterviewSessionDto>(false, null, "Application not found.", ["not_found"]));
        }

        if (!await CanManageApplicationAsync(application, cancellationToken))
        {
            return Forbid();
        }

        var session = new Domain.Entities.InterviewSession
        {
            ApplicationId = application.Id,
            ScheduledAtUtc = request.ScheduledAt,
            Status = Domain.Enums.InterviewSessionStatus.Scheduled,
            CriteriaSnapshot = string.IsNullOrWhiteSpace(request.AgentType)
                ? "General interview"
                : request.AgentType,
            AgentType = string.IsNullOrWhiteSpace(request.AgentType) ? "Mixed" : request.AgentType,
            InterviewTitle = request.InterviewTitle?.Trim() ?? string.Empty,
            MaxQuestions = 5,
        };

        application.Status = Domain.Enums.ApplicationStatus.InterviewScheduled;
        dbContext.InterviewSessions.Add(session);
        await dbContext.SaveChangesAsync(cancellationToken);

        var loaded = await LoadSessionAsync(session.Id, cancellationToken);
        return Ok(new ApiResponse<InterviewSessionDto>(true, ToInterviewSessionDto(loaded!), "Interview scheduled."));
    }

    [Authorize(Roles = "Candidate")]
    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] InterviewContracts.StartInterviewRequest request, CancellationToken cancellationToken) =>
        Ok(await interviewService.StartAsync(GetRequiredUserId(), request, cancellationToken));

    [Authorize(Roles = "Candidate")]
    [HttpPost("{interviewSessionId:long}/start")]
    public async Task<IActionResult> StartById(long interviewSessionId, CancellationToken cancellationToken)
    {
        var response = await interviewService.StartAsync(GetRequiredUserId(), new InterviewContracts.StartInterviewRequest(interviewSessionId, true), cancellationToken);
        if (!response.Success || response.Data is null)
        {
            return BadRequest(new ApiResponse<InterviewSessionDto>(false, null, response.Message, response.Errors));
        }

        var loaded = await LoadSessionAsync(interviewSessionId, cancellationToken);
        return Ok(new ApiResponse<InterviewSessionDto>(true, loaded is null ? null : ToInterviewSessionDto(loaded), response.Message));
    }

    [Authorize(Roles = "Candidate")]
    [HttpPost("{interviewSessionId:long}/end")]
    public async Task<IActionResult> End(long interviewSessionId, CancellationToken cancellationToken)
    {
        var report = await CompleteWithFallbackAsync(interviewSessionId, cancellationToken);
        return report.Success
            ? Ok(report)
            : BadRequest(report);
    }

    [HttpPost("browser-event")]
    public async Task<IActionResult> RecordBrowserEvent([FromBody] InterviewContracts.BrowserEventRequest request, CancellationToken cancellationToken) =>
        Ok(await interviewService.RecordBrowserEventAsync(request, cancellationToken));

    [HttpPost("{interviewSessionId:long}/browser-event")]
    public async Task<IActionResult> RecordBrowserEventCompat(long interviewSessionId, [FromBody] ReportBrowserEventCompatRequest request, CancellationToken cancellationToken)
    {
        var payload = new
        {
            request.TabSwitchCount,
            request.FocusLossCount,
            request.CopyPasteCount,
            request.RightClickCount,
            request.DetailsJson,
        };

        var result = await interviewService.RecordBrowserEventAsync(
            new InterviewContracts.BrowserEventRequest(
                interviewSessionId,
                "browser-activity",
                "info",
                System.Text.Json.JsonSerializer.Serialize(payload)),
            cancellationToken);

        return Ok(result);
    }

    [HttpPost("cheating-event")]
    public async Task<IActionResult> ReportCheatingEvent(
        [FromForm] long sessionId,
        [FromForm] string eventType,
        [FromForm] double? confidence,
        [FromForm] string? details,
        [FromForm] int? timestampSeconds,
        [FromForm] IFormFile? frameImage,
        CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Interview session not found.", ["not_found"]));
        }

        if (!await CanAccessSessionAsync(session, cancellationToken))
        {
            return Forbid();
        }

        if (frameImage is not null)
        {
            await using var stream = frameImage.OpenReadStream();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            var base64 = Convert.ToBase64String(memory.ToArray());
            await interviewService.ProcessVideoFrameAsync(sessionId, base64, timestampSeconds ?? 0, cancellationToken);
        }

        dbContext.ProctoringEvents.Add(new Domain.Entities.ProctoringEvent
        {
            InterviewSessionId = sessionId,
            EventType = eventType,
            Severity = confidence.HasValue && confidence.Value > 0.75 ? "high" : "medium",
            Source = "client",
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { confidence, details, timestampSeconds }),
            OccurredAtUtc = DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new ApiResponse<bool>(true, true, "Cheating event recorded."));
    }

    [HttpPost("{interviewSessionId:long}/video")]
    public async Task<IActionResult> UploadVideo(long interviewSessionId, [FromForm] IFormFile videoFile, CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(interviewSessionId, cancellationToken);
        if (session is null)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Interview session not found.", ["not_found"]));
        }

        if (!await CanAccessSessionAsync(session, cancellationToken))
        {
            return Forbid();
        }

        await using var stream = videoFile.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        var base64 = Convert.ToBase64String(memory.ToArray());

        var ai = await interviewService.ProcessVideoFrameAsync(interviewSessionId, base64, 0, cancellationToken);
        return Ok(new ApiResponse<bool>(ai.Success, ai.Success, ai.Message, ai.Errors));
    }

    [HttpPost("{interviewSessionId:long}/complete")]
    public async Task<IActionResult> Complete(long interviewSessionId, CancellationToken cancellationToken) =>
        Ok(await interviewService.CompleteAsync(interviewSessionId, cancellationToken));

    [HttpGet("{interviewSessionId:long}/report")]
    public async Task<IActionResult> Report(long interviewSessionId, CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(interviewSessionId, cancellationToken);
        if (session is null)
        {
            return NotFound(new ApiResponse<InterviewReportViewDto>(false, null, "Interview session not found.", ["not_found"]));
        }

        if (!await CanAccessSessionAsync(session, cancellationToken))
        {
            return Forbid();
        }

        var report = await BuildReportAsync(session, cancellationToken);
        return Ok(new ApiResponse<InterviewReportViewDto>(true, report));
    }

    [HttpGet("{interviewSessionId:long}")]
    public async Task<IActionResult> GetSession(long interviewSessionId, CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(interviewSessionId, cancellationToken);
        if (session is null)
        {
            return NotFound(new ApiResponse<InterviewSessionDto>(false, null, "Interview session not found.", ["not_found"]));
        }

        if (!await CanAccessSessionAsync(session, cancellationToken))
        {
            return Forbid();
        }

        return Ok(new ApiResponse<InterviewSessionDto>(true, ToInterviewSessionDto(session)));
    }

    [HttpGet("{interviewSessionId:long}/questions")]
    public async Task<IActionResult> GetQuestions(long interviewSessionId, CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(interviewSessionId, cancellationToken);
        if (session is null)
        {
            return NotFound(new ApiResponse<IReadOnlyList<InterviewQuestionDto>>(false, null, "Interview session not found.", ["not_found"]));
        }

        if (!await CanAccessSessionAsync(session, cancellationToken))
        {
            return Forbid();
        }

        var questions = BuildQuestions(session);
        return Ok(new ApiResponse<IReadOnlyList<InterviewQuestionDto>>(true, questions));
    }

    [HttpGet("{interviewSessionId:long}/next-question")]
    public async Task<IActionResult> GetNextQuestion(long interviewSessionId, CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(interviewSessionId, cancellationToken);
        if (session is null)
        {
            return NotFound(new ApiResponse<InterviewQuestionDto>(false, null, "Interview session not found.", ["not_found"]));
        }

        if (!await CanAccessSessionAsync(session, cancellationToken))
        {
            return Forbid();
        }

        var question = BuildQuestions(session).FirstOrDefault(x => !x.IsAnswered);
        if (question is null)
        {
            return Ok(new ApiResponse<InterviewQuestionDto>(false, null, "No pending question."));
        }

        return Ok(new ApiResponse<InterviewQuestionDto>(true, question));
    }

    [HttpPost("submit-answer")]
    public async Task<IActionResult> SubmitAnswer(
        [FromForm] long questionId,
        [FromForm] string? answerText,
        [FromForm] int? responseDurationSeconds,
        [FromForm] IFormFile? audioFile,
        CancellationToken cancellationToken)
    {
        var sessionId = questionId <= 0 ? 0 : ((questionId - 1) / 1000) + 1;
        var session = await LoadSessionAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Interview session not found for provided question.", ["not_found"]));
        }

        if (!await CanAccessSessionAsync(session, cancellationToken))
        {
            return Forbid();
        }

        var candidateText = answerText?.Trim() ?? string.Empty;
        if (audioFile is not null)
        {
            await using var stream = audioFile.OpenReadStream();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);

            if (!string.IsNullOrWhiteSpace(session.InterviewBackendSessionId))
            {
                var base64 = Convert.ToBase64String(memory.ToArray());
                var seq = session.TranscriptSegments.Count / 2 + 1;
                var aiResult = await interviewService.ProcessAudioAsync(session.Id, base64, seq, cancellationToken);
                if (aiResult.Success)
                {
                    return Ok(new ApiResponse<bool>(true, true, "Answer submitted."));
                }
            }
        }

        if (candidateText.Length == 0)
        {
            candidateText = "Candidate provided an audio response.";
        }

        var nextSeq = session.TranscriptSegments.Count == 0
            ? 1
            : session.TranscriptSegments.Max(x => x.Sequence) + 1;
        dbContext.InterviewTranscriptSegments.Add(new Domain.Entities.InterviewTranscriptSegment
        {
            InterviewSessionId = session.Id,
            Sequence = nextSeq,
            Speaker = "candidate",
            Source = "manual",
            Content = candidateText,
            OccurredAtUtc = DateTime.UtcNow,
        });
        dbContext.InterviewTranscriptSegments.Add(new Domain.Entities.InterviewTranscriptSegment
        {
            InterviewSessionId = session.Id,
            Sequence = nextSeq + 1,
            Speaker = "assistant",
            Source = "manual",
            Content = "Thank you. Moving to the next question.",
            OccurredAtUtc = DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new ApiResponse<bool>(true, true, "Answer submitted."));
    }

    [HttpGet("{interviewSessionId:long}/cheating-events")]
    public async Task<IActionResult> GetCheatingEvents(long interviewSessionId, CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(interviewSessionId, cancellationToken);
        if (session is null)
        {
            return NotFound(new ApiResponse<IReadOnlyList<CheatingEventDto>>(false, null, "Interview session not found.", ["not_found"]));
        }

        if (!await CanAccessSessionAsync(session, cancellationToken))
        {
            return Forbid();
        }

        var events = session.ProctoringEvents
            .OrderByDescending(x => x.OccurredAtUtc)
            .Select(x => new CheatingEventDto(
                x.Id,
                session.Id,
                x.EventType,
                null,
                x.OccurredAtUtc,
                string.IsNullOrWhiteSpace(x.PayloadJson) ? null : x.PayloadJson,
                null))
            .ToList();

        return Ok(new ApiResponse<IReadOnlyList<CheatingEventDto>>(true, events));
    }

    [Authorize(Roles = "Recruiter,Admin")]
    [HttpGet("job/{jobId:long}/rankings")]
    public async Task<IActionResult> GetJobRankings(long jobId, CancellationToken cancellationToken)
    {
        var applications = await dbContext.Applications
            .Include(x => x.CandidateProfile)
            .ThenInclude(x => x.User)
            .Include(x => x.AtsAssessments)
            .Include(x => x.InterviewSessions)
            .Where(x => x.JobPostingId == jobId)
            .ToListAsync(cancellationToken);

        var rankings = applications
            .Select(x =>
            {
                var ats = x.AtsAssessments.OrderByDescending(a => a.EvaluatedAtUtc ?? a.CreatedAtUtc).FirstOrDefault()?.Score ?? 0;
                var interview = x.InterviewSessions.OrderByDescending(s => s.UpdatedAtUtc).FirstOrDefault()?.FinalScore ?? 0;
                var score = Math.Round((ats * 0.4) + (interview * 0.6), 2);
                return new InterviewRankingDto(
                    x.Id,
                    x.CandidateProfileId,
                    x.CandidateProfile.User.DisplayName,
                    interview,
                    ats,
                    score,
                    0,
                    x.InterviewSessions.Any(s => s.ProctoringEvents.Any()),
                    FrontendStatusMapper.ToFrontend(x.Status));
            })
            .OrderByDescending(x => x.RankingScore ?? 0)
            .Select((x, idx) => x with { Rank = idx + 1 })
            .ToList();

        return Ok(new ApiResponse<IReadOnlyList<InterviewRankingDto>>(true, rankings));
    }

    [Authorize(Roles = "Recruiter,Admin")]
    [HttpGet("my-interviews")]
    public async Task<IActionResult> GetRecruiterInterviews(
        [FromQuery] string? status,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var recruiter = await dbContext.RecruiterProfiles.FirstOrDefaultAsync(x => x.UserId == GetRequiredUserId(), cancellationToken);
        if (recruiter is null && !User.IsInRole("Admin"))
        {
            return NotFound(new ApiResponse<PaginatedResponseDto<InterviewSessionListDto>>(false, null, "Recruiter profile not found.", ["not_found"]));
        }

        var query = dbContext.InterviewSessions
            .Include(x => x.Application)
            .ThenInclude(x => x.CandidateProfile)
            .ThenInclude(x => x.User)
            .Include(x => x.Application)
            .ThenInclude(x => x.JobPosting)
            .AsQueryable();

        if (recruiter is not null)
        {
            query = query.Where(x => x.Application.JobPosting.CompanyId == recruiter.CompanyId);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = status.Trim().ToLowerInvariant();
            query = query.Where(x => FrontendStatusMapper.ToFrontend(x.Status).ToLower().Contains(normalized));
        }

        var total = await query.CountAsync(cancellationToken);
        var safePage = Math.Max(1, pageNumber);
        var safeSize = Math.Clamp(pageSize, 1, 200);
        var items = await query.OrderByDescending(x => x.ScheduledAtUtc ?? x.CreatedAtUtc)
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .ToListAsync(cancellationToken);

        var mapped = items.Select(ToInterviewSessionListDto).ToList();
        var page = FrontendStatusMapper.ToPage(mapped, safePage, safeSize, total);
        return Ok(new ApiResponse<PaginatedResponseDto<InterviewSessionListDto>>(true, page));
    }

    [Authorize(Roles = "Candidate")]
    [HttpGet("candidate-interviews")]
    public async Task<IActionResult> GetCandidateInterviews(
        [FromQuery] string? status,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var query = dbContext.InterviewSessions
            .Include(x => x.Application)
            .ThenInclude(x => x.CandidateProfile)
            .ThenInclude(x => x.User)
            .Include(x => x.Application)
            .ThenInclude(x => x.JobPosting)
            .Where(x => x.Application.CandidateProfile.UserId == userId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = status.Trim().ToLowerInvariant();
            query = query.Where(x => FrontendStatusMapper.ToFrontend(x.Status).ToLower().Contains(normalized));
        }

        var total = await query.CountAsync(cancellationToken);
        var safePage = Math.Max(1, pageNumber);
        var safeSize = Math.Clamp(pageSize, 1, 200);
        var items = await query.OrderByDescending(x => x.ScheduledAtUtc ?? x.CreatedAtUtc)
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .ToListAsync(cancellationToken);

        var mapped = items.Select(ToInterviewSessionListDto).ToList();
        var page = FrontendStatusMapper.ToPage(mapped, safePage, safeSize, total);
        return Ok(new ApiResponse<PaginatedResponseDto<InterviewSessionListDto>>(true, page));
    }

    [Authorize(Roles = "Recruiter,Admin")]
    [HttpPost("{interviewSessionId:long}/cancel")]
    public async Task<IActionResult> Cancel(long interviewSessionId, [FromBody] CancelInterviewRequest request, CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(interviewSessionId, cancellationToken);
        if (session is null)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Interview session not found.", ["not_found"]));
        }

        if (!await CanManageApplicationAsync(session.Application, cancellationToken))
        {
            return Forbid();
        }

        if (session.Status != Domain.Enums.InterviewSessionStatus.Scheduled)
        {
            return BadRequest(new ApiResponse<bool>(false, false, "Only scheduled interviews can be cancelled."));
        }

        session.Status = Domain.Enums.InterviewSessionStatus.Cancelled;
        session.FinalVerdict = request.Reason?.Trim() ?? session.FinalVerdict;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new ApiResponse<bool>(true, true, "Interview cancelled."));
    }

    [Authorize(Roles = "Recruiter,Admin")]
    [HttpPost("{interviewSessionId:long}/reschedule")]
    public async Task<IActionResult> Reschedule(long interviewSessionId, [FromBody] RescheduleInterviewRequest request, CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(interviewSessionId, cancellationToken);
        if (session is null)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Interview session not found.", ["not_found"]));
        }

        if (!await CanManageApplicationAsync(session.Application, cancellationToken))
        {
            return Forbid();
        }

        if (session.Status != Domain.Enums.InterviewSessionStatus.Scheduled)
        {
            return BadRequest(new ApiResponse<bool>(false, false, "Only scheduled interviews can be rescheduled."));
        }

        if (request.ScheduledAt <= DateTime.UtcNow)
        {
            return BadRequest(new ApiResponse<bool>(false, false, "Scheduled date must be in the future."));
        }

        session.ScheduledAtUtc = request.ScheduledAt;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new ApiResponse<bool>(true, true, "Interview rescheduled."));
    }

    private async Task<Domain.Entities.InterviewSession?> LoadSessionAsync(long interviewSessionId, CancellationToken cancellationToken)
    {
        return await dbContext.InterviewSessions
            .Include(x => x.Application)
            .ThenInclude(x => x.CandidateProfile)
            .ThenInclude(x => x.User)
            .Include(x => x.Application)
            .ThenInclude(x => x.JobPosting)
            .ThenInclude(x => x.Company)
            .Include(x => x.Application)
            .ThenInclude(x => x.Resume)
            .Include(x => x.TranscriptSegments)
            .Include(x => x.ProctoringEvents)
            .Include(x => x.BrowserEvents)
            .Include(x => x.Reports)
            .FirstOrDefaultAsync(x => x.Id == interviewSessionId, cancellationToken);
    }

    private async Task<bool> CanAccessSessionAsync(Domain.Entities.InterviewSession session, CancellationToken cancellationToken)
    {
        if (User.IsInRole("Admin"))
        {
            return true;
        }

        var userId = GetRequiredUserId();
        if (User.IsInRole("Candidate"))
        {
            return session.Application.CandidateProfile.UserId == userId;
        }

        if (User.IsInRole("Recruiter"))
        {
            return await CanManageApplicationAsync(session.Application, cancellationToken);
        }

        return false;
    }

    private async Task<bool> CanManageApplicationAsync(Domain.Entities.JobApplication application, CancellationToken cancellationToken)
    {
        if (User.IsInRole("Admin"))
        {
            return true;
        }

        var recruiter = await dbContext.RecruiterProfiles.FirstOrDefaultAsync(x => x.UserId == GetRequiredUserId(), cancellationToken);
        return recruiter is not null && application.JobPosting.CompanyId == recruiter.CompanyId;
    }

    private static InterviewSessionDto ToInterviewSessionDto(Domain.Entities.InterviewSession session)
    {
        var answeredCount = session.TranscriptSegments.Count(x => string.Equals(x.Speaker, "candidate", StringComparison.OrdinalIgnoreCase));
        var candidateName = session.Application.CandidateProfile.User.DisplayName;
        var jobTitle = session.Application.JobPosting.Title;

        return new InterviewSessionDto(
            session.Id,
            session.ApplicationId,
            string.IsNullOrWhiteSpace(session.AgentType) ? "Mixed" : session.AgentType,
            string.IsNullOrWhiteSpace(session.InterviewTitle) ? null : session.InterviewTitle,
            session.ScheduledAtUtc,
            session.StartedAtUtc,
            session.EndedAtUtc,
            session.FinalScore,
            session.ProctoringEvents.Any(),
            Math.Max(session.MaxQuestions, 1),
            answeredCount,
            FrontendStatusMapper.ToFrontend(session.Status),
            long.TryParse(session.IntegrityBackendSessionId, out var integrityId) ? integrityId : null,
            string.IsNullOrWhiteSpace(session.InterviewBackendSessionId) ? null : session.InterviewBackendSessionId,
            session.Reports.OrderByDescending(x => x.GeneratedAtUtc).FirstOrDefault()?.RecruiterReportJson,
            session.Reports.OrderByDescending(x => x.GeneratedAtUtc).FirstOrDefault()?.CandidateFeedbackJson,
            candidateName,
            jobTitle,
            session.ProctoringEvents.Count);
    }

    private static InterviewSessionListDto ToInterviewSessionListDto(Domain.Entities.InterviewSession session)
    {
        return new InterviewSessionListDto(
            session.Id,
            string.IsNullOrWhiteSpace(session.InterviewTitle) ? null : session.InterviewTitle,
            session.ScheduledAtUtc,
            FrontendStatusMapper.ToFrontend(session.Status),
            long.TryParse(session.IntegrityBackendSessionId, out var integrityId) ? integrityId : null,
            string.IsNullOrWhiteSpace(session.InterviewBackendSessionId) ? null : session.InterviewBackendSessionId,
            session.FinalScore,
            session.ProctoringEvents.Any(),
            session.Application.CandidateProfile.User.DisplayName,
            session.Application.JobPosting.Title);
    }

    private static IReadOnlyList<InterviewQuestionDto> BuildQuestions(Domain.Entities.InterviewSession session)
    {
        var assistantLines = session.TranscriptSegments
            .Where(x => string.Equals(x.Speaker, "assistant", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Sequence)
            .ToList();
        var candidateLines = session.TranscriptSegments
            .Where(x => string.Equals(x.Speaker, "candidate", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Sequence)
            .ToList();

        var generated = new List<InterviewQuestionDto>();
        if (assistantLines.Count == 0)
        {
            var prompts = GenerateDefaultPrompts(session.CriteriaSnapshot, Math.Max(session.MaxQuestions, 5));
            for (var i = 0; i < prompts.Count; i++)
            {
                var questionId = (session.Id - 1) * 1000 + i + 1;
                generated.Add(new InterviewQuestionDto(
                    questionId,
                    session.Id,
                    prompts[i],
                    i + 1,
                    null,
                    null,
                    null,
                    i < candidateLines.Count,
                    i < candidateLines.Count
                        ? new InterviewAnswerDto(
                            questionId,
                            questionId,
                            candidateLines[i].Content,
                            null,
                            null,
                            null,
                            candidateLines[i].OccurredAtUtc)
                        : null));
            }

            return generated;
        }

        for (var i = 0; i < assistantLines.Count; i++)
        {
            var questionId = (session.Id - 1) * 1000 + i + 1;
            var answer = i < candidateLines.Count
                ? new InterviewAnswerDto(
                    questionId,
                    questionId,
                    candidateLines[i].Content,
                    null,
                    null,
                    null,
                    candidateLines[i].OccurredAtUtc)
                : null;

            generated.Add(new InterviewQuestionDto(
                questionId,
                session.Id,
                assistantLines[i].Content,
                i + 1,
                null,
                null,
                null,
                answer is not null,
                answer));
        }

        return generated;
    }

    private static List<string> GenerateDefaultPrompts(string criteria, int max)
    {
        var prompts = new List<string>();
        if (!string.IsNullOrWhiteSpace(criteria))
        {
            prompts.Add($"Tell us about your experience related to: {criteria}.");
        }

        prompts.Add("Walk through a project where you solved a challenging problem.");
        prompts.Add("How do you prioritize tasks when deadlines are tight?");
        prompts.Add("Describe how you collaborate with a cross-functional team.");
        prompts.Add("What would you improve in your previous role and why?");

        return prompts.Take(Math.Max(1, max)).ToList();
    }

    private async Task<ApiResponse<InterviewSessionDto>> CompleteWithFallbackAsync(long interviewSessionId, CancellationToken cancellationToken)
    {
        var complete = await interviewService.CompleteAsync(interviewSessionId, cancellationToken);
        if (complete.Success)
        {
            var updated = await LoadSessionAsync(interviewSessionId, cancellationToken);
            return new ApiResponse<InterviewSessionDto>(true, updated is null ? null : ToInterviewSessionDto(updated), complete.Message);
        }

        var session = await LoadSessionAsync(interviewSessionId, cancellationToken);
        if (session is null)
        {
            return new ApiResponse<InterviewSessionDto>(false, null, complete.Message, complete.Errors);
        }

        session.Status = Domain.Enums.InterviewSessionStatus.Completed;
        session.EndedAtUtc = DateTime.UtcNow;
        session.FinalVerdict = string.IsNullOrWhiteSpace(session.FinalVerdict) ? "Completed" : session.FinalVerdict;
        session.FinalScore ??= 0;
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ApiResponse<InterviewSessionDto>(true, ToInterviewSessionDto(session), "Interview completed with fallback summary.");
    }

    private Task<InterviewReportViewDto> BuildReportAsync(Domain.Entities.InterviewSession session, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var eventsByType = session.ProctoringEvents
            .GroupBy(x => x.EventType)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

        var questionScores = BuildQuestions(session)
            .Select(x => new QuestionScoreDto(x.QuestionText, x.Category, x.Answer?.AiScore, x.Answer?.AiFeedback))
            .ToList();

        var report = session.Reports.OrderByDescending(x => x.GeneratedAtUtc).FirstOrDefault();
        var durationMinutes = session.StartedAtUtc.HasValue && session.EndedAtUtc.HasValue
            ? Math.Max(0, (int)Math.Round((session.EndedAtUtc.Value - session.StartedAtUtc.Value).TotalMinutes))
            : 0;

        var dto = new InterviewReportViewDto(
            session.Id,
            session.Application.CandidateProfile.User.DisplayName,
            session.Application.JobPosting.Title,
            session.StartedAtUtc,
            session.EndedAtUtc,
            durationMinutes,
            session.FinalScore,
            questionScores,
            new CheatingReportDto(
                session.ProctoringEvents.Any(),
                session.ProctoringEvents.Count,
                eventsByType,
                session.BrowserEvents.Count(x => x.EventType.Contains("tab", StringComparison.OrdinalIgnoreCase)),
                session.BrowserEvents.Count(x => x.EventType.Contains("focus", StringComparison.OrdinalIgnoreCase))),
            report?.CandidateFeedbackJson,
            string.IsNullOrWhiteSpace(session.FinalVerdict) ? null : session.FinalVerdict);

        return Task.FromResult(dto);
    }
}

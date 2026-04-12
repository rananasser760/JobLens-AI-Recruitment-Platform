using System.Text.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using GP_Backend.Data;
using GP_Backend.Models.DTOs.Common;
using GP_Backend.Models.DTOs.Interview;
using GP_Backend.Models.Entities;
using GP_Backend.Models.Enums;
using GP_Backend.Services.Interfaces;
using GP_Backend.Services.AI;

namespace GP_Backend.Services.Implementations;

public class InterviewService : IInterviewService
{
    private readonly AppDbContext _context;
    private readonly IFileStorageService _fileStorage;
    private readonly IAiService _aiBackend;
    private readonly IEmailService _emailService;
    private readonly IAuditService _auditService;
    private readonly ILogger<InterviewService> _logger;

    public InterviewService(
        AppDbContext context,
        IFileStorageService fileStorage,
        IAiService aiBackend,
        IEmailService emailService,
        IAuditService auditService,
        ILogger<InterviewService> logger)
    {
        _context = context;
        _fileStorage = fileStorage;
        _aiBackend = aiBackend;
        _emailService = emailService;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<ApiResponse<InterviewSessionDto>> GetSessionAsync(long sessionId)
    {
        try
        {
            var session = await _context.InterviewSessions
                .Include(s => s.Application).ThenInclude(a => a.Candidate)
                .Include(s => s.Application).ThenInclude(a => a.Job)
                .Include(s => s.CheatingEvents)
                .FirstOrDefaultAsync(s => s.Id == sessionId);

            if (session == null)
            {
                return ApiResponse<InterviewSessionDto>.FailureResponse("Interview session not found");
            }

            return ApiResponse<InterviewSessionDto>.SuccessResponse(MapToSessionDto(session));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting interview session");
            return ApiResponse<InterviewSessionDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<InterviewSessionDto>> ScheduleInterviewAsync(long recruiterId, ScheduleInterviewDto dto)
    {
        try
        {
            var application = await _context.Applications
                .Include(a => a.Candidate).ThenInclude(c => c.User)
                .Include(a => a.Job)
                .FirstOrDefaultAsync(a => a.Id == dto.ApplicationId);

            if (application == null)
            {
                return ApiResponse<InterviewSessionDto>.FailureResponse("Application not found");
            }

            // Verify recruiter owns the job
            if (application.Job.RecruiterId != recruiterId)
            {
                return ApiResponse<InterviewSessionDto>.FailureResponse("You don't have permission to schedule this interview");
            }

            var session = new InterviewSession
            {
                ApplicationId = dto.ApplicationId,
                AgentType = dto.AgentType,
                InterviewTitle = dto.InterviewTitle ?? $"Interview for {application.Job.Title}",
                ScheduledAt = dto.ScheduledAt,
                Status = "Scheduled",
                CheatingDetected = false,
                TotalQuestions = 0,
                AnsweredQuestions = 0
            };

            _context.InterviewSessions.Add(session);
            await _context.SaveChangesAsync();

            // Generate interview questions from AI
            // TODO: Call AI backend to generate questions
            // var questionsResponse = await _aiBackend.GenerateInterviewQuestionsAsync(application.JobId, dto.AgentType.ToString(), 10);

            // For now, add placeholder questions
            var questions = new List<InterviewQuestion>
            {
                new() { SessionId = session.Id, QuestionText = "Tell me about yourself and your experience.", OrderIndex = 1, Category = "Introduction", Difficulty = "Easy" },
                new() { SessionId = session.Id, QuestionText = "What are your key technical skills?", OrderIndex = 2, Category = "Technical", Difficulty = "Medium" },
                new() { SessionId = session.Id, QuestionText = "Describe a challenging project you worked on.", OrderIndex = 3, Category = "Experience", Difficulty = "Medium" },
                new() { SessionId = session.Id, QuestionText = "How do you handle tight deadlines?", OrderIndex = 4, Category = "Behavioral", Difficulty = "Medium" },
                new() { SessionId = session.Id, QuestionText = "Where do you see yourself in 5 years?", OrderIndex = 5, Category = "Goals", Difficulty = "Easy" }
            };

            _context.InterviewQuestions.AddRange(questions);
            session.TotalQuestions = questions.Count;
            await _context.SaveChangesAsync();

            // Update application status
            application.Status = ApplicationStatus.InterviewScheduled;
            await _context.SaveChangesAsync();

            // Send interview scheduled email
            await _emailService.SendInterviewScheduledEmailAsync(session.Id);

            var recruiter = await _context.Recruiters.FindAsync(recruiterId);
            await _auditService.LogAsync(recruiter?.UserId, "ScheduleInterview", "InterviewSession", session.Id);

            // Reload with includes
            var createdSession = await _context.InterviewSessions
                .Include(s => s.Application).ThenInclude(a => a.Candidate)
                .Include(s => s.Application).ThenInclude(a => a.Job)
                .FirstAsync(s => s.Id == session.Id);

            return ApiResponse<InterviewSessionDto>.SuccessResponse(MapToSessionDto(createdSession), "Interview scheduled successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scheduling interview");
            return ApiResponse<InterviewSessionDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<InterviewSessionDto>> StartInterviewAsync(long sessionId, long candidateId)
    {
        try
        {
            var session = await _context.InterviewSessions
                .Include(s => s.Application).ThenInclude(a => a.Candidate)
                .Include(s => s.Application).ThenInclude(a => a.Candidate).ThenInclude(c => c.Skills)
                .Include(s => s.Application).ThenInclude(a => a.Job)
                .Include(s => s.Application).ThenInclude(a => a.Resume).ThenInclude(r => r!.ParsingResult)
                .FirstOrDefaultAsync(s => s.Id == sessionId);

            if (session == null)
            {
                return ApiResponse<InterviewSessionDto>.FailureResponse("Interview session not found");
            }

            // Verify candidate owns this interview
            if (session.Application.CandidateId != candidateId)
            {
                return ApiResponse<InterviewSessionDto>.FailureResponse("You don't have permission to start this interview");
            }

            if (session.Status != "Scheduled")
            {
                return ApiResponse<InterviewSessionDto>.FailureResponse($"Interview cannot be started. Current status: {session.Status}");
            }

            var integrityStart = await _aiBackend.StartIntegritySessionAsync(new IntegritySessionStartRequestDto
            {
                CandidateName = session.Application.Candidate.FullName,
                CandidateId = candidateId.ToString()
            });

            if (!integrityStart.Success || integrityStart.Data == null)
            {
                var message = string.IsNullOrWhiteSpace(integrityStart.Message)
                    ? "Failed to start integrity monitoring session"
                    : integrityStart.Message;
                return ApiResponse<InterviewSessionDto>.FailureResponse(message);
            }

            var interviewStartRequest = new InterviewSessionStartRequestDto
            {
                CvText = BuildCandidateCvText(session.Application),
                JobDescription = BuildJobDescription(session.Application.Job),
                EvaluationCriteria = BuildEvaluationCriteria(session.AgentType),
                MaxQuestions = session.TotalQuestions > 0 ? session.TotalQuestions : 5,
                CandidateName = session.Application.Candidate.FullName,
                CandidateId = candidateId.ToString(),
                IntegrityDbSessionId = integrityStart.Data.SessionId
            };

            var interviewStart = await _aiBackend.StartInterviewSessionAsync(interviewStartRequest);
            if (!interviewStart.Success || interviewStart.Data == null)
            {
                if (integrityStart.Data.SessionId > 0)
                {
                    var stopIntegrity = await _aiBackend.EndIntegritySessionAsync(integrityStart.Data.SessionId);
                    if (!stopIntegrity.Success)
                    {
                        _logger.LogWarning(
                            "Failed to rollback integrity session {IntegritySessionId} after interview start failure: {Message}",
                            integrityStart.Data.SessionId,
                            stopIntegrity.Message);
                    }
                }

                var message = string.IsNullOrWhiteSpace(interviewStart.Message)
                    ? "Failed to start interview AI session"
                    : interviewStart.Message;
                return ApiResponse<InterviewSessionDto>.FailureResponse(message);
            }

            session.Status = "InProgress";
            session.StartedAt = DateTime.UtcNow;
            session.IntegritySessionId = integrityStart.Data.SessionId;
            session.InterviewBackendSessionId = interviewStart.Data.InterviewSessionId;
            session.TotalQuestions = interviewStart.Data.MaxQuestions > 0
                ? interviewStart.Data.MaxQuestions
                : session.TotalQuestions;
            await _context.SaveChangesAsync();

            await _auditService.LogAsync(session.Application.Candidate.UserId, "StartInterview", "InterviewSession", session.Id);

            return ApiResponse<InterviewSessionDto>.SuccessResponse(MapToSessionDto(session), "Interview started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting interview");
            return ApiResponse<InterviewSessionDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<InterviewSessionDto>> EndInterviewAsync(long sessionId)
    {
        try
        {
            var session = await _context.InterviewSessions
                .Include(s => s.Application).ThenInclude(a => a.Candidate)
                .Include(s => s.Application).ThenInclude(a => a.Job)
                .Include(s => s.Questions).ThenInclude(q => q.Answer)
                .Include(s => s.CheatingEvents)
                .FirstOrDefaultAsync(s => s.Id == sessionId);

            if (session == null)
            {
                return ApiResponse<InterviewSessionDto>.FailureResponse("Interview session not found");
            }

            if (session.Status == "Completed")
            {
                return ApiResponse<InterviewSessionDto>.SuccessResponse(MapToSessionDto(session), "Interview already completed");
            }

            session.Status = "Completed";
            session.EndedAt ??= DateTime.UtcNow;

            // Calculate overall score
            var answeredQuestions = session.Questions.Where(q => q.Answer != null).ToList();
            session.AnsweredQuestions = answeredQuestions.Count;

            if (answeredQuestions.Any())
            {
                var scores = answeredQuestions.Where(q => q.Answer?.AiScore != null).Select(q => q.Answer!.AiScore!.Value);
                session.OverallScore = scores.Any() ? scores.Average() : 0;
            }

            // Check for cheating
            session.CheatingDetected = session.CheatingEvents.Any();

            UnifiedSessionReportDto? unifiedReport = null;
            string? feedbackFromSummary = null;

            if (!string.IsNullOrWhiteSpace(session.InterviewBackendSessionId))
            {
                var summaryResult = await _aiBackend.GetInterviewSessionSummaryAsync(session.InterviewBackendSessionId);
                if (summaryResult.Success && summaryResult.Data != null)
                {
                    feedbackFromSummary = ExtractSummaryFeedback(summaryResult.Data.SummaryJson);
                }
                else if (!summaryResult.Success)
                {
                    _logger.LogWarning(
                        "Failed to fetch interview summary for backend session {InterviewSessionId}: {Message}",
                        session.InterviewBackendSessionId,
                        summaryResult.Message);
                }
            }

            if (session.IntegritySessionId.HasValue)
            {
                var endIntegrity = await _aiBackend.EndIntegritySessionAsync(session.IntegritySessionId.Value);
                if (!endIntegrity.Success)
                {
                    _logger.LogWarning(
                        "Failed to end integrity session {IntegritySessionId}: {Message}",
                        session.IntegritySessionId.Value,
                        endIntegrity.Message);
                }

                var unifiedResult = await _aiBackend.GetUnifiedSessionReportAsync(session.IntegritySessionId.Value);
                if (unifiedResult.Success && unifiedResult.Data != null)
                {
                    unifiedReport = unifiedResult.Data;
                }
                else if (!unifiedResult.Success)
                {
                    _logger.LogWarning(
                        "Failed to fetch unified report for integrity session {IntegritySessionId}: {Message}",
                        session.IntegritySessionId.Value,
                        unifiedResult.Message);
                }
            }

            if (unifiedReport != null)
            {
                session.CheatingDetected = IsCheatingDetected(unifiedReport);

                if (unifiedReport.InterviewScore.HasValue)
                {
                    var normalized = unifiedReport.InterviewScore.Value > 10f
                        ? unifiedReport.InterviewScore.Value / 10f
                        : unifiedReport.InterviewScore.Value;
                    session.OverallScore = normalized;
                }

                session.FinalReport = !string.IsNullOrWhiteSpace(unifiedReport.RawJson)
                    ? unifiedReport.RawJson
                    : JsonSerializer.Serialize(unifiedReport);

                if (string.IsNullOrWhiteSpace(feedbackFromSummary))
                {
                    feedbackFromSummary = ExtractSummaryFeedback(unifiedReport.InterviewSummaryJson);
                }
            }

            session.AiFeedback = feedbackFromSummary ?? "Interview completed. Overall performance was satisfactory.";

            if (string.IsNullOrWhiteSpace(session.FinalReport))
            {
                session.FinalReport = JsonSerializer.Serialize(new
                {
                    OverallScore = session.OverallScore,
                    TotalQuestions = session.TotalQuestions,
                    AnsweredQuestions = session.AnsweredQuestions,
                    CheatingDetected = session.CheatingDetected,
                    Duration = session.EndedAt.HasValue && session.StartedAt.HasValue
                        ? (session.EndedAt.Value - session.StartedAt.Value).TotalMinutes
                        : 0
                });
            }

            // Update application status
            session.Application.Status = ApplicationStatus.InterviewCompleted;

            await _context.SaveChangesAsync();

            // Send interview completed email
            await _emailService.SendInterviewCompletedEmailAsync(session.Id);

            return ApiResponse<InterviewSessionDto>.SuccessResponse(MapToSessionDto(session), "Interview completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ending interview");
            return ApiResponse<InterviewSessionDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<List<InterviewQuestionDto>>> GetSessionQuestionsAsync(long sessionId)
    {
        try
        {
            var questions = await _context.InterviewQuestions
                .Include(q => q.Answer)
                .Where(q => q.SessionId == sessionId)
                .OrderBy(q => q.OrderIndex)
                .ToListAsync();

            return ApiResponse<List<InterviewQuestionDto>>.SuccessResponse(
                questions.Select(MapToQuestionDto).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting session questions");
            return ApiResponse<List<InterviewQuestionDto>>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<InterviewQuestionDto>> GetNextQuestionAsync(long sessionId)
    {
        try
        {
            var nextQuestion = await _context.InterviewQuestions
                .Include(q => q.Answer)
                .Where(q => q.SessionId == sessionId && q.Answer == null)
                .OrderBy(q => q.OrderIndex)
                .FirstOrDefaultAsync();

            if (nextQuestion == null)
            {
                return ApiResponse<InterviewQuestionDto>.FailureResponse("No more questions");
            }

            return ApiResponse<InterviewQuestionDto>.SuccessResponse(MapToQuestionDto(nextQuestion));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting next question");
            return ApiResponse<InterviewQuestionDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<InterviewAnswerDto>> SubmitAnswerAsync(long candidateId, SubmitAnswerDto dto, Stream? audioStream = null)
    {
        try
        {
            var question = await _context.InterviewQuestions
                .Include(q => q.Session).ThenInclude(s => s.Application)
                .FirstOrDefaultAsync(q => q.Id == dto.QuestionId);

            if (question == null)
            {
                return ApiResponse<InterviewAnswerDto>.FailureResponse("Question not found");
            }

            // Verify candidate owns this interview
            if (question.Session.Application.CandidateId != candidateId)
            {
                return ApiResponse<InterviewAnswerDto>.FailureResponse("You don't have permission to answer this question");
            }

            // Check if already answered
            var existingAnswer = await _context.InterviewAnswers.FirstOrDefaultAsync(a => a.QuestionId == dto.QuestionId);
            if (existingAnswer != null)
            {
                return ApiResponse<InterviewAnswerDto>.FailureResponse("Question already answered");
            }

            string? audioPath = null;
            string? transcribedText = dto.AnswerText;

            // Save audio if provided
            if (audioStream != null)
            {
                audioPath = await _fileStorage.SaveFileAsync(audioStream, $"answer_{dto.QuestionId}.webm", "interview-audio");

                // Transcribe audio
                // TODO: Call AI backend to transcribe audio
                // var transcribeResponse = await _aiBackend.TranscribeAudioAsync(audioStream);
                // transcribedText = transcribeResponse.Data;
            }

            // Evaluate answer
            // TODO: Call AI backend to evaluate answer
            // var evalResponse = await _aiBackend.EvaluateAnswerAsync(question.QuestionText, transcribedText ?? "", question.ExpectedAnswer);

            var answer = new InterviewAnswer
            {
                QuestionId = dto.QuestionId,
                SessionId = question.SessionId,
                AnswerText = transcribedText,
                AnswerAudioPath = audioPath,
                ResponseDurationSeconds = dto.ResponseDurationSeconds,
                AiScore = new Random().Next(60, 100) / 10f, // Placeholder
                AiFeedback = "Good answer with relevant details.",
                AnsweredAt = DateTime.UtcNow
            };

            _context.InterviewAnswers.Add(answer);

            // Update session answered count
            question.Session.AnsweredQuestions++;

            await _context.SaveChangesAsync();

            return ApiResponse<InterviewAnswerDto>.SuccessResponse(new InterviewAnswerDto
            {
                Id = answer.Id,
                QuestionId = answer.QuestionId,
                AnswerText = answer.AnswerText,
                ResponseDurationSeconds = answer.ResponseDurationSeconds,
                AiScore = answer.AiScore,
                AiFeedback = answer.AiFeedback,
                AnsweredAt = answer.AnsweredAt
            }, "Answer submitted successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error submitting answer");
            return ApiResponse<InterviewAnswerDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse> ReportCheatingEventAsync(ReportCheatingEventDto dto, Stream? frameImage = null)
    {
        try
        {
            var session = await _context.InterviewSessions.FindAsync(dto.SessionId);
            if (session == null)
            {
                return ApiResponse.FailureResponse("Interview session not found");
            }

            string? framePath = null;
            if (frameImage != null)
            {
                framePath = await _fileStorage.SaveFileAsync(frameImage, $"cheating_{dto.SessionId}_{DateTime.UtcNow.Ticks}.jpg", "cheating-frames");
            }

            var cheatingEvent = new CheatingEvent
            {
                SessionId = dto.SessionId,
                EventType = dto.EventType,
                Confidence = dto.Confidence,
                Details = dto.Details,
                TimestampSeconds = dto.TimestampSeconds,
                FrameImagePath = framePath,
                DetectedAt = DateTime.UtcNow
            };

            _context.CheatingEvents.Add(cheatingEvent);
            session.CheatingDetected = true;
            await _context.SaveChangesAsync();

            return ApiResponse.SuccessResponse("Cheating event reported");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reporting cheating event");
            return ApiResponse.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse> ReportBrowserEventAsync(ReportBrowserEventDto dto)
    {
        try
        {
            var session = await _context.InterviewSessions.FindAsync(dto.SessionId);
            if (session == null)
            {
                return ApiResponse.FailureResponse("Interview session not found");
            }

            var browserEvent = new BrowserEvent
            {
                SessionId = dto.SessionId,
                TabSwitchCount = dto.TabSwitchCount,
                FocusLossCount = dto.FocusLossCount,
                CopyPasteCount = dto.CopyPasteCount,
                RightClickCount = dto.RightClickCount,
                DetailsJson = dto.DetailsJson,
                RecordedAt = DateTime.UtcNow
            };

            _context.BrowserEvents.Add(browserEvent);
            await _context.SaveChangesAsync();

            return ApiResponse.SuccessResponse("Browser event recorded");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reporting browser event");
            return ApiResponse.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<List<CheatingEventDto>>> GetCheatingEventsAsync(long sessionId)
    {
        try
        {
            var events = await _context.CheatingEvents
                .Where(e => e.SessionId == sessionId)
                .OrderBy(e => e.DetectedAt)
                .Select(e => new CheatingEventDto
                {
                    Id = e.Id,
                    SessionId = e.SessionId,
                    EventType = e.EventType.ToString(),
                    Confidence = e.Confidence,
                    DetectedAt = e.DetectedAt,
                    Details = e.Details,
                    TimestampSeconds = e.TimestampSeconds
                })
                .ToListAsync();

            return ApiResponse<List<CheatingEventDto>>.SuccessResponse(events);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cheating events");
            return ApiResponse<List<CheatingEventDto>>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<InterviewReportDto>> GetInterviewReportAsync(
        long sessionId,
        long? recruiterId = null,
        long? candidateId = null)
    {
        try
        {
            var session = await _context.InterviewSessions
                .Include(s => s.Application).ThenInclude(a => a.Candidate)
                .Include(s => s.Application).ThenInclude(a => a.Job)
                .Include(s => s.Questions).ThenInclude(q => q.Answer)
                .Include(s => s.CheatingEvents)
                .Include(s => s.BrowserEvents)
                .FirstOrDefaultAsync(s => s.Id == sessionId);

            if (session == null)
            {
                return ApiResponse<InterviewReportDto>.FailureResponse("Interview session not found");
            }

            if (candidateId.HasValue && session.Application.CandidateId != candidateId.Value)
            {
                return ApiResponse<InterviewReportDto>.FailureResponse("You don't have permission to view this interview report");
            }

            if (recruiterId.HasValue && session.Application.Job.RecruiterId != recruiterId.Value)
            {
                return ApiResponse<InterviewReportDto>.FailureResponse("You don't have permission to view this interview report");
            }

            if (!candidateId.HasValue && !recruiterId.HasValue)
            {
                return ApiResponse<InterviewReportDto>.FailureResponse("Report access context is missing");
            }

            var cheatingByType = session.CheatingEvents
                .GroupBy(e => e.EventType)
                .ToDictionary(g => g.Key.ToString(), g => g.Count());

            var totalTabSwitches = session.BrowserEvents.Sum(e => e.TabSwitchCount);
            var totalFocusLosses = session.BrowserEvents.Sum(e => e.FocusLossCount);

            var report = new InterviewReportDto
            {
                SessionId = session.Id,
                CandidateName = session.Application.Candidate.FullName ?? "Unknown",
                JobTitle = session.Application.Job.Title,
                StartedAt = session.StartedAt,
                EndedAt = session.EndedAt,
                DurationMinutes = session.StartedAt.HasValue && session.EndedAt.HasValue
                    ? (int)(session.EndedAt.Value - session.StartedAt.Value).TotalMinutes
                    : 0,
                OverallScore = session.OverallScore,
                QuestionScores = session.Questions.Select(q => new QuestionScoreDto
                {
                    QuestionText = q.QuestionText,
                    Category = q.Category,
                    Score = q.Answer?.AiScore,
                    Feedback = q.Answer?.AiFeedback
                }).ToList(),
                CheatingReport = new CheatingReportDto
                {
                    CheatingDetected = session.CheatingDetected,
                    TotalEvents = session.CheatingEvents.Count,
                    EventsByType = cheatingByType,
                    TotalTabSwitches = totalTabSwitches,
                    TotalFocusLosses = totalFocusLosses
                },
                AiFeedback = session.AiFeedback,
                Recommendation = session.OverallScore >= 7 && !session.CheatingDetected
                    ? "Recommended for next round"
                    : "Needs further evaluation"
            };

            if (session.IntegritySessionId.HasValue)
            {
                var unifiedResult = await _aiBackend.GetUnifiedSessionReportAsync(session.IntegritySessionId.Value);
                if (unifiedResult.Success && unifiedResult.Data != null)
                {
                    var unified = unifiedResult.Data;

                    var mergedBreakdown = MergeBreakdowns(unified.AlertBreakdown, unified.YoloAlertBreakdown);
                    report.CheatingReport = new CheatingReportDto
                    {
                        CheatingDetected = IsCheatingDetected(unified),
                        TotalEvents = unified.TotalAlerts + unified.TotalYoloAlerts,
                        EventsByType = mergedBreakdown,
                        TotalTabSwitches = totalTabSwitches,
                        TotalFocusLosses = totalFocusLosses
                    };

                    if (unified.DurationSeconds.HasValue)
                    {
                        report.DurationMinutes = (int)Math.Round(unified.DurationSeconds.Value / 60f);
                    }

                    if (unified.InterviewScore.HasValue)
                    {
                        report.OverallScore = unified.InterviewScore.Value > 10f
                            ? unified.InterviewScore.Value / 10f
                            : unified.InterviewScore.Value;
                    }

                    var summaryFeedback = ExtractSummaryFeedback(unified.InterviewSummaryJson);
                    if (!string.IsNullOrWhiteSpace(summaryFeedback))
                    {
                        report.AiFeedback = summaryFeedback;
                    }

                    if (!string.IsNullOrWhiteSpace(unified.CombinedVerdict))
                    {
                        report.Recommendation = string.IsNullOrWhiteSpace(unified.CombinedReason)
                            ? unified.CombinedVerdict
                            : $"{unified.CombinedVerdict}: {unified.CombinedReason}";
                    }
                    else if (!string.IsNullOrWhiteSpace(unified.Recommendation))
                    {
                        report.Recommendation = unified.Recommendation;
                    }
                }
                else if (!unifiedResult.Success)
                {
                    _logger.LogWarning(
                        "Failed to fetch unified report for session {SessionId} / integrity session {IntegritySessionId}: {Message}",
                        sessionId,
                        session.IntegritySessionId.Value,
                        unifiedResult.Message);
                }
            }

            return ApiResponse<InterviewReportDto>.SuccessResponse(report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting interview report");
            return ApiResponse<InterviewReportDto>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<List<InterviewRankingDto>>> GetInterviewRankingsAsync(long jobId, long recruiterId)
    {
        try
        {
            // Verify recruiter owns the job
            var job = await _context.Jobs.FirstOrDefaultAsync(j => j.Id == jobId);
            if (job == null || job.RecruiterId != recruiterId)
            {
                return ApiResponse<List<InterviewRankingDto>>.FailureResponse("Job not found or access denied");
            }

            var applications = await _context.Applications
                .Include(a => a.Candidate)
                .Include(a => a.Resume)
                .Include(a => a.InterviewSessions)
                .Where(a => a.JobId == jobId && a.InterviewSessions.Any(i => i.Status == "Completed"))
                .ToListAsync();

            var rankings = applications
                .Select(a =>
                {
                    var latestInterview = a.InterviewSessions
                        .Where(i => i.Status == "Completed")
                        .OrderByDescending(i => i.EndedAt)
                        .First();

                    return new InterviewRankingDto
                    {
                        ApplicationId = a.Id,
                        CandidateId = a.CandidateId,
                        CandidateName = a.Candidate.FullName ?? "Unknown",
                        InterviewScore = latestInterview.OverallScore ?? 0,
                        AtsScore = a.Resume?.AtsScore,
                        CheatingDetected = latestInterview.CheatingDetected,
                        Status = a.Status.ToString()
                    };
                })
                .OrderByDescending(r => r.InterviewScore)
                .ThenByDescending(r => r.AtsScore)
                .ToList();

            // Assign ranks
            for (int i = 0; i < rankings.Count; i++)
            {
                rankings[i].Rank = i + 1;
                rankings[i].RankingScore = (rankings[i].InterviewScore * 0.7f) + ((rankings[i].AtsScore ?? 0) * 0.3f / 10);
            }

            return ApiResponse<List<InterviewRankingDto>>.SuccessResponse(rankings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting interview rankings");
            return ApiResponse<List<InterviewRankingDto>>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<PaginatedResponse<InterviewSessionListDto>>> GetRecruiterInterviewsAsync(
        long recruiterId, InterviewSearchParams searchParams)
    {
        try
        {
            var recruiter = await _context.Recruiters.FindAsync(recruiterId);
            if (recruiter == null)
            {
                return ApiResponse<PaginatedResponse<InterviewSessionListDto>>.FailureResponse("Recruiter not found");
            }

            var recruiterJobIds = await _context.Jobs
                .Where(j => j.RecruiterId == recruiterId)
                .Select(j => j.Id)
                .ToListAsync();

            var query = _context.InterviewSessions
                .Include(s => s.Application).ThenInclude(a => a.Candidate)
                .Include(s => s.Application).ThenInclude(a => a.Job)
                .Where(s => recruiterJobIds.Contains(s.Application.JobId));

            // Apply filters
            if (searchParams.JobId.HasValue)
            {
                query = query.Where(s => s.Application.JobId == searchParams.JobId.Value);
            }

            if (!string.IsNullOrEmpty(searchParams.Status))
            {
                query = query.Where(s => s.Status == searchParams.Status);
            }

            if (searchParams.CheatingDetected.HasValue)
            {
                query = query.Where(s => s.CheatingDetected == searchParams.CheatingDetected.Value);
            }

            if (searchParams.FromDate.HasValue)
            {
                query = query.Where(s => s.ScheduledAt >= searchParams.FromDate.Value);
            }

            if (searchParams.ToDate.HasValue)
            {
                query = query.Where(s => s.ScheduledAt <= searchParams.ToDate.Value);
            }

            var totalCount = await query.CountAsync();

            var sessions = await query
                .OrderByDescending(s => s.ScheduledAt)
                .Skip((searchParams.PageNumber - 1) * searchParams.PageSize)
                .Take(searchParams.PageSize)
                .Select(s => new InterviewSessionListDto
                {
                    Id = s.Id,
                    InterviewTitle = s.InterviewTitle,
                    ScheduledAt = s.ScheduledAt,
                    Status = s.Status ?? "Unknown",
                    IntegritySessionId = s.IntegritySessionId,
                    InterviewBackendSessionId = s.InterviewBackendSessionId,
                    OverallScore = s.OverallScore,
                    CheatingDetected = s.CheatingDetected,
                    CandidateName = s.Application.Candidate.FullName ?? "Unknown",
                    JobTitle = s.Application.Job.Title
                })
                .ToListAsync();

            return ApiResponse<PaginatedResponse<InterviewSessionListDto>>.SuccessResponse(new PaginatedResponse<InterviewSessionListDto>
            {
                Items = sessions,
                TotalCount = totalCount,
                PageNumber = searchParams.PageNumber,
                PageSize = searchParams.PageSize
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting recruiter interviews");
            return ApiResponse<PaginatedResponse<InterviewSessionListDto>>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse<PaginatedResponse<InterviewSessionListDto>>> GetCandidateInterviewsAsync(
        long candidateId, InterviewSearchParams searchParams)
    {
        try
        {
            var query = _context.InterviewSessions
                .Include(s => s.Application).ThenInclude(a => a.Job)
                .Where(s => s.Application.CandidateId == candidateId);

            if (!string.IsNullOrEmpty(searchParams.Status))
            {
                query = query.Where(s => s.Status == searchParams.Status);
            }

            var totalCount = await query.CountAsync();

            var sessions = await query
                .OrderByDescending(s => s.ScheduledAt)
                .Skip((searchParams.PageNumber - 1) * searchParams.PageSize)
                .Take(searchParams.PageSize)
                .Select(s => new InterviewSessionListDto
                {
                    Id = s.Id,
                    InterviewTitle = s.InterviewTitle,
                    ScheduledAt = s.ScheduledAt,
                    Status = s.Status ?? "Unknown",
                    IntegritySessionId = s.IntegritySessionId,
                    InterviewBackendSessionId = s.InterviewBackendSessionId,
                    OverallScore = s.OverallScore,
                    CheatingDetected = s.CheatingDetected,
                    CandidateName = s.Application.Candidate.FullName ?? "Unknown",
                    JobTitle = s.Application.Job.Title
                })
                .ToListAsync();

            return ApiResponse<PaginatedResponse<InterviewSessionListDto>>.SuccessResponse(new PaginatedResponse<InterviewSessionListDto>
            {
                Items = sessions,
                TotalCount = totalCount,
                PageNumber = searchParams.PageNumber,
                PageSize = searchParams.PageSize
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting candidate interviews");
            return ApiResponse<PaginatedResponse<InterviewSessionListDto>>.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse> UploadVideoRecordingAsync(long sessionId, Stream videoStream, string fileName)
    {
        try
        {
            var session = await _context.InterviewSessions.FindAsync(sessionId);
            if (session == null)
            {
                return ApiResponse.FailureResponse("Interview session not found");
            }

            var filePath = await _fileStorage.SaveFileAsync(videoStream, fileName, "interview-videos");

            var recording = new VideoRecording
            {
                SessionId = sessionId,
                FilePath = filePath,
                Format = Path.GetExtension(fileName).TrimStart('.'),
                CreatedAt = DateTime.UtcNow
            };

            _context.VideoRecordings.Add(recording);
            await _context.SaveChangesAsync();

            return ApiResponse.SuccessResponse("Video recording uploaded");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading video recording");
            return ApiResponse.FailureResponse("An error occurred");
        }
    }

    #region Helper Methods

    private static string BuildCandidateCvText(Application application)
    {
        if (!string.IsNullOrWhiteSpace(application.Resume?.ResumeText))
        {
            return application.Resume.ResumeText;
        }

        if (!string.IsNullOrWhiteSpace(application.Resume?.ParsingResult?.ParsedJson))
        {
            return application.Resume.ParsingResult.ParsedJson;
        }

        var candidate = application.Candidate;
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(candidate.FullName))
        {
            builder.AppendLine($"Name: {candidate.FullName}");
        }

        if (!string.IsNullOrWhiteSpace(candidate.CurrentTitle))
        {
            builder.AppendLine($"Current title: {candidate.CurrentTitle}");
        }

        if (!string.IsNullOrWhiteSpace(candidate.Summary))
        {
            builder.AppendLine($"Summary: {candidate.Summary}");
        }

        var skills = candidate.Skills
            .Select(s => s.SkillName)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (skills.Any())
        {
            builder.AppendLine($"Skills: {string.Join(", ", skills)}");
        }

        if (!string.IsNullOrWhiteSpace(candidate.Location))
        {
            builder.AppendLine($"Location: {candidate.Location}");
        }

        return builder.Length > 0 ? builder.ToString() : "Candidate profile available in JobLens.";
    }

    private static string BuildJobDescription(Job job)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Title: {job.Title}");

        if (!string.IsNullOrWhiteSpace(job.Description))
        {
            builder.AppendLine($"Description: {job.Description}");
        }

        if (!string.IsNullOrWhiteSpace(job.Requirements))
        {
            builder.AppendLine($"Requirements: {job.Requirements}");
        }

        if (!string.IsNullOrWhiteSpace(job.Responsibilities))
        {
            builder.AppendLine($"Responsibilities: {job.Responsibilities}");
        }

        if (!string.IsNullOrWhiteSpace(job.ExperienceLevel))
        {
            builder.AppendLine($"Experience level: {job.ExperienceLevel}");
        }

        if (!string.IsNullOrWhiteSpace(job.Location))
        {
            builder.AppendLine($"Location: {job.Location}");
        }

        return builder.ToString();
    }

    private static string BuildEvaluationCriteria(InterviewAgentType agentType)
    {
        return agentType switch
        {
            InterviewAgentType.Technical => "Assess technical depth, problem-solving, architecture reasoning, and code communication.",
            InterviewAgentType.Behavioral => "Assess communication, collaboration, ownership, adaptability, and conflict management.",
            _ => "Assess technical fit, behavioral strengths, communication clarity, and role alignment."
        };
    }

    private static bool IsCheatingDetected(UnifiedSessionReportDto report)
    {
        if (report.Recommendation != null
            && (report.Recommendation.Equals("REJECT", StringComparison.OrdinalIgnoreCase)
                || report.Recommendation.Equals("ABANDONED", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return (report.FinalScore ?? 0f) >= 70f || report.TotalAlerts > 0 || report.TotalYoloAlerts > 0;
    }

    private static Dictionary<string, int> MergeBreakdowns(
        Dictionary<string, int>? mediapipeBreakdown,
        Dictionary<string, int>? yoloBreakdown)
    {
        var merged = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in mediapipeBreakdown ?? new Dictionary<string, int>())
        {
            merged[pair.Key] = pair.Value;
        }

        foreach (var pair in yoloBreakdown ?? new Dictionary<string, int>())
        {
            if (merged.ContainsKey(pair.Key))
            {
                merged[pair.Key] += pair.Value;
            }
            else
            {
                merged[pair.Key] = pair.Value;
            }
        }

        return merged;
    }

    private static string? ExtractSummaryFeedback(string? summaryJson)
    {
        if (string.IsNullOrWhiteSpace(summaryJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(summaryJson);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.String)
            {
                return root.GetString();
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "final_feedback", "feedback", "summary", "overall_feedback", "recommendation" })
                {
                    if (root.TryGetProperty(key, out var node))
                    {
                        if (node.ValueKind == JsonValueKind.String)
                        {
                            var text = node.GetString();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                return text;
                            }
                        }
                        else
                        {
                            return node.ToString();
                        }
                    }
                }

                return root.ToString();
            }
        }
        catch
        {
            // Keep original JSON content if parsing fails.
            return summaryJson;
        }

        return summaryJson;
    }

    private static InterviewSessionDto MapToSessionDto(InterviewSession session)
    {
        return new InterviewSessionDto
        {
            Id = session.Id,
            ApplicationId = session.ApplicationId,
            AgentType = session.AgentType.ToString(),
            InterviewTitle = session.InterviewTitle,
            ScheduledAt = session.ScheduledAt,
            StartedAt = session.StartedAt,
            EndedAt = session.EndedAt,
            OverallScore = session.OverallScore,
            CheatingDetected = session.CheatingDetected,
            TotalQuestions = session.TotalQuestions,
            AnsweredQuestions = session.AnsweredQuestions,
            Status = session.Status ?? "Unknown",
            IntegritySessionId = session.IntegritySessionId,
            InterviewBackendSessionId = session.InterviewBackendSessionId,
            FinalReport = session.FinalReport,
            AiFeedback = session.AiFeedback,
            CandidateName = session.Application.Candidate.FullName ?? "Unknown",
            JobTitle = session.Application.Job.Title,
            CheatingEventsCount = session.CheatingEvents?.Count ?? 0
        };
    }

    private static InterviewQuestionDto MapToQuestionDto(InterviewQuestion question)
    {
        return new InterviewQuestionDto
        {
            Id = question.Id,
            SessionId = question.SessionId,
            QuestionText = question.QuestionText,
            OrderIndex = question.OrderIndex,
            Category = question.Category,
            Difficulty = question.Difficulty,
            MaxDurationSeconds = question.MaxDurationSeconds,
            IsAnswered = question.Answer != null,
            Answer = question.Answer != null ? new InterviewAnswerDto
            {
                Id = question.Answer.Id,
                QuestionId = question.Answer.QuestionId,
                AnswerText = question.Answer.AnswerText,
                ResponseDurationSeconds = question.Answer.ResponseDurationSeconds,
                AiScore = question.Answer.AiScore,
                AiFeedback = question.Answer.AiFeedback,
                AnsweredAt = question.Answer.AnsweredAt
            } : null
        };
    }

    #endregion
}

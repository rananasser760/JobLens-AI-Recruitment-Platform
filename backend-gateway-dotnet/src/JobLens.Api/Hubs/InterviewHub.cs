using JobLens.Application.DTOs.Interviews;
using JobLens.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Claims;

namespace JobLens.Api.Hubs;

[Authorize]
public sealed class InterviewHub(IInterviewService interviewService, ILogger<InterviewHub> logger) : Hub
{
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<long, byte>> AuthorizedSessionsByConnection = new();

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        AuthorizedSessionsByConnection.TryRemove(Context.ConnectionId, out _);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinSession(long interviewSessionId)
    {
        await EnsureSessionAccessAsync(interviewSessionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, $"interview:{interviewSessionId}");
    }

    public async Task SubmitAudio(long interviewSessionId, string base64Audio, int sequence)
    {
        await EnsureSessionAccessAsync(interviewSessionId);

        try
        {
            logger.LogDebug(
                "SubmitAudio received for session {InterviewSessionId} (sequence {Sequence}, payloadLength {PayloadLength})",
                interviewSessionId,
                sequence,
                base64Audio?.Length ?? 0);

            if (string.IsNullOrWhiteSpace(base64Audio))
            {
                await SendRealtimeErrorToCallerAsync(
                    "audioProcessed",
                    "empty_audio_chunk",
                    "Received an empty audio chunk. Please verify your microphone input.");
                return;
            }

            var result = await interviewService.ProcessAudioAsync(interviewSessionId, base64Audio, sequence, CancellationToken.None);
            if (!result.Success)
            {
                var code = result.Errors?.FirstOrDefault() ?? "audio_processing_failed";
                await SendRealtimeErrorToCallerAsync(
                    "audioProcessed",
                    code,
                    result.Message ?? "Live audio processing failed.");
                return;
            }

            await Clients.Group($"interview:{interviewSessionId}").SendAsync("audioProcessed", result);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(
                ex,
                "SubmitAudio canceled for connection {ConnectionId} and session {InterviewSessionId}.",
                Context.ConnectionId,
                interviewSessionId);
            await SendRealtimeErrorToCallerAsync(
                "audioProcessed",
                "audio_processing_canceled",
                "Live audio processing was interrupted. Please continue speaking.");
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "SubmitAudio failed for connection {ConnectionId}, session {InterviewSessionId}, sequence {Sequence}.",
                Context.ConnectionId,
                interviewSessionId,
                sequence);
            await SendRealtimeErrorToCallerAsync(
                "audioProcessed",
                "audio_processing_exception",
                "We could not process this audio chunk. Please keep speaking.");
        }
    }

    public async Task RequestOpeningPrompt(long interviewSessionId)
    {
        await EnsureSessionAccessAsync(interviewSessionId);

        try
        {
            var result = await interviewService.RequestOpeningPromptAsync(interviewSessionId, Context.ConnectionAborted);
            if (!result.Success)
            {
                var code = result.Errors?.FirstOrDefault() ?? "opening_prompt_failed";
                await SendRealtimeErrorToCallerAsync(
                    "audioProcessed",
                    code,
                    result.Message ?? "Could not request opening prompt.");
                return;
            }

            if (result.Data is not null && !string.IsNullOrWhiteSpace(result.Data.Reply))
            {
                await Clients.Caller.SendAsync("audioProcessed", result);
            }
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(
                ex,
                "RequestOpeningPrompt canceled for connection {ConnectionId} and session {InterviewSessionId}.",
                Context.ConnectionId,
                interviewSessionId);
            await SendRealtimeErrorToCallerAsync(
                "audioProcessed",
                "opening_prompt_canceled",
                "Opening prompt request was interrupted. Please retry.");
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "RequestOpeningPrompt failed for connection {ConnectionId} and session {InterviewSessionId}.",
                Context.ConnectionId,
                interviewSessionId);
            await SendRealtimeErrorToCallerAsync(
                "audioProcessed",
                "opening_prompt_exception",
                "We could not prepare the opening prompt. Please retry.");
        }
    }

    public async Task SubmitVideoFrame(long interviewSessionId, string base64Frame, int sequence)
    {
        await EnsureSessionAccessAsync(interviewSessionId);

        try
        {
            var result = await interviewService.ProcessVideoFrameAsync(interviewSessionId, base64Frame, sequence, CancellationToken.None);
            if (!result.Success)
            {
                var code = result.Errors?.FirstOrDefault() ?? "video_processing_failed";
                await SendRealtimeErrorToCallerAsync(
                    "videoProcessed",
                    code,
                    result.Message ?? "Live video analysis failed.");
                return;
            }

            await Clients.Group($"interview:{interviewSessionId}").SendAsync("videoProcessed", result);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(
                ex,
                "SubmitVideoFrame canceled for connection {ConnectionId} and session {InterviewSessionId}.",
                Context.ConnectionId,
                interviewSessionId);
            await SendRealtimeErrorToCallerAsync(
                "videoProcessed",
                "video_processing_canceled",
                "Video processing was interrupted. Streaming will continue.");
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "SubmitVideoFrame failed for connection {ConnectionId}, session {InterviewSessionId}, sequence {Sequence}.",
                Context.ConnectionId,
                interviewSessionId,
                sequence);
            await SendRealtimeErrorToCallerAsync(
                "videoProcessed",
                "video_processing_exception",
                "We could not process this video frame. Streaming will continue.");
        }
    }

    public async Task SubmitBrowserEvent(BrowserEventRequest request)
    {
        await EnsureSessionAccessAsync(request.InterviewSessionId);
        var result = await interviewService.RecordBrowserEventAsync(request, Context.ConnectionAborted);
        await Clients.Group($"interview:{request.InterviewSessionId}").SendAsync("browserEventRecorded", result);
    }

    public async Task CompleteInterview(long interviewSessionId)
    {
        await EnsureSessionAccessAsync(interviewSessionId);
        var result = await interviewService.CompleteAsync(interviewSessionId, Context.ConnectionAborted);
        await Clients.Group($"interview:{interviewSessionId}").SendAsync("interviewCompleted", result);
    }

    private async Task SendRealtimeErrorToCallerAsync(string eventName, string code, string message, string? details = null)
    {
        await Clients.Caller.SendAsync(
            eventName,
            new
            {
                success = false,
                error = new
                {
                    code,
                    message,
                    details,
                }
            });
    }

    private async Task EnsureSessionAccessAsync(long interviewSessionId)
    {
        if (IsSessionAuthorized(interviewSessionId))
        {
            return;
        }

        var userIdRaw = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? Context.User?.FindFirstValue("sub");

        if (!long.TryParse(userIdRaw, out var userId))
        {
            throw new HubException("Unauthorized.");
        }

        var isAdmin = Context.User?.IsInRole("Admin") == true;
        var isRecruiter = Context.User?.IsInRole("Recruiter") == true;
        var isCandidate = Context.User?.IsInRole("Candidate") == true;

        bool canAccess;
        try
        {
            canAccess = await interviewService.CanUserAccessSessionAsync(
                interviewSessionId,
                userId,
                isAdmin,
                isRecruiter,
                isCandidate,
                Context.ConnectionAborted);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogDebug(ex, "Session access validation canceled for connection {ConnectionId} and interview session {InterviewSessionId}.", Context.ConnectionId, interviewSessionId);
            throw new HubException("Connection was interrupted while validating interview session access.");
        }
        catch (HubException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Session access validation failed for connection {ConnectionId} and interview session {InterviewSessionId}.", Context.ConnectionId, interviewSessionId);
            throw new HubException("Unable to validate interview session access right now. Please retry.");
        }

        if (!canAccess)
        {
            throw new HubException("Forbidden: you cannot access this interview session.");
        }

        MarkSessionAuthorized(interviewSessionId);
    }

    private bool IsSessionAuthorized(long interviewSessionId)
    {
        return AuthorizedSessionsByConnection.TryGetValue(Context.ConnectionId, out var sessions)
            && sessions.ContainsKey(interviewSessionId);
    }

    private void MarkSessionAuthorized(long interviewSessionId)
    {
        var sessions = AuthorizedSessionsByConnection.GetOrAdd(
            Context.ConnectionId,
            _ => new ConcurrentDictionary<long, byte>());

        sessions[interviewSessionId] = 0;
    }
}

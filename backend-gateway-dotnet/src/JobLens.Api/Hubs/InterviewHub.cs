using JobLens.Application.DTOs.Interviews;
using JobLens.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace JobLens.Api.Hubs;

[Authorize]
public sealed class InterviewHub(IInterviewService interviewService) : Hub
{
    public async Task JoinSession(long interviewSessionId)
    {
        await EnsureSessionAccessAsync(interviewSessionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, $"interview:{interviewSessionId}");
    }

    public async Task SubmitAudio(long interviewSessionId, string base64Audio, int sequence)
    {
        await EnsureSessionAccessAsync(interviewSessionId);
        var result = await interviewService.ProcessAudioAsync(interviewSessionId, base64Audio, sequence, Context.ConnectionAborted);
        await Clients.Group($"interview:{interviewSessionId}").SendAsync("audioProcessed", result, Context.ConnectionAborted);
    }

    public async Task SubmitVideoFrame(long interviewSessionId, string base64Frame, int sequence)
    {
        await EnsureSessionAccessAsync(interviewSessionId);
        var result = await interviewService.ProcessVideoFrameAsync(interviewSessionId, base64Frame, sequence, Context.ConnectionAborted);
        await Clients.Group($"interview:{interviewSessionId}").SendAsync("videoProcessed", result, Context.ConnectionAborted);
    }

    public async Task SubmitBrowserEvent(BrowserEventRequest request)
    {
        await EnsureSessionAccessAsync(request.InterviewSessionId);
        var result = await interviewService.RecordBrowserEventAsync(request, Context.ConnectionAborted);
        await Clients.Group($"interview:{request.InterviewSessionId}").SendAsync("browserEventRecorded", result, Context.ConnectionAborted);
    }

    public async Task CompleteInterview(long interviewSessionId)
    {
        await EnsureSessionAccessAsync(interviewSessionId);
        var result = await interviewService.CompleteAsync(interviewSessionId, Context.ConnectionAborted);
        await Clients.Group($"interview:{interviewSessionId}").SendAsync("interviewCompleted", result, Context.ConnectionAborted);
    }

    private async Task EnsureSessionAccessAsync(long interviewSessionId)
    {
        var userIdRaw = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? Context.User?.FindFirstValue("sub");

        if (!long.TryParse(userIdRaw, out var userId))
        {
            throw new HubException("Unauthorized.");
        }

        var isAdmin = Context.User?.IsInRole("Admin") == true;
        var isRecruiter = Context.User?.IsInRole("Recruiter") == true;
        var isCandidate = Context.User?.IsInRole("Candidate") == true;

        var canAccess = await interviewService.CanUserAccessSessionAsync(
            interviewSessionId,
            userId,
            isAdmin,
            isRecruiter,
            isCandidate,
            Context.ConnectionAborted);

        if (!canAccess)
        {
            throw new HubException("Forbidden: you cannot access this interview session.");
        }
    }
}

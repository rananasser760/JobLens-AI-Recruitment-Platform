using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GP_Backend.Data;
using GP_Backend.Models.DTOs.Interview;
using GP_Backend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace GP_Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class InterviewsController : ControllerBase
{
    private readonly IInterviewService _interviewService;
    private readonly AppDbContext _context;

    public InterviewsController(IInterviewService interviewService, AppDbContext context)
    {
        _interviewService = interviewService;
        _context = context;
    }

    [HttpGet("{sessionId}")]
    public async Task<IActionResult> GetSession(long sessionId)
    {
        var result = await _interviewService.GetSessionAsync(sessionId);
        if (!result.Success)
        {
            return NotFound(result);
        }
        return Ok(result);
    }

    [HttpPost("schedule")]
    [Authorize(Roles = "Recruiter")]
    public async Task<IActionResult> ScheduleInterview([FromBody] ScheduleInterviewDto dto)
    {
        var recruiterId = await GetRecruiterIdAsync();
        if (recruiterId == null) return Unauthorized();

        var result = await _interviewService.ScheduleInterviewAsync(recruiterId.Value, dto);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpPost("{sessionId}/start")]
    [Authorize(Roles = "Candidate")]
    public async Task<IActionResult> StartInterview(long sessionId)
    {
        var candidateId = await GetCandidateIdAsync();
        if (candidateId == null) return Unauthorized();

        var result = await _interviewService.StartInterviewAsync(sessionId, candidateId.Value);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpPost("{sessionId}/end")]
    public async Task<IActionResult> EndInterview(long sessionId)
    {
        var result = await _interviewService.EndInterviewAsync(sessionId);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpGet("{sessionId}/questions")]
    public async Task<IActionResult> GetQuestions(long sessionId)
    {
        var result = await _interviewService.GetSessionQuestionsAsync(sessionId);
        return Ok(result);
    }

    [HttpGet("{sessionId}/next-question")]
    public async Task<IActionResult> GetNextQuestion(long sessionId)
    {
        var result = await _interviewService.GetNextQuestionAsync(sessionId);
        if (!result.Success)
        {
            return NotFound(result);
        }
        return Ok(result);
    }

    [HttpPost("submit-answer")]
    [Authorize(Roles = "Candidate")]
    public async Task<IActionResult> SubmitAnswer([FromForm] SubmitAnswerDto dto, IFormFile? audioFile = null)
    {
        var candidateId = await GetCandidateIdAsync();
        if (candidateId == null) return Unauthorized();

        Stream? audioStream = null;
        if (audioFile != null)
        {
            audioStream = audioFile.OpenReadStream();
        }

        var result = await _interviewService.SubmitAnswerAsync(candidateId.Value, dto, audioStream);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpPost("cheating-event")]
    public async Task<IActionResult> ReportCheatingEvent([FromForm] ReportCheatingEventDto dto, IFormFile? frameImage = null)
    {
        Stream? imageStream = null;
        if (frameImage != null)
        {
            imageStream = frameImage.OpenReadStream();
        }

        var result = await _interviewService.ReportCheatingEventAsync(dto, imageStream);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpPost("browser-event")]
    public async Task<IActionResult> ReportBrowserEvent([FromBody] ReportBrowserEventDto dto)
    {
        var result = await _interviewService.ReportBrowserEventAsync(dto);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpGet("{sessionId}/cheating-events")]
    [Authorize(Roles = "Recruiter")]
    public async Task<IActionResult> GetCheatingEvents(long sessionId)
    {
        var result = await _interviewService.GetCheatingEventsAsync(sessionId);
        return Ok(result);
    }

    [HttpGet("{sessionId}/report")]
    [Authorize(Roles = "Recruiter,Candidate")]
    public async Task<IActionResult> GetInterviewReport(long sessionId)
    {
        long? recruiterId = null;
        long? candidateId = null;

        if (User.IsInRole("Recruiter"))
        {
            recruiterId = await GetRecruiterIdAsync();
            if (recruiterId == null) return Unauthorized();
        }
        else if (User.IsInRole("Candidate"))
        {
            candidateId = await GetCandidateIdAsync();
            if (candidateId == null) return Unauthorized();
        }
        else
        {
            return Forbid();
        }

        var result = await _interviewService.GetInterviewReportAsync(sessionId, recruiterId, candidateId);
        if (!result.Success)
        {
            if (!string.IsNullOrWhiteSpace(result.Message) &&
                result.Message.Contains("permission", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            return NotFound(result);
        }
        return Ok(result);
    }

    [HttpGet("job/{jobId}/rankings")]
    [Authorize(Roles = "Recruiter")]
    public async Task<IActionResult> GetInterviewRankings(long jobId)
    {
        var recruiterId = await GetRecruiterIdAsync();
        if (recruiterId == null) return Unauthorized();

        var result = await _interviewService.GetInterviewRankingsAsync(jobId, recruiterId.Value);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpGet("my-interviews")]
    [Authorize(Roles = "Recruiter")]
    public async Task<IActionResult> GetRecruiterInterviews([FromQuery] InterviewSearchParams searchParams)
    {
        var recruiterId = await GetRecruiterIdAsync();
        if (recruiterId == null) return Unauthorized();

        var result = await _interviewService.GetRecruiterInterviewsAsync(recruiterId.Value, searchParams);
        return Ok(result);
    }

    [HttpGet("candidate-interviews")]
    [Authorize(Roles = "Candidate")]
    public async Task<IActionResult> GetCandidateInterviews([FromQuery] InterviewSearchParams searchParams)
    {
        var candidateId = await GetCandidateIdAsync();
        if (candidateId == null) return Unauthorized();

        var result = await _interviewService.GetCandidateInterviewsAsync(candidateId.Value, searchParams);
        return Ok(result);
    }

    [HttpPost("{sessionId}/video")]
    public async Task<IActionResult> UploadVideo(long sessionId, IFormFile videoFile)
    {
        if (videoFile == null || videoFile.Length == 0)
        {
            return BadRequest("No video file uploaded");
        }

        using var stream = videoFile.OpenReadStream();
        var result = await _interviewService.UploadVideoRecordingAsync(sessionId, stream, videoFile.FileName);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    private long? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(userIdClaim, out var userId) ? userId : null;
    }

    private async Task<long?> GetRecruiterIdAsync()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return null;

        var recruiter = await _context.Recruiters.FirstOrDefaultAsync(r => r.UserId == userId.Value);
        return recruiter?.Id;
    }

    private async Task<long?> GetCandidateIdAsync()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return null;

        var candidate = await _context.Candidates.FirstOrDefaultAsync(c => c.UserId == userId.Value);
        return candidate?.Id;
    }
}

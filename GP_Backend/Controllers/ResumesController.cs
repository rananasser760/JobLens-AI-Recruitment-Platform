using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GP_Backend.Data;
using GP_Backend.Models.DTOs.Resume;
using GP_Backend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace GP_Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ResumesController : ControllerBase
{
    private readonly IResumeService _resumeService;
    private readonly AppDbContext _context;

    public ResumesController(IResumeService resumeService, AppDbContext context)
    {
        _resumeService = resumeService;
        _context = context;
    }

    [HttpGet("{resumeId}")]
    public async Task<IActionResult> GetResume(long resumeId)
    {
        var result = await _resumeService.GetResumeAsync(resumeId);
        if (!result.Success)
        {
            return NotFound(result);
        }
        return Ok(result);
    }

    [HttpGet("my-resumes")]
    [Authorize(Roles = "Candidate")]
    public async Task<IActionResult> GetMyResumes()
    {
        var candidateId = await GetCandidateIdAsync();
        if (candidateId == null) return Unauthorized();

        var result = await _resumeService.GetCandidateResumesAsync(candidateId.Value);
        return Ok(result);
    }

    [HttpPost]
    [Authorize(Roles = "Candidate")]
    public async Task<IActionResult> UploadResume(IFormFile file, [FromQuery] bool isDefault = false, [FromQuery] bool parseNow = true)
    {
        var candidateId = await GetCandidateIdAsync();
        if (candidateId == null) return Unauthorized();

        if (file == null || file.Length == 0)
        {
            return BadRequest("No file uploaded");
        }

        using var stream = file.OpenReadStream();
        var dto = new UploadResumeDto { IsDefault = isDefault, ParseNow = parseNow };
        var result = await _resumeService.UploadResumeAsync(candidateId.Value, stream, file.FileName, dto);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpDelete("{resumeId}")]
    [Authorize(Roles = "Candidate")]
    public async Task<IActionResult> DeleteResume(long resumeId)
    {
        var candidateId = await GetCandidateIdAsync();
        if (candidateId == null) return Unauthorized();

        var result = await _resumeService.DeleteResumeAsync(resumeId, candidateId.Value);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpPost("{resumeId}/set-default")]
    [Authorize(Roles = "Candidate")]
    public async Task<IActionResult> SetDefaultResume(long resumeId)
    {
        var candidateId = await GetCandidateIdAsync();
        if (candidateId == null) return Unauthorized();

        var result = await _resumeService.SetDefaultResumeAsync(resumeId, candidateId.Value);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpPost("{resumeId}/parse")]
    [Authorize(Roles = "Candidate")]
    public async Task<IActionResult> ParseResume(long resumeId)
    {
        var result = await _resumeService.ParseResumeAsync(resumeId);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpGet("{resumeId}/ats-score")]
    public async Task<IActionResult> GetAtsScore(long resumeId, [FromQuery] long? jobId = null)
    {
        var result = await _resumeService.GetAtsScoreAsync(resumeId, jobId);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpPost("parse-text")]
    [Authorize(Roles = "Candidate,Recruiter,Admin")]
    public async Task<IActionResult> ParseResumeText([FromBody] ResumeTextRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.ResumeText))
        {
            return BadRequest("Resume text is required");
        }

        var result = await _resumeService.ParseResumeTextAsync(request.ResumeText);
        if (!result.Success)
        {
            return StatusCode(StatusCodes.Status502BadGateway, result);
        }

        return Ok(result);
    }

    [HttpPost("ats-score-text")]
    [Authorize(Roles = "Candidate,Recruiter,Admin")]
    public async Task<IActionResult> GetAtsScoreFromText([FromBody] ResumeTextAtsRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.ResumeText))
        {
            return BadRequest("Resume text is required");
        }

        var result = await _resumeService.GetAtsScoreFromTextAsync(request.ResumeText, request.JobDescription);
        if (!result.Success)
        {
            return StatusCode(StatusCodes.Status502BadGateway, result);
        }

        return Ok(result);
    }

    [HttpPost("improvements")]
    [Authorize(Roles = "Candidate,Recruiter,Admin")]
    public async Task<IActionResult> GetResumeImprovements([FromBody] ResumeTextRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.ResumeText))
        {
            return BadRequest("Resume text is required");
        }

        var result = await _resumeService.GetResumeImprovementsAsync(request.ResumeText);
        if (!result.Success)
        {
            return StatusCode(StatusCodes.Status502BadGateway, result);
        }

        return Ok(result);
    }

    [HttpPost("full-analysis")]
    [Authorize(Roles = "Candidate,Recruiter,Admin")]
    public async Task<IActionResult> GetFullResumeAnalysis([FromBody] ResumeFullAnalysisRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.ResumeText))
        {
            return BadRequest("Resume text is required");
        }

        var result = await _resumeService.GetFullResumeAnalysisAsync(
            request.ResumeText,
            request.IncludeImprovements,
            request.JobMatchLimit);

        if (!result.Success)
        {
            return StatusCode(StatusCodes.Status502BadGateway, result);
        }

        return Ok(result);
    }

    [HttpGet("{resumeId}/download")]
    public async Task<IActionResult> DownloadResume(long resumeId)
    {
        var fileInfo = await _resumeService.DownloadResumeAsync(resumeId);
        if (fileInfo == null)
        {
            return NotFound();
        }

        return File(fileInfo.Value.content, fileInfo.Value.contentType, fileInfo.Value.fileName);
    }

    private long? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(userIdClaim, out var userId) ? userId : null;
    }

    private async Task<long?> GetCandidateIdAsync()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return null;

        var candidate = await _context.Candidates.FirstOrDefaultAsync(c => c.UserId == userId.Value);
        return candidate?.Id;
    }
}

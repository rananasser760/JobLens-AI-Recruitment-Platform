using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GP_Backend.Data;
using GP_Backend.Models.DTOs.Job;
using GP_Backend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace GP_Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class JobsController : ControllerBase
{
    private readonly IJobService _jobService;
    private readonly AppDbContext _context;

    public JobsController(IJobService jobService, AppDbContext context)
    {
        _jobService = jobService;
        _context = context;
    }

    [HttpGet]
    public async Task<IActionResult> SearchJobs([FromQuery] JobSearchParams searchParams)
    {
        var result = await _jobService.SearchJobsAsync(searchParams);
        return Ok(result);
    }

    [HttpGet("{jobId}")]
    public async Task<IActionResult> GetJob(long jobId)
    {
        var result = await _jobService.GetJobAsync(jobId);
        if (!result.Success)
        {
            return NotFound(result);
        }
        return Ok(result);
    }

    [HttpPost]
    [Authorize(Roles = "Recruiter")]
    public async Task<IActionResult> CreateJob([FromBody] CreateJobDto dto)
    {
        var recruiterId = await GetRecruiterIdAsync();
        if (recruiterId == null) return Unauthorized();

        var result = await _jobService.CreateJobAsync(recruiterId.Value, dto);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpPut("{jobId}")]
    [Authorize(Roles = "Recruiter")]
    public async Task<IActionResult> UpdateJob(long jobId, [FromBody] UpdateJobDto dto)
    {
        var recruiterId = await GetRecruiterIdAsync();
        if (recruiterId == null) return Unauthorized();

        var result = await _jobService.UpdateJobAsync(jobId, recruiterId.Value, dto);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpDelete("{jobId}")]
    [Authorize(Roles = "Recruiter")]
    public async Task<IActionResult> DeleteJob(long jobId)
    {
        var recruiterId = await GetRecruiterIdAsync();
        if (recruiterId == null) return Unauthorized();

        var result = await _jobService.DeleteJobAsync(jobId, recruiterId.Value);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpPost("{jobId}/toggle-status")]
    [Authorize(Roles = "Recruiter")]
    public async Task<IActionResult> ToggleJobStatus(long jobId)
    {
        var recruiterId = await GetRecruiterIdAsync();
        if (recruiterId == null) return Unauthorized();

        var result = await _jobService.ToggleJobStatusAsync(jobId, recruiterId.Value);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpPost("{jobId}/skills")]
    [Authorize(Roles = "Recruiter")]
    public async Task<IActionResult> AddJobSkill(long jobId, [FromBody] CreateJobSkillDto dto)
    {
        var result = await _jobService.AddJobSkillAsync(jobId, dto);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpDelete("{jobId}/skills/{skillId}")]
    [Authorize(Roles = "Recruiter")]
    public async Task<IActionResult> RemoveJobSkill(long jobId, long skillId)
    {
        var result = await _jobService.RemoveJobSkillAsync(jobId, skillId);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpGet("recommendations")]
    [Authorize(Roles = "Candidate")]
    public async Task<IActionResult> GetRecommendedJobs([FromQuery] int limit = 10)
    {
        var candidateId = await GetCandidateIdAsync();
        if (candidateId == null) return Unauthorized();

        var result = await _jobService.GetRecommendedJobsAsync(candidateId.Value, limit);
        return Ok(result);
    }

    [HttpPost("recommendations/match-from-text")]
    [Authorize(Roles = "Candidate,Recruiter,Admin")]
    public async Task<IActionResult> MatchJobsFromText([FromBody] MatchJobsFromTextRequestDto request, [FromQuery] int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(request.ResumeText))
        {
            return BadRequest("Resume text is required");
        }

        var result = await _jobService.MatchJobsFromTextAsync(request.ResumeText, limit);
        if (!result.Success)
        {
            return StatusCode(StatusCodes.Status502BadGateway, result);
        }

        return Ok(result);
    }

    [HttpGet("scraping/jobs")]
    [Authorize(Roles = "Recruiter,Admin")]
    public async Task<IActionResult> GetScrapedJobs([FromQuery] string? keyword = null, [FromQuery] string? location = null, [FromQuery] int limit = 50)
    {
        var result = await _jobService.GetScrapedJobsFromAiAsync(keyword, location, limit);
        if (!result.Success)
        {
            return StatusCode(StatusCodes.Status502BadGateway, result);
        }

        return Ok(result);
    }

    [HttpGet("scraping/status")]
    [Authorize(Roles = "Recruiter,Admin")]
    public async Task<IActionResult> GetScrapingStatus()
    {
        var result = await _jobService.GetScrapingStatusAsync();
        if (!result.Success)
        {
            return StatusCode(StatusCodes.Status502BadGateway, result);
        }

        return Ok(result);
    }

    [HttpPost("scraping/trigger")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> TriggerScraping([FromQuery] int? maxCategories = null)
    {
        var result = await _jobService.TriggerScrapingAsync(maxCategories);
        if (!result.Success)
        {
            return StatusCode(StatusCodes.Status502BadGateway, result);
        }

        return Accepted(result);
    }

    [HttpGet("recruitment/status")]
    [Authorize(Roles = "Recruiter,Admin")]
    public async Task<IActionResult> GetRecruitmentStatus()
    {
        var result = await _jobService.GetRecruitmentStatusAsync();
        if (!result.Success)
        {
            return StatusCode(StatusCodes.Status502BadGateway, result);
        }

        return Ok(result);
    }

    [HttpGet("my-jobs")]
    [Authorize(Roles = "Recruiter")]
    public async Task<IActionResult> GetMyJobs([FromQuery] JobSearchParams searchParams)
    {
        var recruiterId = await GetRecruiterIdAsync();
        if (recruiterId == null) return Unauthorized();

        var result = await _jobService.GetRecruiterJobsAsync(recruiterId.Value, searchParams);
        return Ok(result);
    }

    [HttpPost("import-scraped")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ImportScrapedJobs([FromBody] List<ScrapedJobDto> jobs)
    {
        var result = await _jobService.ImportScrapedJobsAsync(jobs);
        return Ok(result);
    }

    [HttpPost("cleanup-expired")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CleanupExpiredJobs([FromQuery] int daysOld = 30)
    {
        var result = await _jobService.CleanupExpiredScrapedJobsAsync(daysOld);
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

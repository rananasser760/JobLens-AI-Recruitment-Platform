using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GP_Backend.Data;
using GP_Backend.Models.DTOs.Application;
using GP_Backend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace GP_Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ApplicationsController : ControllerBase
{
    private readonly IApplicationService _applicationService;
    private readonly AppDbContext _context;

    public ApplicationsController(IApplicationService applicationService, AppDbContext context)
    {
        _applicationService = applicationService;
        _context = context;
    }

    [HttpGet("{applicationId}")]
    public async Task<IActionResult> GetApplication(long applicationId)
    {
        var result = await _applicationService.GetApplicationAsync(applicationId);
        if (!result.Success)
        {
            return NotFound(result);
        }
        return Ok(result);
    }

    [HttpPost]
    [Authorize(Roles = "Candidate")]
    public async Task<IActionResult> ApplyToJob([FromBody] ApplyToJobDto dto)
    {
        var candidateId = await GetCandidateIdAsync();
        if (candidateId == null) return Unauthorized();

        var result = await _applicationService.ApplyToJobAsync(candidateId.Value, dto);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpPut("{applicationId}/status")]
    [Authorize(Roles = "Recruiter")]
    public async Task<IActionResult> UpdateStatus(long applicationId, [FromBody] UpdateApplicationStatusDto dto)
    {
        var recruiterId = await GetRecruiterIdAsync();
        if (recruiterId == null) return Unauthorized();

        var result = await _applicationService.UpdateStatusAsync(applicationId, recruiterId.Value, dto);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpPut("bulk-status")]
    [Authorize(Roles = "Recruiter")]
    public async Task<IActionResult> BulkUpdateStatus([FromBody] BulkUpdateApplicationStatusDto dto)
    {
        var recruiterId = await GetRecruiterIdAsync();
        if (recruiterId == null) return Unauthorized();

        var result = await _applicationService.BulkUpdateStatusAsync(recruiterId.Value, dto);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    [HttpPost("{applicationId}/withdraw")]
    [Authorize(Roles = "Candidate")]
    public async Task<IActionResult> WithdrawApplication(long applicationId)
    {
        var candidateId = await GetCandidateIdAsync();
        if (candidateId == null) return Unauthorized();

        var result = await _applicationService.WithdrawApplicationAsync(applicationId, candidateId.Value);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpGet("my-applications")]
    [Authorize(Roles = "Candidate")]
    public async Task<IActionResult> GetMyApplications([FromQuery] ApplicationSearchParams searchParams)
    {
        var candidateId = await GetCandidateIdAsync();
        if (candidateId == null) return Unauthorized();

        var result = await _applicationService.GetCandidateApplicationsAsync(candidateId.Value, searchParams);
        return Ok(result);
    }

    [HttpGet("job/{jobId}")]
    [Authorize(Roles = "Recruiter")]
    public async Task<IActionResult> GetJobApplications(long jobId, [FromQuery] ApplicationSearchParams searchParams)
    {
        var recruiterId = await GetRecruiterIdAsync();
        if (recruiterId == null) return Unauthorized();

        var result = await _applicationService.GetJobApplicationsAsync(jobId, recruiterId.Value, searchParams);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpGet("job/{jobId}/ranked")]
    [Authorize(Roles = "Recruiter")]
    public async Task<IActionResult> GetRankedCandidates(long jobId)
    {
        var recruiterId = await GetRecruiterIdAsync();
        if (recruiterId == null) return Unauthorized();

        var result = await _applicationService.GetRankedCandidatesAsync(jobId, recruiterId.Value);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpGet("check/{jobId}")]
    [Authorize(Roles = "Candidate")]
    public async Task<IActionResult> CheckIfApplied(long jobId)
    {
        var candidateId = await GetCandidateIdAsync();
        if (candidateId == null) return Unauthorized();

        var hasApplied = await _applicationService.HasCandidateAppliedAsync(candidateId.Value, jobId);
        return Ok(new { hasApplied });
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

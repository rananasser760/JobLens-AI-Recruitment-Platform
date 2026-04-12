using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GP_Backend.Models.DTOs.Candidate;
using GP_Backend.Services.Interfaces;

namespace GP_Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CandidatesController : ControllerBase
{
    private readonly ICandidateService _candidateService;

    public CandidatesController(ICandidateService candidateService)
    {
        _candidateService = candidateService;
    }

    [HttpGet("profile")]
    [Authorize(Roles = "Candidate")]
    public async Task<IActionResult> GetProfile()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var result = await _candidateService.GetProfileAsync(userId.Value);
        if (!result.Success)
        {
            return NotFound(result);
        }
        return Ok(result);
    }

    [HttpGet("dashboard")]
    [Authorize(Roles = "Candidate")]
    public async Task<IActionResult> GetDashboard()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var result = await _candidateService.GetDashboardAsync(userId.Value);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    [HttpGet("{candidateId}")]
    public async Task<IActionResult> GetCandidateById(long candidateId)
    {
        var result = await _candidateService.GetProfileByIdAsync(candidateId);
        if (!result.Success)
        {
            return NotFound(result);
        }
        return Ok(result);
    }

    [HttpPut("profile")]
    [Authorize(Roles = "Candidate")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateCandidateProfileDto dto)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var result = await _candidateService.UpdateProfileAsync(userId.Value, dto);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpPost("profile/image")]
    [Authorize(Roles = "Candidate")]
    public async Task<IActionResult> UpdateProfileImage(IFormFile file)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        if (file == null || file.Length == 0)
        {
            return BadRequest("No file uploaded");
        }

        using var stream = file.OpenReadStream();
        var result = await _candidateService.UpdateProfileImageAsync(userId.Value, stream, file.FileName);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpGet("{candidateId}/skills")]
    public async Task<IActionResult> GetSkills(long candidateId)
    {
        var result = await _candidateService.GetSkillsAsync(candidateId);
        if (!result.Success)
        {
            return NotFound(result);
        }
        return Ok(result);
    }

    [HttpPost("skills")]
    [Authorize(Roles = "Candidate")]
    public async Task<IActionResult> AddSkill([FromBody] AddSkillDto dto)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var result = await _candidateService.AddSkillAsync(userId.Value, dto);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpDelete("skills/{skillId}")]
    [Authorize(Roles = "Candidate")]
    public async Task<IActionResult> RemoveSkill(long skillId)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var result = await _candidateService.RemoveSkillAsync(userId.Value, skillId);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpGet("search")]
    [Authorize(Roles = "Recruiter,Admin")]
    public async Task<IActionResult> SearchCandidates([FromQuery] CandidateSearchParams searchParams)
    {
        var result = await _candidateService.SearchCandidatesAsync(searchParams);
        return Ok(result);
    }

    [HttpPost("fill-from-resume/{resumeId}")]
    [Authorize(Roles = "Candidate")]
    public async Task<IActionResult> FillProfileFromResume(long resumeId)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var result = await _candidateService.FillProfileFromResumeAsync(userId.Value, resumeId);
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
}

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GP_Backend.Models.DTOs.Recruiter;
using GP_Backend.Services.Interfaces;

namespace GP_Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class RecruitersController : ControllerBase
{
    private readonly IRecruiterService _recruiterService;

    public RecruitersController(IRecruiterService recruiterService)
    {
        _recruiterService = recruiterService;
    }

    [HttpGet("profile")]
    [Authorize(Roles = "Recruiter")]
    public async Task<IActionResult> GetProfile()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var result = await _recruiterService.GetProfileAsync(userId.Value);
        if (!result.Success)
        {
            return NotFound(result);
        }
        return Ok(result);
    }

    [HttpPut("profile")]
    [Authorize(Roles = "Recruiter")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateRecruiterProfileDto dto)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var result = await _recruiterService.UpdateProfileAsync(userId.Value, dto);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpGet("dashboard")]
    [Authorize(Roles = "Recruiter")]
    public async Task<IActionResult> GetDashboard()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var result = await _recruiterService.GetDashboardAsync(userId.Value);
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

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CompaniesController : ControllerBase
{
    private readonly IRecruiterService _recruiterService;

    public CompaniesController(IRecruiterService recruiterService)
    {
        _recruiterService = recruiterService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAllCompanies()
    {
        var result = await _recruiterService.GetAllCompaniesAsync();
        return Ok(result);
    }

    [HttpGet("{companyId}")]
    public async Task<IActionResult> GetCompany(long companyId)
    {
        var result = await _recruiterService.GetCompanyAsync(companyId);
        if (!result.Success)
        {
            return NotFound(result);
        }
        return Ok(result);
    }

    [HttpPost]
    [Authorize(Roles = "Recruiter,Admin")]
    public async Task<IActionResult> CreateCompany([FromBody] CreateCompanyDto dto)
    {
        var result = await _recruiterService.CreateCompanyAsync(dto);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpPut("{companyId}")]
    [Authorize(Roles = "Recruiter,Admin")]
    public async Task<IActionResult> UpdateCompany(long companyId, [FromBody] UpdateCompanyDto dto)
    {
        var result = await _recruiterService.UpdateCompanyAsync(companyId, dto);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpPost("{companyId}/logo")]
    [Authorize(Roles = "Recruiter,Admin")]
    public async Task<IActionResult> UpdateCompanyLogo(long companyId, IFormFile file)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest("No file uploaded");
        }

        using var stream = file.OpenReadStream();
        var result = await _recruiterService.UpdateCompanyLogoAsync(companyId, stream, file.FileName);
        if (!result.Success)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }
}

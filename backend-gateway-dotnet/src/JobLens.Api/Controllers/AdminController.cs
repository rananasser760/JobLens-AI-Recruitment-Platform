using JobLens.Application.Common;
using JobLens.Application.DTOs.Admin;
using JobLens.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JobLens.Api.Controllers;

[Authorize(Roles = "Admin")]
[Route("api/admin")]
public sealed class AdminController(IAdminService adminService) : AppControllerBase
{
    [HttpPost("scraping/trigger")]
    public async Task<IActionResult> TriggerScrape(
        [FromBody] TriggerScrapeRequest? request,
        CancellationToken cancellationToken)
    {
        var payload = request ?? new TriggerScrapeRequest();
        var result = await adminService.TriggerScrapeAsync(payload, cancellationToken);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("jobs/cleanup")]
    public async Task<IActionResult> CleanupJobs(
        [FromBody] CleanupJobsRequest? request,
        CancellationToken cancellationToken)
    {
        var payload = request ?? new CleanupJobsRequest();
        var result = await adminService.CleanupJobsAsync(payload, cancellationToken);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("recommendations/refresh")]
    public async Task<IActionResult> RefreshRecommendations(CancellationToken cancellationToken)
    {
        var result = await adminService.RefreshRecommendationsAsync(cancellationToken);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet("background-jobs")]
    public async Task<IActionResult> BackgroundJobs(CancellationToken cancellationToken)
    {
        var result = await adminService.GetBackgroundJobsAsync(cancellationToken);
        return result.Success ? Ok(result) : BadRequest(result);
    }
}
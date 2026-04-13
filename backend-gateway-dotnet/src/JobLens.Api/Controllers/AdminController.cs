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
    public async Task<IActionResult> TriggerScrape([FromBody] TriggerScrapeRequest request, CancellationToken cancellationToken) =>
        Ok(await adminService.TriggerScrapeAsync(request, cancellationToken));

    [HttpPost("jobs/cleanup")]
    public async Task<IActionResult> CleanupJobs([FromBody] CleanupJobsRequest request, CancellationToken cancellationToken) =>
        Ok(await adminService.CleanupJobsAsync(request, cancellationToken));

    [HttpPost("recommendations/refresh")]
    public async Task<IActionResult> RefreshRecommendations(CancellationToken cancellationToken) =>
        Ok(await adminService.RefreshRecommendationsAsync(cancellationToken));

    [HttpGet("background-jobs")]
    public async Task<IActionResult> BackgroundJobs(CancellationToken cancellationToken) =>
        Ok(await adminService.GetBackgroundJobsAsync(cancellationToken));
}

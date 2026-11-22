using JobLens.Api.Compatibility;
using JobLens.Api.Contracts;
using JobLens.Application.Common;
using JobLens.Application.Interfaces;
using JobLens.Domain.Entities;
using JobLens.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JobLens.Api.Controllers;

[Route("api/companies")]
public sealed class CompaniesController(JobLensDbContext dbContext, IFileStorageService fileStorageService) : AppControllerBase
{
    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var companies = await dbContext.Companies
            .OrderBy(x => x.Name)
            .Select(x => new CompanyDto(
                x.Id,
                x.Name,
                string.IsNullOrWhiteSpace(x.WebsiteUrl) ? null : x.WebsiteUrl,
                string.IsNullOrWhiteSpace(x.Industry) ? null : x.Industry,
                x.Size,
                MediaUrls.CompanyLogo(x.Id, x.LogoUrl),
                string.IsNullOrWhiteSpace(x.Description) ? null : x.Description,
                string.IsNullOrWhiteSpace(x.Location) ? null : x.Location,
                x.CreatedAtUtc,
                x.Jobs.Count,
                x.Jobs.Count(job => job.IsActive)))
            .ToListAsync(cancellationToken);

        return Ok(new ApiResponse<IReadOnlyList<CompanyDto>>(true, companies));
    }

    [AllowAnonymous]
    [HttpGet("{companyId:long}")]
    public async Task<IActionResult> GetById(long companyId, CancellationToken cancellationToken)
    {
        var company = await dbContext.Companies
            .Where(x => x.Id == companyId)
            .Select(x => new CompanyDto(
                x.Id,
                x.Name,
                string.IsNullOrWhiteSpace(x.WebsiteUrl) ? null : x.WebsiteUrl,
                string.IsNullOrWhiteSpace(x.Industry) ? null : x.Industry,
                x.Size,
                MediaUrls.CompanyLogo(x.Id, x.LogoUrl),
                string.IsNullOrWhiteSpace(x.Description) ? null : x.Description,
                string.IsNullOrWhiteSpace(x.Location) ? null : x.Location,
                x.CreatedAtUtc,
                x.Jobs.Count,
                x.Jobs.Count(job => job.IsActive)))
            .FirstOrDefaultAsync(cancellationToken);

        return company is null
            ? NotFound(new ApiResponse<CompanyDto>(false, null, "Company not found.", ["not_found"]))
            : Ok(new ApiResponse<CompanyDto>(true, company));
    }

    [Authorize(Roles = "Recruiter,Admin")]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCompanyRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new ApiResponse<CompanyDto>(false, null, "Company name is required.", ["validation_error"]));
        }

        var slug = FrontendStatusMapper.Slugify(request.Name);
        if (await dbContext.Companies.AnyAsync(x => x.Slug == slug, cancellationToken))
        {
            return BadRequest(new ApiResponse<CompanyDto>(false, null, "Company with this name already exists.", ["duplicate"]));
        }

        var company = new Company
        {
            Name = request.Name.Trim(),
            Slug = slug,
            WebsiteUrl = request.Website?.Trim() ?? string.Empty,
            Industry = request.Industry?.Trim() ?? string.Empty,
            Size = request.Size,
            Description = request.Description?.Trim() ?? string.Empty,
            Location = request.Location?.Trim() ?? string.Empty,
        };

        dbContext.Companies.Add(company);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (!User.IsInRole("Admin"))
        {
            var creatorId = GetRequiredUserId();
            var recruiter = await dbContext.RecruiterProfiles.FirstOrDefaultAsync(x => x.UserId == creatorId, cancellationToken);
            if (recruiter is not null && !recruiter.CompanyId.HasValue)
            {
                recruiter.CompanyId = company.Id;
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        var dto = new CompanyDto(
            company.Id,
            company.Name,
            string.IsNullOrWhiteSpace(company.WebsiteUrl) ? null : company.WebsiteUrl,
            string.IsNullOrWhiteSpace(company.Industry) ? null : company.Industry,
            company.Size,
            MediaUrls.CompanyLogo(company.Id, company.LogoUrl),
            string.IsNullOrWhiteSpace(company.Description) ? null : company.Description,
            string.IsNullOrWhiteSpace(company.Location) ? null : company.Location,
            company.CreatedAtUtc,
            0,
            0);

        return Ok(new ApiResponse<CompanyDto>(true, dto, "Company created."));
    }

    [Authorize(Roles = "Recruiter,Admin")]
    [HttpPut("{companyId:long}")]
    public async Task<IActionResult> Update(long companyId, [FromBody] UpdateCompanyRequest request, CancellationToken cancellationToken)
    {
        if (!await CanManageCompanyAsync(companyId, cancellationToken))
        {
            return Forbid();
        }

        var company = await dbContext.Companies.FirstOrDefaultAsync(x => x.Id == companyId, cancellationToken);
        if (company is null)
        {
            return NotFound(new ApiResponse<CompanyDto>(false, null, "Company not found.", ["not_found"]));
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            var newSlug = FrontendStatusMapper.Slugify(request.Name);
            var conflict = await dbContext.Companies.AnyAsync(x => x.Slug == newSlug && x.Id != companyId, cancellationToken);
            if (conflict)
            {
                return BadRequest(new ApiResponse<CompanyDto>(false, null, "Another company with this name already exists.", ["duplicate_name"]));
            }

            company.Name = request.Name.Trim();
            company.Slug = newSlug;
        }

        if (request.Website is not null)
        {
            company.WebsiteUrl = request.Website.Trim();
        }

        if (request.Industry is not null)
        {
            company.Industry = request.Industry.Trim();
        }

        if (request.Size.HasValue)
        {
            company.Size = request.Size.Value;
        }

        if (request.Description is not null)
        {
            company.Description = request.Description.Trim();
        }

        if (request.Location is not null)
        {
            company.Location = request.Location.Trim();
        }

        company.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        var totalJobs = await dbContext.Jobs.CountAsync(x => x.CompanyId == company.Id, cancellationToken);
        var activeJobs = await dbContext.Jobs.CountAsync(x => x.CompanyId == company.Id && x.IsActive, cancellationToken);

        var dto = new CompanyDto(
            company.Id,
            company.Name,
            string.IsNullOrWhiteSpace(company.WebsiteUrl) ? null : company.WebsiteUrl,
            string.IsNullOrWhiteSpace(company.Industry) ? null : company.Industry,
            company.Size,
            MediaUrls.CompanyLogo(company.Id, company.LogoUrl),
            string.IsNullOrWhiteSpace(company.Description) ? null : company.Description,
            string.IsNullOrWhiteSpace(company.Location) ? null : company.Location,
            company.CreatedAtUtc,
            totalJobs,
            activeJobs);

        return Ok(new ApiResponse<CompanyDto>(true, dto, "Company updated."));
    }

    [AllowAnonymous]
    [HttpGet("{companyId:long}/logo")]
    public async Task<IActionResult> GetLogo(long companyId, CancellationToken cancellationToken)
    {
        var logo = await dbContext.Companies
            .Where(x => x.Id == companyId)
            .Select(x => x.LogoUrl)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(logo) || logo.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound();
        }

        var bytes = await fileStorageService.ReadAsync(logo, cancellationToken);
        if (bytes is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=86400";
        return File(bytes, UploadRules.ContentTypeFor(logo));
    }

    [Authorize(Roles = "Recruiter,Admin")]
    [HttpPost("{companyId:long}/logo")]
    [RequestSizeLimit(8_000_000)]
    public async Task<IActionResult> UpdateLogo(long companyId, [FromForm] IFormFile file, CancellationToken cancellationToken)
    {
        if (!await CanManageCompanyAsync(companyId, cancellationToken))
        {
            return Forbid();
        }

        if (file is null || file.Length == 0)
        {
            return BadRequest(new ApiResponse<bool>(false, false, "Logo file is required."));
        }

        if (!UploadRules.HasAllowedExtension(file.FileName, UploadRules.ImageExtensions))
        {
            return BadRequest(new ApiResponse<bool>(false, false, "Logo must be a PNG, JPG, WEBP or GIF file.", ["invalid_file_type"]));
        }

        var company = await dbContext.Companies.FirstOrDefaultAsync(x => x.Id == companyId, cancellationToken);
        if (company is null)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Company not found.", ["not_found"]));
        }

        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);

        var oldLogo = company.LogoUrl;
        var newLogoPath = await fileStorageService.SaveAsync(file.FileName, memory.ToArray(), cancellationToken);

        try
        {
            company.LogoUrl = newLogoPath;
            company.UpdatedAtUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(oldLogo))
            {
                await fileStorageService.DeleteAsync(oldLogo, cancellationToken);
            }

            return Ok(new ApiResponse<bool>(true, true, "Company logo updated."));
        }
        catch
        {
            await fileStorageService.DeleteAsync(newLogoPath, CancellationToken.None);
            throw;
        }
    }

    private async Task<bool> CanManageCompanyAsync(long companyId, CancellationToken cancellationToken)
    {
        if (User.IsInRole("Admin"))
        {
            return true;
        }

        var userId = GetRequiredUserId();
        var recruiter = await dbContext.RecruiterProfiles.FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        return recruiter is not null && recruiter.CompanyId == companyId;
    }
}
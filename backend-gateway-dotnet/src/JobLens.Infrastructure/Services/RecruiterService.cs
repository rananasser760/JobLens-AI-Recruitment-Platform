using JobLens.Application.Common;
using JobLens.Application.DTOs.Recruiters;
using JobLens.Application.Interfaces;
using JobLens.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace JobLens.Infrastructure.Services;

public sealed class RecruiterService(Persistence.JobLensDbContext dbContext) : IRecruiterService
{
    public async Task<ApiResponse<RecruiterProfileDto>> GetProfileAsync(long userId, CancellationToken cancellationToken)
    {
        var profile = await dbContext.RecruiterProfiles
            .Include(x => x.User)
            .Include(x => x.Company)
            .FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);

        if (profile is null)
        {
            return new ApiResponse<RecruiterProfileDto>(false, null, "Recruiter profile not found.", ["not_found"]);
        }

        return new ApiResponse<RecruiterProfileDto>(true, ToDto(profile));
    }

    public async Task<ApiResponse<RecruiterProfileDto>> UpdateProfileAsync(long userId, UpdateRecruiterProfileRequest request, CancellationToken cancellationToken)
    {
        var profile = await dbContext.RecruiterProfiles
            .Include(x => x.User)
            .Include(x => x.Company)
            .FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);

        if (profile is null)
        {
            return new ApiResponse<RecruiterProfileDto>(false, null, "Recruiter profile not found.", ["not_found"]);
        }

        profile.JobTitle = request.JobTitle.Trim();

        var trimmedCompName = request.CompanyName.Trim();
        var slug = GenerateSlug(trimmedCompName);

        if (profile.Company is null)
        {
            var company = await dbContext.Companies.FirstOrDefaultAsync(x => x.Slug == slug, cancellationToken);
            if (company is not null && await dbContext.RecruiterProfiles.AnyAsync(x => x.CompanyId == company.Id && x.Id != profile.Id, cancellationToken))
            {
                return new ApiResponse<RecruiterProfileDto>(false, null, "This company is already managed by another recruiter.", ["company_already_claimed"]);
            }

            if (company is null)
            {
                company = new Company
                {
                    Name = trimmedCompName,
                    Slug = slug,
                    Description = request.CompanyDescription.Trim(),
                    WebsiteUrl = request.CompanyWebsite.Trim(),
                };
                dbContext.Companies.Add(company);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            profile.CompanyId = company.Id;
            profile.Company = company;
        }
        else
        {
            var slugConflict = await dbContext.Companies.AnyAsync(x => x.Slug == slug && x.Id != profile.Company.Id, cancellationToken);
            if (!slugConflict)
            {
                profile.Company.Name = trimmedCompName;
                profile.Company.Slug = slug;
            }

            profile.Company.Description = request.CompanyDescription.Trim();
            profile.Company.WebsiteUrl = request.CompanyWebsite.Trim();
            profile.Company.UpdatedAtUtc = DateTime.UtcNow;
        }

        profile.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ApiResponse<RecruiterProfileDto>(true, ToDto(profile), "Recruiter profile updated.");
    }

    private static string GenerateSlug(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();

        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Trim('-');
    }

    private static RecruiterProfileDto ToDto(Domain.Entities.RecruiterProfile profile) =>
        new(
            profile.Id,
            profile.UserId,
            profile.User.DisplayName,
            profile.JobTitle,
            profile.CompanyId,
            profile.Company?.Name ?? string.Empty,
            profile.Company?.Slug ?? string.Empty,
            profile.Company?.WebsiteUrl ?? string.Empty);
}
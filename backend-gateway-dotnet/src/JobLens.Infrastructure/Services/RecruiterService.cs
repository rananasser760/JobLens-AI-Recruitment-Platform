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

        var slug = request.CompanyName.Trim().ToLowerInvariant().Replace(" ", "-");
        var company = profile.Company;
        if (company is null)
        {
            company = await dbContext.Companies.FirstOrDefaultAsync(x => x.Slug == slug, cancellationToken);
            if (company is null)
            {
                company = new Company { Name = request.CompanyName.Trim(), Slug = slug };
                dbContext.Companies.Add(company);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            profile.CompanyId = company.Id;
        }

        company.Name = request.CompanyName.Trim();
        company.Description = request.CompanyDescription.Trim();
        company.WebsiteUrl = request.CompanyWebsite.Trim();

        await dbContext.SaveChangesAsync(cancellationToken);
        return new ApiResponse<RecruiterProfileDto>(true, ToDto(profile), "Recruiter profile updated.");
    }

    private static RecruiterProfileDto ToDto(Domain.Entities.RecruiterProfile profile) =>
        new(profile.Id, profile.UserId, profile.User.DisplayName, profile.JobTitle, profile.CompanyId, profile.Company?.Name ?? string.Empty, profile.Company?.Slug ?? string.Empty, profile.Company?.WebsiteUrl ?? string.Empty);
}

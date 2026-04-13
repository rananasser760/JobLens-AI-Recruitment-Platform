using JobLens.Application.Common;
using JobLens.Application.DTOs.Candidates;
using JobLens.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace JobLens.Infrastructure.Services;

public sealed class CandidateService(Persistence.JobLensDbContext dbContext) : ICandidateService
{
    public async Task<ApiResponse<CandidateProfileDto>> GetProfileAsync(long userId, CancellationToken cancellationToken)
    {
        var profile = await dbContext.CandidateProfiles.Include(x => x.User).FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (profile is null)
        {
            return new ApiResponse<CandidateProfileDto>(false, null, "Candidate profile not found.", ["not_found"]);
        }

        return new ApiResponse<CandidateProfileDto>(true, ToDto(profile));
    }

    public async Task<ApiResponse<CandidateProfileDto>> UpdateProfileAsync(long userId, UpdateCandidateProfileRequest request, CancellationToken cancellationToken)
    {
        var profile = await dbContext.CandidateProfiles.Include(x => x.User).FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (profile is null)
        {
            return new ApiResponse<CandidateProfileDto>(false, null, "Candidate profile not found.", ["not_found"]);
        }

        profile.Headline = request.Headline.Trim();
        profile.Summary = request.Summary.Trim();
        profile.Location = request.Location.Trim();
        profile.YearsExperience = request.YearsExperience;
        profile.SkillsJson = ServiceJson.Serialize(request.Skills);

        await dbContext.SaveChangesAsync(cancellationToken);
        return new ApiResponse<CandidateProfileDto>(true, ToDto(profile), "Candidate profile updated.");
    }

    private static CandidateProfileDto ToDto(Domain.Entities.CandidateProfile profile) =>
        new(profile.Id, profile.UserId, profile.User.DisplayName, profile.Headline, profile.Summary, profile.Location, profile.YearsExperience, ServiceJson.DeserializeStringList(profile.SkillsJson));
}

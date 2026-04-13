namespace JobLens.Application.DTOs.Recruiters;

public sealed record RecruiterProfileDto(
    long RecruiterId,
    long UserId,
    string DisplayName,
    string JobTitle,
    long? CompanyId,
    string CompanyName,
    string CompanySlug,
    string CompanyWebsite);

public sealed record UpdateRecruiterProfileRequest(
    string JobTitle,
    string CompanyName,
    string CompanyDescription,
    string CompanyWebsite);

namespace JobLens.Application.DTOs.Candidates;

public sealed record CandidateProfileDto(
    long CandidateId,
    long UserId,
    string DisplayName,
    string Headline,
    string Summary,
    string Location,
    int YearsExperience,
    IReadOnlyList<string> Skills);

public sealed record UpdateCandidateProfileRequest(
    string Headline,
    string Summary,
    string Location,
    int YearsExperience,
    IReadOnlyList<string> Skills);

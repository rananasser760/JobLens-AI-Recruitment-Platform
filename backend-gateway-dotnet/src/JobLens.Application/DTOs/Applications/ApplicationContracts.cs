using JobLens.Domain.Enums;

namespace JobLens.Application.DTOs.Applications;

public sealed record CreateApplicationRequest(long JobId, long? ResumeId);

public sealed record ApplicationDto(
    long ApplicationId,
    long JobId,
    string JobTitle,
    ApplicationStatus Status,
    double? LatestAtsScore,
    DateTime SubmittedAtUtc,
    string RedirectUrl);

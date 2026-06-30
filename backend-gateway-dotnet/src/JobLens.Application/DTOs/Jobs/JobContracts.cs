using JobLens.Domain.Enums;

namespace JobLens.Application.DTOs.Jobs;

public sealed record JobPostingDto(
    long JobId,
    JobSourceType SourceType,
    string Title,
    string Description,
    string Location,
    string CompanyName,
    bool IsActive,
    string RedirectUrl,
    DateTime? PostedAtUtc,
    IReadOnlyList<string> Skills);

public sealed record CreateJobRequest(
    string Title,
    string Description,
    string Location,
    IReadOnlyList<string> Skills,
    DateTime? ExpiresAtUtc = null);

public sealed record UpdateJobRequest(
    string Title,
    string Description,
    string Location,
    IReadOnlyList<string> Skills,
    bool IsActive,
    DateTime? ExpiresAtUtc = null);

public sealed record RecommendationDto(long TargetId, string TargetType, double Score, string Reason, string PreviewJson);

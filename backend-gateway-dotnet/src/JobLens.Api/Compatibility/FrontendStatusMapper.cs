using JobLens.Api.Contracts;
using JobLens.Domain.Enums;

namespace JobLens.Api.Compatibility;

internal static class FrontendStatusMapper
{
    public static string ToFrontend(ApplicationStatus status) => status switch
    {
        ApplicationStatus.Submitted => nameof(ApplicationStatus.Submitted),
        ApplicationStatus.AtsPending => nameof(ApplicationStatus.AtsPending),
        ApplicationStatus.AtsQualified => nameof(ApplicationStatus.AtsQualified),
        ApplicationStatus.AtsRejected => nameof(ApplicationStatus.AtsRejected),
        ApplicationStatus.InterviewScheduled => "InterviewScheduled",
        ApplicationStatus.InterviewCompleted => "InterviewCompleted",
        ApplicationStatus.Offered => nameof(ApplicationStatus.Offered),
        ApplicationStatus.Rejected => nameof(ApplicationStatus.Rejected),
        ApplicationStatus.Withdrawn => nameof(ApplicationStatus.Withdrawn),
        ApplicationStatus.ExternalRedirected => nameof(ApplicationStatus.ExternalRedirected),
        _ => nameof(ApplicationStatus.Submitted),
    };

    public static ApplicationStatus FromFrontend(string? status) => (status ?? string.Empty).Trim() switch
    {
        nameof(ApplicationStatus.Submitted) => ApplicationStatus.Submitted,
        nameof(ApplicationStatus.AtsPending) => ApplicationStatus.AtsPending,
        nameof(ApplicationStatus.AtsQualified) => ApplicationStatus.AtsQualified,
        nameof(ApplicationStatus.AtsRejected) => ApplicationStatus.AtsRejected,
        nameof(ApplicationStatus.InterviewScheduled) => ApplicationStatus.InterviewScheduled,
        nameof(ApplicationStatus.InterviewCompleted) => ApplicationStatus.InterviewCompleted,
        nameof(ApplicationStatus.Offered) => ApplicationStatus.Offered,
        nameof(ApplicationStatus.Rejected) => ApplicationStatus.Rejected,
        nameof(ApplicationStatus.Withdrawn) => ApplicationStatus.Withdrawn,
        nameof(ApplicationStatus.ExternalRedirected) => ApplicationStatus.ExternalRedirected,

        _ => ApplicationStatus.Submitted,
    };

    public static string ToFrontend(InterviewSessionStatus status) => status switch
    {
        InterviewSessionStatus.Draft => nameof(InterviewSessionStatus.Draft),
        InterviewSessionStatus.Scheduled => nameof(InterviewSessionStatus.Scheduled),
        InterviewSessionStatus.Live => nameof(InterviewSessionStatus.Live),
        InterviewSessionStatus.Completed => nameof(InterviewSessionStatus.Completed),
        InterviewSessionStatus.Abandoned => nameof(InterviewSessionStatus.Abandoned),
        InterviewSessionStatus.ReviewRequired => nameof(InterviewSessionStatus.ReviewRequired),
        InterviewSessionStatus.Cancelled => nameof(InterviewSessionStatus.Cancelled),
        _ => nameof(InterviewSessionStatus.Draft),
    };

    public static string ToFrontend(JobSourceType sourceType) => sourceType switch
    {
        JobSourceType.Internal => "Internal",
        JobSourceType.External => "Scraped",
        _ => "Internal",
    };

    public static PaginatedResponseDto<T> ToPage<T>(IReadOnlyList<T> items, int pageNumber, int pageSize, int totalCount)
    {
        var safePage = pageNumber <= 0 ? 1 : pageNumber;
        var safeSize = pageSize <= 0 ? 20 : pageSize;
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)safeSize);

        return new PaginatedResponseDto<T>(
            items,
            totalCount,
            safePage,
            safeSize,
            totalPages,
            safePage > 1,
            safePage < totalPages);
    }

    public static string Slugify(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value.Trim().ToLowerInvariant().Select(ch =>
            char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Trim('-');
    }
}

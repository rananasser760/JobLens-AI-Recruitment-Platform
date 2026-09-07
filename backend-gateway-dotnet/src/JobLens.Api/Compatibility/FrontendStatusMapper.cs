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
        ApplicationStatus.InterviewScheduled => nameof(ApplicationStatus.InterviewScheduled),
        ApplicationStatus.InterviewCompleted => nameof(ApplicationStatus.InterviewCompleted),
        ApplicationStatus.Offered => nameof(ApplicationStatus.Offered),
        ApplicationStatus.Rejected => nameof(ApplicationStatus.Rejected),
        ApplicationStatus.Withdrawn => nameof(ApplicationStatus.Withdrawn),
        ApplicationStatus.ExternalRedirected => nameof(ApplicationStatus.ExternalRedirected),
        _ => nameof(ApplicationStatus.Submitted),
    };

    public static ApplicationStatus FromFrontend(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return ApplicationStatus.Submitted;
        }

        var normalized = status.Trim();

        if (string.Equals(normalized, nameof(ApplicationStatus.Submitted), StringComparison.OrdinalIgnoreCase))
            return ApplicationStatus.Submitted;
        if (string.Equals(normalized, nameof(ApplicationStatus.AtsPending), StringComparison.OrdinalIgnoreCase))
            return ApplicationStatus.AtsPending;
        if (string.Equals(normalized, nameof(ApplicationStatus.AtsQualified), StringComparison.OrdinalIgnoreCase))
            return ApplicationStatus.AtsQualified;
        if (string.Equals(normalized, nameof(ApplicationStatus.AtsRejected), StringComparison.OrdinalIgnoreCase))
            return ApplicationStatus.AtsRejected;
        if (string.Equals(normalized, nameof(ApplicationStatus.InterviewScheduled), StringComparison.OrdinalIgnoreCase))
            return ApplicationStatus.InterviewScheduled;
        if (string.Equals(normalized, nameof(ApplicationStatus.InterviewCompleted), StringComparison.OrdinalIgnoreCase))
            return ApplicationStatus.InterviewCompleted;
        if (string.Equals(normalized, nameof(ApplicationStatus.Offered), StringComparison.OrdinalIgnoreCase))
            return ApplicationStatus.Offered;
        if (string.Equals(normalized, nameof(ApplicationStatus.Rejected), StringComparison.OrdinalIgnoreCase))
            return ApplicationStatus.Rejected;
        if (string.Equals(normalized, nameof(ApplicationStatus.Withdrawn), StringComparison.OrdinalIgnoreCase))
            return ApplicationStatus.Withdrawn;
        if (string.Equals(normalized, nameof(ApplicationStatus.ExternalRedirected), StringComparison.OrdinalIgnoreCase))
            return ApplicationStatus.ExternalRedirected;

        return ApplicationStatus.Submitted;
    }

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

    public static InterviewSessionStatus ParseInterviewStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return InterviewSessionStatus.Draft;
        }

        var normalized = status.Trim();

        if (string.Equals(normalized, nameof(InterviewSessionStatus.Draft), StringComparison.OrdinalIgnoreCase))
            return InterviewSessionStatus.Draft;
        if (string.Equals(normalized, nameof(InterviewSessionStatus.Scheduled), StringComparison.OrdinalIgnoreCase))
            return InterviewSessionStatus.Scheduled;
        if (string.Equals(normalized, nameof(InterviewSessionStatus.Live), StringComparison.OrdinalIgnoreCase))
            return InterviewSessionStatus.Live;
        if (string.Equals(normalized, nameof(InterviewSessionStatus.Completed), StringComparison.OrdinalIgnoreCase))
            return InterviewSessionStatus.Completed;
        if (string.Equals(normalized, nameof(InterviewSessionStatus.Abandoned), StringComparison.OrdinalIgnoreCase))
            return InterviewSessionStatus.Abandoned;
        if (string.Equals(normalized, nameof(InterviewSessionStatus.ReviewRequired), StringComparison.OrdinalIgnoreCase))
            return InterviewSessionStatus.ReviewRequired;
        if (string.Equals(normalized, nameof(InterviewSessionStatus.Cancelled), StringComparison.OrdinalIgnoreCase))
            return InterviewSessionStatus.Cancelled;

        return InterviewSessionStatus.Draft;
    }

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
namespace JobLens.Application.DTOs.Admin;

public sealed record TriggerScrapeRequest(int? MaxCategories = null);
public sealed record CleanupJobsRequest(int StaleAfterDays = 45);
public sealed record BackgroundJobDto(long JobId, string JobType, string Status, int Attempts, DateTime? LastRunAtUtc, DateTime? NextRunAtUtc, string Error);

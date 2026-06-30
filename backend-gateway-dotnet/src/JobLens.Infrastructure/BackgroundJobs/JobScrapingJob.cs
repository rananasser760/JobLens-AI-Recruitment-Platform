using Hangfire;
using JobLens.Application.Contracts;
using JobLens.Application.Interfaces;
using JobLens.Domain.Entities;
using JobLens.Domain.Enums;
using JobLens.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace JobLens.Infrastructure.BackgroundJobs;

public sealed class JobScrapingJob(
    Persistence.JobLensDbContext dbContext,
    IAiBackendClient aiBackendClient,
    IContentHashService contentHashService,
    IBackgroundJobClient backgroundJobs)
{
    public async Task RunAsync(int? maxCategories)
    {
        var state = new BackgroundJobState
        {
            JobType = "ScrapeJobs",
            CorrelationId = Guid.NewGuid().ToString("N"),
            Status = "Running",
            LastRunAtUtc = DateTime.UtcNow,
            PayloadJson = BuildPayloadJson(
                phase: "scraping",
                message: "Collecting jobs from sources...",
                progressPercent: 10,
                requestedMaxCategories: maxCategories),
        };
        dbContext.BackgroundJobStates.Add(state);
        await dbContext.SaveChangesAsync();

        var processedJobs = 0;
        var insertedJobs = 0;
        var updatedJobs = 0;
        var processedCategories = 0;
        var upsertedJobs = 0;
        var totalJobs = 0;
        var skippedInvalidJobs = 0;

        try
        {
            var result = await aiBackendClient.ScrapeJobsAsync(new ScrapeJobsRequest(maxCategories), CancellationToken.None);
            if (!result.Success || result.Data is null)
            {
                state.Status = "Failed";
                state.Error = result.Error?.Message ?? "Scrape failed";
                state.LastRunAtUtc = DateTime.UtcNow;
                state.PayloadJson = BuildPayloadJson(
                    phase: "failed",
                    message: state.Error,
                    progressPercent: 100,
                    requestedMaxCategories: maxCategories,
                    processedJobs: processedJobs,
                    totalJobs: totalJobs,
                    insertedJobs: insertedJobs,
                    updatedJobs: updatedJobs,
                    processedCategories: processedCategories,
                    upsertedJobs: upsertedJobs,
                    skippedInvalidJobs: skippedInvalidJobs);
                await dbContext.SaveChangesAsync();
                return;
            }

            processedCategories = result.Data.ProcessedCategories;
            upsertedJobs = result.Data.UpsertedJobs;
            totalJobs = result.Data.Jobs.Count;

            state.PayloadJson = BuildPayloadJson(
                phase: "persisting",
                message: totalJobs > 0 ? "Persisting scraped jobs..." : "No jobs returned from scraper.",
                progressPercent: totalJobs > 0 ? 55 : 95,
                requestedMaxCategories: maxCategories,
                processedJobs: processedJobs,
                totalJobs: totalJobs,
                insertedJobs: insertedJobs,
                updatedJobs: updatedJobs,
                processedCategories: processedCategories,
                upsertedJobs: upsertedJobs,
                skippedInvalidJobs: skippedInvalidJobs);
            state.LastRunAtUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync();

            foreach (var item in result.Data.Jobs)
            {
                var incomingSource = (item.Source ?? string.Empty).Trim();
                var incomingExternalJobId = (item.ExternalJobId ?? string.Empty).Trim();
                var incomingSourceUrl = (item.SourceUrl ?? string.Empty).Trim();
                var incomingRedirectUrl = string.IsNullOrWhiteSpace(item.RedirectUrl)
                    ? incomingSourceUrl
                    : item.RedirectUrl.Trim();
                var incomingTitle = (item.Title ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(incomingSourceUrl))
                {
                    incomingSourceUrl = incomingRedirectUrl;
                }

                if (string.IsNullOrWhiteSpace(incomingRedirectUrl))
                {
                    incomingRedirectUrl = incomingSourceUrl;
                }

                var hasRecognizedExternalSource = IsExternalSource(incomingSource)
                    || LooksLikeExternalJobUrl(incomingSourceUrl)
                    || LooksLikeExternalJobUrl(incomingRedirectUrl);

                if (string.IsNullOrWhiteSpace(incomingTitle)
                    || string.IsNullOrWhiteSpace(incomingSourceUrl)
                    || !hasRecognizedExternalSource)
                {
                    skippedInvalidJobs += 1;
                    continue;
                }

                var incomingDescription = (item.Description ?? string.Empty).Trim();
                var incomingRequirements = (item.Requirements ?? string.Empty).Trim();
                var incomingResponsibilities = (item.Responsibilities ?? string.Empty).Trim();
                var incomingLocation = (item.Location ?? string.Empty).Trim();
                var incomingCity = (item.City ?? string.Empty).Trim();
                var incomingCountry = (item.Country ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(incomingLocation))
                {
                    incomingLocation = BuildLocation(incomingCity, incomingCountry);
                }
                var incomingEmploymentType = (item.EmploymentType ?? string.Empty).Trim();
                var incomingExperienceLevel = (item.ExperienceLevel ?? string.Empty).Trim();
                var incomingEnrichmentSource = (item.EnrichmentSource ?? string.Empty).Trim();
                var incomingSkills = (item.Skills ?? [])
                    .Where(skill => !string.IsNullOrWhiteSpace(skill))
                    .Select(skill => skill.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var metadataPayload = item.Metadata is not null
                    ? new Dictionary<string, object?>(item.Metadata)
                    : new Dictionary<string, object?>();

                if (!string.IsNullOrWhiteSpace(incomingSource))
                {
                    metadataPayload["source"] = incomingSource;
                }

                if (!string.IsNullOrWhiteSpace(incomingCity))
                {
                    metadataPayload["city"] = incomingCity;
                }

                if (!string.IsNullOrWhiteSpace(incomingCountry))
                {
                    metadataPayload["country"] = incomingCountry;
                }

                if (!string.IsNullOrWhiteSpace(incomingEnrichmentSource))
                {
                    metadataPayload["enrichment_source"] = incomingEnrichmentSource;
                }

                var incomingMetadataJson = metadataPayload.Count > 0
                    ? ServiceJson.Serialize(metadataPayload)
                    : null;

                var existing = await dbContext.Jobs.FirstOrDefaultAsync(x =>
                    x.SourceType == JobSourceType.External &&
                    x.ExternalJobId == incomingExternalJobId);

                if (existing is null && !string.IsNullOrWhiteSpace(incomingSourceUrl))
                {
                    existing = await dbContext.Jobs.FirstOrDefaultAsync(x =>
                        x.SourceType == JobSourceType.External &&
                        x.SourceUrl == incomingSourceUrl);
                }

                if (existing is null)
                {
                    existing = new JobPosting
                    {
                        SourceType = JobSourceType.External,
                        ExternalJobId = incomingExternalJobId,
                        SourceUrl = incomingSourceUrl,
                        RedirectUrl = incomingRedirectUrl,
                        Title = incomingTitle,
                        Description = incomingDescription,
                        Requirements = string.Empty,
                        Responsibilities = string.Empty,
                        EmploymentType = "FullTime",
                        ExperienceLevel = string.Empty,
                        MetadataJson = "{}",
                        SkillsJson = "[]",
                    };
                    dbContext.Jobs.Add(existing);
                    insertedJobs += 1;
                }
                else
                {
                    updatedJobs += 1;
                }

                existing.ExternalJobId = PreferText(incomingExternalJobId, existing.ExternalJobId);
                existing.SourceUrl = PreferText(incomingSourceUrl, existing.SourceUrl);
                existing.RedirectUrl = PreferText(incomingRedirectUrl, existing.RedirectUrl, existing.SourceUrl);
                existing.Title = PreferText(incomingTitle, existing.Title);
                existing.Description = PreferText(incomingDescription, existing.Description);
                existing.Requirements = PreferText(incomingRequirements, existing.Requirements, string.Empty);
                existing.Responsibilities = PreferText(incomingResponsibilities, existing.Responsibilities, string.Empty);
                existing.Location = PreferText(incomingLocation, existing.Location);
                existing.EmploymentType = PreferText(incomingEmploymentType, existing.EmploymentType, "FullTime");
                existing.ExperienceLevel = PreferText(incomingExperienceLevel, existing.ExperienceLevel, string.Empty);

                if (!string.IsNullOrWhiteSpace(incomingMetadataJson))
                {
                    existing.MetadataJson = incomingMetadataJson;
                }
                else if (string.IsNullOrWhiteSpace(existing.MetadataJson))
                {
                    existing.MetadataJson = "{}";
                }

                if (incomingSkills.Length > 0)
                {
                    existing.SkillsJson = ServiceJson.Serialize(incomingSkills);
                }
                else if (string.IsNullOrWhiteSpace(existing.SkillsJson))
                {
                    existing.SkillsJson = "[]";
                }

                if (item.PostedAtUtc.HasValue)
                {
                    existing.PostedAtUtc = item.PostedAtUtc;
                }

                var finalSkills = ServiceJson.DeserializeStringList(existing.SkillsJson);
                existing.ContentHash = contentHashService.Compute(
                    $"{existing.Title}\n{existing.Description}\n{existing.Location}\n{string.Join(',', finalSkills)}");
                existing.IsActive = true;

                processedJobs += 1;
                if (processedJobs % 25 == 0 || processedJobs == totalJobs)
                {
                    var processingPercent = 55;
                    if (totalJobs > 0)
                    {
                        processingPercent = 55 + (int)Math.Round((processedJobs / (double)totalJobs) * 40d);
                    }

                    state.PayloadJson = BuildPayloadJson(
                        phase: "persisting",
                        message: $"Saved {processedJobs}/{totalJobs} scraped jobs.",
                        progressPercent: Math.Clamp(processingPercent, 55, 95),
                        requestedMaxCategories: maxCategories,
                        processedJobs: processedJobs,
                        totalJobs: totalJobs,
                        insertedJobs: insertedJobs,
                        updatedJobs: updatedJobs,
                        processedCategories: processedCategories,
                        upsertedJobs: upsertedJobs,
                        skippedInvalidJobs: skippedInvalidJobs);
                    state.LastRunAtUtc = DateTime.UtcNow;
                    await dbContext.SaveChangesAsync();
                }
            }

            state.Status = "Completed";
            state.LastRunAtUtc = DateTime.UtcNow;
            state.PayloadJson = BuildPayloadJson(
                phase: "completed",
                message: "Scrape completed successfully.",
                progressPercent: 100,
                requestedMaxCategories: maxCategories,
                processedJobs: processedJobs,
                totalJobs: totalJobs,
                insertedJobs: insertedJobs,
                updatedJobs: updatedJobs,
                processedCategories: processedCategories,
                upsertedJobs: upsertedJobs,
                skippedInvalidJobs: skippedInvalidJobs);
            await dbContext.SaveChangesAsync();

            if (processedJobs > 0)
            {
                // Scraped SQL jobs must be re-indexed in recommendation vectors after ingestion.
                backgroundJobs.Enqueue<RecommendationRefreshJob>(job => job.RefreshAllAsync());
            }
        }
        catch (Exception ex)
        {
            state.Status = "Failed";
            state.Error = ex.Message;
            state.Attempts += 1;
            state.LastRunAtUtc = DateTime.UtcNow;
            var failurePercent = totalJobs > 0
                ? 55 + (int)Math.Round((processedJobs / (double)totalJobs) * 40d)
                : 35;
            state.PayloadJson = BuildPayloadJson(
                phase: "failed",
                message: ex.Message,
                progressPercent: Math.Clamp(failurePercent, 0, 100),
                requestedMaxCategories: maxCategories,
                processedJobs: processedJobs,
                totalJobs: totalJobs,
                insertedJobs: insertedJobs,
                updatedJobs: updatedJobs,
                processedCategories: processedCategories,
                upsertedJobs: upsertedJobs,
                skippedInvalidJobs: skippedInvalidJobs);
            await dbContext.SaveChangesAsync();
        }
    }

    private static string BuildPayloadJson(
        string phase,
        string message,
        int progressPercent,
        int? requestedMaxCategories,
        int processedJobs = 0,
        int totalJobs = 0,
        int insertedJobs = 0,
        int updatedJobs = 0,
        int processedCategories = 0,
        int upsertedJobs = 0,
        int skippedInvalidJobs = 0)
    {
        return ServiceJson.Serialize(new
        {
            phase,
            message,
            progressPercent = Math.Clamp(progressPercent, 0, 100),
            requestedMaxCategories,
            processedJobs,
            totalJobs,
            insertedJobs,
            updatedJobs,
            processedCategories,
            upsertedJobs,
            skippedInvalidJobs,
            timestampUtc = DateTime.UtcNow,
        });
    }

    private static bool IsExternalSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        return source.Equals("Wuzzuf", StringComparison.OrdinalIgnoreCase)
            || source.Equals("LinkedIn", StringComparison.OrdinalIgnoreCase)
            || source.Equals("External", StringComparison.OrdinalIgnoreCase)
            || source.Equals("Scraped", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeExternalJobUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return url.Contains("linkedin.com", StringComparison.OrdinalIgnoreCase)
            || url.Contains("wuzzuf.net", StringComparison.OrdinalIgnoreCase);
    }

    private static string PreferText(string? incoming, string? existing, string fallback = "")
    {
        if (!string.IsNullOrWhiteSpace(incoming))
        {
            return incoming.Trim();
        }

        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing.Trim();
        }

        return fallback;
    }

    private static string BuildLocation(string? city, string? country)
    {
        var cityValue = (city ?? string.Empty).Trim();
        var countryValue = (country ?? string.Empty).Trim();

        if (!string.IsNullOrWhiteSpace(cityValue) && !string.IsNullOrWhiteSpace(countryValue))
        {
            return $"{cityValue}, {countryValue}";
        }

        if (!string.IsNullOrWhiteSpace(cityValue))
        {
            return cityValue;
        }

        if (!string.IsNullOrWhiteSpace(countryValue))
        {
            return countryValue;
        }

        return string.Empty;
    }
}

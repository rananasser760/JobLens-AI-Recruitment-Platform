using System.Text.Json;
using JobLens.Application.Contracts;
using JobLens.Application.Interfaces;
using JobLens.Domain.Entities;
using JobLens.Domain.Enums;
using JobLens.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace JobLens.Infrastructure.BackgroundJobs;

public sealed class ResumeWorkflowJob(
    Persistence.JobLensDbContext dbContext,
    IAiBackendClient aiBackendClient,
    RecommendationRefreshJob recommendationRefreshJob)
{
    public async Task ProcessResumeAsync(long resumeId)
    {
        var resume = await dbContext.Resumes
            .Include(x => x.CandidateProfile)
                .ThenInclude(x => x.User)
            .Include(x => x.ParsedResumeResult)
            .FirstOrDefaultAsync(x => x.Id == resumeId);

        if (resume is null)
        {
            return;
        }

        if (resume.ParseStatus == "Completed" && resume.ParsedResumeResult != null)
        {
            await RunVectorUpsertOnlyAsync(resume);
            return;
        }

        if (resume.ParseStatus == "Parsing" && resume.UpdatedAtUtc > DateTime.UtcNow.AddMinutes(-10))
        {
            throw new InvalidOperationException("Resume is currently being parsed by another process. Will retry later.");
        }

        resume.ParseStatus = "Parsing";
        resume.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();

        var state = await StartStateAsync("ResumeParse", resumeId.ToString());

        try
        {
            if (string.IsNullOrWhiteSpace(resume.RawText))
            {
                resume.ParseStatus = "Failed";
                resume.UpdatedAtUtc = DateTime.UtcNow;
                await FailStateAsync(state, "No extractable resume text found.");
                await dbContext.SaveChangesAsync();
                return;
            }

            using var parseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var parsed = await aiBackendClient.ParseResumeTextAsync(resume.RawText, parseTimeout.Token);
            if (!parsed.Success || parsed.Data is null)
            {
                var error = parsed.Error?.Code == "RequestCanceled"
                    ? "Resume parse timed out after 90 seconds."
                    : parsed.Error?.Message ?? "Resume parse failed.";

                await FailStateAsync(state, error);
                resume.ParseStatus = "Failed";
                resume.UpdatedAtUtc = DateTime.UtcNow;
                await dbContext.SaveChangesAsync();
                return;
            }

            resume.ParseStatus = "Completed";
            resume.UpdatedAtUtc = DateTime.UtcNow;

            if (resume.ParsedResumeResult is null)
            {
                resume.ParsedResumeResult = new ParsedResumeResult { ResumeId = resume.Id };
            }

            var structuredJson = parsed.Data.StructuredJson;
            resume.ParsedResumeResult.StructuredJson = structuredJson;
            resume.ParsedResumeResult.FullName = parsed.Data.FullName ?? string.Empty;
            resume.ParsedResumeResult.Email = parsed.Data.Email ?? string.Empty;
            resume.ParsedResumeResult.Phone = parsed.Data.Phone ?? string.Empty;
            resume.ParsedResumeResult.SkillsJson = ServiceJson.Serialize(parsed.Data.Skills);
            resume.ParsedResumeResult.ParsedAtUtc = DateTime.UtcNow;

            ExtractExtendedDetails(structuredJson, resume.ParsedResumeResult);

            if (parsed.Data.Skills is { Count: > 0 })
            {
                var mergedSkills = ServiceJson.DeserializeStringList(resume.CandidateProfile.SkillsJson)
                    .Concat(parsed.Data.Skills)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                resume.CandidateProfile.SkillsJson = ServiceJson.Serialize(mergedSkills);
            }

            if (!string.IsNullOrWhiteSpace(resume.ParsedResumeResult.Summary) && string.IsNullOrWhiteSpace(resume.CandidateProfile.Summary))
            {
                resume.CandidateProfile.Summary = resume.ParsedResumeResult.Summary;
            }

            if (!string.IsNullOrWhiteSpace(resume.ParsedResumeResult.Headline) && string.IsNullOrWhiteSpace(resume.CandidateProfile.Headline))
            {
                resume.CandidateProfile.Headline = resume.ParsedResumeResult.Headline;
            }

            var vector = await aiBackendClient.UpsertCandidateVectorAsync(
                new CandidateVectorSyncRequest(
                    resume.CandidateProfileId,
                    new Dictionary<string, object?>
                    {
                        ["candidateId"] = resume.CandidateProfileId,
                        ["headline"] = resume.CandidateProfile.Headline,
                        ["summary"] = resume.CandidateProfile.Summary,
                        ["skills"] = parsed.Data.Skills,
                        ["resumeText"] = resume.RawText,
                    },
                    resume.ContentHash),
                CancellationToken.None);

            var entry = await dbContext.VectorIndexEntries
                .FirstOrDefaultAsync(x => x.EntityType == "candidate" && x.EntityId == resume.CandidateProfileId);

            if (entry is null)
            {
                entry = new VectorIndexEntry { EntityType = "candidate", EntityId = resume.CandidateProfileId };
                dbContext.VectorIndexEntries.Add(entry);
            }

            entry.ContentHash = resume.ContentHash;
            entry.EmbeddedAtUtc = DateTime.UtcNow;

            if (vector.Success && vector.Data is not null)
            {
                entry.Collection = vector.Data.Collection;
                entry.VectorId = vector.Data.VectorId;
                entry.Model = vector.Data.Model;
                entry.Status = VectorIndexStatus.Ready;
                entry.LastError = string.Empty;
            }
            else
            {
                entry.Status = VectorIndexStatus.Failed;
                entry.LastError = vector.Error?.Message ?? "Candidate vector upsert failed.";
            }

            await CompleteStateAsync(state);
            await dbContext.SaveChangesAsync();

            if (vector.Success && vector.Data is not null)
            {
                await recommendationRefreshJob.RefreshCandidateAsync(resume.CandidateProfileId);
            }
        }
        catch (Exception ex)
        {
            await FailStateAsync(state, ex.Message);
            resume.ParseStatus = "Failed";
            resume.UpdatedAtUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync();
        }
    }

    private static void ExtractExtendedDetails(string? structuredJson, ParsedResumeResult target)
    {
        if (string.IsNullOrWhiteSpace(structuredJson))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(structuredJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("summary", out var summaryEl) && summaryEl.ValueKind == JsonValueKind.String)
            {
                target.Summary = summaryEl.GetString()?.Trim() ?? string.Empty;
            }

            if (root.TryGetProperty("job_title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
            {
                target.Headline = titleEl.GetString()?.Trim() ?? string.Empty;
            }
        }
        catch
        {
            // Fallback gracefully on custom LLM json structures
        }
    }

    private async Task<BackgroundJobState> StartStateAsync(string jobType, string correlationId)
    {
        var state = new BackgroundJobState 
        { 
            JobType = jobType, 
            CorrelationId = correlationId, 
            Status = "Running", 
            LastRunAtUtc = DateTime.UtcNow 
        };
        dbContext.BackgroundJobStates.Add(state);
        await dbContext.SaveChangesAsync();
        return state;
    }

    private async Task CompleteStateAsync(BackgroundJobState state)
    {
        state.Status = "Completed";
        state.LastRunAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();
    }

    private async Task FailStateAsync(BackgroundJobState state, string error)
    {
        state.Status = "Failed";
        state.Error = error;
        state.Attempts += 1;
        state.LastRunAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();
    }

    private async Task RunVectorUpsertOnlyAsync(Resume resume)
    {
        var skills = new List<string>();
        if (resume.ParsedResumeResult != null && !string.IsNullOrWhiteSpace(resume.ParsedResumeResult.SkillsJson))
        {
            skills = ServiceJson.DeserializeStringList(resume.ParsedResumeResult.SkillsJson);
        }

        var vector = await aiBackendClient.UpsertCandidateVectorAsync(
            new CandidateVectorSyncRequest(
                resume.CandidateProfileId,
                new Dictionary<string, object?>
                {
                    ["candidateId"] = resume.CandidateProfileId,
                    ["headline"] = resume.CandidateProfile.Headline,
                    ["summary"] = resume.CandidateProfile.Summary,
                    ["skills"] = skills,
                    ["resumeText"] = resume.RawText,
                },
                resume.ContentHash),
            CancellationToken.None);

        var entry = await dbContext.VectorIndexEntries
            .FirstOrDefaultAsync(x => x.EntityType == "candidate" && x.EntityId == resume.CandidateProfileId);

        if (entry is null)
        {
            entry = new VectorIndexEntry { EntityType = "candidate", EntityId = resume.CandidateProfileId };
            dbContext.VectorIndexEntries.Add(entry);
        }

        entry.ContentHash = resume.ContentHash;
        entry.EmbeddedAtUtc = DateTime.UtcNow;

        if (vector.Success && vector.Data is not null)
        {
            entry.Collection = vector.Data.Collection;
            entry.VectorId = vector.Data.VectorId;
            entry.Model = vector.Data.Model;
            entry.Status = VectorIndexStatus.Ready;
            entry.LastError = string.Empty;
        }
        else
        {
            entry.Status = VectorIndexStatus.Failed;
            entry.LastError = vector.Error?.Message ?? "Candidate vector upsert failed.";
        }

        await dbContext.SaveChangesAsync();

        if (vector.Success && vector.Data is not null)
        {
            await recommendationRefreshJob.RefreshCandidateAsync(resume.CandidateProfileId);
        }
    }
}
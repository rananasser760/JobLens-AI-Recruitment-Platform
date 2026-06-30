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

        // If the API controller (or someone else) already completed parsing, skip the heavy AI call
        if (resume.ParseStatus == "Completed" && resume.ParsedResumeResult != null)
        {
            await RunVectorUpsertOnlyAsync(resume);
            return;
        }
        
        // If it's already parsing concurrently, throw to let Hangfire retry later!
        if (resume.ParseStatus == "Parsing")
        {
            throw new InvalidOperationException("Resume is currently being parsed by another process. Will retry later.");
        }

        // Mark as parsing and save, to lock out other processes
        resume.ParseStatus = "Parsing";
        await dbContext.SaveChangesAsync();

        var state = await StartStateAsync("ResumeParse", resumeId.ToString());

        try
        {
            if (string.IsNullOrWhiteSpace(resume.RawText))
            {
                resume.ParseStatus = "Failed";
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
                await dbContext.SaveChangesAsync();
                return;
            }

            resume.ParseStatus = "Completed";
            if (resume.ParsedResumeResult is null)
            {
                resume.ParsedResumeResult = new ParsedResumeResult { ResumeId = resume.Id };
            }

            resume.ParsedResumeResult.StructuredJson = parsed.Data.StructuredJson;
            resume.ParsedResumeResult.FullName = parsed.Data.FullName;
            resume.ParsedResumeResult.Email = parsed.Data.Email;
            resume.ParsedResumeResult.Phone = parsed.Data.Phone;
            resume.ParsedResumeResult.SkillsJson = ServiceJson.Serialize(parsed.Data.Skills);
            resume.ParsedResumeResult.ParsedAtUtc = DateTime.UtcNow;
            resume.CandidateProfile.SkillsJson = ServiceJson.Serialize(parsed.Data.Skills);

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

            if (vector.Success && vector.Data is not null)
            {
                var entry = await dbContext.VectorIndexEntries.FirstOrDefaultAsync(x => x.EntityType == "candidate" && x.EntityId == resume.CandidateProfileId);
                if (entry is null)
                {
                    entry = new VectorIndexEntry { EntityType = "candidate", EntityId = resume.CandidateProfileId };
                    dbContext.VectorIndexEntries.Add(entry);
                }

                entry.ContentHash = resume.ContentHash;
                entry.Collection = vector.Data.Collection;
                entry.VectorId = vector.Data.VectorId;
                entry.Model = vector.Data.Model;
                entry.Status = VectorIndexStatus.Ready;
                entry.EmbeddedAtUtc = DateTime.UtcNow;
                entry.LastError = string.Empty;
            }
            else
            {
                var entry = await dbContext.VectorIndexEntries.FirstOrDefaultAsync(x => x.EntityType == "candidate" && x.EntityId == resume.CandidateProfileId);
                if (entry is null)
                {
                    entry = new VectorIndexEntry { EntityType = "candidate", EntityId = resume.CandidateProfileId };
                    dbContext.VectorIndexEntries.Add(entry);
                }

                entry.Status = VectorIndexStatus.Failed;
                entry.LastError = vector.Error?.Message ?? "Candidate vector upsert failed.";
                entry.EmbeddedAtUtc = DateTime.UtcNow;
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
            await dbContext.SaveChangesAsync();
        }
    }

    private async Task<BackgroundJobState> StartStateAsync(string jobType, string correlationId)
    {
        var state = new BackgroundJobState { JobType = jobType, CorrelationId = correlationId, Status = "Running", LastRunAtUtc = DateTime.UtcNow };
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

    private async Task RunVectorUpsertOnlyAsync(JobLens.Domain.Entities.Resume resume)
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

        if (vector.Success && vector.Data is not null)
        {
            var entry = await dbContext.VectorIndexEntries.FirstOrDefaultAsync(x => x.EntityType == "candidate" && x.EntityId == resume.CandidateProfileId);
            if (entry is null)
            {
                entry = new VectorIndexEntry { EntityType = "candidate", EntityId = resume.CandidateProfileId };
                dbContext.VectorIndexEntries.Add(entry);
            }

            entry.ContentHash = resume.ContentHash;
            entry.Collection = vector.Data.Collection;
            entry.VectorId = vector.Data.VectorId;
            entry.Model = vector.Data.Model;
            entry.Status = VectorIndexStatus.Ready;
            entry.EmbeddedAtUtc = DateTime.UtcNow;
            entry.LastError = string.Empty;
        }
        else
        {
            var entry = await dbContext.VectorIndexEntries.FirstOrDefaultAsync(x => x.EntityType == "candidate" && x.EntityId == resume.CandidateProfileId);
            if (entry is null)
            {
                entry = new VectorIndexEntry { EntityType = "candidate", EntityId = resume.CandidateProfileId };
                dbContext.VectorIndexEntries.Add(entry);
            }

            entry.Status = VectorIndexStatus.Failed;
            entry.LastError = vector.Error?.Message ?? "Candidate vector upsert failed.";
            entry.EmbeddedAtUtc = DateTime.UtcNow;
        }
        await dbContext.SaveChangesAsync();

        if (vector.Success && vector.Data is not null)
        {
            await recommendationRefreshJob.RefreshCandidateAsync(resume.CandidateProfileId);
        }
    }
}

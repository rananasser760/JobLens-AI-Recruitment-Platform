using Hangfire;
using JobLens.Application.Interfaces;
using JobLens.Domain.Entities;
using JobLens.Domain.Enums;
using JobLens.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace JobLens.Infrastructure.BackgroundJobs;

public sealed class AtsScoringJob(
    Persistence.JobLensDbContext dbContext,
    IAiBackendClient aiBackendClient,
    IBackgroundJobClient backgroundJobs)
{
    public const double QualificationThreshold = 65;
    private static readonly TimeSpan ScoringTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(20);

    [AutomaticRetry(Attempts = 3)]
    public async Task ScoreApplicationAsync(long applicationId)
    {
        var application = await dbContext.Applications
            .Include(x => x.Resume)
            .Include(x => x.JobPosting)
            .Include(x => x.AtsAssessments)
            .FirstOrDefaultAsync(x => x.Id == applicationId);

        if (application is null || application.Status != ApplicationStatus.AtsPending)
        {
            return;
        }

        var assessment = application.AtsAssessments
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefault(x => x.Status is "Pending" or "Failed");

        if (assessment is null)
        {
            assessment = new AtsAssessment { ApplicationId = application.Id, Status = "Pending" };
            dbContext.AtsAssessments.Add(assessment);
        }

        var resumeText = application.Resume?.RawText;
        var jobDescription = application.JobPosting?.Description;
        if (string.IsNullOrWhiteSpace(resumeText) || string.IsNullOrWhiteSpace(jobDescription))
        {
            assessment.Status = "Failed";
            assessment.Summary = "Resume text or job description is missing; manual review required.";
            application.Status = ApplicationStatus.Submitted;
            await dbContext.SaveChangesAsync();
            return;
        }

        using var timeout = new CancellationTokenSource(ScoringTimeout);
        var ats = await aiBackendClient.ScoreAtsAsync(resumeText, jobDescription, timeout.Token);

        if (!ats.Success || ats.Data is null)
        {
            assessment.Status = "Failed";
            assessment.Summary = ats.Error?.Message ?? "ATS scoring failed.";
            await dbContext.SaveChangesAsync();
            throw new InvalidOperationException($"ATS scoring failed for application {applicationId}: {assessment.Summary}");
        }

        assessment.Status = "Completed";
        assessment.Score = ats.Data.Score;
        assessment.Summary = ats.Data.Summary ?? string.Empty;
        assessment.MissingSkillsJson = ServiceJson.Serialize(ats.Data.MissingSkills ?? []);
        assessment.SuggestionsJson = ServiceJson.Serialize(ats.Data.Suggestions ?? []);
        assessment.RawResponseJson = ServiceJson.Serialize(ats.Data);
        assessment.EvaluatedAtUtc = DateTime.UtcNow;

        application.Status = ats.Data.Score >= QualificationThreshold ? ApplicationStatus.AtsQualified : ApplicationStatus.AtsRejected;
        application.ReviewedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();

        var candidateProfileId = application.CandidateProfileId;
        backgroundJobs.Enqueue<RecommendationRefreshJob>(j => j.RefreshCandidateAsync(candidateProfileId));
    }

    public async Task RequeuePendingAsync()
    {
        var cutoff = DateTime.UtcNow - StaleAfter;
        var pendingIds = await dbContext.Applications
            .Where(x => x.Status == ApplicationStatus.AtsPending && x.UpdatedAtUtc < cutoff)
            .OrderBy(x => x.UpdatedAtUtc)
            .Select(x => x.Id)
            .Take(50)
            .ToListAsync();

        foreach (var id in pendingIds)
        {
            backgroundJobs.Enqueue<AtsScoringJob>(j => j.ScoreApplicationAsync(id));
        }
    }
}

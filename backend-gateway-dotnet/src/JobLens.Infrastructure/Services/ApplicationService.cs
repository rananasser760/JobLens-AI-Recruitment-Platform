using Hangfire;
using JobLens.Application.Common;
using JobLens.Application.DTOs.Applications;
using JobLens.Application.Interfaces;
using JobLens.Domain.Entities;
using JobLens.Domain.Enums;
using JobLens.Infrastructure.BackgroundJobs;
using Microsoft.EntityFrameworkCore;

namespace JobLens.Infrastructure.Services;

public sealed class ApplicationService(
    Persistence.JobLensDbContext dbContext,
    IAiBackendClient aiBackendClient,
    IBackgroundJobClient backgroundJobs) : IApplicationService
{
    public async Task<ApiResponse<ApplicationDto>> CreateAsync(long candidateUserId, CreateApplicationRequest request, CancellationToken cancellationToken)
    {
        var candidate = await dbContext.CandidateProfiles.FirstOrDefaultAsync(x => x.UserId == candidateUserId, cancellationToken);
        var job = await dbContext.Jobs.FirstOrDefaultAsync(x => x.Id == request.JobId, cancellationToken);

        if (candidate is null || job is null)
        {
            return new ApiResponse<ApplicationDto>(false, null, "Candidate or job not found.", ["not_found"]);
        }

        var resume = request.ResumeId.HasValue && request.ResumeId.Value > 0
            ? await dbContext.Resumes.FirstOrDefaultAsync(
                x => x.Id == request.ResumeId.Value && x.CandidateProfileId == candidate.Id,
                cancellationToken)
            : await dbContext.Resumes
                .Where(x => x.CandidateProfileId == candidate.Id)
                .OrderByDescending(x => x.IsDefault)
                .ThenByDescending(x => x.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

        if (resume is null)
        {
            return new ApiResponse<ApplicationDto>(false, null, "No resume found for this application.", ["missing_resume"]);
        }

        var existing = await dbContext.Applications.FirstOrDefaultAsync(x => x.CandidateProfileId == candidate.Id && x.JobPostingId == job.Id, cancellationToken);
        if (existing is not null)
        {
            return new ApiResponse<ApplicationDto>(false, null, "Application already exists.", ["duplicate_application"]);
        }

        var application = new JobApplication
        {
            CandidateProfileId = candidate.Id,
            JobPostingId = job.Id,
            ResumeId = resume.Id,
            Status = job.SourceType == JobSourceType.External ? ApplicationStatus.ExternalRedirected : ApplicationStatus.Submitted,
        };

        dbContext.Applications.Add(application);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (job.SourceType == JobSourceType.External)
        {
            return new ApiResponse<ApplicationDto>(true, ToDto(application, job, null), "External job tracked. Redirect to source to finish applying.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            var ats = await aiBackendClient.ScoreAtsAsync(resume.RawText, job.Description, timeoutCts.Token);
            if (ats.Success && ats.Data is not null)
            {
                var assessment = new AtsAssessment
                {
                    ApplicationId = application.Id,
                    Status = "Completed",
                    Score = ats.Data.Score,
                    Summary = ats.Data.Summary,
                    MissingSkillsJson = ServiceJson.Serialize(ats.Data.MissingSkills),
                    SuggestionsJson = ServiceJson.Serialize(ats.Data.Suggestions),
                    RawResponseJson = ServiceJson.Serialize(ats.Data),
                    EvaluatedAtUtc = DateTime.UtcNow,
                };

                application.Status = ats.Data.Score >= 65 ? ApplicationStatus.AtsQualified : ApplicationStatus.AtsRejected;
                dbContext.AtsAssessments.Add(assessment);
                await dbContext.SaveChangesAsync(cancellationToken);

                return new ApiResponse<ApplicationDto>(true, ToDto(application, job, ats.Data.Score), "Application submitted and scored.");
            }
        }
        catch
        {
            // Fall back to pending background processing below.
        }

        application.Status = ApplicationStatus.AtsPending;
        dbContext.AtsAssessments.Add(new AtsAssessment
        {
            ApplicationId = application.Id,
            Status = "Pending",
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        backgroundJobs.Enqueue<RecommendationRefreshJob>(jobRunner => jobRunner.RefreshCandidateAsync(candidate.Id));
        return new ApiResponse<ApplicationDto>(true, ToDto(application, job, null), "Application submitted. ATS evaluation queued.");
    }

    public async Task<ApiResponse<IReadOnlyList<ApplicationDto>>> GetCandidateApplicationsAsync(long candidateUserId, CancellationToken cancellationToken)
    {
        var apps = await dbContext.Applications
            .Include(x => x.JobPosting)
            .Include(x => x.AtsAssessments)
            .Where(x => x.CandidateProfile.UserId == candidateUserId)
            .OrderByDescending(x => x.SubmittedAtUtc)
            .ToListAsync(cancellationToken);

        return new ApiResponse<IReadOnlyList<ApplicationDto>>(true, apps.Select(x => ToDto(x, x.JobPosting, x.AtsAssessments.OrderByDescending(y => y.CreatedAtUtc).FirstOrDefault()?.Score)).ToList());
    }

    public async Task<ApiResponse<IReadOnlyList<ApplicationDto>>> GetApplicantsForJobAsync(long recruiterUserId, long jobId, CancellationToken cancellationToken)
    {
        var recruiter = await dbContext.RecruiterProfiles.FirstOrDefaultAsync(x => x.UserId == recruiterUserId, cancellationToken);
        if (recruiter is null)
        {
            return new ApiResponse<IReadOnlyList<ApplicationDto>>(false, null, "Recruiter profile not found.", ["not_found"]);
        }

        var apps = await dbContext.Applications
            .Include(x => x.JobPosting)
            .Include(x => x.AtsAssessments)
            .Where(x => x.JobPostingId == jobId)
            .OrderByDescending(x => x.SubmittedAtUtc)
            .ToListAsync(cancellationToken);

        return new ApiResponse<IReadOnlyList<ApplicationDto>>(true, apps.Select(x => ToDto(x, x.JobPosting, x.AtsAssessments.OrderByDescending(y => y.CreatedAtUtc).FirstOrDefault()?.Score)).ToList());
    }

    private static ApplicationDto ToDto(JobApplication application, JobPosting job, double? latestScore) =>
        new(application.Id, job.Id, job.Title, application.Status, latestScore, application.SubmittedAtUtc, job.RedirectUrl);
}

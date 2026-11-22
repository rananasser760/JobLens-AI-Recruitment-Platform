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
    IBackgroundJobClient backgroundJobs) : IApplicationService
{
    public async Task<ApiResponse<ApplicationDto>> CreateAsync(long candidateUserId, CreateApplicationRequest request, CancellationToken cancellationToken)
    {
        var candidate = await dbContext.CandidateProfiles
            .Include(x => x.User)
            .FirstOrDefaultAsync(x => x.UserId == candidateUserId, cancellationToken);

        var job = await dbContext.Jobs
            .Include(x => x.Company)
            .FirstOrDefaultAsync(x => x.Id == request.JobId, cancellationToken);

        if (candidate is null || job is null)
        {
            return new ApiResponse<ApplicationDto>(false, null, "Candidate or job not found.", ["not_found"]);
        }

        if (!job.IsActive)
        {
            return new ApiResponse<ApplicationDto>(false, null, "This job posting is no longer active.", ["job_inactive"]);
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

        var existing = await dbContext.Applications.FirstOrDefaultAsync(
            x => x.CandidateProfileId == candidate.Id && x.JobPostingId == job.Id,
            cancellationToken);

        if (existing is not null)
        {
            return new ApiResponse<ApplicationDto>(false, null, "You have already applied for this job.", ["duplicate_application"]);
        }

        var application = new JobApplication
        {
            CandidateProfileId = candidate.Id,
            JobPostingId = job.Id,
            ResumeId = resume.Id,
            Status = job.SourceType == JobSourceType.External 
                ? ApplicationStatus.ExternalRedirected 
                : ApplicationStatus.Submitted,
            SubmittedAtUtc = DateTime.UtcNow,
        };

        dbContext.Applications.Add(application);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (job.SourceType == JobSourceType.External)
        {
            return new ApiResponse<ApplicationDto>(true, ToDto(application, job, null), "External job tracked. Redirect to source to finish applying.");
        }

        if (string.IsNullOrWhiteSpace(resume.RawText) || string.IsNullOrWhiteSpace(job.Description))
        {
            return new ApiResponse<ApplicationDto>(true, ToDto(application, job, null), "Application submitted for manual review.");
        }

        application.Status = ApplicationStatus.AtsPending;
        dbContext.AtsAssessments.Add(new AtsAssessment
        {
            ApplicationId = application.Id,
            Status = "Pending",
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        backgroundJobs.Enqueue<AtsScoringJob>(j => j.ScoreApplicationAsync(application.Id));
        return new ApiResponse<ApplicationDto>(true, ToDto(application, job, null), "Application submitted. ATS evaluation is in progress.");
    }

    public async Task<ApiResponse<IReadOnlyList<ApplicationDto>>> GetCandidateApplicationsAsync(long candidateUserId, CancellationToken cancellationToken)
    {
        var apps = await dbContext.Applications
            .Include(x => x.JobPosting)
                .ThenInclude(x => x.Company)
            .Include(x => x.AtsAssessments)
            .Where(x => x.CandidateProfile.UserId == candidateUserId)
            .OrderByDescending(x => x.SubmittedAtUtc)
            .ToListAsync(cancellationToken);

        var list = apps.Select(x => ToDto(
            x, 
            x.JobPosting, 
            x.AtsAssessments.OrderByDescending(y => y.EvaluatedAtUtc ?? y.CreatedAtUtc).FirstOrDefault()?.Score)).ToList();

        return new ApiResponse<IReadOnlyList<ApplicationDto>>(true, list);
    }

    public async Task<ApiResponse<IReadOnlyList<ApplicationDto>>> GetApplicantsForJobAsync(long recruiterUserId, long jobId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(x => x.Id == recruiterUserId, cancellationToken);
        if (user is null)
        {
            return new ApiResponse<IReadOnlyList<ApplicationDto>>(false, null, "User not found.", ["not_found"]);
        }

        var job = await dbContext.Jobs.FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (job is null)
        {
            return new ApiResponse<IReadOnlyList<ApplicationDto>>(false, null, "Job not found.", ["not_found"]);
        }

        if (user.Role != AppRole.Admin)
        {
            var recruiter = await dbContext.RecruiterProfiles.FirstOrDefaultAsync(x => x.UserId == recruiterUserId, cancellationToken);
            if (recruiter is null || !recruiter.CompanyId.HasValue || recruiter.CompanyId.Value != job.CompanyId)
            {
                return new ApiResponse<IReadOnlyList<ApplicationDto>>(false, null, "You are not authorized to view applicants for this job.", ["unauthorized"]);
            }
        }

        var apps = await dbContext.Applications
            .Include(x => x.JobPosting)
            .Include(x => x.AtsAssessments)
            .Include(x => x.CandidateProfile)
                .ThenInclude(x => x.User)
            .Where(x => x.JobPostingId == jobId)
            .OrderByDescending(x => x.SubmittedAtUtc)
            .ToListAsync(cancellationToken);

        var list = apps.Select(x => ToDto(
            x, 
            x.JobPosting, 
            x.AtsAssessments.OrderByDescending(y => y.EvaluatedAtUtc ?? y.CreatedAtUtc).FirstOrDefault()?.Score)).ToList();

        return new ApiResponse<IReadOnlyList<ApplicationDto>>(true, list);
    }

    private static ApplicationDto ToDto(JobApplication application, JobPosting job, double? latestScore) =>
        new(
            application.Id,
            job.Id,
            job.Title,
            application.Status,
            latestScore,
            application.SubmittedAtUtc,
            job.RedirectUrl);
}
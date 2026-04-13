using Hangfire;
using JobLens.Application.Common;
using JobLens.Application.DTOs.Resumes;
using JobLens.Application.Interfaces;
using JobLens.Domain.Entities;
using JobLens.Infrastructure.BackgroundJobs;
using Microsoft.EntityFrameworkCore;

namespace JobLens.Infrastructure.Services;

public sealed class ResumeService(
    Persistence.JobLensDbContext dbContext,
    IFileStorageService fileStorageService,
    IContentHashService contentHashService,
    IBackgroundJobClient backgroundJobs,
    IAiBackendClient aiBackendClient) : IResumeService
{
    public async Task<ApiResponse<ResumeDto>> UploadAsync(long candidateUserId, ResumeUploadRequest request, CancellationToken cancellationToken)
    {
        var candidate = await dbContext.CandidateProfiles.FirstOrDefaultAsync(x => x.UserId == candidateUserId, cancellationToken);
        if (candidate is null)
        {
            return new ApiResponse<ResumeDto>(false, null, "Candidate profile not found.", ["not_found"]);
        }

        var storageKey = await fileStorageService.SaveAsync(request.FileName, request.Content, cancellationToken);
        var contentHash = contentHashService.Compute(request.Content);
        var rawText = await ExtractRawTextAsync(request, cancellationToken);

        if (request.IsDefault)
        {
            var defaults = dbContext.Resumes.Where(x => x.CandidateProfileId == candidate.Id && x.IsDefault);
            await defaults.ForEachAsync(x => x.IsDefault = false, cancellationToken);
        }

        var resume = new Resume
        {
            CandidateProfileId = candidate.Id,
            FileName = request.FileName,
            ContentType = request.ContentType,
            FileSizeBytes = request.Content.LongLength,
            StorageKey = storageKey,
            ContentHash = contentHash,
            RawText = rawText,
            IsDefault = request.IsDefault,
            ParseStatus = "Queued",
        };

        dbContext.Resumes.Add(resume);
        await dbContext.SaveChangesAsync(cancellationToken);

        backgroundJobs.Enqueue<ResumeWorkflowJob>(job => job.ProcessResumeAsync(resume.Id));

        return new ApiResponse<ResumeDto>(true, new ResumeDto(resume.Id, resume.FileName, resume.ContentType, resume.IsDefault, resume.ParseStatus, resume.CreatedAtUtc), "Resume uploaded.");
    }

    private async Task<string> ExtractRawTextAsync(ResumeUploadRequest request, CancellationToken cancellationToken)
    {
        var extraction = await aiBackendClient.ExtractResumeTextAsync(
            new ResumeTextExtractionRequest(
                request.FileName,
                request.ContentType,
                Convert.ToBase64String(request.Content)),
            cancellationToken);

        if (extraction.Success && extraction.Data is not null)
        {
            var text = extraction.Data.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    public async Task<ApiResponse<IReadOnlyList<ResumeDto>>> GetMyResumesAsync(long candidateUserId, CancellationToken cancellationToken)
    {
        var resumes = await dbContext.Resumes
            .Where(x => x.CandidateProfile.UserId == candidateUserId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new ResumeDto(x.Id, x.FileName, x.ContentType, x.IsDefault, x.ParseStatus, x.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        return new ApiResponse<IReadOnlyList<ResumeDto>>(true, resumes);
    }

    public async Task<ApiResponse<ParsedResumeResultDto>> GetParsedResumeAsync(long candidateUserId, long resumeId, CancellationToken cancellationToken)
    {
        var resume = await dbContext.Resumes
            .Include(x => x.ParsedResumeResult)
            .Include(x => x.CandidateProfile)
            .FirstOrDefaultAsync(x => x.Id == resumeId && x.CandidateProfile.UserId == candidateUserId, cancellationToken);

        if (resume?.ParsedResumeResult is null)
        {
            return new ApiResponse<ParsedResumeResultDto>(false, null, "Parsed resume result not available yet.", ["not_ready"]);
        }

        var parsed = resume.ParsedResumeResult;
        return new ApiResponse<ParsedResumeResultDto>(
            true,
            new ParsedResumeResultDto(parsed.FullName, parsed.Email, parsed.Phone, ServiceJson.DeserializeStringList(parsed.SkillsJson), parsed.StructuredJson));
    }
}

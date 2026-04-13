using System.Text.Json;
using JobLens.Api.Contracts;
using JobLens.Application.Common;
using JobLens.Application.DTOs.Resumes;
using JobLens.Application.Interfaces;
using JobLens.Domain.Entities;
using JobLens.Infrastructure.Persistence;
using JobLens.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JobLens.Api.Controllers;

[Authorize(Roles = "Candidate")]
[Route("api/resumes")]
public sealed class ResumesController(
    IResumeService resumeService,
    IAiBackendClient aiBackendClient,
    JobLensDbContext dbContext,
    IFileStorageService fileStorageService) : AppControllerBase
{
    [HttpPost]
    [RequestSizeLimit(15_000_000)]
    public async Task<IActionResult> Upload([FromForm] IFormFile file, [FromQuery] bool isDefault = false, [FromQuery] bool parseNow = true, CancellationToken cancellationToken = default)
    {
        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);

        var request = new ResumeUploadRequest(file.FileName, file.ContentType, memory.ToArray(), isDefault);
        var result = await resumeService.UploadAsync(GetRequiredUserId(), request, cancellationToken);
        if (!result.Success || result.Data is null)
        {
            return BadRequest(new ApiResponse<ResumeViewDto>(false, null, result.Message, result.Errors));
        }

        if (parseNow)
        {
            await ParseStoredResumeInternalAsync(result.Data.ResumeId, cancellationToken);
        }

        var resume = await LoadResumeAsync(result.Data.ResumeId, cancellationToken);
        var dto = resume is null ? null : await ToResumeViewAsync(resume, cancellationToken);
        return Ok(new ApiResponse<ResumeViewDto>(true, dto, result.Message));
    }

    [HttpGet("mine")]
    public Task<IActionResult> GetMine(CancellationToken cancellationToken) => GetMyResumes(cancellationToken);

    [HttpGet("my-resumes")]
    public async Task<IActionResult> GetMyResumes(CancellationToken cancellationToken)
    {
        var candidate = await dbContext.CandidateProfiles.FirstOrDefaultAsync(x => x.UserId == GetRequiredUserId(), cancellationToken);
        if (candidate is null)
        {
            return NotFound(new ApiResponse<IReadOnlyList<ResumeViewDto>>(false, null, "Candidate profile not found.", ["not_found"]));
        }

        var resumes = await dbContext.Resumes
            .Include(x => x.ParsedResumeResult)
            .Where(x => x.CandidateProfileId == candidate.Id)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var mapped = new List<ResumeViewDto>(resumes.Count);
        foreach (var resume in resumes)
        {
            mapped.Add(await ToResumeViewAsync(resume, cancellationToken));
        }

        return Ok(new ApiResponse<IReadOnlyList<ResumeViewDto>>(true, mapped));
    }

    [HttpGet("{resumeId:long}")]
    public async Task<IActionResult> GetById(long resumeId, CancellationToken cancellationToken)
    {
        var resume = await LoadResumeAsync(resumeId, cancellationToken);
        if (resume is null)
        {
            return NotFound(new ApiResponse<ResumeViewDto>(false, null, "Resume not found.", ["not_found"]));
        }

        var dto = await ToResumeViewAsync(resume, cancellationToken);
        return Ok(new ApiResponse<ResumeViewDto>(true, dto));
    }

    [HttpDelete("{resumeId:long}")]
    public async Task<IActionResult> Delete(long resumeId, CancellationToken cancellationToken)
    {
        var resume = await LoadResumeAsync(resumeId, cancellationToken);
        if (resume is null)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Resume not found.", ["not_found"]));
        }

        await fileStorageService.DeleteAsync(resume.StorageKey, cancellationToken);
        dbContext.Resumes.Remove(resume);

        var hasOtherResumes = await dbContext.Resumes
            .AnyAsync(x => x.CandidateProfileId == resume.CandidateProfileId && x.Id != resume.Id, cancellationToken);

        if (!hasOtherResumes)
        {
            var vectorEntry = await dbContext.VectorIndexEntries
                .FirstOrDefaultAsync(x => x.EntityType == "candidate" && x.EntityId == resume.CandidateProfileId, cancellationToken);
            if (vectorEntry is not null)
            {
                dbContext.VectorIndexEntries.Remove(vectorEntry);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        if (!hasOtherResumes)
        {
            _ = await aiBackendClient.DeleteCandidateVectorAsync(resume.CandidateProfileId, cancellationToken);
        }

        return Ok(new ApiResponse<bool>(true, true, "Resume deleted."));
    }

    [HttpPost("{resumeId:long}/set-default")]
    public async Task<IActionResult> SetDefault(long resumeId, CancellationToken cancellationToken)
    {
        var resume = await LoadResumeAsync(resumeId, cancellationToken);
        if (resume is null)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Resume not found.", ["not_found"]));
        }

        var others = await dbContext.Resumes
            .Where(x => x.CandidateProfileId == resume.CandidateProfileId && x.Id != resume.Id && x.IsDefault)
            .ToListAsync(cancellationToken);
        foreach (var other in others)
        {
            other.IsDefault = false;
        }

        resume.IsDefault = true;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new ApiResponse<bool>(true, true, "Default resume updated."));
    }

    [HttpPost("{resumeId:long}/parse")]
    public async Task<IActionResult> ParseStoredResume(long resumeId, CancellationToken cancellationToken)
    {
        var success = await ParseStoredResumeInternalAsync(resumeId, cancellationToken);
        if (!success)
        {
            return BadRequest(new ApiResponse<bool>(false, false, "Could not parse this resume."));
        }

        return Ok(new ApiResponse<bool>(true, true, "Resume parsed."));
    }

    [HttpGet("{resumeId:long}/ats-score")]
    public async Task<IActionResult> GetStoredAtsScore(long resumeId, [FromQuery] long? jobId, CancellationToken cancellationToken)
    {
        var resume = await LoadResumeAsync(resumeId, cancellationToken);
        if (resume is null)
        {
            return NotFound(new ApiResponse<AtsScoreDto>(false, null, "Resume not found.", ["not_found"]));
        }

        string jobDescription;
        if (jobId.HasValue && jobId.Value > 0)
        {
            var job = await dbContext.Jobs.FirstOrDefaultAsync(x => x.Id == jobId.Value, cancellationToken);
            jobDescription = job?.Description ?? resume.RawText;
        }
        else
        {
            jobDescription = await dbContext.Applications
                .Where(x => x.ResumeId == resume.Id)
                .Select(x => x.JobPosting.Description)
                .FirstOrDefaultAsync(cancellationToken)
                ?? resume.RawText;
        }

        var ai = await aiBackendClient.ScoreAtsAsync(resume.RawText, jobDescription, cancellationToken);
        if (!ai.Success || ai.Data is null)
        {
            return BadRequest(new ApiResponse<AtsScoreDto>(false, null, ai.Error?.Message ?? "ATS scoring failed."));
        }

        var dto = ToAtsScoreDto(resume.Id, ai.Data);
        return Ok(new ApiResponse<AtsScoreDto>(true, dto));
    }

    [HttpGet("{resumeId:long}/parsed")]
    public async Task<IActionResult> GetParsed(long resumeId, CancellationToken cancellationToken) =>
        Ok(await resumeService.GetParsedResumeAsync(GetRequiredUserId(), resumeId, cancellationToken));

    [HttpPost("parse-text")]
    public async Task<IActionResult> ParseText([FromBody] ResumeTextRequest request, CancellationToken cancellationToken)
    {
        var ai = await aiBackendClient.ParseResumeTextAsync(request.ResumeText, cancellationToken);
        if (!ai.Success || ai.Data is null)
        {
            return BadRequest(new ApiResponse<ParsedResumeResultDto>(false, null, ai.Error?.Message ?? "Could not parse resume text."));
        }

        return Ok(new ApiResponse<ParsedResumeResultDto>(true, ai.Data));
    }

    [HttpPost("ats-score-text")]
    public async Task<IActionResult> AtsScoreText([FromBody] ResumeTextAtsRequest request, CancellationToken cancellationToken)
    {
        var ai = await aiBackendClient.ScoreAtsAsync(request.ResumeText, request.JobDescription ?? request.ResumeText, cancellationToken);
        if (!ai.Success || ai.Data is null)
        {
            return BadRequest(new ApiResponse<AtsScoreDto>(false, null, ai.Error?.Message ?? "Could not score resume text."));
        }

        var dto = ToAtsScoreDto(0, ai.Data);
        return Ok(new ApiResponse<AtsScoreDto>(true, dto));
    }

    [HttpPost("improvements")]
    public async Task<IActionResult> Improvements([FromBody] ResumeTextRequest request, CancellationToken cancellationToken)
    {
        var ai = await aiBackendClient.ScoreAtsAsync(request.ResumeText, request.ResumeText, cancellationToken);
        if (!ai.Success || ai.Data is null)
        {
            return BadRequest(new ApiResponse<object>(false, null, ai.Error?.Message ?? "Could not analyze resume text."));
        }

        var payload = new
        {
            score = ai.Data.Score,
            summary = ai.Data.Summary,
            recommendations = ai.Data.Suggestions,
            missingSkills = ai.Data.MissingSkills,
        };

        return Ok(new ApiResponse<object>(true, payload));
    }

    [HttpPost("full-analysis")]
    public async Task<IActionResult> FullAnalysis([FromBody] ResumeFullAnalysisRequest request, CancellationToken cancellationToken)
    {
        var parse = await aiBackendClient.ParseResumeTextAsync(request.ResumeText, cancellationToken);
        var ats = await aiBackendClient.ScoreAtsAsync(request.ResumeText, request.ResumeText, cancellationToken);

        if (!parse.Success || parse.Data is null || !ats.Success || ats.Data is null)
        {
            return BadRequest(new ApiResponse<object>(false, null, parse.Error?.Message ?? ats.Error?.Message ?? "Analysis failed."));
        }

        var payload = new
        {
            parsedCv = parse.Data,
            atsResult = ToAtsScoreDto(0, ats.Data),
            improvements = request.IncludeImprovements ? ats.Data.Suggestions : [],
            jobMatches = Array.Empty<object>(),
        };

        return Ok(new ApiResponse<object>(true, payload));
    }

    [HttpGet("{resumeId:long}/download")]
    public async Task<IActionResult> Download(long resumeId, CancellationToken cancellationToken)
    {
        var resume = await LoadResumeAsync(resumeId, cancellationToken);
        if (resume is null)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Resume not found.", ["not_found"]));
        }

        var bytes = await fileStorageService.ReadAsync(resume.StorageKey, cancellationToken);
        if (bytes is null)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Resume file not found.", ["not_found"]));
        }

        return File(bytes, resume.ContentType, resume.FileName);
    }

    private async Task<Resume?> LoadResumeAsync(long resumeId, CancellationToken cancellationToken)
    {
        var userId = GetRequiredUserId();
        return await dbContext.Resumes
            .Include(x => x.ParsedResumeResult)
            .Include(x => x.CandidateProfile)
            .FirstOrDefaultAsync(x => x.Id == resumeId && x.CandidateProfile.UserId == userId, cancellationToken);
    }

    private async Task<bool> ParseStoredResumeInternalAsync(long resumeId, CancellationToken cancellationToken)
    {
        var resume = await LoadResumeAsync(resumeId, cancellationToken);
        if (resume is null)
        {
            return false;
        }

        var ai = await aiBackendClient.ParseResumeTextAsync(resume.RawText, cancellationToken);
        if (!ai.Success || ai.Data is null)
        {
            return false;
        }

        var parsed = resume.ParsedResumeResult;
        if (parsed is null)
        {
            parsed = new ParsedResumeResult { ResumeId = resume.Id };
            dbContext.ParsedResumeResults.Add(parsed);
            resume.ParsedResumeResult = parsed;
        }

        parsed.FullName = ai.Data.FullName;
        parsed.Email = ai.Data.Email;
        parsed.Phone = ai.Data.Phone;
        parsed.SkillsJson = ServiceJson.Serialize(ai.Data.Skills);
        parsed.StructuredJson = ai.Data.StructuredJson;
        parsed.ParsedAtUtc = DateTime.UtcNow;
        resume.ParseStatus = "Completed";

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<ResumeViewDto> ToResumeViewAsync(Resume resume, CancellationToken cancellationToken)
    {
        var latestAts = await dbContext.AtsAssessments
            .Where(x => x.Application.ResumeId == resume.Id)
            .OrderByDescending(x => x.EvaluatedAtUtc ?? x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        ResumeParsingResultDto? parsing = null;
        if (resume.ParsedResumeResult is not null)
        {
            var structured = resume.ParsedResumeResult.StructuredJson;
            var parsedJson = TryParseJson(structured);
            var extractedExperience = ExtractObjectArray(parsedJson, "experience");
            var extractedEducation = ExtractObjectArray(parsedJson, "education");

            parsing = new ResumeParsingResultDto(
                resume.ParsedResumeResult.Id,
                structured,
                null,
                null,
                string.IsNullOrWhiteSpace(resume.ParsedResumeResult.FullName) ? null : resume.ParsedResumeResult.FullName,
                string.IsNullOrWhiteSpace(resume.ParsedResumeResult.Email) ? null : resume.ParsedResumeResult.Email,
                string.IsNullOrWhiteSpace(resume.ParsedResumeResult.Phone) ? null : resume.ParsedResumeResult.Phone,
                ServiceJson.DeserializeStringList(resume.ParsedResumeResult.SkillsJson),
                extractedExperience,
                extractedEducation);
        }

        return new ResumeViewDto(
            resume.Id,
            resume.CandidateProfileId,
            resume.FileName,
            string.IsNullOrWhiteSpace(resume.ContentType) ? null : resume.ContentType,
            resume.FileSizeBytes,
            resume.RawText,
            resume.ParsedResumeResult is not null,
            latestAts?.Score,
            latestAts?.Score >= 65,
            latestAts?.Summary,
            resume.IsDefault,
            resume.CreatedAtUtc,
            parsing);
    }

    private static AtsScoreDto ToAtsScoreDto(long resumeId, JobLens.Application.DTOs.Resumes.AtsScoreResultDto dto)
    {
        var recommendations = dto.Suggestions?.ToArray() ?? [];
        var categoryScores = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["overall"] = dto.Score,
        };

        return new AtsScoreDto(
            resumeId,
            dto.Score,
            dto.Score >= 65,
            recommendations,
            categoryScores);
    }

    private static JsonElement? TryParseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<Dictionary<string, object?>> ExtractObjectArray(JsonElement? root, string propertyName)
    {
        if (!root.HasValue || root.Value.ValueKind != JsonValueKind.Object || !root.Value.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<Dictionary<string, object?>>();
        foreach (var item in node.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in item.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt64(out var iv) ? iv : prop.Value.TryGetDouble(out var dv) ? dv : prop.Value.ToString(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => prop.Value.ToString(),
                };
            }

            list.Add(dict);
        }

        return list;
    }
}

using JobLens.Application.Common;
using JobLens.Application.DTOs.Resumes;

namespace JobLens.Application.Interfaces;

public interface IResumeService
{
    Task<ApiResponse<ResumeDto>> UploadAsync(long candidateUserId, ResumeUploadRequest request, CancellationToken cancellationToken);
    Task<ApiResponse<IReadOnlyList<ResumeDto>>> GetMyResumesAsync(long candidateUserId, CancellationToken cancellationToken);
    Task<ApiResponse<ParsedResumeResultDto>> GetParsedResumeAsync(long candidateUserId, long resumeId, CancellationToken cancellationToken);
}

using GP_Backend.Models.DTOs.Common;
using GP_Backend.Models.DTOs.Resume;

namespace GP_Backend.Services.Interfaces;

public interface IResumeService
{
    Task<ApiResponse<ResumeDto>> GetResumeAsync(long resumeId);
    Task<ApiResponse<List<ResumeDto>>> GetCandidateResumesAsync(long candidateId);
    Task<ApiResponse<ResumeDto>> UploadResumeAsync(long candidateId, Stream fileStream, string fileName, UploadResumeDto dto);
    Task<ApiResponse> DeleteResumeAsync(long resumeId, long candidateId);
    Task<ApiResponse> SetDefaultResumeAsync(long resumeId, long candidateId);
    
    // CV Parsing (calls FastAPI)
    Task<ApiResponse<ResumeParsingResultDto>> ParseResumeAsync(long resumeId);
    
    // ATS Score (calls FastAPI)
    Task<ApiResponse<AtsScoreDto>> GetAtsScoreAsync(long resumeId, long? jobId = null);
    
    // Download
    Task<(byte[] content, string contentType, string fileName)?> DownloadResumeAsync(long resumeId);
}

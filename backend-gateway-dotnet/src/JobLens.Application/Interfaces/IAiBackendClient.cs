using JobLens.Application.Contracts;
using JobLens.Application.DTOs.Interviews;
using JobLens.Application.DTOs.Resumes;

namespace JobLens.Application.Interfaces;

public interface IAiBackendClient
{
    Task<InternalApiEnvelope<ResumeExtractedTextDto>> ExtractResumeTextAsync(ResumeTextExtractionRequest request, CancellationToken cancellationToken);
    Task<InternalApiEnvelope<ParsedResumeResultDto>> ParseResumeTextAsync(string resumeText, CancellationToken cancellationToken);
    Task<InternalApiEnvelope<AtsScoreResultDto>> ScoreAtsAsync(string resumeText, string jobDescription, CancellationToken cancellationToken);
    Task<InternalApiEnvelope<VectorUpsertResponseDto>> UpsertCandidateVectorAsync(CandidateVectorSyncRequest request, CancellationToken cancellationToken);
    Task<InternalApiEnvelope<VectorUpsertResponseDto>> UpsertJobVectorAsync(JobVectorSyncRequest request, CancellationToken cancellationToken);
    Task<InternalApiEnvelope<bool>> DeleteCandidateVectorAsync(long candidateId, CancellationToken cancellationToken);
    Task<InternalApiEnvelope<bool>> DeleteJobVectorAsync(long jobId, CancellationToken cancellationToken);
    Task<InternalApiEnvelope<IReadOnlyList<RecommendationResultDto>>> RecommendJobsAsync(JobRecommendationRequest request, CancellationToken cancellationToken);
    Task<InternalApiEnvelope<IReadOnlyList<RecommendationResultDto>>> RecommendCandidatesAsync(CandidateRecommendationRequest request, CancellationToken cancellationToken);
    Task<InternalApiEnvelope<ScrapeJobsResponseDto>> ScrapeJobsAsync(ScrapeJobsRequest request, CancellationToken cancellationToken);
    Task<InternalApiEnvelope<InterviewSessionInitResponseDto>> StartInterviewSessionAsync(StartInterviewAiRequest request, CancellationToken cancellationToken);
    Task<InternalApiEnvelope<AudioAnalysisResponseDto>> AnalyzeAudioAsync(AudioAnalysisRequest request, CancellationToken cancellationToken);
    Task<InternalApiEnvelope<VideoAnalysisResponseDto>> AnalyzeVideoAsync(VideoAnalysisRequest request, CancellationToken cancellationToken);
    Task<InternalApiEnvelope<InterviewFinalizationResponseDto>> FinalizeInterviewAsync(FinalizeInterviewRequest request, CancellationToken cancellationToken);
}

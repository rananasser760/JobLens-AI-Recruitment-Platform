using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using JobLens.Application.Contracts;
using JobLens.Application.DTOs.Interviews;
using JobLens.Application.DTOs.Resumes;
using JobLens.Application.Interfaces;

namespace JobLens.Infrastructure.AI;

public sealed class AiBackendClient(HttpClient httpClient) : IAiBackendClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public Task<InternalApiEnvelope<ResumeExtractedTextDto>> ExtractResumeTextAsync(ResumeTextExtractionRequest request, CancellationToken cancellationToken) =>
        PostAsync<ResumeTextExtractionRequest, ResumeExtractedTextDto>(
            "/internal/v1/resumes/extract-text",
            request,
            cancellationToken);

    public Task<InternalApiEnvelope<ParsedResumeResultDto>> ParseResumeTextAsync(string resumeText, CancellationToken cancellationToken) =>
        PostAsync<object, ParsedResumeResultDto>(
            "/internal/v1/resumes/parse-text",
            new { resumeText },
            cancellationToken);

    public Task<InternalApiEnvelope<AtsScoreResultDto>> ScoreAtsAsync(string resumeText, string jobDescription, CancellationToken cancellationToken) =>
        PostAsync<object, AtsScoreResultDto>(
            "/internal/v1/resumes/score-ats",
            new { resumeText, jobDescription },
            cancellationToken);

    public Task<InternalApiEnvelope<VectorUpsertResponseDto>> UpsertCandidateVectorAsync(CandidateVectorSyncRequest request, CancellationToken cancellationToken) =>
        PostAsync<CandidateVectorSyncRequest, VectorUpsertResponseDto>("/internal/v1/vectors/candidates/upsert", request, cancellationToken);

    public Task<InternalApiEnvelope<VectorUpsertResponseDto>> UpsertJobVectorAsync(JobVectorSyncRequest request, CancellationToken cancellationToken) =>
        PostAsync<JobVectorSyncRequest, VectorUpsertResponseDto>("/internal/v1/vectors/jobs/upsert", request, cancellationToken);

    public Task<InternalApiEnvelope<bool>> DeleteCandidateVectorAsync(long candidateId, CancellationToken cancellationToken) =>
        PostAsync<object, bool>(
            "/internal/v1/vectors/candidates/delete",
            new { candidateId },
            cancellationToken);

    public Task<InternalApiEnvelope<bool>> DeleteJobVectorAsync(long jobId, CancellationToken cancellationToken) =>
        PostAsync<object, bool>(
            "/internal/v1/vectors/jobs/delete",
            new { jobId },
            cancellationToken);

    public Task<InternalApiEnvelope<IReadOnlyList<RecommendationResultDto>>> RecommendJobsAsync(JobRecommendationRequest request, CancellationToken cancellationToken) =>
        PostAsync<JobRecommendationRequest, IReadOnlyList<RecommendationResultDto>>("/internal/v1/recommendations/jobs", request, cancellationToken);

    public Task<InternalApiEnvelope<IReadOnlyList<RecommendationResultDto>>> RecommendCandidatesAsync(CandidateRecommendationRequest request, CancellationToken cancellationToken) =>
        PostAsync<CandidateRecommendationRequest, IReadOnlyList<RecommendationResultDto>>("/internal/v1/recommendations/candidates", request, cancellationToken);

    public Task<InternalApiEnvelope<ScrapeJobsResponseDto>> ScrapeJobsAsync(ScrapeJobsRequest request, CancellationToken cancellationToken) =>
        PostAsync<ScrapeJobsRequest, ScrapeJobsResponseDto>("/internal/v1/scrape/jobs", request, cancellationToken);

    public Task<InternalApiEnvelope<InterviewSessionInitResponseDto>> StartInterviewSessionAsync(StartInterviewAiRequest request, CancellationToken cancellationToken) =>
        PostAsync<StartInterviewAiRequest, InterviewSessionInitResponseDto>("/internal/v1/interviews/sessions", request, cancellationToken);

    public Task<InternalApiEnvelope<AudioAnalysisResponseDto>> AnalyzeAudioAsync(AudioAnalysisRequest request, CancellationToken cancellationToken) =>
        PostAsync<AudioAnalysisRequest, AudioAnalysisResponseDto>("/internal/v1/interviews/analyze-audio", request, cancellationToken);

    public Task<InternalApiEnvelope<VideoAnalysisResponseDto>> AnalyzeVideoAsync(VideoAnalysisRequest request, CancellationToken cancellationToken) =>
        PostAsync<VideoAnalysisRequest, VideoAnalysisResponseDto>("/internal/v1/interviews/analyze-video", request, cancellationToken);

    public Task<InternalApiEnvelope<InterviewFinalizationResponseDto>> FinalizeInterviewAsync(FinalizeInterviewRequest request, CancellationToken cancellationToken) =>
        PostAsync<FinalizeInterviewRequest, InterviewFinalizationResponseDto>("/internal/v1/interviews/finalize", request, cancellationToken);

    private async Task<InternalApiEnvelope<TResponse>> PostAsync<TRequest, TResponse>(string path, TRequest payload, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(path, payload, JsonOptions, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new InternalApiEnvelope<TResponse>(
                    Guid.NewGuid().ToString("N"),
                    false,
                    default,
                    new InternalApiError(response.StatusCode.ToString(), "AI backend request failed", body));
            }

            var envelope = JsonSerializer.Deserialize<InternalApiEnvelope<TResponse>>(body, JsonOptions);
            return envelope ?? new InternalApiEnvelope<TResponse>(
                Guid.NewGuid().ToString("N"),
                false,
                default,
                new InternalApiError("DeserializationFailure", "Could not deserialize AI backend response", body));
        }
        catch (OperationCanceledException ex)
        {
            return new InternalApiEnvelope<TResponse>(
                Guid.NewGuid().ToString("N"),
                false,
                default,
                new InternalApiError(
                    cancellationToken.IsCancellationRequested ? "RequestCanceled" : "RequestTimeout",
                    cancellationToken.IsCancellationRequested
                        ? "AI backend request canceled by caller"
                        : "AI backend request timed out",
                    ex.Message));
        }
        catch (HttpRequestException ex)
        {
            return new InternalApiEnvelope<TResponse>(
                Guid.NewGuid().ToString("N"),
                false,
                default,
                new InternalApiError("HttpRequestFailure", "Could not reach AI backend", ex.Message));
        }
        catch (Exception ex)
        {
            return new InternalApiEnvelope<TResponse>(
                Guid.NewGuid().ToString("N"),
                false,
                default,
                new InternalApiError("UnexpectedFailure", "Unexpected AI backend error", ex.Message));
        }
    }
}

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Linq;
using GP_Backend.Models.DTOs.Common;
using GP_Backend.Models.DTOs.Job;
using GP_Backend.Models.DTOs.Resume;
using GP_Backend.Services.AI;

namespace GP_Backend.Services.AI;

public class AiService : IAiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AiService> _logger;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    public AiService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<AiService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _baseUrl = configuration["AIBackend:BaseUrl"] ?? "http://localhost:8000";
        _apiKey = configuration["AIBackend:ApiKey"] ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            _httpClient.DefaultRequestHeaders.Remove("X-API-Key");
            _httpClient.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
        }
    }

    // TODO: Implement actual FastAPI calls when AI backend is ready
    // All methods below are placeholders that will be connected to the FastAPI endpoints

    public async Task<ApiResponse<ParsedCvResponseDto>> ParseCvAsync(Stream fileStream, string fileName)
    {
        try
        {
            if (fileStream.CanSeek)
            {
                fileStream.Position = 0;
            }

            using var content = new MultipartFormDataContent();
            using var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(fileContent, "file", fileName);

            var response = await _httpClient.PostAsync(BuildUrl("/api/cv/parse"), content);
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to parse CV"
                    : envelope.Message;
                return ApiResponse<ParsedCvResponseDto>.FailureResponse(message);
            }

            if (envelope.Data.ValueKind != JsonValueKind.Object)
            {
                return ApiResponse<ParsedCvResponseDto>.FailureResponse("AI parse response was empty.");
            }

            var parsed = MapParsedCv(envelope.Data);

            return ApiResponse<ParsedCvResponseDto>.SuccessResponse(parsed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling CV parse API");
            return ApiResponse<ParsedCvResponseDto>.FailureResponse("Failed to parse CV");
        }
    }

    public async Task<ApiResponse<ParsedCvResponseDto>> ParseCvFromTextAsync(string resumeText)
    {
        try
        {
            var payload = new
            {
                resume_text = resumeText
            };

            using var content = CreateJsonContent(payload);
            var response = await _httpClient.PostAsync(BuildUrl("/api/cv/parse-text"), content);
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to parse CV text"
                    : envelope.Message;
                return ApiResponse<ParsedCvResponseDto>.FailureResponse(message);
            }

            if (envelope.Data.ValueKind != JsonValueKind.Object)
            {
                return ApiResponse<ParsedCvResponseDto>.FailureResponse("AI text parse response was empty.");
            }

            var parsed = MapParsedCv(envelope.Data);
            return ApiResponse<ParsedCvResponseDto>.SuccessResponse(parsed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling CV parse text API");
            return ApiResponse<ParsedCvResponseDto>.FailureResponse("Failed to parse CV text");
        }
    }

    public async Task<ApiResponse<AtsScoreResponseDto>> GetAtsScoreAsync(string resumeText, string? jobDescription = null)
    {
        try
        {
            var payload = new
            {
                resume_text = resumeText,
                job_description = jobDescription
            };

            using var content = CreateJsonContent(payload);
            var response = await _httpClient.PostAsync(BuildUrl("/api/cv/ats-score"), content);
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to get ATS score"
                    : envelope.Message;
                return ApiResponse<AtsScoreResponseDto>.FailureResponse(message);
            }

            if (envelope.Data.ValueKind != JsonValueKind.Object)
            {
                return ApiResponse<AtsScoreResponseDto>.FailureResponse("AI ATS response was empty.");
            }

            var mapped = MapAtsScore(envelope.Data);
            return ApiResponse<AtsScoreResponseDto>.SuccessResponse(mapped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling ATS score API");
            return ApiResponse<AtsScoreResponseDto>.FailureResponse("Failed to get ATS score");
        }
    }

    public async Task<ApiResponse<JsonElement>> GetCvImprovementsAsync(string resumeText)
    {
        try
        {
            var payload = new
            {
                resume_text = resumeText
            };

            using var content = CreateJsonContent(payload);
            var response = await _httpClient.PostAsync(BuildUrl("/api/cv/improvements"), content);
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to generate CV improvements"
                    : envelope.Message;
                return ApiResponse<JsonElement>.FailureResponse(message);
            }

            return ApiResponse<JsonElement>.SuccessResponse(envelope.Data, "CV improvements generated");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling CV improvements API");
            return ApiResponse<JsonElement>.FailureResponse("Failed to generate CV improvements");
        }
    }

    public async Task<ApiResponse<JsonElement>> GetFullCvAnalysisAsync(string resumeText, bool includeImprovements = true, int jobMatchLimit = 5)
    {
        try
        {
            var payload = new
            {
                resume_text = resumeText,
                include_improvements = includeImprovements,
                job_match_limit = jobMatchLimit
            };

            using var content = CreateJsonContent(payload);
            var response = await _httpClient.PostAsync(BuildUrl("/api/cv/full-analysis"), content);
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to run full CV analysis"
                    : envelope.Message;
                return ApiResponse<JsonElement>.FailureResponse(message);
            }

            return ApiResponse<JsonElement>.SuccessResponse(envelope.Data, "Full CV analysis completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling full CV analysis API");
            return ApiResponse<JsonElement>.FailureResponse("Failed to run full CV analysis");
        }
    }

    private string BuildUrl(string path)
    {
        return $"{_baseUrl.TrimEnd('/')}{path}";
    }

    private static StringContent CreateJsonContent(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static JsonElement EmptyJsonObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    private async Task<(bool Success, string Message, JsonElement Data)> ReadEnvelopeAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(raw))
        {
            var message = response.IsSuccessStatusCode
                ? string.Empty
                : $"{(int)response.StatusCode} {response.ReasonPhrase}";
            return (response.IsSuccessStatusCode, message, EmptyJsonObject());
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            var success = response.IsSuccessStatusCode;
            if (TryGetPropertyAny(root, out var successNode, "success")
                && (successNode.ValueKind == JsonValueKind.True || successNode.ValueKind == JsonValueKind.False))
            {
                success = successNode.GetBoolean();
            }

            var message = string.Empty;
            if (TryGetPropertyAny(root, out var messageNode, "message") && messageNode.ValueKind == JsonValueKind.String)
            {
                message = messageNode.GetString() ?? string.Empty;
            }
            else if (TryGetPropertyAny(root, out var detailNode, "detail"))
            {
                message = detailNode.ValueKind == JsonValueKind.String
                    ? detailNode.GetString() ?? string.Empty
                    : detailNode.ToString();
            }
            else if (TryGetPropertyAny(root, out var errorNode, "error"))
            {
                message = errorNode.ValueKind == JsonValueKind.String
                    ? errorNode.GetString() ?? string.Empty
                    : errorNode.ToString();
            }

            JsonElement data = root.Clone();
            if (TryGetPropertyAny(root, out var dataNode, "data"))
            {
                data = dataNode.Clone();
            }

            if (!response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(message))
            {
                message = $"{(int)response.StatusCode} {response.ReasonPhrase}";
            }

            return (success, message, data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse AI backend envelope.");
            var message = response.IsSuccessStatusCode
                ? string.Empty
                : $"{(int)response.StatusCode} {response.ReasonPhrase}";
            return (response.IsSuccessStatusCode, message, EmptyJsonObject());
        }
    }

    private static ParsedCvResponseDto MapParsedCv(JsonElement data)
    {
        var skills = new List<string>();
        if (TryGetPropertyAny(data, out var skillsNode, "skills"))
        {
            skills = ExtractStringList(skillsNode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var experience = new List<ParsedExperienceDto>();
        if (TryGetPropertyAny(data, out var experienceNode, "experience") && experienceNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in experienceNode.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var endDate = GetStringAny(item, "end_date", "to", "end", "duration");
                var description = GetStringAny(item, "description");
                if (string.IsNullOrWhiteSpace(description)
                    && TryGetPropertyAny(item, out var responsibilitiesNode, "responsibilities")
                    && responsibilitiesNode.ValueKind == JsonValueKind.Array)
                {
                    description = string.Join("; ", ExtractStringList(responsibilitiesNode));
                }

                experience.Add(new ParsedExperienceDto
                {
                    JobTitle = GetStringAny(item, "job_title", "title", "position"),
                    Company = GetStringAny(item, "company", "organization"),
                    StartDate = GetStringAny(item, "start_date", "from", "start"),
                    EndDate = endDate,
                    Description = description
                });
            }
        }

        var education = new List<ParsedEducationDto>();
        if (TryGetPropertyAny(data, out var educationNode, "education") && educationNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in educationNode.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                education.Add(new ParsedEducationDto
                {
                    Degree = GetStringAny(item, "degree"),
                    Institution = GetStringAny(item, "institution", "school", "university"),
                    GraduationYear = GetStringAny(item, "graduation_year", "year", "end_date"),
                    FieldOfStudy = GetStringAny(item, "field_of_study", "field", "major")
                });
            }
        }

        return new ParsedCvResponseDto
        {
            FullName = GetStringAny(data, "full_name", "name"),
            Email = GetStringAny(data, "email"),
            Phone = GetStringAny(data, "phone"),
            Location = GetStringAny(data, "location"),
            Summary = GetStringAny(data, "summary"),
            Skills = skills,
            Experience = experience,
            Education = education,
            Confidence = GetFloatAny(data, "confidence")
        };
    }

    private static AtsScoreResponseDto MapAtsScore(JsonElement data)
    {
        var categoryScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (TryGetPropertyAny(data, out var scoresNode, "category_scores", "scores")
            && scoresNode.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in scoresNode.EnumerateObject())
            {
                categoryScores[prop.Name] = ToInt(prop.Value);
            }
        }

        var recommendations = new List<string>();
        if (TryGetPropertyAny(data, out var recNode, "recommendations") && recNode.ValueKind == JsonValueKind.Array)
        {
            recommendations.AddRange(ExtractStringList(recNode));
        }
        else if (TryGetPropertyAny(data, out var improvNode, "improvement_suggestions") && improvNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in improvNode.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var text = item.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        recommendations.Add(text);
                    }
                    continue;
                }

                if (item.ValueKind == JsonValueKind.Object)
                {
                    var suggestion = GetStringAny(item, "suggestion", "action");
                    if (!string.IsNullOrWhiteSpace(suggestion))
                    {
                        recommendations.Add(suggestion);
                    }
                }
            }
        }

        var overallScore = ToInt(GetValueOrDefault(data, "overall_score", "score"));
        var isFriendly = overallScore >= 70;
        if (TryGetPropertyAny(data, out var isFriendlyNode, "is_friendly")
            && (isFriendlyNode.ValueKind == JsonValueKind.True || isFriendlyNode.ValueKind == JsonValueKind.False))
        {
            isFriendly = isFriendlyNode.GetBoolean();
        }

        return new AtsScoreResponseDto
        {
            OverallScore = overallScore,
            IsFriendly = isFriendly,
            Recommendations = recommendations,
            CategoryScores = categoryScores
        };
    }

    private static JsonElement GetValueOrDefault(JsonElement element, params string[] names)
    {
        return TryGetPropertyAny(element, out var node, names) ? node : default;
    }

    private static string? GetStringAny(JsonElement element, params string[] names)
    {
        if (!TryGetPropertyAny(element, out var node, names))
        {
            return null;
        }

        if (node.ValueKind == JsonValueKind.String)
        {
            return node.GetString();
        }

        if (node.ValueKind == JsonValueKind.Number
            || node.ValueKind == JsonValueKind.True
            || node.ValueKind == JsonValueKind.False)
        {
            return node.ToString();
        }

        return null;
    }

    private static float GetFloatAny(JsonElement element, params string[] names)
    {
        if (!TryGetPropertyAny(element, out var node, names))
        {
            return 0f;
        }

        if (node.ValueKind == JsonValueKind.Number && node.TryGetSingle(out var single))
        {
            return single;
        }

        if (node.ValueKind == JsonValueKind.String
            && float.TryParse(node.GetString(), out var parsed))
        {
            return parsed;
        }

        return 0f;
    }

    private static int ToInt(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Number)
        {
            if (node.TryGetInt32(out var intValue))
            {
                return intValue;
            }

            if (node.TryGetDouble(out var doubleValue))
            {
                return Convert.ToInt32(Math.Round(doubleValue));
            }
        }

        if (node.ValueKind == JsonValueKind.String && int.TryParse(node.GetString(), out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    private static bool TryGetPropertyAny(JsonElement element, out JsonElement value, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                foreach (var name in names)
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = prop.Value;
                        return true;
                    }
                }
            }
        }

        value = default;
        return false;
    }

    private static List<string> ExtractStringList(JsonElement node)
    {
        var values = new List<string>();

        if (node.ValueKind == JsonValueKind.String)
        {
            var value = node.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
            return values;
        }

        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                values.AddRange(ExtractStringList(item));
            }
            return values;
        }

        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in node.EnumerateObject())
            {
                values.AddRange(ExtractStringList(prop.Value));
            }
        }

        return values;
    }

    private static JsonElement ParseJsonObjectOrWrap(string? rawJson, string fallbackFieldName)
    {
        if (!string.IsNullOrWhiteSpace(rawJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return doc.RootElement.Clone();
                }
            }
            catch
            {
                // If the payload isn't valid JSON we wrap it below.
            }
        }

        var fallback = JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            [fallbackFieldName] = rawJson
        });

        using var fallbackDoc = JsonDocument.Parse(fallback);
        return fallbackDoc.RootElement.Clone();
    }

    private static bool TryParseObjectNode(JsonElement node, out JsonElement parsedObject)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            parsedObject = node.Clone();
            return true;
        }

        if (node.ValueKind == JsonValueKind.String)
        {
            var text = node.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        parsedObject = doc.RootElement.Clone();
                        return true;
                    }
                }
                catch
                {
                    // Not a JSON string object.
                }
            }
        }

        parsedObject = default;
        return false;
    }

    private static long ToLong(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Number)
        {
            if (node.TryGetInt64(out var int64Value))
            {
                return int64Value;
            }

            if (node.TryGetDouble(out var doubleValue))
            {
                return Convert.ToInt64(Math.Round(doubleValue));
            }
        }

        if (node.ValueKind == JsonValueKind.String)
        {
            var raw = node.GetString() ?? string.Empty;
            if (long.TryParse(raw, out var direct))
            {
                return direct;
            }

            var digits = new string(raw.Where(char.IsDigit).ToArray());
            if (!string.IsNullOrWhiteSpace(digits) && long.TryParse(digits, out var fromDigits))
            {
                return fromDigits;
            }
        }

        return 0;
    }

    private static DateTime ParseDateOrDefault(string? raw)
    {
        if (!string.IsNullOrWhiteSpace(raw) && DateTime.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return DateTime.UtcNow;
    }

    private static float? GetNullableFloatAny(JsonElement element, params string[] names)
    {
        if (!TryGetPropertyAny(element, out var node, names))
        {
            return null;
        }

        if (node.ValueKind == JsonValueKind.Number && node.TryGetSingle(out var single))
        {
            return single;
        }

        if (node.ValueKind == JsonValueKind.String
            && float.TryParse(node.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static Dictionary<string, int> ExtractIntDictionary(JsonElement node)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (node.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var prop in node.EnumerateObject())
        {
            result[prop.Name] = ToInt(prop.Value);
        }

        return result;
    }

    public async Task<ApiResponse> CreateCandidateEmbeddingAsync(long candidateId, string profileData)
    {
        try
        {
            var payload = new
            {
                candidate_id = candidateId,
                profile_data = ParseJsonObjectOrWrap(profileData, "profile_text")
            };

            using var content = CreateJsonContent(payload);
            var response = await _httpClient.PostAsync(BuildUrl("/api/embeddings/candidate"), content);
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to create candidate embedding"
                    : envelope.Message;
                return ApiResponse.FailureResponse(message);
            }

            var successMessage = string.IsNullOrWhiteSpace(envelope.Message)
                ? "Embedding created"
                : envelope.Message;
            return ApiResponse.SuccessResponse(successMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating candidate embedding");
            return ApiResponse.FailureResponse("Failed to create embedding");
        }
    }

    public async Task<ApiResponse> UpdateCandidateEmbeddingAsync(long candidateId, string profileData)
    {
        try
        {
            var payload = new
            {
                candidate_id = candidateId,
                profile_data = ParseJsonObjectOrWrap(profileData, "profile_text")
            };

            using var content = CreateJsonContent(payload);
            var response = await _httpClient.PutAsync(BuildUrl($"/api/embeddings/candidate/{candidateId}"), content);
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to update candidate embedding"
                    : envelope.Message;
                return ApiResponse.FailureResponse(message);
            }

            var successMessage = string.IsNullOrWhiteSpace(envelope.Message)
                ? "Embedding updated"
                : envelope.Message;
            return ApiResponse.SuccessResponse(successMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating candidate embedding");
            return ApiResponse.FailureResponse("Failed to update embedding");
        }
    }

    public async Task<ApiResponse> DeleteCandidateEmbeddingAsync(long candidateId)
    {
        try
        {
            var response = await _httpClient.DeleteAsync(BuildUrl($"/api/embeddings/candidate/{candidateId}"));
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to delete candidate embedding"
                    : envelope.Message;
                return ApiResponse.FailureResponse(message);
            }

            var successMessage = string.IsNullOrWhiteSpace(envelope.Message)
                ? "Embedding deleted"
                : envelope.Message;
            return ApiResponse.SuccessResponse(successMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting candidate embedding");
            return ApiResponse.FailureResponse("Failed to delete embedding");
        }
    }

    public async Task<ApiResponse> CreateJobEmbeddingAsync(long jobId, string jobData)
    {
        try
        {
            var payload = new
            {
                job_id = jobId,
                job_data = ParseJsonObjectOrWrap(jobData, "job_text")
            };

            using var content = CreateJsonContent(payload);
            var response = await _httpClient.PostAsync(BuildUrl("/api/embeddings/job"), content);
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to create job embedding"
                    : envelope.Message;
                return ApiResponse.FailureResponse(message);
            }

            var successMessage = string.IsNullOrWhiteSpace(envelope.Message)
                ? "Embedding created"
                : envelope.Message;
            return ApiResponse.SuccessResponse(successMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating job embedding");
            return ApiResponse.FailureResponse("Failed to create embedding");
        }
    }

    public async Task<ApiResponse> UpdateJobEmbeddingAsync(long jobId, string jobData)
    {
        try
        {
            var payload = new
            {
                job_id = jobId,
                job_data = ParseJsonObjectOrWrap(jobData, "job_text")
            };

            using var content = CreateJsonContent(payload);
            var response = await _httpClient.PutAsync(BuildUrl($"/api/embeddings/job/{jobId}"), content);
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to update job embedding"
                    : envelope.Message;
                return ApiResponse.FailureResponse(message);
            }

            var successMessage = string.IsNullOrWhiteSpace(envelope.Message)
                ? "Embedding updated"
                : envelope.Message;
            return ApiResponse.SuccessResponse(successMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating job embedding");
            return ApiResponse.FailureResponse("Failed to update embedding");
        }
    }

    public async Task<ApiResponse> DeleteJobEmbeddingAsync(long jobId)
    {
        try
        {
            var response = await _httpClient.DeleteAsync(BuildUrl($"/api/embeddings/job/{jobId}"));
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to delete job embedding"
                    : envelope.Message;
                return ApiResponse.FailureResponse(message);
            }

            var successMessage = string.IsNullOrWhiteSpace(envelope.Message)
                ? "Embedding deleted"
                : envelope.Message;
            return ApiResponse.SuccessResponse(successMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting job embedding");
            return ApiResponse.FailureResponse("Failed to delete embedding");
        }
    }

    public async Task<ApiResponse<List<JobRecommendationDto>>> GetJobRecommendationsForCandidateAsync(long candidateId, int limit = 10)
    {
        try
        {
            var response = await _httpClient.GetAsync(BuildUrl($"/api/recommendations/jobs/{candidateId}?limit={limit}"));
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to get recommendations"
                    : envelope.Message;
                return ApiResponse<List<JobRecommendationDto>>.FailureResponse(message);
            }

            var recommendations = new List<JobRecommendationDto>();
            if (envelope.Data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in envelope.Data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var preview = default(JsonElement);
                    var hasPreview = TryGetPropertyAny(item, out var previewNode, "job_preview")
                        && TryParseObjectNode(previewNode, out preview);

                    var idSource = GetValueOrDefault(item, "job_id", "id");
                    var mappedId = ToLong(idSource);
                    if (mappedId == 0 && hasPreview)
                    {
                        mappedId = ToLong(GetValueOrDefault(preview, "job_id", "id"));
                    }

                    var title = hasPreview
                        ? GetStringAny(preview, "title", "job_title")
                        : null;
                    title ??= GetStringAny(item, "title", "job_title");

                    var company = hasPreview
                        ? GetStringAny(preview, "company", "company_name")
                        : null;
                    company ??= GetStringAny(item, "company", "company_name");

                    var location = hasPreview
                        ? GetStringAny(preview, "location")
                        : null;
                    location ??= GetStringAny(item, "location");

                    var matchingSkills = new List<string>();
                    if (hasPreview && TryGetPropertyAny(preview, out var previewSkills, "skills", "required_skills", "matching_skills"))
                    {
                        matchingSkills.AddRange(ExtractStringList(previewSkills));
                    }
                    if (TryGetPropertyAny(item, out var itemSkills, "matching_skills"))
                    {
                        matchingSkills.AddRange(ExtractStringList(itemSkills));
                    }

                    var reason = GetStringAny(item, "match_reason", "reason", "recommendation");
                    var score = GetFloatAny(item, "match_score", "score", "semantic_similarity");

                    recommendations.Add(new JobRecommendationDto
                    {
                        JobId = mappedId,
                        Title = title ?? "Recommended job",
                        CompanyName = company,
                        Location = location,
                        MatchScore = score,
                        MatchingSkills = matchingSkills
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList(),
                        MatchReason = reason
                    });
                }
            }

            return ApiResponse<List<JobRecommendationDto>>.SuccessResponse(recommendations);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting job recommendations");
            return ApiResponse<List<JobRecommendationDto>>.FailureResponse("Failed to get recommendations");
        }
    }

    public async Task<ApiResponse<List<CandidateRankingResultDto>>> GetCandidateRankingsForJobAsync(long jobId, int limit = 50)
    {
        try
        {
            var response = await _httpClient.GetAsync(BuildUrl($"/api/recommendations/candidates/{jobId}?limit={limit}"));
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to get rankings"
                    : envelope.Message;
                return ApiResponse<List<CandidateRankingResultDto>>.FailureResponse(message);
            }

            var rankings = new List<CandidateRankingResultDto>();
            if (envelope.Data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in envelope.Data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var candidateIdValue = ToLong(GetValueOrDefault(item, "candidate_id", "candidateId", "id"));
                    var score = GetFloatAny(item, "score", "match_score");
                    var reason = GetStringAny(item, "reason", "recommendation");

                    if (string.IsNullOrWhiteSpace(reason)
                        && TryGetPropertyAny(item, out var previewNode, "candidate_preview"))
                    {
                        reason = previewNode.ValueKind == JsonValueKind.String
                            ? previewNode.GetString()
                            : previewNode.ToString();
                    }

                    rankings.Add(new CandidateRankingResultDto
                    {
                        CandidateId = candidateIdValue,
                        Score = score,
                        Reason = reason
                    });
                }
            }

            return ApiResponse<List<CandidateRankingResultDto>>.SuccessResponse(rankings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting candidate rankings");
            return ApiResponse<List<CandidateRankingResultDto>>.FailureResponse("Failed to get rankings");
        }
    }

    public async Task<ApiResponse<List<JobRecommendationDto>>> MatchJobsFromTextAsync(string resumeText, int limit = 5)
    {
        try
        {
            var payload = new
            {
                resume_text = resumeText
            };

            using var content = CreateJsonContent(payload);
            var response = await _httpClient.PostAsync(BuildUrl($"/api/recommendations/match-from-text?limit={limit}"), content);
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to match jobs from CV text"
                    : envelope.Message;
                return ApiResponse<List<JobRecommendationDto>>.FailureResponse(message);
            }

            var matches = new List<JobRecommendationDto>();
            if (envelope.Data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in envelope.Data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var score = GetFloatAny(item, "match_score", "semantic_similarity", "score");

                    matches.Add(new JobRecommendationDto
                    {
                        JobId = ToLong(GetValueOrDefault(item, "job_id", "id", "db_id")),
                        Title = GetStringAny(item, "title", "job_title") ?? "Matched job",
                        CompanyName = GetStringAny(item, "company", "company_name"),
                        Location = GetStringAny(item, "location"),
                        MatchScore = score,
                        MatchingSkills = TryGetPropertyAny(item, out var skillsNode, "skills", "matching_skills")
                            ? ExtractStringList(skillsNode)
                            : new List<string>(),
                        MatchReason = GetStringAny(item, "match_reason", "reason", "explanation")
                    });
                }
            }

            return ApiResponse<List<JobRecommendationDto>>.SuccessResponse(matches);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error matching jobs from CV text");
            return ApiResponse<List<JobRecommendationDto>>.FailureResponse("Failed to match jobs from CV text");
        }
    }

    public async Task<ApiResponse<List<ScrapedJobDto>>> GetScrapedJobsAsync(string? keyword = null, string? location = null, int limit = 50)
    {
        try
        {
            var query = new List<string>();
            query.Add($"limit={Math.Clamp(limit, 1, 200)}");
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                query.Add($"keyword={Uri.EscapeDataString(keyword)}");
            }
            if (!string.IsNullOrWhiteSpace(location))
            {
                query.Add($"location={Uri.EscapeDataString(location)}");
            }

            var path = "/api/scraping/jobs";
            if (query.Any())
            {
                path += "?" + string.Join("&", query);
            }

            var response = await _httpClient.GetAsync(BuildUrl(path));
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to get scraped jobs"
                    : envelope.Message;
                return ApiResponse<List<ScrapedJobDto>>.FailureResponse(message);
            }

            var jobs = new List<ScrapedJobDto>();
            if (envelope.Data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in envelope.Data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    jobs.Add(new ScrapedJobDto
                    {
                        ExternalJobId = GetStringAny(item, "external_job_id", "db_id", "id") ?? string.Empty,
                        Title = GetStringAny(item, "title", "job_title") ?? "Untitled Job",
                        Description = GetStringAny(item, "description") ?? string.Empty,
                        Requirements = GetStringAny(item, "requirements"),
                        Location = GetStringAny(item, "location"),
                        SalaryRange = GetStringAny(item, "salary_range"),
                        EmploymentType = GetStringAny(item, "employment_type"),
                        ExternalUrl = GetStringAny(item, "external_url", "apply_link", "job_page_link") ?? string.Empty,
                        ExternalSource = GetStringAny(item, "external_source", "source") ?? string.Empty,
                        CompanyName = GetStringAny(item, "company_name", "company"),
                        PostedAt = ParseDateOrDefault(GetStringAny(item, "posted_at", "posted_time")),
                        Skills = TryGetPropertyAny(item, out var skillsNode, "skills")
                            ? ExtractStringList(skillsNode)
                                .Where(x => !string.IsNullOrWhiteSpace(x))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList()
                            : null
                    });
                }
            }

            return ApiResponse<List<ScrapedJobDto>>.SuccessResponse(jobs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting scraped jobs");
            return ApiResponse<List<ScrapedJobDto>>.FailureResponse("Failed to get scraped jobs");
        }
    }

    public async Task<ApiResponse<JsonElement>> GetScrapingStatusAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync(BuildUrl("/api/scraping/status"));
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to get scraping status"
                    : envelope.Message;
                return ApiResponse<JsonElement>.FailureResponse(message);
            }

            return ApiResponse<JsonElement>.SuccessResponse(envelope.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting scraping status");
            return ApiResponse<JsonElement>.FailureResponse("Failed to get scraping status");
        }
    }

    public async Task<ApiResponse> TriggerScrapingAsync(int? maxCategories = null)
    {
        try
        {
            var path = maxCategories.HasValue
                ? $"/api/scraping/trigger?max_categories={maxCategories.Value}"
                : "/api/scraping/trigger";

            var response = await _httpClient.PostAsync(BuildUrl(path), content: null);
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to trigger scraping"
                    : envelope.Message;
                return ApiResponse.FailureResponse(message);
            }

            return ApiResponse.SuccessResponse(
                string.IsNullOrWhiteSpace(envelope.Message) ? "Scraping triggered." : envelope.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error triggering scraping");
            return ApiResponse.FailureResponse("Failed to trigger scraping");
        }
    }

    public async Task<ApiResponse<JsonElement>> GetRecruitmentStatusAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync(BuildUrl("/api/recruitment/status"));
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to get recruitment status"
                    : envelope.Message;
                return ApiResponse<JsonElement>.FailureResponse(message);
            }

            return ApiResponse<JsonElement>.SuccessResponse(envelope.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting recruitment status");
            return ApiResponse<JsonElement>.FailureResponse("Failed to get recruitment status");
        }
    }

    public async Task<ApiResponse<List<GeneratedQuestionDto>>> GenerateInterviewQuestionsAsync(long jobId, string agentType, int questionCount = 10)
    {
        try
        {
            var safeCount = Math.Clamp(questionCount, 3, 20);
            var normalizedType = string.IsNullOrWhiteSpace(agentType) ? "General" : agentType.Trim();

            // FastAPI exposes /interview/start but not a standalone question-generation endpoint.
            // Return deterministic baseline questions so callers still get a usable response.
            var generated = Enumerable.Range(1, safeCount)
                .Select(i => new GeneratedQuestionDto
                {
                    QuestionText = $"({normalizedType}) Question {i}: Describe your experience relevant to this role.",
                    Category = normalizedType,
                    Difficulty = i <= 2 ? "Easy" : i <= 5 ? "Medium" : "Hard",
                    MaxDurationSeconds = 120
                })
                .ToList();

            _logger.LogWarning(
                "Standalone AI question-generation endpoint is not exposed by FastAPI. Returning deterministic fallback questions for job {JobId}.",
                jobId);

            return ApiResponse<List<GeneratedQuestionDto>>.SuccessResponse(
                generated,
                "Fallback questions returned because FastAPI does not expose a standalone generation endpoint.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating interview questions");
            return ApiResponse<List<GeneratedQuestionDto>>.FailureResponse("Failed to generate questions");
        }
    }

    public async Task<ApiResponse<AnswerEvaluationDto>> EvaluateAnswerAsync(string question, string answer, string? expectedAnswer = null)
    {
        try
        {
            // FastAPI evaluates answers in-session via websocket and summary endpoints.
            // Provide a lightweight local fallback score to avoid fake AI success placeholders.
            var answerLength = string.IsNullOrWhiteSpace(answer) ? 0 : answer.Trim().Length;
            var score = answerLength switch
            {
                < 20 => 2.5f,
                < 60 => 5.5f,
                < 120 => 7.0f,
                _ => 8.0f
            };

            var feedback = answerLength < 20
                ? "Answer is too short. Add concrete examples and technical details."
                : "Fallback evaluation only. Use active interview session for full AI assessment.";

            var fallback = new AnswerEvaluationDto
            {
                Score = score,
                Feedback = feedback,
                StrongPoints = new List<string> { "Response submitted" },
                ImprovementAreas = new List<string> { "Request full interview-session AI summary for accurate evaluation." }
            };

            _logger.LogWarning("Standalone answer-evaluation endpoint is not exposed by FastAPI. Returning fallback evaluation.");
            return ApiResponse<AnswerEvaluationDto>.SuccessResponse(
                fallback,
                "Fallback evaluation returned because FastAPI does not expose a standalone answer-evaluation endpoint.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating answer");
            return ApiResponse<AnswerEvaluationDto>.FailureResponse("Failed to evaluate answer");
        }
    }

    public async Task<ApiResponse<string>> TranscribeAudioAsync(Stream audioStream)
    {
        try
        {
            _logger.LogWarning("Speech-to-text HTTP endpoint is not exposed by FastAPI. Transcription is handled in websocket interview flow.");
            return await Task.FromResult(ApiResponse<string>.FailureResponse(
                "Speech-to-text endpoint is not available over HTTP. Use the interview websocket flow for transcription."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error transcribing audio");
            return ApiResponse<string>.FailureResponse("Failed to transcribe audio");
        }
    }

    public async Task<ApiResponse<Stream>> TextToSpeechAsync(string text)
    {
        try
        {
            _logger.LogWarning("Text-to-speech HTTP endpoint is not exposed by FastAPI. Audio synthesis is handled in websocket interview flow.");
            return await Task.FromResult(ApiResponse<Stream>.FailureResponse(
                "Text-to-speech endpoint is not available over HTTP. Use the interview websocket flow for synthesized audio."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in text to speech");
            return ApiResponse<Stream>.FailureResponse("Failed to synthesize speech");
        }
    }

    public async Task<ApiResponse<string>> GenerateInterviewReportAsync(long sessionId, List<QuestionAnswerPairDto> qaList, float overallScore)
    {
        try
        {
            // FastAPI currently provides summary through /interview/{session_id}/summary.
            // Build a deterministic local report string when this standalone method is called.
            var answered = qaList?.Count ?? 0;
            var avgScore = answered > 0 ? qaList!.Average(x => x.Score) : overallScore;

            var report = new
            {
                session_id = sessionId,
                answered_questions = answered,
                average_score = Math.Round(avgScore, 2),
                overall_score = Math.Round(overallScore, 2),
                note = "Fallback report generated by .NET because FastAPI does not expose a standalone report endpoint."
            };

            _logger.LogWarning(
                "Standalone interview report endpoint is not exposed by FastAPI. Returning deterministic fallback report for session {SessionId}.",
                sessionId);

            return ApiResponse<string>.SuccessResponse(
                JsonSerializer.Serialize(report),
                "Fallback report returned because FastAPI does not expose a standalone report endpoint.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating interview report");
            return ApiResponse<string>.FailureResponse("Failed to generate report");
        }
    }

    public async Task<ApiResponse<IntegritySessionStartResponseDto>> StartIntegritySessionAsync(IntegritySessionStartRequestDto request)
    {
        try
        {
            var payload = new
            {
                candidate_name = request.CandidateName,
                candidate_id = request.CandidateId,
                interview_session_id = request.InterviewSessionId
            };

            using var content = CreateJsonContent(payload);
            var response = await _httpClient.PostAsync(BuildUrl("/api/sessions/start"), content);
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to start integrity session"
                    : envelope.Message;
                return ApiResponse<IntegritySessionStartResponseDto>.FailureResponse(message);
            }

            if (envelope.Data.ValueKind != JsonValueKind.Object)
            {
                return ApiResponse<IntegritySessionStartResponseDto>.FailureResponse("Integrity start response was empty.");
            }

            var mapped = new IntegritySessionStartResponseDto
            {
                SessionId = ToLong(GetValueOrDefault(envelope.Data, "session_id", "id")),
                StartedAt = GetStringAny(envelope.Data, "started_at"),
                CandidateName = GetStringAny(envelope.Data, "candidate_name"),
                CandidateId = GetStringAny(envelope.Data, "candidate_id")
            };

            if (mapped.SessionId <= 0)
            {
                return ApiResponse<IntegritySessionStartResponseDto>.FailureResponse("Integrity session id was missing from AI response.");
            }

            return ApiResponse<IntegritySessionStartResponseDto>.SuccessResponse(mapped,
                string.IsNullOrWhiteSpace(envelope.Message) ? "Integrity session started" : envelope.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting integrity session");
            return ApiResponse<IntegritySessionStartResponseDto>.FailureResponse("Failed to start integrity session");
        }
    }

    public async Task<ApiResponse<IntegritySessionEndResponseDto>> EndIntegritySessionAsync(long integritySessionId)
    {
        try
        {
            var response = await _httpClient.PostAsync(BuildUrl($"/api/sessions/{integritySessionId}/end"), content: null);
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to end integrity session"
                    : envelope.Message;
                return ApiResponse<IntegritySessionEndResponseDto>.FailureResponse(message);
            }

            if (envelope.Data.ValueKind != JsonValueKind.Object)
            {
                return ApiResponse<IntegritySessionEndResponseDto>.FailureResponse("Integrity end response was empty.");
            }

            var mapped = new IntegritySessionEndResponseDto
            {
                SessionId = ToLong(GetValueOrDefault(envelope.Data, "session_id", "id")),
                FinalScore = GetNullableFloatAny(envelope.Data, "final_score"),
                Recommendation = GetStringAny(envelope.Data, "recommendation"),
                Reason = GetStringAny(envelope.Data, "reason"),
                DurationSeconds = GetNullableFloatAny(envelope.Data, "duration_seconds")
            };

            if (mapped.SessionId <= 0)
            {
                mapped.SessionId = integritySessionId;
            }

            return ApiResponse<IntegritySessionEndResponseDto>.SuccessResponse(mapped,
                string.IsNullOrWhiteSpace(envelope.Message) ? "Integrity session ended" : envelope.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ending integrity session {IntegritySessionId}", integritySessionId);
            return ApiResponse<IntegritySessionEndResponseDto>.FailureResponse("Failed to end integrity session");
        }
    }

    public async Task<ApiResponse<InterviewSessionStartResponseDto>> StartInterviewSessionAsync(InterviewSessionStartRequestDto request)
    {
        try
        {
            var fields = new Dictionary<string, string>
            {
                ["cv_text"] = request.CvText,
                ["job_description"] = request.JobDescription,
                ["evaluation_criteria"] = request.EvaluationCriteria,
                ["max_questions"] = request.MaxQuestions.ToString()
            };

            if (!string.IsNullOrWhiteSpace(request.CandidateName))
            {
                fields["candidate_name"] = request.CandidateName;
            }

            if (!string.IsNullOrWhiteSpace(request.CandidateId))
            {
                fields["candidate_id"] = request.CandidateId;
            }

            if (request.IntegrityDbSessionId.HasValue)
            {
                fields["integrity_db_session_id"] = request.IntegrityDbSessionId.Value.ToString();
            }

            using var content = new FormUrlEncodedContent(fields);
            var response = await _httpClient.PostAsync(BuildUrl("/interview/start"), content);
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to start interview session"
                    : envelope.Message;
                return ApiResponse<InterviewSessionStartResponseDto>.FailureResponse(message);
            }

            if (envelope.Data.ValueKind != JsonValueKind.Object)
            {
                return ApiResponse<InterviewSessionStartResponseDto>.FailureResponse("Interview start response was empty.");
            }

            var interviewSessionId = GetStringAny(envelope.Data, "interview_session_id", "session_id") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(interviewSessionId))
            {
                return ApiResponse<InterviewSessionStartResponseDto>.FailureResponse("Interview session id was missing from AI response.");
            }

            var mapped = new InterviewSessionStartResponseDto
            {
                InterviewSessionId = interviewSessionId,
                MaxQuestions = ToInt(GetValueOrDefault(envelope.Data, "max_questions")),
                Message = GetStringAny(envelope.Data, "message")
            };

            if (mapped.MaxQuestions <= 0)
            {
                mapped.MaxQuestions = request.MaxQuestions;
            }

            return ApiResponse<InterviewSessionStartResponseDto>.SuccessResponse(mapped,
                string.IsNullOrWhiteSpace(envelope.Message) ? "Interview session started" : envelope.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting interview session");
            return ApiResponse<InterviewSessionStartResponseDto>.FailureResponse("Failed to start interview session");
        }
    }

    public async Task<ApiResponse<InterviewSessionSummaryDto>> GetInterviewSessionSummaryAsync(string interviewSessionId)
    {
        try
        {
            var response = await _httpClient.GetAsync(BuildUrl($"/interview/{interviewSessionId}/summary"));
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to fetch interview summary"
                    : envelope.Message;
                return ApiResponse<InterviewSessionSummaryDto>.FailureResponse(message);
            }

            if (envelope.Data.ValueKind != JsonValueKind.Object)
            {
                return ApiResponse<InterviewSessionSummaryDto>.FailureResponse("Interview summary response was empty.");
            }

            var summaryJson = default(string);
            if (TryGetPropertyAny(envelope.Data, out var summaryNode, "summary")
                && summaryNode.ValueKind != JsonValueKind.Null
                && summaryNode.ValueKind != JsonValueKind.Undefined)
            {
                summaryJson = summaryNode.ToString();
            }

            var mapped = new InterviewSessionSummaryDto
            {
                Status = GetStringAny(envelope.Data, "status"),
                SummaryJson = summaryJson
            };

            return ApiResponse<InterviewSessionSummaryDto>.SuccessResponse(mapped,
                string.IsNullOrWhiteSpace(envelope.Message) ? "Interview summary fetched" : envelope.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching interview summary for session {InterviewSessionId}", interviewSessionId);
            return ApiResponse<InterviewSessionSummaryDto>.FailureResponse("Failed to fetch interview summary");
        }
    }

    public async Task<ApiResponse<JsonElement>> GetInterviewSessionHistoryAsync(string interviewSessionId)
    {
        try
        {
            var response = await _httpClient.GetAsync(BuildUrl($"/interview/{interviewSessionId}/history"));
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to fetch interview history"
                    : envelope.Message;
                return ApiResponse<JsonElement>.FailureResponse(message);
            }

            return ApiResponse<JsonElement>.SuccessResponse(envelope.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching interview history for session {InterviewSessionId}", interviewSessionId);
            return ApiResponse<JsonElement>.FailureResponse("Failed to fetch interview history");
        }
    }

    public async Task<ApiResponse<UnifiedSessionReportDto>> GetUnifiedSessionReportAsync(long integritySessionId)
    {
        try
        {
            var response = await _httpClient.GetAsync(BuildUrl($"/api/report/{integritySessionId}"));
            var envelope = await ReadEnvelopeAsync(response);
            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                var message = string.IsNullOrWhiteSpace(envelope.Message)
                    ? "Failed to fetch unified session report"
                    : envelope.Message;
                return ApiResponse<UnifiedSessionReportDto>.FailureResponse(message);
            }

            if (envelope.Data.ValueKind != JsonValueKind.Object)
            {
                return ApiResponse<UnifiedSessionReportDto>.FailureResponse("Unified report response was empty.");
            }

            string? interviewSummaryJson = null;
            if (TryGetPropertyAny(envelope.Data, out var summaryNode, "interview_summary")
                && summaryNode.ValueKind != JsonValueKind.Null
                && summaryNode.ValueKind != JsonValueKind.Undefined)
            {
                interviewSummaryJson = summaryNode.ToString();
            }
            else if (TryGetPropertyAny(envelope.Data, out var interviewNode, "interview")
                && interviewNode.ValueKind == JsonValueKind.Object
                && TryGetPropertyAny(interviewNode, out var nestedSummaryNode, "summary")
                && nestedSummaryNode.ValueKind != JsonValueKind.Null
                && nestedSummaryNode.ValueKind != JsonValueKind.Undefined)
            {
                interviewSummaryJson = nestedSummaryNode.ToString();
            }

            string? combinedVerdict = null;
            string? combinedReason = null;
            if (TryGetPropertyAny(envelope.Data, out var combinedNode, "combined_recommendation")
                && combinedNode.ValueKind == JsonValueKind.Object)
            {
                combinedVerdict = GetStringAny(combinedNode, "verdict");
                combinedReason = GetStringAny(combinedNode, "reason");
            }

            float? interviewScore = GetNullableFloatAny(envelope.Data, "interview_score");
            if (!interviewScore.HasValue
                && TryGetPropertyAny(envelope.Data, out var nestedInterviewNode, "interview")
                && nestedInterviewNode.ValueKind == JsonValueKind.Object)
            {
                interviewScore = GetNullableFloatAny(nestedInterviewNode, "score");
            }

            var mapped = new UnifiedSessionReportDto
            {
                SessionId = ToLong(GetValueOrDefault(envelope.Data, "session_id", "id")),
                StartedAt = GetStringAny(envelope.Data, "started_at"),
                EndedAt = GetStringAny(envelope.Data, "ended_at"),
                DurationSeconds = GetNullableFloatAny(envelope.Data, "duration_seconds"),
                FinalScore = GetNullableFloatAny(envelope.Data, "final_score"),
                Recommendation = GetStringAny(envelope.Data, "recommendation"),
                TotalAlerts = ToInt(GetValueOrDefault(envelope.Data, "total_alerts")),
                TotalYoloAlerts = ToInt(GetValueOrDefault(envelope.Data, "total_yolo_alerts")),
                AlertBreakdown = TryGetPropertyAny(envelope.Data, out var alertNode, "alert_breakdown")
                    ? ExtractIntDictionary(alertNode)
                    : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                YoloAlertBreakdown = TryGetPropertyAny(envelope.Data, out var yoloNode, "yolo_alert_breakdown")
                    ? ExtractIntDictionary(yoloNode)
                    : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                InterviewSessionId = GetStringAny(envelope.Data, "interview_session_id"),
                InterviewScore = interviewScore,
                InterviewSummaryJson = interviewSummaryJson,
                CombinedVerdict = combinedVerdict,
                CombinedReason = combinedReason,
                RawJson = envelope.Data.ToString()
            };

            if (mapped.SessionId <= 0)
            {
                mapped.SessionId = integritySessionId;
            }

            return ApiResponse<UnifiedSessionReportDto>.SuccessResponse(mapped,
                string.IsNullOrWhiteSpace(envelope.Message) ? "Unified report fetched" : envelope.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching unified report for integrity session {IntegritySessionId}", integritySessionId);
            return ApiResponse<UnifiedSessionReportDto>.FailureResponse("Failed to fetch unified session report");
        }
    }
}

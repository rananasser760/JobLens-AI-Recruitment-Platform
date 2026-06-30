namespace JobLens.Infrastructure.Configuration;

public sealed class AiBackendOptions
{
    public const string SectionName = "AIBackend";

    public string BaseUrl { get; set; } = "http://localhost:8000";
    public string ApiKey { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 300;
}

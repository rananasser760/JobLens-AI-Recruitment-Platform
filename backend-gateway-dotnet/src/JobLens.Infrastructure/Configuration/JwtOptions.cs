namespace JobLens.Infrastructure.Configuration;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "JobLens";
    public string Audience { get; set; } = "JobLens.Frontend";
    public string SigningKey { get; set; } = "joblens-dev-signing-key-change-me-immediately";
    public int ExpiryMinutes { get; set; } = 120;
}

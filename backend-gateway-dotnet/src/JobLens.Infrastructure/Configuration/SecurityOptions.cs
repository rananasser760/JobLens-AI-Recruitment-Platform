namespace JobLens.Infrastructure.Configuration;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    public string FrontendBaseUrl { get; set; } = "http://localhost:4200";
    public int PasswordResetTokenExpiryMinutes { get; set; } = 30;
}

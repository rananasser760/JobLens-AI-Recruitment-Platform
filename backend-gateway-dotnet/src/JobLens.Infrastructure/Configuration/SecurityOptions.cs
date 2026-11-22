namespace JobLens.Infrastructure.Configuration;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    public string FrontendBaseUrl { get; set; } = "http://localhost:4200";
    public int PasswordResetTokenExpiryMinutes { get; set; } = 30;

    public bool SeedAdminEnabled { get; set; } = false;
    public string SeedAdminEmail { get; set; } = "admin@joblens.local";
    public string SeedAdminPassword { get; set; } = string.Empty;
    public string SeedAdminDisplayName { get; set; } = "System Admin";
}
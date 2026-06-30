namespace JobLens.Infrastructure.Configuration;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public bool Enabled { get; set; } = false;
    public string FromAddress { get; set; } = "no-reply@joblens.local";
    public string FromName { get; set; } = "JobLens";
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

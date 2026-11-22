namespace JobLens.Api.Compatibility;

internal static class UploadRules
{
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];
    public static readonly string[] ResumeExtensions = [".pdf", ".docx", ".png", ".jpg", ".jpeg"];
    public static readonly string[] ChatExtensions = [".pdf", ".doc", ".docx", ".txt", ".png", ".jpg", ".jpeg", ".gif", ".webp"];

    public static bool HasAllowedExtension(string? fileName, IReadOnlyCollection<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return allowed.Contains(extension);
    }

    public static string ContentTypeFor(string? fileName) => Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".txt" => "text/plain",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };
}

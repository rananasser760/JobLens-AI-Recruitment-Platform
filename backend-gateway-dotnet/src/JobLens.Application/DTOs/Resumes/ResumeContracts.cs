namespace JobLens.Application.DTOs.Resumes;

public sealed record ResumeDto(
    long ResumeId,
    string FileName,
    string ContentType,
    bool IsDefault,
    string ParseStatus,
    DateTime CreatedAtUtc);

public sealed record ParsedResumeResultDto(
    string FullName,
    string Email,
    string Phone,
    IReadOnlyList<string> Skills,
    string StructuredJson);

public sealed record ResumeUploadRequest(string FileName, string ContentType, byte[] Content, bool IsDefault = false);
public sealed record ResumeTextExtractionRequest(string FileName, string ContentType, string Base64Content);
public sealed record ResumeExtractedTextDto(string Text);
public sealed record ResumeTextParseRequest(string ResumeText);
public sealed record AtsScoreResultDto(double Score, string Summary, IReadOnlyList<string> MissingSkills, IReadOnlyList<string> Suggestions);

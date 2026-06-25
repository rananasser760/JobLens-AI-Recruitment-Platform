namespace JobLens.Application.DTOs.Chat;

public sealed record ChatAttachmentDto
{
    public long Id { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string FileUrl { get; init; } = string.Empty;
    public string FileType { get; init; } = string.Empty;
    public long FileSize { get; init; }
}

namespace JobLens.Application.DTOs.Chat;

public sealed record SendMessageDto
{
    public long ReceiverId { get; init; }
    public string Content { get; init; } = string.Empty;
    public IReadOnlyList<long>? AttachmentIds { get; init; }
}

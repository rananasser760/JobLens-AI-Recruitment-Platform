namespace JobLens.Application.DTOs.Chat;

public sealed record ChatMessageDto
{
    public long Id { get; init; }
    public long ConversationId { get; init; }
    public long SenderId { get; init; }
    public string SenderName { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public bool IsRead { get; init; }
    public DateTime SentAtUtc { get; init; }
    public IReadOnlyList<ChatAttachmentDto> Attachments { get; init; } = Array.Empty<ChatAttachmentDto>();
}

namespace JobLens.Application.DTOs.Chat;

public sealed record ChatConversationDto
{
    public long Id { get; init; }
    public long OtherParticipantId { get; init; }
    public string OtherParticipantName { get; init; } = string.Empty;
    public string LastMessagePreview { get; init; } = string.Empty;
    public DateTime LastMessageAtUtc { get; init; }
    public int UnreadCount { get; init; }
}

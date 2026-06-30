using JobLens.Domain.Common;

namespace JobLens.Domain.Entities;

public sealed class ChatConversation : BaseEntity
{
    public long Participant1Id { get; set; }
    public User Participant1 { get; set; } = null!;

    public long Participant2Id { get; set; }
    public User Participant2 { get; set; } = null!;

    public ICollection<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
}

using JobLens.Domain.Common;

namespace JobLens.Domain.Entities;

public sealed class ChatMessage : BaseEntity
{
    public long ConversationId { get; set; }
    public ChatConversation Conversation { get; set; } = null!;

    public long SenderId { get; set; }
    public User Sender { get; set; } = null!;

    public string Content { get; set; } = string.Empty;
    public bool IsRead { get; set; }

    public ICollection<ChatAttachment> Attachments { get; set; } = new List<ChatAttachment>();
}

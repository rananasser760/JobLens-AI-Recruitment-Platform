using JobLens.Application.DTOs.Chat;

namespace JobLens.Application.Interfaces;

public interface IChatService
{
    Task<IReadOnlyList<ChatConversationDto>> GetConversationsForUserAsync(long userId, CancellationToken cancellationToken = default);
    
    Task<ChatConversationDto> GetOrCreateConversationAsync(long userId, long otherUserId, CancellationToken cancellationToken = default);
    
    Task<IReadOnlyList<ChatMessageDto>> GetMessagesForConversationAsync(long userId, long conversationId, int skip = 0, int take = 50, CancellationToken cancellationToken = default);
    
    Task<ChatMessageDto> SendMessageAsync(long senderId, SendMessageDto dto, CancellationToken cancellationToken = default);
    
    Task MarkConversationAsReadAsync(long userId, long conversationId, CancellationToken cancellationToken = default);
    
    Task<int> GetUnreadCountAsync(long userId, CancellationToken cancellationToken = default);
}

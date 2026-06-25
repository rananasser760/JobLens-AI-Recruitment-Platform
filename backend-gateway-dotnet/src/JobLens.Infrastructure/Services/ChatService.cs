using JobLens.Application.DTOs.Chat;
using JobLens.Application.Interfaces;
using JobLens.Domain.Entities;
using JobLens.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JobLens.Infrastructure.Services;

public sealed class ChatService(JobLensDbContext dbContext, ILogger<ChatService> logger) : IChatService
{
    public async Task<IReadOnlyList<ChatConversationDto>> GetConversationsForUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        var conversations = await dbContext.ChatConversations
            .AsNoTracking()
            .Include(c => c.Participant1)
            .Include(c => c.Participant2)
            .Where(c => c.Participant1Id == userId || c.Participant2Id == userId)
            .Select(c => new
            {
                ConversationId = c.Id,
                OtherParticipantId = c.Participant1Id == userId ? c.Participant2Id : c.Participant1Id,
                OtherParticipantName = c.Participant1Id == userId ? c.Participant2.DisplayName : c.Participant1.DisplayName,
                LastMessage = c.Messages.OrderByDescending(m => m.CreatedAtUtc).FirstOrDefault(),
                UnreadCount = c.Messages.Count(m => m.SenderId != userId && !m.IsRead)
            })
            .ToListAsync(cancellationToken);

        return conversations
            .Select(c => new ChatConversationDto
            {
                Id = c.ConversationId,
                OtherParticipantId = c.OtherParticipantId,
                OtherParticipantName = c.OtherParticipantName,
                LastMessagePreview = c.LastMessage?.Content ?? string.Empty,
                LastMessageAtUtc = c.LastMessage?.CreatedAtUtc ?? DateTime.MinValue,
                UnreadCount = c.UnreadCount
            })
            .OrderByDescending(c => c.LastMessageAtUtc)
            .ToList();
    }

    public async Task<ChatConversationDto> GetOrCreateConversationAsync(long userId, long otherUserId, CancellationToken cancellationToken = default)
    {
        var conversation = await dbContext.ChatConversations
            .Include(c => c.Participant1)
            .Include(c => c.Participant2)
            .FirstOrDefaultAsync(c => 
                (c.Participant1Id == userId && c.Participant2Id == otherUserId) ||
                (c.Participant1Id == otherUserId && c.Participant2Id == userId), cancellationToken);

        if (conversation == null)
        {
            conversation = new ChatConversation
            {
                Participant1Id = userId,
                Participant2Id = otherUserId
            };
            dbContext.ChatConversations.Add(conversation);
            await dbContext.SaveChangesAsync(cancellationToken);
            
            // Reload with participants for DTO
            conversation = await dbContext.ChatConversations
                .Include(c => c.Participant1)
                .Include(c => c.Participant2)
                .FirstAsync(c => c.Id == conversation.Id, cancellationToken);
        }

        return new ChatConversationDto
        {
            Id = conversation.Id,
            OtherParticipantId = otherUserId,
            OtherParticipantName = conversation.Participant1Id == userId ? conversation.Participant2.DisplayName : conversation.Participant1.DisplayName,
            LastMessagePreview = string.Empty,
            LastMessageAtUtc = DateTime.UtcNow,
            UnreadCount = 0
        };
    }

    public async Task<IReadOnlyList<ChatMessageDto>> GetMessagesForConversationAsync(long userId, long conversationId, int skip = 0, int take = 50, CancellationToken cancellationToken = default)
    {
        var hasAccess = await dbContext.ChatConversations
            .AnyAsync(c => c.Id == conversationId && (c.Participant1Id == userId || c.Participant2Id == userId), cancellationToken);

        if (!hasAccess)
        {
            throw new UnauthorizedAccessException("You do not have access to this conversation.");
        }

        var messages = await dbContext.ChatMessages
            .AsNoTracking()
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.CreatedAtUtc)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return messages.Select(m => new ChatMessageDto
        {
            Id = m.Id,
            ConversationId = m.ConversationId,
            SenderId = m.SenderId,
            SenderName = m.Sender.DisplayName,
            Content = m.Content,
            IsRead = m.IsRead,
            SentAtUtc = m.CreatedAtUtc,
            Attachments = m.Attachments.Select(a => new ChatAttachmentDto
            {
                Id = a.Id,
                FileName = a.FileName,
                FileUrl = a.FileUrl,
                FileType = a.FileType,
                FileSize = a.FileSize
            }).ToList()
        }).OrderBy(m => m.SentAtUtc).ToList(); // Order by asc for chat UI
    }

    public async Task<ChatMessageDto> SendMessageAsync(long senderId, SendMessageDto dto, CancellationToken cancellationToken = default)
    {
        var conversation = await dbContext.ChatConversations
            .FirstOrDefaultAsync(c => 
                (c.Participant1Id == senderId && c.Participant2Id == dto.ReceiverId) ||
                (c.Participant1Id == dto.ReceiverId && c.Participant2Id == senderId), cancellationToken);

        if (conversation is null)
        {
            conversation = new ChatConversation
            {
                Participant1Id = senderId,
                Participant2Id = dto.ReceiverId
            };
            dbContext.ChatConversations.Add(conversation);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var message = new ChatMessage
        {
            ConversationId = conversation.Id,
            SenderId = senderId,
            Content = dto.Content,
            IsRead = false
        };

        // Note: Attachment handling would involve fetching actual uploaded file records if they were uploaded beforehand
        // For now we assume no attachments are added inline or handle it in a separate flow.
        
        dbContext.ChatMessages.Add(message);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Fetch sender to return name
        var sender = await dbContext.Users.FindAsync(new object[] { senderId }, cancellationToken);

        return new ChatMessageDto
        {
            Id = message.Id,
            ConversationId = message.ConversationId,
            SenderId = message.SenderId,
            SenderName = sender?.DisplayName ?? "Unknown",
            Content = message.Content,
            IsRead = message.IsRead,
            SentAtUtc = message.CreatedAtUtc,
            Attachments = new List<ChatAttachmentDto>()
        };
    }

    public async Task MarkConversationAsReadAsync(long userId, long conversationId, CancellationToken cancellationToken = default)
    {
        var unreadMessages = await dbContext.ChatMessages
            .Where(m => m.ConversationId == conversationId && m.SenderId != userId && !m.IsRead)
            .ToListAsync(cancellationToken);

        if (unreadMessages.Any())
        {
            foreach (var message in unreadMessages)
            {
                message.IsRead = true;
            }
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<int> GetUnreadCountAsync(long userId, CancellationToken cancellationToken = default)
    {
        return await dbContext.ChatMessages
            .Include(m => m.Conversation)
            .CountAsync(m => m.SenderId != userId && (m.Conversation.Participant1Id == userId || m.Conversation.Participant2Id == userId) && !m.IsRead, cancellationToken);
    }
}

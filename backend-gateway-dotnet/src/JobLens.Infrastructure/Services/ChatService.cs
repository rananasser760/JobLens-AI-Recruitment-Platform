using JobLens.Application.DTOs.Chat;
using JobLens.Application.Interfaces;
using JobLens.Domain.Entities;
using JobLens.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JobLens.Infrastructure.Services;

public sealed class ChatService(
    JobLensDbContext dbContext,
    IFileStorageService fileStorageService,
    ILogger<ChatService> logger) : IChatService
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

        if (conversation is null)
        {
            conversation = new ChatConversation
            {
                Participant1Id = userId,
                Participant2Id = otherUserId,
                LastMessageAtUtc = DateTime.UtcNow
            };
            dbContext.ChatConversations.Add(conversation);
            await dbContext.SaveChangesAsync(cancellationToken);
            
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
            LastMessageAtUtc = conversation.LastMessageAtUtc ?? DateTime.UtcNow,
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
        }).OrderBy(m => m.SentAtUtc).ToList();
    }

    public async Task<ChatMessageDto> SendMessageAsync(long senderId, SendMessageDto dto, CancellationToken cancellationToken = default)
    {
        var receiverExists = await dbContext.Users.AnyAsync(u => u.Id == dto.ReceiverId && u.IsActive, cancellationToken);
        if (!receiverExists)
        {
            throw new InvalidOperationException("Receiver does not exist.");
        }

        var conversation = await dbContext.ChatConversations
            .FirstOrDefaultAsync(c => 
                (c.Participant1Id == senderId && c.Participant2Id == dto.ReceiverId) ||
                (c.Participant1Id == dto.ReceiverId && c.Participant2Id == senderId), cancellationToken);

        if (conversation is null)
        {
            conversation = new ChatConversation
            {
                Participant1Id = senderId,
                Participant2Id = dto.ReceiverId,
                LastMessageAtUtc = DateTime.UtcNow
            };
            dbContext.ChatConversations.Add(conversation);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var message = new ChatMessage
        {
            ConversationId = conversation.Id,
            SenderId = senderId,
            Content = dto.Content ?? string.Empty,
            IsRead = false
        };

        dbContext.ChatMessages.Add(message);
        await dbContext.SaveChangesAsync(cancellationToken);

        var attachmentsList = new List<ChatAttachmentDto>();
        if (dto.AttachmentIds != null && dto.AttachmentIds.Count > 0)
        {
            var pendingAttachments = await dbContext.ChatAttachments
                .Where(a => dto.AttachmentIds.Contains(a.Id) && a.MessageId == null && a.UploadedByUserId == senderId)
                .ToListAsync(cancellationToken);

            foreach (var att in pendingAttachments)
            {
                att.MessageId = message.Id;
                attachmentsList.Add(new ChatAttachmentDto
                {
                    Id = att.Id,
                    FileName = att.FileName,
                    FileUrl = att.FileUrl,
                    FileType = att.FileType,
                    FileSize = att.FileSize
                });
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        conversation.LastMessageAtUtc = message.CreatedAtUtc;
        await dbContext.SaveChangesAsync(cancellationToken);

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
            Attachments = attachmentsList
        };
    }

    public async Task MarkConversationAsReadAsync(long userId, long conversationId, CancellationToken cancellationToken = default)
    {
        var unreadMessages = await dbContext.ChatMessages
            .Where(m => m.ConversationId == conversationId && m.SenderId != userId && !m.IsRead)
            .ToListAsync(cancellationToken);

        if (unreadMessages.Count > 0)
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

    public async Task<ChatAttachmentDto> SaveAttachmentAsync(long userId, string fileName, string contentType, byte[] content, CancellationToken cancellationToken = default)
    {
        var storageKey = await fileStorageService.SaveAsync(fileName, content, cancellationToken);
        var attachment = new ChatAttachment
        {
            FileName = Path.GetFileName(fileName),
            StorageKey = storageKey,
            UploadedByUserId = userId,
            FileType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            FileSize = content.LongLength,
        };

        dbContext.ChatAttachments.Add(attachment);
        await dbContext.SaveChangesAsync(cancellationToken);

        attachment.FileUrl = $"/api/chat/attachments/{attachment.Id}";
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ChatAttachmentDto
        {
            Id = attachment.Id,
            FileName = attachment.FileName,
            FileUrl = attachment.FileUrl,
            FileType = attachment.FileType,
            FileSize = attachment.FileSize
        };
    }
}
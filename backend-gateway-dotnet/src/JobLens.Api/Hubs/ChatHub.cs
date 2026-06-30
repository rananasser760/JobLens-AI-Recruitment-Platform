using JobLens.Application.DTOs.Chat;
using JobLens.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace JobLens.Api.Hubs;

[Authorize]
public sealed class ChatHub(IChatService chatService, ILogger<ChatHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userIdStr = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? Context.User?.FindFirstValue("sub");
        if (long.TryParse(userIdStr, out var userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{userId}");
        }
        await base.OnConnectedAsync();
    }

    public async Task SendMessage(SendMessageDto dto)
    {
        var userIdStr = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? Context.User?.FindFirstValue("sub");
        if (!long.TryParse(userIdStr, out var senderId))
        {
            throw new HubException("Unauthorized.");
        }

        try
        {
            var messageDto = await chatService.SendMessageAsync(senderId, dto, Context.ConnectionAborted);
            
            // Send to receiver
            await Clients.Group($"user_{dto.ReceiverId}").SendAsync("ReceiveMessage", messageDto);
            
            // Also send back to sender's other connections to sync across tabs
            await Clients.Group($"user_{senderId}").SendAsync("ReceiveMessage", messageDto);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send message from {SenderId} to {ReceiverId}", senderId, dto.ReceiverId);
            throw new HubException("Failed to send message.");
        }
    }

    public async Task MarkAsRead(long conversationId)
    {
        var userIdStr = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? Context.User?.FindFirstValue("sub");
        if (!long.TryParse(userIdStr, out var userId))
        {
            return;
        }

        try
        {
            await chatService.MarkConversationAsReadAsync(userId, conversationId, Context.ConnectionAborted);
            
            // Notify the other participant that messages were read (optional enhancement)
            // await Clients.Group($"user_{otherParticipantId}").SendAsync("MessagesRead", conversationId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark conversation {ConversationId} as read for user {UserId}", conversationId, userId);
        }
    }
}

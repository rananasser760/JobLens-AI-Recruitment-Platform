using System.Security.Claims;
using JobLens.Application.DTOs.Chat;
using JobLens.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace JobLens.Api.Hubs;

[Authorize]
public sealed class ChatHub(IChatService chatService, ILogger<ChatHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userIdStr = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) 
            ?? Context.User?.FindFirstValue("sub")
            ?? Context.User?.FindFirstValue("id");

        if (long.TryParse(userIdStr, out var userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{userId}");
        }

        await base.OnConnectedAsync();
    }

    public async Task SendMessage(SendMessageDto dto)
    {
        var userIdStr = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) 
            ?? Context.User?.FindFirstValue("sub")
            ?? Context.User?.FindFirstValue("id");

        if (!long.TryParse(userIdStr, out var senderId))
        {
            throw new HubException("Unauthorized.");
        }

        if (senderId == dto.ReceiverId)
        {
            throw new HubException("Cannot send a message to yourself.");
        }

        try
        {
            var messageDto = await chatService.SendMessageAsync(senderId, dto, Context.ConnectionAborted);

            // Send to receiver group
            await Clients.Group($"user_{dto.ReceiverId}").SendAsync("ReceiveMessage", messageDto);

            // Send back to sender's other connections to sync across tabs/devices
            await Clients.Group($"user_{senderId}").SendAsync("ReceiveMessage", messageDto);
        }
        catch (HubException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send message from {SenderId} to {ReceiverId}", senderId, dto.ReceiverId);
            throw new HubException("Failed to send message.");
        }
    }

    public async Task MarkAsRead(long conversationId)
    {
        var userIdStr = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) 
            ?? Context.User?.FindFirstValue("sub")
            ?? Context.User?.FindFirstValue("id");

        if (!long.TryParse(userIdStr, out var userId))
        {
            return;
        }

        try
        {
            await chatService.MarkConversationAsReadAsync(userId, conversationId, Context.ConnectionAborted);

            var conversations = await chatService.GetConversationsForUserAsync(userId, Context.ConnectionAborted);
            var conversation = conversations.FirstOrDefault(c => c.Id == conversationId);
            if (conversation != null)
            {
                await Clients.Group($"user_{conversation.OtherParticipantId}").SendAsync("MessagesRead", conversationId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark conversation {ConversationId} as read for user {UserId}", conversationId, userId);
        }
    }

    public async Task SendTypingIndicator(long receiverId, bool isTyping)
    {
        var userIdStr = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) 
            ?? Context.User?.FindFirstValue("sub")
            ?? Context.User?.FindFirstValue("id");

        if (!long.TryParse(userIdStr, out var senderId))
        {
            return;
        }

        var eventName = isTyping ? "UserTyping" : "UserStoppedTyping";
        await Clients.Group($"user_{receiverId}").SendAsync(eventName, senderId);
    }
}
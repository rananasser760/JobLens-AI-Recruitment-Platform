using JobLens.Application.DTOs.Chat;
using JobLens.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using JobLens.Api.Hubs;
using System.Security.Claims;

namespace JobLens.Api.Controllers;

[ApiController]
[Route("api/chat")]
[Authorize]
public sealed class ChatController(IChatService chatService, IHubContext<ChatHub> hubContext) : ControllerBase
{
    [HttpGet("conversations")]
    public async Task<ActionResult<IReadOnlyList<ChatConversationDto>>> GetConversations(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var conversations = await chatService.GetConversationsForUserAsync(userId, cancellationToken);
        return Ok(conversations);
    }

    [HttpPost("conversations/with/{otherUserId}")]
    public async Task<ActionResult<ChatConversationDto>> GetOrCreateConversation([FromRoute] long otherUserId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var conversation = await chatService.GetOrCreateConversationAsync(userId, otherUserId, cancellationToken);
        return Ok(conversation);
    }

    [HttpGet("conversations/{conversationId}/messages")]
    public async Task<ActionResult<IReadOnlyList<ChatMessageDto>>> GetMessages(
        [FromRoute] long conversationId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        var messages = await chatService.GetMessagesForConversationAsync(userId, conversationId, skip, take, cancellationToken);
        return Ok(messages);
    }

    [HttpGet("unread-count")]
    public async Task<ActionResult<int>> GetUnreadCount(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var count = await chatService.GetUnreadCountAsync(userId, cancellationToken);
        return Ok(new { count });
    }

    [HttpPost("conversations/{conversationId}/read")]
    public async Task<IActionResult> MarkAsRead([FromRoute] long conversationId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        await chatService.MarkConversationAsReadAsync(userId, conversationId, cancellationToken);
        
        var convs = await chatService.GetConversationsForUserAsync(userId, cancellationToken);
        var targetConv = convs.FirstOrDefault(c => c.Id == conversationId);
        if (targetConv != null)
        {
            await hubContext.Clients.Group($"user_{targetConv.OtherParticipantId}").SendAsync("MessagesRead", conversationId);
        }
        
        return NoContent();
    }

    private long GetUserId()
    {
        var idStr = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return long.Parse(idStr!);
    }
}

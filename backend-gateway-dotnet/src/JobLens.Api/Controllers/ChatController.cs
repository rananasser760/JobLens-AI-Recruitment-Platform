using JobLens.Api.Compatibility;
using JobLens.Application.Common;
using JobLens.Application.DTOs.Chat;
using JobLens.Application.Interfaces;
using JobLens.Domain.Entities;
using JobLens.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using JobLens.Api.Hubs;
using System.Security.Claims;

namespace JobLens.Api.Controllers;

[ApiController]
[Route("api/chat")]
[Authorize]
public sealed class ChatController(
    IChatService chatService,
    IFileStorageService fileStorageService,
    JobLensDbContext dbContext,
    IHubContext<ChatHub> hubContext) : AppControllerBase
{
    [HttpGet("conversations")]
    public async Task<ActionResult<IReadOnlyList<ChatConversationDto>>> GetConversations(CancellationToken cancellationToken)
    {
        var userId = GetRequiredUserId();
        var conversations = await chatService.GetConversationsForUserAsync(userId, cancellationToken);
        return Ok(conversations);
    }

    [HttpPost("conversations/with/{otherUserId}")]
    public async Task<ActionResult<ChatConversationDto>> GetOrCreateConversation([FromRoute] long otherUserId, CancellationToken cancellationToken)
    {
        var userId = GetRequiredUserId();
        if (userId == otherUserId)
        {
            return BadRequest(new ApiResponse<ChatConversationDto>(false, null, "Cannot start a conversation with yourself.", ["invalid_participant"]));
        }

        var conversation = await chatService.GetOrCreateConversationAsync(userId, otherUserId, cancellationToken);
        return Ok(conversation);
    }

    [HttpGet("conversations/{conversationId:long}/messages")]
    public async Task<ActionResult<IReadOnlyList<ChatMessageDto>>> GetMessages(
        [FromRoute] long conversationId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var isParticipant = await dbContext.ChatConversations.AnyAsync(
            c => c.Id == conversationId && (c.Participant1Id == userId || c.Participant2Id == userId),
            cancellationToken);

        if (!isParticipant)
        {
            return Forbid();
        }

        var messages = await chatService.GetMessagesForConversationAsync(userId, conversationId, skip, take, cancellationToken);
        return Ok(messages);
    }

    [HttpGet("unread-count")]
    public async Task<ActionResult<int>> GetUnreadCount(CancellationToken cancellationToken)
    {
        var userId = GetRequiredUserId();
        var count = await chatService.GetUnreadCountAsync(userId, cancellationToken);
        return Ok(new { count });
    }

    [HttpPost("conversations/{conversationId:long}/read")]
    public async Task<IActionResult> MarkAsRead([FromRoute] long conversationId, CancellationToken cancellationToken)
    {
        var userId = GetRequiredUserId();
        var conversation = await dbContext.ChatConversations.FirstOrDefaultAsync(
            c => c.Id == conversationId && (c.Participant1Id == userId || c.Participant2Id == userId),
            cancellationToken);

        if (conversation is null)
        {
            return Forbid();
        }

        await chatService.MarkConversationAsReadAsync(userId, conversationId, cancellationToken);

        var otherUserId = conversation.Participant1Id == userId ? conversation.Participant2Id : conversation.Participant1Id;
        await hubContext.Clients.Group($"user_{otherUserId}").SendAsync("MessagesRead", conversationId, cancellationToken);

        return NoContent();
    }

    [HttpPost("attachments")]
    [RequestSizeLimit(15_000_000)]
    public async Task<IActionResult> UploadAttachment([FromForm] IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new ApiResponse<ChatAttachmentDto>(false, null, "File is required.", ["empty_file"]));
        }

        if (!UploadRules.HasAllowedExtension(file.FileName, UploadRules.ChatExtensions))
        {
            return BadRequest(new ApiResponse<ChatAttachmentDto>(false, null, "Only PDF, Word, text and image files can be attached.", ["invalid_file_type"]));
        }

        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);

        var dto = await chatService.SaveAttachmentAsync(
            GetRequiredUserId(),
            file.FileName,
            UploadRules.ContentTypeFor(file.FileName),
            memory.ToArray(),
            cancellationToken);

        return Ok(new ApiResponse<ChatAttachmentDto>(true, dto, "Attachment uploaded successfully."));
    }

    [HttpGet("attachments/{attachmentId:long}")]
    public async Task<IActionResult> DownloadAttachment([FromRoute] long attachmentId, CancellationToken cancellationToken)
    {
        var userId = GetRequiredUserId();
        var attachment = await dbContext.ChatAttachments
            .Include(a => a.Message)
            .ThenInclude(m => m!.Conversation)
            .FirstOrDefaultAsync(a => a.Id == attachmentId, cancellationToken);

        if (attachment is null || string.IsNullOrWhiteSpace(attachment.StorageKey))
        {
            return NotFound(new ApiResponse<bool>(false, false, "Attachment not found.", ["not_found"]));
        }

        var conversation = attachment.Message?.Conversation;
        var isParticipant = conversation is not null && (conversation.Participant1Id == userId || conversation.Participant2Id == userId);
        if (attachment.UploadedByUserId != userId && !isParticipant)
        {
            return Forbid();
        }

        var bytes = await fileStorageService.ReadAsync(attachment.StorageKey, cancellationToken);
        if (bytes is null)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Attachment file not found.", ["not_found"]));
        }

        return File(bytes, UploadRules.ContentTypeFor(attachment.FileName), attachment.FileName);
    }
}
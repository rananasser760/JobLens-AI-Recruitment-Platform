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

        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);

        var storageKey = await fileStorageService.SaveAsync(file.FileName, memory.ToArray(), cancellationToken);

        var attachment = new ChatAttachment
        {
            FileName = Path.GetFileName(file.FileName),
            FileUrl = $"/api/chat/attachments/{storageKey}",
            FileType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            FileSize = file.Length,
        };

        dbContext.ChatAttachments.Add(attachment);
        await dbContext.SaveChangesAsync(cancellationToken);

        var dto = new ChatAttachmentDto
        {
            Id = attachment.Id,
            FileName = attachment.FileName,
            FileUrl = attachment.FileUrl,
            FileType = attachment.FileType,
            FileSize = attachment.FileSize,
        };

        return Ok(new ApiResponse<ChatAttachmentDto>(true, dto, "Attachment uploaded successfully."));
    }

    [HttpGet("attachments/{storageKey}")]
    public async Task<IActionResult> DownloadAttachment([FromRoute] string storageKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            return BadRequest();
        }

        var attachment = await dbContext.ChatAttachments.FirstOrDefaultAsync(
            a => a.FileUrl.EndsWith(storageKey),
            cancellationToken);

        var bytes = await fileStorageService.ReadAsync(storageKey, cancellationToken);
        if (bytes is null)
        {
            return NotFound(new ApiResponse<bool>(false, false, "Attachment file not found.", ["not_found"]));
        }

        var contentType = attachment?.FileType ?? "application/octet-stream";
        var fileName = attachment?.FileName ?? storageKey;

        return File(bytes, contentType, fileName);
    }
}
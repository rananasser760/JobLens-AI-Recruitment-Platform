using JobLens.Domain.Common;

namespace JobLens.Domain.Entities;

public sealed class ChatAttachment : BaseEntity
{
    public long MessageId { get; set; }
    public ChatMessage Message { get; set; } = null!;

    public string FileName { get; set; } = string.Empty;
    public string FileUrl { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public long FileSize { get; set; }
}

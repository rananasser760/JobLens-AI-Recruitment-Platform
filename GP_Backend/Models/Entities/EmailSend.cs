using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using GP_Backend.Models.Enums;

namespace GP_Backend.Models.Entities;

public class EmailSend
{
    [Key]
    public long Id { get; set; }

    [Required]
    [MaxLength(255)]
    public string ToEmail { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? FromEmail { get; set; }

    [Required]
    [MaxLength(500)]
    public string Subject { get; set; } = string.Empty;

    [Required]
    public string BodyHtml { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? TemplateName { get; set; }

    public DateTime? SentAt { get; set; }

    public EmailStatus Status { get; set; } = EmailStatus.Pending;

    [MaxLength(500)]
    public string? ErrorMessage { get; set; }

    public long? RelatedApplicationId { get; set; }

    public long? RelatedUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    [ForeignKey(nameof(RelatedApplicationId))]
    public virtual Application? Application { get; set; }
}

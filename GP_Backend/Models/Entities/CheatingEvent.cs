using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using GP_Backend.Models.Enums;

namespace GP_Backend.Models.Entities;

public class CheatingEvent
{
    [Key]
    public long Id { get; set; }

    [Required]
    public long SessionId { get; set; }

    public CheatingEventType EventType { get; set; }

    public float? Confidence { get; set; }

    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(500)]
    public string? FrameImagePath { get; set; }

    [MaxLength(1000)]
    public string? Details { get; set; }

    public int? TimestampSeconds { get; set; }

    // Navigation properties
    [ForeignKey(nameof(SessionId))]
    public virtual InterviewSession Session { get; set; } = null!;
}

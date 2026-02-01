using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GP_Backend.Models.Entities;

public class VideoRecording
{
    [Key]
    public long Id { get; set; }

    [Required]
    public long SessionId { get; set; }

    [Required]
    [MaxLength(500)]
    public string FilePath { get; set; } = string.Empty;

    public int? DurationSeconds { get; set; }

    [MaxLength(500)]
    public string? ThumbnailPath { get; set; }

    public long? FileSize { get; set; }

    [MaxLength(50)]
    public string? Format { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    [ForeignKey(nameof(SessionId))]
    public virtual InterviewSession Session { get; set; } = null!;
}

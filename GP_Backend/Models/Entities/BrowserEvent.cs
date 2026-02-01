using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GP_Backend.Models.Entities;

public class BrowserEvent
{
    [Key]
    public long Id { get; set; }

    [Required]
    public long SessionId { get; set; }

    public int TabSwitchCount { get; set; } = 0;

    public int FocusLossCount { get; set; } = 0;

    public int CopyPasteCount { get; set; } = 0;

    public int RightClickCount { get; set; } = 0;

    public string? DetailsJson { get; set; }

    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    [ForeignKey(nameof(SessionId))]
    public virtual InterviewSession Session { get; set; } = null!;
}

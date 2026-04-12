using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using GP_Backend.Models.Enums;

namespace GP_Backend.Models.Entities;

public class InterviewSession
{
    [Key]
    public long Id { get; set; }

    [Required]
    public long ApplicationId { get; set; }

    public InterviewAgentType AgentType { get; set; }

    [MaxLength(100)]
    public string? InterviewTitle { get; set; }

    public DateTime? ScheduledAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    public float? OverallScore { get; set; }

    public bool CheatingDetected { get; set; } = false;

    public int TotalQuestions { get; set; } = 0;

    public int AnsweredQuestions { get; set; } = 0;

    [MaxLength(50)]
    public string? Status { get; set; } = "Scheduled";

    public long? IntegritySessionId { get; set; }

    [MaxLength(100)]
    public string? InterviewBackendSessionId { get; set; }

    public string? FinalReport { get; set; }

    public string? AiFeedback { get; set; }

    // Navigation properties
    [ForeignKey(nameof(ApplicationId))]
    public virtual Application Application { get; set; } = null!;

    public virtual ICollection<InterviewQuestion> Questions { get; set; } = new List<InterviewQuestion>();
    public virtual ICollection<VideoRecording> VideoRecordings { get; set; } = new List<VideoRecording>();
    public virtual ICollection<CheatingEvent> CheatingEvents { get; set; } = new List<CheatingEvent>();
    public virtual ICollection<BrowserEvent> BrowserEvents { get; set; } = new List<BrowserEvent>();
}

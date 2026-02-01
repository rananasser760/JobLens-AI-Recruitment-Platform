using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GP_Backend.Models.Entities;

public class InterviewAnswer
{
    [Key]
    public long Id { get; set; }

    [Required]
    public long QuestionId { get; set; }

    [Required]
    public long SessionId { get; set; }

    public string? AnswerText { get; set; }

    [MaxLength(500)]
    public string? AnswerAudioPath { get; set; }

    public int? ResponseDurationSeconds { get; set; }

    public float? AiScore { get; set; }

    public string? AiFeedback { get; set; }

    public DateTime AnsweredAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    [ForeignKey(nameof(QuestionId))]
    public virtual InterviewQuestion Question { get; set; } = null!;

    [ForeignKey(nameof(SessionId))]
    public virtual InterviewSession Session { get; set; } = null!;
}

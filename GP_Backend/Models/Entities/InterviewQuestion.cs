using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GP_Backend.Models.Entities;

public class InterviewQuestion
{
    [Key]
    public long Id { get; set; }

    [Required]
    public long SessionId { get; set; }

    [Required]
    public string QuestionText { get; set; } = string.Empty;

    public int OrderIndex { get; set; }

    public string? ExpectedAnswer { get; set; }

    [MaxLength(100)]
    public string? Category { get; set; }

    [MaxLength(50)]
    public string? Difficulty { get; set; }

    public int? MaxDurationSeconds { get; set; }

    // Navigation properties
    [ForeignKey(nameof(SessionId))]
    public virtual InterviewSession Session { get; set; } = null!;

    public virtual InterviewAnswer? Answer { get; set; }
}

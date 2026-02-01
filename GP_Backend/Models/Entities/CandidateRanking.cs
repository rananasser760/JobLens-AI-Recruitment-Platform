using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GP_Backend.Models.Entities;

public class CandidateRanking
{
    [Key]
    public long Id { get; set; }

    [Required]
    public long JobId { get; set; }

    [Required]
    public long CandidateId { get; set; }

    // Cosine similarity score from AI backend
    public float Score { get; set; }

    public string? ReasonsJson { get; set; }

    public int? Rank { get; set; }

    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    [ForeignKey(nameof(JobId))]
    public virtual Job Job { get; set; } = null!;

    [ForeignKey(nameof(CandidateId))]
    public virtual Candidate Candidate { get; set; } = null!;
}

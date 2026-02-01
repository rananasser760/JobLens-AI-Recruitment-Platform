using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GP_Backend.Models.Entities;

public class Resume
{
    [Key]
    public long Id { get; set; }

    [Required]
    public long CandidateId { get; set; }

    [Required]
    [MaxLength(500)]
    public string FilePath { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    public string FileName { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? FileType { get; set; }

    public long? FileSize { get; set; }

    public string? ResumeText { get; set; }

    public bool IsParsed { get; set; } = false;

    public int? AtsScore { get; set; }

    public bool AtsFriendly { get; set; } = false;

    public string? AtsRecommendations { get; set; }

    public bool IsDefault { get; set; } = false;

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    [ForeignKey(nameof(CandidateId))]
    public virtual Candidate Candidate { get; set; } = null!;

    public virtual ResumeParsingResult? ParsingResult { get; set; }
    public virtual ICollection<Application> Applications { get; set; } = new List<Application>();
}

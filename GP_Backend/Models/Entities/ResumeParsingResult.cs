using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GP_Backend.Models.Entities;

public class ResumeParsingResult
{
    [Key]
    public long Id { get; set; }

    [Required]
    public long ResumeId { get; set; }

    // Parsed data stored as JSON from FastAPI LLM response
    [Required]
    public string ParsedJson { get; set; } = string.Empty;

    public float? Confidence { get; set; }

    [MaxLength(2000)]
    public string? Summary { get; set; }

    // Extracted fields for quick access
    [MaxLength(200)]
    public string? ExtractedName { get; set; }

    [MaxLength(255)]
    public string? ExtractedEmail { get; set; }

    [MaxLength(20)]
    public string? ExtractedPhone { get; set; }

    public string? ExtractedSkills { get; set; }

    public string? ExtractedExperience { get; set; }

    public string? ExtractedEducation { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    [ForeignKey(nameof(ResumeId))]
    public virtual Resume Resume { get; set; } = null!;
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GP_Backend.Models.Entities;

public class CandidateSkill
{
    [Key]
    public long Id { get; set; }

    [Required]
    public long CandidateId { get; set; }

    [Required]
    [MaxLength(100)]
    public string SkillName { get; set; } = string.Empty;

    public int? ExperienceYears { get; set; }

    // 0-100 confidence level from parsing
    public int? SkillConfidence { get; set; }

    [MaxLength(50)]
    public string? ProficiencyLevel { get; set; }

    public bool IsVerified { get; set; } = false;

    // Navigation properties
    [ForeignKey(nameof(CandidateId))]
    public virtual Candidate Candidate { get; set; } = null!;
}

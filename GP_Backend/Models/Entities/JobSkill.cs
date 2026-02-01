using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GP_Backend.Models.Entities;

public class JobSkill
{
    [Key]
    public long Id { get; set; }

    [Required]
    public long JobId { get; set; }

    [Required]
    [MaxLength(100)]
    public string SkillName { get; set; } = string.Empty;

    // 1-10 importance scale
    public int Importance { get; set; } = 5;

    public bool IsRequired { get; set; } = false;

    // Navigation properties
    [ForeignKey(nameof(JobId))]
    public virtual Job Job { get; set; } = null!;
}

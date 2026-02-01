using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GP_Backend.Models.Entities;

public class Company
{
    [Key]
    public long Id { get; set; }

    [Required]
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? Website { get; set; }

    [MaxLength(100)]
    public string? Industry { get; set; }

    public int? Size { get; set; }

    [MaxLength(255)]
    public string? LogoPath { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    [MaxLength(255)]
    public string? Location { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public virtual ICollection<Recruiter> Recruiters { get; set; } = new List<Recruiter>();
    public virtual ICollection<Job> Jobs { get; set; } = new List<Job>();
}

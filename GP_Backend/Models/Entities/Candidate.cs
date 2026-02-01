using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GP_Backend.Models.Entities;

public class Candidate
{
    [Key]
    public long Id { get; set; }

    [Required]
    public long UserId { get; set; }

    [MaxLength(200)]
    public string? FullName { get; set; }

    [MaxLength(20)]
    public string? Phone { get; set; }

    [MaxLength(255)]
    public string? Location { get; set; }

    [MaxLength(200)]
    public string? CurrentTitle { get; set; }

    [MaxLength(500)]
    public string? Summary { get; set; }

    [MaxLength(255)]
    public string? LinkedInUrl { get; set; }

    [MaxLength(255)]
    public string? PortfolioUrl { get; set; }

    [MaxLength(255)]
    public string? ProfileImagePath { get; set; }

    public int? YearsOfExperience { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    [ForeignKey(nameof(UserId))]
    public virtual User User { get; set; } = null!;

    public virtual ICollection<Resume> Resumes { get; set; } = new List<Resume>();
    public virtual ICollection<Application> Applications { get; set; } = new List<Application>();
    public virtual ICollection<CandidateSkill> Skills { get; set; } = new List<CandidateSkill>();
    public virtual ICollection<CandidateRanking> Rankings { get; set; } = new List<CandidateRanking>();
}

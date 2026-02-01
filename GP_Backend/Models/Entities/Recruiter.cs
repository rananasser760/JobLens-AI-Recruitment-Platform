using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GP_Backend.Models.Entities;

public class Recruiter
{
    [Key]
    public long Id { get; set; }

    [Required]
    public long UserId { get; set; }

    public long? CompanyId { get; set; }

    [MaxLength(200)]
    public string? FullName { get; set; }

    [MaxLength(20)]
    public string? Phone { get; set; }

    [MaxLength(100)]
    public string? Position { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    [ForeignKey(nameof(UserId))]
    public virtual User User { get; set; } = null!;

    [ForeignKey(nameof(CompanyId))]
    public virtual Company? Company { get; set; }

    public virtual ICollection<Job> Jobs { get; set; } = new List<Job>();
}

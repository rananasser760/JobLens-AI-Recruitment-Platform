using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using GP_Backend.Models.Enums;

namespace GP_Backend.Models.Entities;

public class AutoApplyLog
{
    [Key]
    public long Id { get; set; }

    [Required]
    public long ApplicationId { get; set; }

    [MaxLength(100)]
    public string? ExternalSource { get; set; }

    [MaxLength(255)]
    public string? ExternalJobId { get; set; }

    public AutoApplyStatus Status { get; set; }

    [MaxLength(1000)]
    public string? ResponseMessage { get; set; }

    public DateTime AttemptedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    [ForeignKey(nameof(ApplicationId))]
    public virtual Application Application { get; set; } = null!;
}

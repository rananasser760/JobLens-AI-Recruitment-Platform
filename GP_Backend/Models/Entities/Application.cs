using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using GP_Backend.Models.Enums;

namespace GP_Backend.Models.Entities;

public class Application
{
    [Key]
    public long Id { get; set; }

    [Required]
    public long CandidateId { get; set; }

    [Required]
    public long JobId { get; set; }

    public long? ResumeId { get; set; }

    // "Internal" for our platform jobs, "External" for scraped jobs
    [MaxLength(50)]
    public string AppliedVia { get; set; } = "Internal";

    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;

    public ApplicationStatus Status { get; set; } = ApplicationStatus.Pending;

    [MaxLength(1000)]
    public string? CoverLetter { get; set; }

    public string? RecruiterNotes { get; set; }

    public DateTime? ReviewedAt { get; set; }

    public long? ReviewedBy { get; set; }

    // Navigation properties
    [ForeignKey(nameof(CandidateId))]
    public virtual Candidate Candidate { get; set; } = null!;

    [ForeignKey(nameof(JobId))]
    public virtual Job Job { get; set; } = null!;

    [ForeignKey(nameof(ResumeId))]
    public virtual Resume? Resume { get; set; }

    public virtual ICollection<AutoApplyLog> AutoApplyLogs { get; set; } = new List<AutoApplyLog>();
    public virtual ICollection<InterviewSession> InterviewSessions { get; set; } = new List<InterviewSession>();
    public virtual ICollection<EmailSend> EmailSends { get; set; } = new List<EmailSend>();
}

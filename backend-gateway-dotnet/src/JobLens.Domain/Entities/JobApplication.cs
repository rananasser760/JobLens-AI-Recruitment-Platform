using JobLens.Domain.Common;
using JobLens.Domain.Enums;

namespace JobLens.Domain.Entities;

public sealed class JobApplication : BaseEntity
{
    public long CandidateProfileId { get; set; }
    public long JobPostingId { get; set; }
    public long ResumeId { get; set; }
    public string AppliedVia { get; set; } = "Portal";
    public string CoverLetter { get; set; } = string.Empty;
    public string RecruiterNotes { get; set; } = string.Empty;
    public DateTime? ReviewedAtUtc { get; set; }
    public ApplicationStatus Status { get; set; } = ApplicationStatus.Submitted;
    public DateTime SubmittedAtUtc { get; set; } = DateTime.UtcNow;

    public CandidateProfile CandidateProfile { get; set; } = null!;
    public JobPosting JobPosting { get; set; } = null!;
    public Resume Resume { get; set; } = null!;
    public ICollection<AtsAssessment> AtsAssessments { get; set; } = new List<AtsAssessment>();
    public ICollection<InterviewSession> InterviewSessions { get; set; } = new List<InterviewSession>();
}

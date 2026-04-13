using JobLens.Domain.Common;

namespace JobLens.Domain.Entities;

public sealed class Resume : BaseEntity
{
    public long CandidateProfileId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public string ParseStatus { get; set; } = "Pending";

    public CandidateProfile CandidateProfile { get; set; } = null!;
    public ParsedResumeResult? ParsedResumeResult { get; set; }
    public ICollection<JobApplication> Applications { get; set; } = new List<JobApplication>();
}

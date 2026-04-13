using JobLens.Domain.Common;

namespace JobLens.Domain.Entities;

public sealed class AtsAssessment : BaseEntity
{
    public long ApplicationId { get; set; }
    public string Status { get; set; } = "Pending";
    public double? Score { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string MissingSkillsJson { get; set; } = "[]";
    public string SuggestionsJson { get; set; } = "[]";
    public string RawResponseJson { get; set; } = "{}";
    public DateTime? EvaluatedAtUtc { get; set; }

    public JobApplication Application { get; set; } = null!;
}

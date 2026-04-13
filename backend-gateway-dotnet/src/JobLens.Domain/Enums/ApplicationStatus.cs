namespace JobLens.Domain.Enums;

public enum ApplicationStatus
{
    Submitted = 1,
    AtsPending = 2,
    AtsQualified = 3,
    AtsRejected = 4,
    InterviewScheduled = 5,
    InterviewCompleted = 6,
    Offered = 7,
    Rejected = 8,
    Withdrawn = 9,
    ExternalRedirected = 10,
}

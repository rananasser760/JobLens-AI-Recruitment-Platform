using JobLens.Api.Contracts;
using JobLens.Application.Common;
using JobLens.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JobLens.Api.Controllers;

[Authorize]
[Route("api/metadata")]
public sealed class MetadataController : AppControllerBase
{
    [HttpGet("enums")]
    public IActionResult GetEnums()
    {
        var enums = new Dictionary<string, IReadOnlyList<EnumOptionDto>>(StringComparer.Ordinal)
        {
            ["ApplicationStatus"] =
            [
                new EnumOptionDto((int)ApplicationStatus.Submitted, nameof(ApplicationStatus.Submitted)),
                new EnumOptionDto((int)ApplicationStatus.AtsPending, nameof(ApplicationStatus.AtsPending)),
                new EnumOptionDto((int)ApplicationStatus.AtsQualified, nameof(ApplicationStatus.AtsQualified)),
                new EnumOptionDto((int)ApplicationStatus.AtsRejected, nameof(ApplicationStatus.AtsRejected)),
                new EnumOptionDto((int)ApplicationStatus.InterviewScheduled, nameof(ApplicationStatus.InterviewScheduled)),
                new EnumOptionDto((int)ApplicationStatus.InterviewCompleted, nameof(ApplicationStatus.InterviewCompleted)),
                new EnumOptionDto((int)ApplicationStatus.Offered, nameof(ApplicationStatus.Offered)),
                new EnumOptionDto((int)ApplicationStatus.Rejected, nameof(ApplicationStatus.Rejected)),
                new EnumOptionDto((int)ApplicationStatus.Withdrawn, nameof(ApplicationStatus.Withdrawn)),
                new EnumOptionDto((int)ApplicationStatus.ExternalRedirected, nameof(ApplicationStatus.ExternalRedirected)),
            ],
            ["AppRole"] = Enum.GetValues<AppRole>()
                .Select(x => new EnumOptionDto((int)x, x.ToString()))
                .ToArray(),
            ["InterviewSessionStatus"] =
            [
                new EnumOptionDto((int)InterviewSessionStatus.Draft, nameof(InterviewSessionStatus.Draft)),
                new EnumOptionDto((int)InterviewSessionStatus.Scheduled, nameof(InterviewSessionStatus.Scheduled)),
                new EnumOptionDto((int)InterviewSessionStatus.Live, nameof(InterviewSessionStatus.Live)),
                new EnumOptionDto((int)InterviewSessionStatus.Completed, nameof(InterviewSessionStatus.Completed)),
                new EnumOptionDto((int)InterviewSessionStatus.Abandoned, nameof(InterviewSessionStatus.Abandoned)),
                new EnumOptionDto((int)InterviewSessionStatus.ReviewRequired, nameof(InterviewSessionStatus.ReviewRequired)),
                new EnumOptionDto((int)InterviewSessionStatus.Cancelled, nameof(InterviewSessionStatus.Cancelled)),
            ],
            ["JobSourceType"] =
            [
                new EnumOptionDto((int)JobSourceType.Internal, nameof(JobSourceType.Internal)),
                new EnumOptionDto((int)JobSourceType.External, nameof(JobSourceType.External)),
            ],
            ["EmploymentType"] =
            [
                new EnumOptionDto(1, "FullTime"),
                new EnumOptionDto(2, "PartTime"),
                new EnumOptionDto(3, "Contract"),
                new EnumOptionDto(4, "Internship"),
                new EnumOptionDto(5, "Freelance"),
                new EnumOptionDto(6, "Remote"),
            ],
        };

        return Ok(new ApiResponse<EnumMetadataDto>(true, new EnumMetadataDto(enums)));
    }
}

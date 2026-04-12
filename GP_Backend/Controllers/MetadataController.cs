using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GP_Backend.Models.DTOs.Common;
using GP_Backend.Models.Enums;

namespace GP_Backend.Controllers;

[ApiController]
[Route("api/metadata")]
[Authorize]
public class MetadataController : ControllerBase
{
    [HttpGet("enums")]
    public IActionResult GetEnums()
    {
        var payload = new EnumMetadataDto
        {
            Enums = new Dictionary<string, List<EnumOptionDto>>
            {
                [nameof(UserRole)] = BuildEnumOptions<UserRole>(),
                [nameof(ApplicationStatus)] = BuildEnumOptions<ApplicationStatus>(),
                [nameof(EmploymentType)] = BuildEnumOptions<EmploymentType>(),
                [nameof(InterviewAgentType)] = BuildEnumOptions<InterviewAgentType>(),
                [nameof(CheatingEventType)] = BuildEnumOptions<CheatingEventType>(),
                [nameof(JobSource)] = BuildEnumOptions<JobSource>()
            }
        };

        return Ok(ApiResponse<EnumMetadataDto>.SuccessResponse(payload));
    }

    private static List<EnumOptionDto> BuildEnumOptions<TEnum>() where TEnum : struct, Enum
    {
        return Enum.GetValues<TEnum>()
            .Select(value => new EnumOptionDto
            {
                Value = Convert.ToInt32(value),
                Name = value.ToString()
            })
            .ToList();
    }
}

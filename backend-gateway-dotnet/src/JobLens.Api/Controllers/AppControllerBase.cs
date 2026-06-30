using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace JobLens.Api.Controllers;

[ApiController]
public abstract class AppControllerBase : ControllerBase
{
    protected long GetRequiredUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue(ClaimTypes.Name)
            ?? User.FindFirstValue("sub");

        return long.TryParse(raw, out var userId)
            ? userId
            : throw new InvalidOperationException("Authenticated user id claim is missing.");
    }
}

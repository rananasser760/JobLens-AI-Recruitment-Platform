using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace JobLens.Api.Controllers;

[ApiController]
public abstract class AppControllerBase : ControllerBase
{
    protected long GetRequiredUserId()
    {
        var userId = GetOptionalUserId();
        return userId ?? throw new UnauthorizedAccessException("Authenticated user id claim is missing or invalid.");
    }

    protected long? GetOptionalUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue("sub")
            ?? User.FindFirstValue("id")
            ?? User.FindFirstValue(ClaimTypes.Name);

        return long.TryParse(raw, out var userId) ? userId : null;
    }

    protected string GetRequiredUserRole()
    {
        var role = User.FindFirstValue(ClaimTypes.Role)
            ?? User.FindFirstValue("role");

        return !string.IsNullOrWhiteSpace(role)
            ? role.Trim()
            : throw new UnauthorizedAccessException("Authenticated user role claim is missing.");
    }

    protected string GetRequiredUserEmail()
    {
        var email = User.FindFirstValue(ClaimTypes.Email)
            ?? User.FindFirstValue(JwtRegisteredClaimNames.Email)
            ?? User.FindFirstValue("email");

        return !string.IsNullOrWhiteSpace(email)
            ? email.Trim().ToLowerInvariant()
            : throw new UnauthorizedAccessException("Authenticated user email claim is missing.");
    }
}
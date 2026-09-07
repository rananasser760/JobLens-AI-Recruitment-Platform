using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Hangfire.Dashboard;
using JobLens.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace JobLens.Api.Security;

public sealed class AdminHangfireDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        var user = httpContext.User;

        // 1. Direct authentication check (if user identity is already set)
        if (user.Identity?.IsAuthenticated == true && IsAdmin(user))
        {
            return true;
        }

        // 2. Query string token check for browser page navigation (e.g. /hangfire?access_token=...)
        var accessToken = httpContext.Request.Query["access_token"].ToString();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            accessToken = httpContext.Request.Query["token"].ToString();
        }

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            var tokenService = httpContext.RequestServices.GetService<ITokenService>();
            if (tokenService is not null)
            {
                var principal = tokenService.GetPrincipalFromToken(accessToken, validateLifetime: true);
                if (principal is not null && IsAdmin(principal))
                {
                    httpContext.User = principal;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsAdmin(ClaimsPrincipal principal)
    {
        var role = principal.FindFirstValue(ClaimTypes.Role)
            ?? principal.FindFirstValue("role")
            ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);

        return string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)
            || principal.IsInRole("Admin");
    }
}
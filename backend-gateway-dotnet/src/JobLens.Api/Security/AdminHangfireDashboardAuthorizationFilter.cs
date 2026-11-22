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

        if (user.Identity?.IsAuthenticated == true && IsAdmin(user))
        {
            return true;
        }

        var accessToken = httpContext.Request.Query["access_token"].ToString();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return false;
        }

        var tokenService = httpContext.RequestServices.GetService<ITokenService>();
        var principal = tokenService?.GetAccessPrincipal(accessToken);
        if (principal is not null && IsAdmin(principal))
        {
            httpContext.User = principal;
            return true;
        }

        return false;
    }

    private static bool IsAdmin(ClaimsPrincipal principal)
    {
        var role = principal.FindFirstValue(ClaimTypes.Role) ?? principal.FindFirstValue("role");
        return string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase) || principal.IsInRole("Admin");
    }
}

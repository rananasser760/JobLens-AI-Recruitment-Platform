using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using JobLens.Domain.Entities;
using JobLens.Domain.Enums;
using JobLens.Infrastructure.Configuration;
using JobLens.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace JobLens.Api.Tests.Security;

public sealed class TokenServiceTests
{
    [Fact]
    public void GenerateToken_WhenValidated_ReturnsPrincipalWithExpectedClaims()
    {
        var service = CreateService();
        var user = CreateUser();

        var (token, _) = service.GenerateToken(user);
        var principal = service.GetPrincipalFromToken(token, validateLifetime: true);

        Assert.NotNull(principal);
        Assert.Equal(user.Id.ToString(), principal!.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(user.DisplayName, principal.FindFirstValue(ClaimTypes.Name));
        Assert.Equal(user.Role.ToString(), principal.FindFirstValue(ClaimTypes.Role));

        var email = principal.Claims.FirstOrDefault(claim =>
            claim.Type == JwtRegisteredClaimNames.Email || claim.Type == ClaimTypes.Email)?.Value;
        Assert.Equal(user.Email, email);
    }

    [Fact]
    public void GetPrincipalFromToken_WhenTokenIsInvalid_ReturnsNull()
    {
        var service = CreateService();

        var principal = service.GetPrincipalFromToken("not-a-token", validateLifetime: true);

        Assert.Null(principal);
    }

    [Fact]
    public void GetPrincipalFromToken_WhenExpired_RespectsValidateLifetimeFlag()
    {
        var service = CreateService(expiryMinutes: -5);
        var user = CreateUser();

        var (token, _) = service.GenerateToken(user);

        var strictPrincipal = service.GetPrincipalFromToken(token, validateLifetime: true);
        var relaxedPrincipal = service.GetPrincipalFromToken(token, validateLifetime: false);

        Assert.Null(strictPrincipal);
        Assert.NotNull(relaxedPrincipal);
        Assert.Equal(user.Id.ToString(), relaxedPrincipal!.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    private static TokenService CreateService(int expiryMinutes = 120)
    {
        var options = Options.Create(new JwtOptions
        {
            Issuer = "JobLens.Tests",
            Audience = "JobLens.Tests.Client",
            SigningKey = "this-is-a-test-signing-key-with-minimum-32chars",
            ExpiryMinutes = expiryMinutes,
        });

        return new TokenService(options);
    }

    private static User CreateUser() => new()
    {
        Id = 42,
        Email = "candidate@example.com",
        DisplayName = "Candidate User",
        Role = AppRole.Candidate,
    };
}

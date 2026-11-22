using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using JobLens.Application.Interfaces;
using JobLens.Domain.Entities;
using JobLens.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace JobLens.Infrastructure.Security;

public sealed class TokenService(IOptions<JwtOptions> options) : ITokenService
{
    public const string TokenTypeClaim = "token_type";
    public const string AccessTokenType = "access";
    public const string RefreshTokenType = "refresh";
    public const string PasswordResetTokenType = "password_reset";
    private const string PasswordStampClaim = "pwd_stamp";

    private readonly JwtOptions _options = options.Value;

    public (string AccessToken, DateTime ExpiresAtUtc) GenerateToken(User user)
    {
        var expiresAtUtc = DateTime.UtcNow.AddMinutes(_options.ExpiryMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.Role.ToString()),
            new("role", user.Role.ToString()),
            new(TokenTypeClaim, AccessTokenType),
        };

        return (WriteToken(claims, expiresAtUtc), expiresAtUtc);
    }

    public (string RefreshToken, DateTime ExpiresAtUtc) GenerateRefreshToken(User user)
    {
        var days = _options.RefreshTokenDays <= 0 ? 7 : _options.RefreshTokenDays;
        var expiresAtUtc = DateTime.UtcNow.AddDays(days);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(TokenTypeClaim, RefreshTokenType),
            new(PasswordStampClaim, ComputePasswordStamp(user.PasswordHash)),
        };

        return (WriteToken(claims, expiresAtUtc), expiresAtUtc);
    }

    public long? ReadRefreshTokenUserId(string token)
    {
        var principal = GetTypedPrincipal(token, RefreshTokenType);
        return principal is null ? null : ReadUserId(principal);
    }

    public bool ValidateRefreshToken(string token, User user)
    {
        var principal = GetTypedPrincipal(token, RefreshTokenType);
        if (principal is null || ReadUserId(principal) != user.Id)
        {
            return false;
        }

        return FixedTimeEquals(principal.FindFirstValue(PasswordStampClaim), ComputePasswordStamp(user.PasswordHash));
    }

    public (string ResetToken, DateTime ExpiresAtUtc) GeneratePasswordResetToken(User user, TimeSpan lifetime)
    {
        var effectiveLifetime = lifetime <= TimeSpan.Zero ? TimeSpan.FromMinutes(30) : lifetime;
        var expiresAtUtc = DateTime.UtcNow.Add(effectiveLifetime);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(TokenTypeClaim, PasswordResetTokenType),
            new(PasswordStampClaim, ComputePasswordStamp(user.PasswordHash)),
        };

        return (WriteToken(claims, expiresAtUtc), expiresAtUtc);
    }

    public bool ValidatePasswordResetToken(string token, User user)
    {
        var principal = GetTypedPrincipal(token, PasswordResetTokenType);
        if (principal is null || ReadUserId(principal) != user.Id)
        {
            return false;
        }

        var tokenEmail = principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.FindFirstValue(JwtRegisteredClaimNames.Email)
            ?? principal.FindFirstValue("email");

        if (!string.Equals(tokenEmail, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return FixedTimeEquals(principal.FindFirstValue(PasswordStampClaim), ComputePasswordStamp(user.PasswordHash));
    }

    public ClaimsPrincipal? GetAccessPrincipal(string token)
    {
        var principal = GetPrincipalFromToken(token, validateLifetime: true);
        if (principal is null)
        {
            return null;
        }

        var tokenType = principal.FindFirstValue(TokenTypeClaim);
        return string.IsNullOrEmpty(tokenType) || string.Equals(tokenType, AccessTokenType, StringComparison.Ordinal)
            ? principal
            : null;
    }

    public ClaimsPrincipal? GetPrincipalFromToken(string token, bool validateLifetime)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var cleanToken = token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? token["Bearer ".Length..].Trim()
            : token.Trim();

        var handler = new JwtSecurityTokenHandler();

        try
        {
            return handler.ValidateToken(
                cleanToken,
                new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = validateLifetime,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = _options.Issuer,
                    ValidAudience = _options.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
                    ClockSkew = validateLifetime ? TimeSpan.FromSeconds(5) : TimeSpan.Zero,
                },
                out _);
        }
        catch
        {
            return null;
        }
    }

    private ClaimsPrincipal? GetTypedPrincipal(string token, string expectedType)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var principal = GetPrincipalFromToken(token.Trim(), validateLifetime: true);
        if (principal is null)
        {
            return null;
        }

        return string.Equals(principal.FindFirstValue(TokenTypeClaim), expectedType, StringComparison.Ordinal)
            ? principal
            : null;
    }

    private string WriteToken(IEnumerable<Claim> claims, DateTime expiresAtUtc)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: expiresAtUtc,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static long? ReadUserId(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue("sub");

        return long.TryParse(raw, out var id) ? id : null;
    }

    private static string ComputePasswordStamp(string passwordHash)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash));
        return Convert.ToHexString(hashBytes);
    }

    private static bool FixedTimeEquals(string? provided, string expected)
    {
        if (string.IsNullOrWhiteSpace(provided))
        {
            return false;
        }

        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return providedBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}

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
    private const string PasswordResetTokenType = "password_reset";
    private const string TokenTypeClaim = "token_type";
    private const string PasswordStampClaim = "pwd_stamp";

    private readonly JwtOptions _options = options.Value;

    public (string AccessToken, DateTime ExpiresAtUtc) GenerateToken(User user)
    {
        var expiresAtUtc = DateTime.UtcNow.AddMinutes(_options.ExpiryMinutes);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Role, user.Role.ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: expiresAtUtc,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAtUtc);
    }

    public (string ResetToken, DateTime ExpiresAtUtc) GeneratePasswordResetToken(User user, TimeSpan lifetime)
    {
        var effectiveLifetime = lifetime <= TimeSpan.Zero ? TimeSpan.FromMinutes(30) : lifetime;
        var expiresAtUtc = DateTime.UtcNow.Add(effectiveLifetime);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(TokenTypeClaim, PasswordResetTokenType),
            new(PasswordStampClaim, ComputePasswordStamp(user.PasswordHash)),
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: expiresAtUtc,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAtUtc);
    }

    public bool ValidatePasswordResetToken(string token, User user)
    {
        var principal = GetPrincipalFromToken(token, validateLifetime: true);
        if (principal is null)
        {
            return false;
        }

        var tokenType = principal.FindFirstValue(TokenTypeClaim);
        if (!string.Equals(tokenType, PasswordResetTokenType, StringComparison.Ordinal))
        {
            return false;
        }

        var rawUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue("sub");

        if (!long.TryParse(rawUserId, out var tokenUserId) || tokenUserId != user.Id)
        {
            return false;
        }

        var tokenEmail = principal.FindFirstValue(JwtRegisteredClaimNames.Email)
            ?? principal.FindFirstValue(ClaimTypes.Email);

        if (!string.Equals(tokenEmail, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var tokenStamp = principal.FindFirstValue(PasswordStampClaim);
        var expectedStamp = ComputePasswordStamp(user.PasswordHash);
        return FixedTimeEquals(tokenStamp, expectedStamp);
    }

    public ClaimsPrincipal? GetPrincipalFromToken(string token, bool validateLifetime)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var handler = new JwtSecurityTokenHandler();

        try
        {
            return handler.ValidateToken(
                token,
                new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = validateLifetime,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = _options.Issuer,
                    ValidAudience = _options.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
                    ClockSkew = TimeSpan.FromSeconds(30),
                },
                out _);
        }
        catch
        {
            return null;
        }
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

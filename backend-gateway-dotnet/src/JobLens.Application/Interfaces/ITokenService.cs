using JobLens.Domain.Entities;
using System.Security.Claims;

namespace JobLens.Application.Interfaces;

public interface ITokenService
{
    (string AccessToken, DateTime ExpiresAtUtc) GenerateToken(User user);
    (string ResetToken, DateTime ExpiresAtUtc) GeneratePasswordResetToken(User user, TimeSpan lifetime);
    bool ValidatePasswordResetToken(string token, User user);
    ClaimsPrincipal? GetPrincipalFromToken(string token, bool validateLifetime);
}

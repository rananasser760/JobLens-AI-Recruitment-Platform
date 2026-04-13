using JobLens.Domain.Enums;

namespace JobLens.Application.DTOs.Auth;

public sealed record RegisterRequest(
    string Email,
    string Password,
    string? DisplayName,
    AppRole Role,
    string? CompanyName = null,
    string? Username = null,
    string? FullName = null,
    string? ConfirmPassword = null,
    long? CompanyId = null);

public sealed record LoginRequest(string Email, string Password);

public sealed record AuthResponse(
    long UserId,
    string Username,
    string Email,
    string Role,
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresAtUtc,
    long? ProfileId = null,
    string? FullName = null);

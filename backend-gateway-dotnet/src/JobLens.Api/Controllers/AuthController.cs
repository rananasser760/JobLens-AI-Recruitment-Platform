using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using JobLens.Api.Contracts;
using JobLens.Application.Common;
using JobLens.Application.DTOs.Auth;
using JobLens.Application.Interfaces;
using JobLens.Infrastructure.Configuration;
using JobLens.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JobLens.Api.Controllers;

[Route("api/auth")]
public sealed class AuthController(
    IAuthService authService,
    JobLensDbContext dbContext,
    ITokenService tokenService,
    IPasswordHasher passwordHasher,
    IEmailDeliveryService emailDeliveryService,
    IOptions<SecurityOptions> securityOptionsAccessor,
    ILogger<AuthController> logger) : AppControllerBase
{
    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterCompatRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal))
        {
            return BadRequest(new ApiResponse<AuthSessionDto>(false, null, "Password confirmation does not match.", ["password_mismatch"]));
        }

        var role = string.Equals(request.Role, "Recruiter", StringComparison.OrdinalIgnoreCase)
            ? Domain.Enums.AppRole.Recruiter
            : Domain.Enums.AppRole.Candidate;

        var registerRequest = new RegisterRequest(
            request.Email,
            request.Password,
            request.FullName ?? request.Username,
            role,
            request.CompanyId is > 0 ? null : null,
            request.Username,
            request.FullName,
            request.ConfirmPassword,
            request.CompanyId);

        var result = await authService.RegisterAsync(registerRequest, cancellationToken);
        if (!result.Success || result.Data is null)
        {
            return BadRequest(new ApiResponse<AuthSessionDto>(false, null, result.Message, result.Errors));
        }

        var session = new AuthSessionDto(
            result.Data.UserId,
            result.Data.Username,
            result.Data.Email,
            result.Data.Role,
            result.Data.AccessToken,
            result.Data.RefreshToken,
            result.Data.ExpiresAtUtc,
            result.Data.ProfileId,
            result.Data.FullName);

        return Ok(new ApiResponse<AuthSessionDto>(true, session, result.Message));
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await authService.LoginAsync(request, cancellationToken);
        if (!result.Success || result.Data is null)
        {
            return Unauthorized(new ApiResponse<AuthSessionDto>(false, null, result.Message, result.Errors));
        }

        var session = new AuthSessionDto(
            result.Data.UserId,
            result.Data.Username,
            result.Data.Email,
            result.Data.Role,
            result.Data.AccessToken,
            result.Data.RefreshToken,
            result.Data.ExpiresAtUtc,
            result.Data.ProfileId,
            result.Data.FullName);

        return Ok(new ApiResponse<AuthSessionDto>(true, session, result.Message));
    }

    [AllowAnonymous]
    [HttpPost("refresh-token")]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.AccessToken) || string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Unauthorized(new ApiResponse<AuthSessionDto>(false, null, "Invalid refresh payload.", ["invalid_refresh"]));
        }

        var principal = tokenService.GetPrincipalFromToken(request.AccessToken, validateLifetime: false);
        var rawUserId = principal?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal?.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal?.FindFirstValue("sub");

        if (!long.TryParse(rawUserId, out var userId))
        {
            return Unauthorized(new ApiResponse<AuthSessionDto>(false, null, "Invalid access token.", ["invalid_token"]));
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(x => x.Id == userId && x.IsActive, cancellationToken);
        if (user is null)
        {
            return Unauthorized(new ApiResponse<AuthSessionDto>(false, null, "User not found.", ["not_found"]));
        }

        var token = tokenService.GenerateToken(user);
        var response = await BuildAuthSessionAsync(user, token.AccessToken, token.ExpiresAtUtc, cancellationToken);
        return Ok(new ApiResponse<AuthSessionDto>(true, response, "Token refreshed."));
    }

    [Authorize]
    [HttpPost("logout")]
    public IActionResult Logout() => Ok(new ApiResponse<bool>(true, true, "Logged out."));

    [Authorize]
    [HttpGet("validate")]
    public IActionResult Validate() => Ok(new ApiResponse<bool>(true, true, "Token is valid."));

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.NewPassword, request.ConfirmNewPassword, StringComparison.Ordinal))
        {
            return BadRequest(new ApiResponse<bool>(false, false, "Password confirmation does not match.", ["password_mismatch"]));
        }

        var userId = GetRequiredUserId();
        var user = await dbContext.Users.FirstOrDefaultAsync(x => x.Id == userId && x.IsActive, cancellationToken);
        if (user is null)
        {
            return NotFound(new ApiResponse<bool>(false, false, "User not found.", ["not_found"]));
        }

        if (!passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
        {
            return BadRequest(new ApiResponse<bool>(false, false, "Current password is incorrect.", ["invalid_credentials"]));
        }

        user.PasswordHash = passwordHasher.Hash(request.NewPassword);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new ApiResponse<bool>(true, true, "Password changed successfully."));
    }

    [AllowAnonymous]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        const string genericMessage = "If the email exists, a reset link has been sent.";
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return Ok(new ApiResponse<bool>(true, true, genericMessage));
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await dbContext.Users.FirstOrDefaultAsync(x => x.Email == normalizedEmail && x.IsActive, cancellationToken);
        if (user is not null)
        {
            var security = securityOptionsAccessor.Value;
            var expiryMinutes = Math.Clamp(security.PasswordResetTokenExpiryMinutes, 5, 180);
            var token = tokenService.GeneratePasswordResetToken(user, TimeSpan.FromMinutes(expiryMinutes));
            var resetLink = BuildPasswordResetLink(security.FrontendBaseUrl, user.Email, token.ResetToken);

            try
            {
                await emailDeliveryService.SendPasswordResetAsync(user.Email, resetLink, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send password reset email for user {UserId}.", user.Id);
            }
        }

        return Ok(new ApiResponse<bool>(true, true, genericMessage));
    }

    [AllowAnonymous]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.NewPassword, request.ConfirmNewPassword, StringComparison.Ordinal))
        {
            return BadRequest(new ApiResponse<bool>(false, false, "Password confirmation does not match.", ["password_mismatch"]));
        }

        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Token))
        {
            return BadRequest(new ApiResponse<bool>(false, false, "Reset token and email are required.", ["invalid_request"]));
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await dbContext.Users.FirstOrDefaultAsync(x => x.Email == normalizedEmail && x.IsActive, cancellationToken);
        if (user is null)
        {
            return Ok(new ApiResponse<bool>(true, true, "Password reset completed."));
        }

        if (!tokenService.ValidatePasswordResetToken(request.Token, user))
        {
            return BadRequest(new ApiResponse<bool>(false, false, "Invalid or expired reset token.", ["invalid_token"]));
        }

        user.PasswordHash = passwordHasher.Hash(request.NewPassword);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new ApiResponse<bool>(true, true, "Password reset completed."));
    }

    private static string BuildPasswordResetLink(string frontendBaseUrl, string email, string token)
    {
        var baseUrl = string.IsNullOrWhiteSpace(frontendBaseUrl)
            ? "http://localhost:4200"
            : frontendBaseUrl.TrimEnd('/');

        return $"{baseUrl}/auth/reset-password?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
    }

    private async Task<AuthSessionDto> BuildAuthSessionAsync(Domain.Entities.User user, string accessToken, DateTime expiresAtUtc, CancellationToken cancellationToken)
    {
        var refreshToken = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        long? profileId = null;

        if (user.Role == Domain.Enums.AppRole.Candidate)
        {
            profileId = await dbContext.CandidateProfiles
                .Where(x => x.UserId == user.Id)
                .Select(x => (long?)x.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }
        else if (user.Role == Domain.Enums.AppRole.Recruiter)
        {
            profileId = await dbContext.RecruiterProfiles
                .Where(x => x.UserId == user.Id)
                .Select(x => (long?)x.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return new AuthSessionDto(
            user.Id,
            user.DisplayName,
            user.Email,
            user.Role.ToString(),
            accessToken,
            refreshToken,
            expiresAtUtc,
            profileId,
            user.DisplayName);
    }
}

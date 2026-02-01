using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using GP_Backend.Data;
using GP_Backend.Models.DTOs.Auth;
using GP_Backend.Models.DTOs.Common;
using GP_Backend.Models.Entities;
using GP_Backend.Models.Enums;
using GP_Backend.Services.Interfaces;

namespace GP_Backend.Services.Implementations;

public class AuthService : IAuthService
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IEmailService _emailService;
    private readonly IAuditService _auditService;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        AppDbContext context,
        IConfiguration configuration,
        IEmailService emailService,
        IAuditService auditService,
        ILogger<AuthService> logger)
    {
        _context = context;
        _configuration = configuration;
        _emailService = emailService;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<ApiResponse<AuthResponseDto>> RegisterAsync(RegisterDto dto)
    {
        try
        {
            // Check if email already exists
            if (await _context.Users.AnyAsync(u => u.Email == dto.Email))
            {
                return ApiResponse<AuthResponseDto>.FailureResponse("Email already registered");
            }

            // Check if username already exists
            if (await _context.Users.AnyAsync(u => u.Username == dto.Username))
            {
                return ApiResponse<AuthResponseDto>.FailureResponse("Username already taken");
            }

            // Parse role
            if (!Enum.TryParse<UserRole>(dto.Role, true, out var role))
            {
                return ApiResponse<AuthResponseDto>.FailureResponse("Invalid role specified");
            }

            // Create user
            var user = new User
            {
                Username = dto.Username,
                Email = dto.Email,
                PasswordHash = HashPassword(dto.Password),
                Role = role,
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            // Create profile based on role
            string? fullName = null;
            long? profileId = null;

            if (role == UserRole.Candidate)
            {
                var candidate = new Candidate
                {
                    UserId = user.Id,
                    FullName = dto.FullName,
                    Phone = dto.Phone,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _context.Candidates.Add(candidate);
                await _context.SaveChangesAsync();
                fullName = candidate.FullName;
                profileId = candidate.Id;
            }
            else if (role == UserRole.Recruiter)
            {
                var recruiter = new Recruiter
                {
                    UserId = user.Id,
                    FullName = dto.FullName,
                    Phone = dto.Phone,
                    CompanyId = dto.CompanyId,
                    CreatedAt = DateTime.UtcNow
                };
                _context.Recruiters.Add(recruiter);
                await _context.SaveChangesAsync();
                fullName = recruiter.FullName;
                profileId = recruiter.Id;
            }

            // Generate tokens
            var accessToken = GenerateAccessToken(user);
            var refreshToken = GenerateRefreshToken();

            // Save refresh token
            user.RefreshToken = refreshToken;
            user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(7);
            await _context.SaveChangesAsync();

            // Log audit
            await _auditService.LogAsync(user.Id, "Register", "User", user.Id);

            // Send welcome email
            await _emailService.SendWelcomeEmailAsync(user.Id);

            return ApiResponse<AuthResponseDto>.SuccessResponse(new AuthResponseDto
            {
                UserId = user.Id,
                Username = user.Username,
                Email = user.Email,
                Role = user.Role.ToString(),
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresAt = DateTime.UtcNow.AddHours(1),
                ProfileId = profileId,
                FullName = fullName
            }, "Registration successful");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during registration");
            return ApiResponse<AuthResponseDto>.FailureResponse("An error occurred during registration");
        }
    }

    public async Task<ApiResponse<AuthResponseDto>> LoginAsync(LoginDto dto)
    {
        try
        {
            var user = await _context.Users
                .Include(u => u.Candidate)
                .Include(u => u.Recruiter)
                .FirstOrDefaultAsync(u => u.Email == dto.Email);

            if (user == null || !VerifyPassword(dto.Password, user.PasswordHash))
            {
                return ApiResponse<AuthResponseDto>.FailureResponse("Invalid email or password");
            }

            if (!user.IsActive)
            {
                return ApiResponse<AuthResponseDto>.FailureResponse("Account is deactivated");
            }

            // Generate tokens
            var accessToken = GenerateAccessToken(user);
            var refreshToken = GenerateRefreshToken();

            // Update user
            user.RefreshToken = refreshToken;
            user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(7);
            user.LastLogin = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            // Log audit
            await _auditService.LogAsync(user.Id, "Login", "User", user.Id);

            // Get profile info
            string? fullName = null;
            long? profileId = null;

            if (user.Role == UserRole.Candidate && user.Candidate != null)
            {
                fullName = user.Candidate.FullName;
                profileId = user.Candidate.Id;
            }
            else if (user.Role == UserRole.Recruiter && user.Recruiter != null)
            {
                fullName = user.Recruiter.FullName;
                profileId = user.Recruiter.Id;
            }

            return ApiResponse<AuthResponseDto>.SuccessResponse(new AuthResponseDto
            {
                UserId = user.Id,
                Username = user.Username,
                Email = user.Email,
                Role = user.Role.ToString(),
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresAt = DateTime.UtcNow.AddHours(1),
                ProfileId = profileId,
                FullName = fullName
            }, "Login successful");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during login");
            return ApiResponse<AuthResponseDto>.FailureResponse("An error occurred during login");
        }
    }

    public async Task<ApiResponse<AuthResponseDto>> RefreshTokenAsync(RefreshTokenDto dto)
    {
        try
        {
            var principal = GetPrincipalFromExpiredToken(dto.AccessToken);
            if (principal == null)
            {
                return ApiResponse<AuthResponseDto>.FailureResponse("Invalid access token");
            }

            var userIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!long.TryParse(userIdClaim, out var userId))
            {
                return ApiResponse<AuthResponseDto>.FailureResponse("Invalid token");
            }

            var user = await _context.Users
                .Include(u => u.Candidate)
                .Include(u => u.Recruiter)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null || user.RefreshToken != dto.RefreshToken || user.RefreshTokenExpiryTime <= DateTime.UtcNow)
            {
                return ApiResponse<AuthResponseDto>.FailureResponse("Invalid refresh token");
            }

            // Generate new tokens
            var accessToken = GenerateAccessToken(user);
            var refreshToken = GenerateRefreshToken();

            // Update refresh token
            user.RefreshToken = refreshToken;
            user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(7);
            await _context.SaveChangesAsync();

            // Get profile info
            string? fullName = null;
            long? profileId = null;

            if (user.Role == UserRole.Candidate && user.Candidate != null)
            {
                fullName = user.Candidate.FullName;
                profileId = user.Candidate.Id;
            }
            else if (user.Role == UserRole.Recruiter && user.Recruiter != null)
            {
                fullName = user.Recruiter.FullName;
                profileId = user.Recruiter.Id;
            }

            return ApiResponse<AuthResponseDto>.SuccessResponse(new AuthResponseDto
            {
                UserId = user.Id,
                Username = user.Username,
                Email = user.Email,
                Role = user.Role.ToString(),
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresAt = DateTime.UtcNow.AddHours(1),
                ProfileId = profileId,
                FullName = fullName
            }, "Token refreshed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing token");
            return ApiResponse<AuthResponseDto>.FailureResponse("An error occurred while refreshing token");
        }
    }

    public async Task<ApiResponse> LogoutAsync(long userId)
    {
        try
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                return ApiResponse.FailureResponse("User not found");
            }

            user.RefreshToken = null;
            user.RefreshTokenExpiryTime = null;
            await _context.SaveChangesAsync();

            await _auditService.LogAsync(userId, "Logout", "User", userId);

            return ApiResponse.SuccessResponse("Logged out successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during logout");
            return ApiResponse.FailureResponse("An error occurred during logout");
        }
    }

    public async Task<ApiResponse> ChangePasswordAsync(long userId, ChangePasswordDto dto)
    {
        try
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                return ApiResponse.FailureResponse("User not found");
            }

            if (!VerifyPassword(dto.CurrentPassword, user.PasswordHash))
            {
                return ApiResponse.FailureResponse("Current password is incorrect");
            }

            user.PasswordHash = HashPassword(dto.NewPassword);
            user.RefreshToken = null;
            user.RefreshTokenExpiryTime = null;
            await _context.SaveChangesAsync();

            await _auditService.LogAsync(userId, "ChangePassword", "User", userId);

            return ApiResponse.SuccessResponse("Password changed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing password");
            return ApiResponse.FailureResponse("An error occurred while changing password");
        }
    }

    public async Task<ApiResponse> ForgotPasswordAsync(ForgotPasswordDto dto)
    {
        try
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == dto.Email);
            if (user == null)
            {
                // Don't reveal if email exists
                return ApiResponse.SuccessResponse("If the email exists, a reset link has been sent");
            }

            var resetToken = GenerateResetToken();
            // Store token in a secure way (could use a separate table or cache)
            // For simplicity, we'll encode it in the refresh token field temporarily
            user.RefreshToken = $"RESET:{resetToken}";
            user.RefreshTokenExpiryTime = DateTime.UtcNow.AddHours(1);
            await _context.SaveChangesAsync();

            await _emailService.SendPasswordResetEmailAsync(dto.Email, resetToken);

            return ApiResponse.SuccessResponse("If the email exists, a reset link has been sent");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in forgot password");
            return ApiResponse.FailureResponse("An error occurred");
        }
    }

    public async Task<ApiResponse> ResetPasswordAsync(ResetPasswordDto dto)
    {
        try
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => 
                u.Email == dto.Email && 
                u.RefreshToken == $"RESET:{dto.Token}" &&
                u.RefreshTokenExpiryTime > DateTime.UtcNow);

            if (user == null)
            {
                return ApiResponse.FailureResponse("Invalid or expired reset token");
            }

            user.PasswordHash = HashPassword(dto.NewPassword);
            user.RefreshToken = null;
            user.RefreshTokenExpiryTime = null;
            await _context.SaveChangesAsync();

            await _auditService.LogAsync(user.Id, "ResetPassword", "User", user.Id);

            return ApiResponse.SuccessResponse("Password reset successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting password");
            return ApiResponse.FailureResponse("An error occurred while resetting password");
        }
    }

    public async Task<ApiResponse> ValidateTokenAsync(string token)
    {
        try
        {
            var principal = GetPrincipalFromToken(token);
            if (principal == null)
            {
                return ApiResponse.FailureResponse("Invalid token");
            }

            var userIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!long.TryParse(userIdClaim, out var userId))
            {
                return ApiResponse.FailureResponse("Invalid token");
            }

            var user = await _context.Users.FindAsync(userId);
            if (user == null || !user.IsActive)
            {
                return ApiResponse.FailureResponse("Invalid token");
            }

            return ApiResponse.SuccessResponse("Token is valid");
        }
        catch
        {
            return ApiResponse.FailureResponse("Invalid token");
        }
    }

    #region Helper Methods

    private string GenerateAccessToken(User user)
    {
        var jwtSettings = _configuration.GetSection("JwtSettings");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings["SecretKey"]!));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: jwtSettings["Issuer"],
            audience: jwtSettings["Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string GenerateRefreshToken()
    {
        var randomNumber = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomNumber);
        return Convert.ToBase64String(randomNumber);
    }

    private static string GenerateResetToken()
    {
        var randomNumber = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomNumber);
        return Convert.ToBase64String(randomNumber).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private static string HashPassword(string password)
    {
        return BCrypt.Net.BCrypt.HashPassword(password);
    }

    private static bool VerifyPassword(string password, string hash)
    {
        return BCrypt.Net.BCrypt.Verify(password, hash);
    }

    private ClaimsPrincipal? GetPrincipalFromExpiredToken(string token)
    {
        var jwtSettings = _configuration.GetSection("JwtSettings");
        var tokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = true,
            ValidateIssuer = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings["SecretKey"]!)),
            ValidateLifetime = false,
            ValidIssuer = jwtSettings["Issuer"],
            ValidAudience = jwtSettings["Audience"]
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        var principal = tokenHandler.ValidateToken(token, tokenValidationParameters, out var securityToken);

        if (securityToken is not JwtSecurityToken jwtSecurityToken || 
            !jwtSecurityToken.Header.Alg.Equals(SecurityAlgorithms.HmacSha256, StringComparison.InvariantCultureIgnoreCase))
        {
            return null;
        }

        return principal;
    }

    private ClaimsPrincipal? GetPrincipalFromToken(string token)
    {
        var jwtSettings = _configuration.GetSection("JwtSettings");
        var tokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = true,
            ValidateIssuer = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings["SecretKey"]!)),
            ValidateLifetime = true,
            ValidIssuer = jwtSettings["Issuer"],
            ValidAudience = jwtSettings["Audience"]
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        try
        {
            var principal = tokenHandler.ValidateToken(token, tokenValidationParameters, out _);
            return principal;
        }
        catch
        {
            return null;
        }
    }

    #endregion
}

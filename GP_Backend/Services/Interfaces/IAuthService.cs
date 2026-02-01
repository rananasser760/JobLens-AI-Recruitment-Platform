using GP_Backend.Models.DTOs.Auth;
using GP_Backend.Models.DTOs.Common;

namespace GP_Backend.Services.Interfaces;

public interface IAuthService
{
    Task<ApiResponse<AuthResponseDto>> RegisterAsync(RegisterDto dto);
    Task<ApiResponse<AuthResponseDto>> LoginAsync(LoginDto dto);
    Task<ApiResponse<AuthResponseDto>> RefreshTokenAsync(RefreshTokenDto dto);
    Task<ApiResponse> LogoutAsync(long userId);
    Task<ApiResponse> ChangePasswordAsync(long userId, ChangePasswordDto dto);
    Task<ApiResponse> ForgotPasswordAsync(ForgotPasswordDto dto);
    Task<ApiResponse> ResetPasswordAsync(ResetPasswordDto dto);
    Task<ApiResponse> ValidateTokenAsync(string token);
}

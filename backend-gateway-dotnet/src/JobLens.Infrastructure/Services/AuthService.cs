using JobLens.Application.Common;
using JobLens.Application.DTOs.Auth;
using JobLens.Application.Interfaces;
using JobLens.Domain.Entities;
using JobLens.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace JobLens.Infrastructure.Services;

public sealed class AuthService(
    Persistence.JobLensDbContext dbContext,
    IPasswordHasher passwordHasher,
    ITokenService tokenService) : IAuthService
{
    public async Task<ApiResponse<AuthResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var displayName = (request.FullName ?? request.DisplayName ?? request.Username ?? request.Email).Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = normalizedEmail;
        }

        if (!string.IsNullOrWhiteSpace(request.ConfirmPassword) && !string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal))
        {
            return new ApiResponse<AuthResponse>(false, null, "Password confirmation does not match.", ["password_mismatch"]);
        }

        if (await dbContext.Users.AnyAsync(x => x.Email == normalizedEmail, cancellationToken))
        {
            return new ApiResponse<AuthResponse>(false, null, "An account with this email already exists.", ["email_taken"]);
        }

        Company? company = null;
        if (request.Role == AppRole.Recruiter)
        {
            if (request.CompanyId is long companyId and > 0)
            {
                company = await dbContext.Companies.FirstOrDefaultAsync(x => x.Id == companyId, cancellationToken);
                if (company is null)
                {
                    return new ApiResponse<AuthResponse>(false, null, "Selected company does not exist.", ["company_not_found"]);
                }
            }

            if (company is null && !string.IsNullOrWhiteSpace(request.CompanyName))
            {
                var trimmedCompName = request.CompanyName.Trim();
                var slug = trimmedCompName.ToLowerInvariant().Replace(" ", "-");
                company = await dbContext.Companies.FirstOrDefaultAsync(x => x.Slug == slug, cancellationToken);
                if (company is null)
                {
                    company = new Company
                    {
                        Name = trimmedCompName,
                        Slug = slug,
                    };
                    dbContext.Companies.Add(company);
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
            }
        }

        var user = new User
        {
            Email = normalizedEmail,
            DisplayName = displayName,
            PasswordHash = passwordHasher.Hash(request.Password),
            Role = request.Role,
            IsActive = true,
        };

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (request.Role == AppRole.Candidate)
        {
            dbContext.CandidateProfiles.Add(new CandidateProfile { UserId = user.Id });
        }
        else if (request.Role == AppRole.Recruiter)
        {
            dbContext.RecruiterProfiles.Add(new RecruiterProfile 
            { 
                UserId = user.Id, 
                CompanyId = company?.Id 
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var token = tokenService.GenerateToken(user);
        var refreshToken = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var profileId = await ResolveProfileIdAsync(user.Id, user.Role, cancellationToken);

        return new ApiResponse<AuthResponse>(
            true,
            new AuthResponse(
                user.Id,
                user.DisplayName,
                user.Email,
                user.Role.ToString(),
                token.AccessToken,
                refreshToken,
                token.ExpiresAtUtc,
                profileId,
                user.DisplayName),
            "User registered successfully.");
    }

    public async Task<ApiResponse<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await dbContext.Users.FirstOrDefaultAsync(x => x.Email == normalizedEmail, cancellationToken);

        if (user is null || !passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            return new ApiResponse<AuthResponse>(false, null, "Invalid email or password.", ["invalid_credentials"]);
        }

        if (!user.IsActive)
        {
            return new ApiResponse<AuthResponse>(false, null, "Your account has been deactivated. Please contact support.", ["account_deactivated"]);
        }

        var token = tokenService.GenerateToken(user);
        var refreshToken = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var profileId = await ResolveProfileIdAsync(user.Id, user.Role, cancellationToken);

        return new ApiResponse<AuthResponse>(
            true,
            new AuthResponse(
                user.Id,
                user.DisplayName,
                user.Email,
                user.Role.ToString(),
                token.AccessToken,
                refreshToken,
                token.ExpiresAtUtc,
                profileId,
                user.DisplayName),
            "Login successful.");
    }

    private async Task<long?> ResolveProfileIdAsync(long userId, AppRole role, CancellationToken cancellationToken)
    {
        return role switch
        {
            AppRole.Candidate => await dbContext.CandidateProfiles
                .Where(x => x.UserId == userId)
                .Select(x => (long?)x.Id)
                .FirstOrDefaultAsync(cancellationToken),
            AppRole.Recruiter => await dbContext.RecruiterProfiles
                .Where(x => x.UserId == userId)
                .Select(x => (long?)x.Id)
                .FirstOrDefaultAsync(cancellationToken),
            _ => null,
        };
    }
}
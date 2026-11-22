using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.SqlServer;
using JobLens.Api.Security;
using JobLens.Api.Setup;
using JobLens.Application.Interfaces;
using JobLens.Domain.Entities;
using JobLens.Domain.Enums;
using JobLens.Infrastructure.AI;
using JobLens.Infrastructure.BackgroundJobs;
using JobLens.Infrastructure.Configuration;
using JobLens.Infrastructure.Persistence;
using JobLens.Infrastructure.Security;
using JobLens.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.EntityFrameworkCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
var isDevelopment = builder.Environment.IsDevelopment();

builder.WebHost.UseUrls("http://0.0.0.0:5245");

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 25 * 1024 * 1024;
});
builder.Services.Configure<AiBackendOptions>(builder.Configuration.GetSection(AiBackendOptions.SectionName));
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));

var configuredConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
var connectionString = string.IsNullOrWhiteSpace(configuredConnectionString)
    ? "Server=(localdb)\\MSSQLLocalDB;Database=GPDot;Integrated Security=True;TrustServerCertificate=True;"
    : configuredConnectionString;

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
var jwtSigningKeyFromEnv = Environment.GetEnvironmentVariable("JWT_SIGNING_KEY");
if (!string.IsNullOrWhiteSpace(jwtSigningKeyFromEnv))
{
    jwtOptions.SigningKey = jwtSigningKeyFromEnv.Trim();
}

var effectiveSigningKey = jwtOptions.SigningKey;
builder.Services.PostConfigure<JwtOptions>(options => options.SigningKey = effectiveSigningKey);

if (!isDevelopment)
{
    string[] placeholderMarkers = ["change-me", "your_super_secret", "dev_super_secret", "replace_with"];
    if (string.IsNullOrWhiteSpace(effectiveSigningKey) ||
        effectiveSigningKey.Length < 32 ||
        placeholderMarkers.Any(marker => effectiveSigningKey.Contains(marker, StringComparison.OrdinalIgnoreCase)))
    {
        throw new InvalidOperationException("A random JWT signing key of at least 32 characters must be provided via JWT_SIGNING_KEY outside Development.");
    }

    var aiApiKey = builder.Configuration["AIBackend:ApiKey"];
    if (string.IsNullOrWhiteSpace(aiApiKey) || placeholderMarkers.Any(marker => aiApiKey.Contains(marker, StringComparison.OrdinalIgnoreCase)))
    {
        throw new InvalidOperationException("AIBackend__ApiKey must be set to the shared internal API key outside Development.");
    }
}
else if (string.IsNullOrWhiteSpace(effectiveSigningKey))
{
    throw new InvalidOperationException("Jwt:SigningKey or JWT_SIGNING_KEY must be configured.");
}

builder.Services.AddDbContext<JobLensDbContext>(options =>
{
    options.UseSqlServer(
        connectionString,
        sqlServer => sqlServer.EnableRetryOnFailure())
    .ConfigureWarnings(warnings =>
        warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(effectiveSigningKey)),
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;

                if (!string.IsNullOrWhiteSpace(accessToken) &&
                    (path.StartsWithSegments("/hubs/interview") || path.StartsWithSegments("/hubs/chat")))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                var tokenType = context.Principal?.FindFirstValue(TokenService.TokenTypeClaim);
                if (!string.IsNullOrEmpty(tokenType) && !string.Equals(tokenType, TokenService.AccessTokenType, StringComparison.Ordinal))
                {
                    context.Fail("Only access tokens can be used for authentication.");
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddCors(options =>
{
    options.AddPolicy(
        "Frontend",
        policy => policy
            .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:4200", "http://localhost:4000"])
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(RateLimitPolicies.Auth, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    options.AddPolicy(RateLimitPolicies.Ai, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
            }));
});

builder.Services.AddHangfire(config =>
{
    config.UseSqlServerStorage(
        connectionString,
        new SqlServerStorageOptions
        {
            PrepareSchemaIfNecessary = true,
            QueuePollInterval = TimeSpan.FromSeconds(15),
            CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
            SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
            UseRecommendedIsolationLevel = true,
            DisableGlobalLocks = true,
        });
});
builder.Services.AddHangfireServer();

builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = isDevelopment;
    options.MaximumReceiveMessageSize = 5 * 1024 * 1024;
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddTransient<CorrelationIdHandler>();

builder.Services.AddHttpClient<IAiBackendClient, AiBackendClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiBackendOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/'));
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

    if (!string.IsNullOrWhiteSpace(options.ApiKey))
    {
        client.DefaultRequestHeaders.Add("X-API-Key", options.ApiKey);
    }
})
.AddHttpMessageHandler<CorrelationIdHandler>()
.AddStandardResilienceHandler(options =>
{
    options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(5);
    options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
    options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(10);
});

builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ICandidateService, CandidateService>();
builder.Services.AddScoped<IRecruiterService, RecruiterService>();
builder.Services.AddScoped<IResumeService, ResumeService>();
builder.Services.AddScoped<IJobService, JobService>();
builder.Services.AddScoped<IApplicationService, ApplicationService>();
builder.Services.AddScoped<IInterviewService, InterviewService>();
builder.Services.AddScoped<IAdminService, AdminService>();
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddSingleton<IPasswordHasher, PasswordHasher>();
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddSingleton<IContentHashService, ContentHashService>();
builder.Services.AddScoped<IFileStorageService, LocalFileStorageService>();
builder.Services.AddScoped<IEmailDeliveryService, EmailDeliveryService>();

builder.Services.AddScoped<ResumeWorkflowJob>();
builder.Services.AddScoped<RecommendationRefreshJob>();
builder.Services.AddScoped<JobScrapingJob>();
builder.Services.AddScoped<JobCleanupJob>();
builder.Services.AddScoped<AtsScoringJob>();

var app = builder.Build();

app.UseForwardedHeaders();

app.Use(async (context, next) =>
{
    var incoming = context.Request.Headers["X-Correlation-Id"].ToString().Trim();
    var correlationId = incoming.Length is > 0 and <= 64 && incoming.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')
        ? incoming
        : Guid.NewGuid().ToString("N");

    context.TraceIdentifier = correlationId;
    context.Response.Headers["X-Correlation-Id"] = correlationId;

    using (app.Logger.BeginScope(new Dictionary<string, object?>
    {
        ["CorrelationId"] = correlationId,
    }))
    {
        await next();
    }
});

var scrapeCron = builder.Configuration["BackgroundJobs:ScrapeCron"] ?? "0 */6 * * *";
var cleanupCron = builder.Configuration["BackgroundJobs:CleanupCron"] ?? "0 2 * * *";
var recommendationCron = builder.Configuration["BackgroundJobs:RecommendationRefreshCron"] ?? "0 */8 * * *";
var atsRetryCron = builder.Configuration["BackgroundJobs:AtsRetryCron"] ?? "*/30 * * * *";
var staleAfterDays = 30;
if (int.TryParse(builder.Configuration["BackgroundJobs:CleanupStaleAfterDays"], out var configuredStaleAfterDays) && configuredStaleAfterDays > 0)
{
    staleAfterDays = configuredStaleAfterDays;
}

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<JobLensDbContext>();
    var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

    const int maxMigrationAttempts = 18;
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await dbContext.Database.MigrateAsync();
            break;
        }
        catch (Exception ex) when (attempt < maxMigrationAttempts)
        {
            app.Logger.LogWarning(
                "Database is not ready yet (attempt {Attempt}/{MaxAttempts}): {Message}. Retrying in 10 seconds.",
                attempt,
                maxMigrationAttempts,
                ex.Message);
            dbContext.ChangeTracker.Clear();
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            if (!isDevelopment)
            {
                app.Logger.LogCritical(ex, "Database migration failed on startup.");
                throw;
            }

            app.Logger.LogWarning(ex, "Database migration skipped or encountered warnings on startup.");
            break;
        }
    }

    try
    {
        await dbContext.Database.ExecuteSqlRawAsync(@"
            IF OBJECT_ID(N'dbo.ParsedResumeResults', N'U') IS NOT NULL
            BEGIN
                IF COL_LENGTH(N'dbo.ParsedResumeResults', N'Confidence') IS NULL
                    ALTER TABLE [dbo].[ParsedResumeResults] ADD [Confidence] FLOAT NULL;
                IF COL_LENGTH(N'dbo.ParsedResumeResults', N'Headline') IS NULL
                    ALTER TABLE [dbo].[ParsedResumeResults] ADD [Headline] NVARCHAR(MAX) NULL;
                IF COL_LENGTH(N'dbo.ParsedResumeResults', N'Summary') IS NULL
                    ALTER TABLE [dbo].[ParsedResumeResults] ADD [Summary] NVARCHAR(MAX) NULL;
            END

            IF OBJECT_ID(N'dbo.Jobs', N'U') IS NOT NULL
            BEGIN
                IF COL_LENGTH(N'dbo.Jobs', N'SalaryMin') IS NULL
                    ALTER TABLE [dbo].[Jobs] ADD [SalaryMin] DECIMAL(18,2) NULL;
                IF COL_LENGTH(N'dbo.Jobs', N'SalaryMax') IS NULL
                    ALTER TABLE [dbo].[Jobs] ADD [SalaryMax] DECIMAL(18,2) NULL;
            END

            IF OBJECT_ID(N'dbo.ChatAttachments', N'U') IS NOT NULL
            BEGIN
                IF COL_LENGTH(N'dbo.ChatAttachments', N'UploadedByUserId') IS NULL
                    ALTER TABLE [dbo].[ChatAttachments] ADD [UploadedByUserId] BIGINT NULL;
                IF COL_LENGTH(N'dbo.ChatAttachments', N'StorageKey') IS NULL
                    ALTER TABLE [dbo].[ChatAttachments] ADD [StorageKey] NVARCHAR(MAX) NOT NULL CONSTRAINT [DF_ChatAttachments_StorageKey] DEFAULT N'';
            END
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Schema synchronization encountered an issue; proceeding with startup.");
    }

    var seedAdminEnabled = builder.Configuration.GetValue<bool?>("Security:SeedAdminEnabled") ?? false;
    var seedAdminPassword = builder.Configuration["Security:SeedAdminPassword"];
    if (seedAdminEnabled && !string.IsNullOrWhiteSpace(seedAdminPassword))
    {
        var seedAdminEmail = (builder.Configuration["Security:SeedAdminEmail"] ?? "admin@joblens.local").Trim().ToLowerInvariant();
        var seedAdminDisplayName = builder.Configuration["Security:SeedAdminDisplayName"] ?? "System Admin";

        var existingAdmin = await dbContext.Users.FirstOrDefaultAsync(x => x.Email == seedAdminEmail);
        if (existingAdmin is null)
        {
            dbContext.Users.Add(new User
            {
                Email = seedAdminEmail,
                DisplayName = seedAdminDisplayName,
                PasswordHash = passwordHasher.Hash(seedAdminPassword),
                Role = AppRole.Admin,
                IsActive = true,
            });

            await dbContext.SaveChangesAsync();
        }
    }
    else if (seedAdminEnabled)
    {
        app.Logger.LogWarning("Admin seeding is enabled but Security:SeedAdminPassword is empty; no admin account was created.");
    }

    await DemoDataSeeder.SeedAsync(dbContext, passwordHasher, builder.Configuration, app.Logger);

    var recurringJobs = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    recurringJobs.AddOrUpdate<JobScrapingJob>(
        "joblens-scrape-jobs",
        job => job.RunAsync(null),
        scrapeCron);

    recurringJobs.AddOrUpdate<JobCleanupJob>(
        "joblens-cleanup-jobs",
        job => job.CleanupAsync(staleAfterDays),
        cleanupCron);

    recurringJobs.AddOrUpdate<RecommendationRefreshJob>(
        "joblens-refresh-recommendations",
        job => job.RefreshAllAsync(),
        recommendationCron);

    recurringJobs.AddOrUpdate<AtsScoringJob>(
        "joblens-retry-pending-ats",
        job => job.RequeuePendingAsync(),
        atsRetryCron);
}

app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapHangfireDashboard(
    "/hangfire",
    new DashboardOptions
    {
        Authorization = [new AdminHangfireDashboardAuthorizationFilter()],
    });

app.MapHub<JobLens.Api.Hubs.InterviewHub>("/hubs/interview");
app.MapHub<JobLens.Api.Hubs.ChatHub>("/hubs/chat");

app.MapGet("/", () => Results.Ok(new
{
    service = "JobLens Gateway",
    version = "1.0.0",
    status = "running",
}));

app.Run();

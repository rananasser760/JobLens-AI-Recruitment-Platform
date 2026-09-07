using System.Text;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.SqlServer;
using JobLens.Api.Security;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.EntityFrameworkCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Ensure Kestrel binds to all network interfaces inside Docker
builder.WebHost.UseUrls("http://0.0.0.0:5245");

// Align Kestrel payload limit with Nginx (25MB) for heavy resume and frame uploads
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 25 * 1024 * 1024;
});
builder.Services.Configure<AiBackendOptions>(builder.Configuration.GetSection(AiBackendOptions.SectionName));
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Server=(localdb)\\MSSQLLocalDB;Database=GPDot;Integrated Security=True;TrustServerCertificate=True;";
var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
var jwtSigningKeyFromEnv = Environment.GetEnvironmentVariable("JWT_SIGNING_KEY");
if (!string.IsNullOrWhiteSpace(jwtSigningKeyFromEnv))
{
    jwtOptions.SigningKey = jwtSigningKeyFromEnv;
}

if (builder.Environment.IsProduction())
{
    if (string.IsNullOrWhiteSpace(jwtOptions.SigningKey) ||
        jwtOptions.SigningKey.Contains("change-me", StringComparison.OrdinalIgnoreCase) ||
        jwtOptions.SigningKey.Contains("your_super_secret", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("A secure JWT signing key must be provided via JWT_SIGNING_KEY in production.");
    }
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
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;

                // Bind JWT token automatically for WebSocket SignalR hubs
                if (!string.IsNullOrWhiteSpace(accessToken) && 
                    (path.StartsWithSegments("/hubs/interview") || path.StartsWithSegments("/hubs/chat")))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            }
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
    options.EnableDetailedErrors = true;
    options.MaximumReceiveMessageSize = 5 * 1024 * 1024; // 5 MB for large base64 frames
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

var app = builder.Build();

app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers.TryGetValue("X-Correlation-Id", out var incoming)
        && !string.IsNullOrWhiteSpace(incoming.ToString())
        ? incoming.ToString().Trim()
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
var staleAfterDays = 30;
if (int.TryParse(builder.Configuration["BackgroundJobs:CleanupStaleAfterDays"], out var configuredStaleAfterDays) && configuredStaleAfterDays > 0)
{
    staleAfterDays = configuredStaleAfterDays;
}

// Database initialization and Hangfire Seed Data
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<JobLensDbContext>();
    var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
    
    // Automatically apply Entity Framework Core migrations on startup
    try
    {
        await dbContext.Database.MigrateAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Database migration skipped or encountered warnings on startup.");
    }

// Ensure newly added entity properties exist in SQL Server
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
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Schema synchronization encountered an issue; proceeding with startup.");
    }

    var seedAdminEnabled = builder.Configuration.GetValue<bool?>("Security:SeedAdminEnabled") ?? true;
    if (seedAdminEnabled)
    {
        var seedAdminEmail = (builder.Configuration["Security:SeedAdminEmail"] ?? "admin@gmail.com").Trim().ToLowerInvariant();
        var seedAdminPassword = builder.Configuration["Security:SeedAdminPassword"] ?? "Admin@123";
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

    // Register Background Jobs
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
}

app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHangfireDashboard(
    "/hangfire",
    new DashboardOptions
    {
        Authorization = [new AdminHangfireDashboardAuthorizationFilter()],
    });

// Map Real-time WebSockets
app.MapHub<JobLens.Api.Hubs.InterviewHub>("/hubs/interview");
app.MapHub<JobLens.Api.Hubs.ChatHub>("/hubs/chat");

app.MapGet("/", () => Results.Ok(new
{
    service = "JobLens Gateway",
    version = "1.0.0",
    status = "running",
}));

app.Run();
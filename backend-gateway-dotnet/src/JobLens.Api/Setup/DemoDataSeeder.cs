using JobLens.Application.Interfaces;
using JobLens.Domain.Entities;
using JobLens.Domain.Enums;
using JobLens.Infrastructure.Persistence;
using JobLens.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace JobLens.Api.Setup;

public static class DemoDataSeeder
{
    private const string DemoCompanySlug = "joblens-demo";

    public static async Task SeedAsync(JobLensDbContext dbContext, IPasswordHasher passwordHasher, IConfiguration configuration, ILogger logger)
    {
        if (!(configuration.GetValue<bool?>("Demo:SeedEnabled") ?? false))
        {
            return;
        }

        var password = configuration["Demo:Password"];
        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("Demo seeding is enabled but Demo:Password is empty; demo accounts were not created.");
            return;
        }

        var recruiterEmail = (configuration["Demo:RecruiterEmail"] ?? "recruiter@demo.joblens").Trim().ToLowerInvariant();
        var candidateEmail = (configuration["Demo:CandidateEmail"] ?? "candidate@demo.joblens").Trim().ToLowerInvariant();

        var company = await dbContext.Companies.FirstOrDefaultAsync(x => x.Slug == DemoCompanySlug);
        if (company is null)
        {
            company = new Company
            {
                Name = "JobLens Demo Co.",
                Slug = DemoCompanySlug,
                Description = "A sample company used to showcase the JobLens recruitment workflow.",
                Industry = "Software",
                Location = "Cairo, Egypt",
                Size = 50,
            };
            dbContext.Companies.Add(company);
            await dbContext.SaveChangesAsync();
        }

        if (!await dbContext.Users.AnyAsync(x => x.Email == recruiterEmail))
        {
            var recruiter = new User
            {
                Email = recruiterEmail,
                DisplayName = "Demo Recruiter",
                PasswordHash = passwordHasher.Hash(password),
                Role = AppRole.Recruiter,
                IsActive = true,
            };
            dbContext.Users.Add(recruiter);
            await dbContext.SaveChangesAsync();

            dbContext.RecruiterProfiles.Add(new RecruiterProfile
            {
                UserId = recruiter.Id,
                CompanyId = company.Id,
                JobTitle = "Talent Acquisition Lead",
            });
        }

        if (!await dbContext.Users.AnyAsync(x => x.Email == candidateEmail))
        {
            var candidate = new User
            {
                Email = candidateEmail,
                DisplayName = "Demo Candidate",
                PasswordHash = passwordHasher.Hash(password),
                Role = AppRole.Candidate,
                IsActive = true,
            };
            dbContext.Users.Add(candidate);
            await dbContext.SaveChangesAsync();

            dbContext.CandidateProfiles.Add(new CandidateProfile
            {
                UserId = candidate.Id,
                Headline = "Junior Software Engineer",
                Location = "Cairo, Egypt",
                SkillsJson = ServiceJson.Serialize(new[] { "Python", "C#", "SQL", "Angular" }),
                YearsExperience = 1,
            });
        }

        await dbContext.SaveChangesAsync();

        if (!await dbContext.Jobs.AnyAsync(x => x.CompanyId == company.Id))
        {
            dbContext.Jobs.AddRange(
                BuildJob(
                    company.Id,
                    "Junior Backend Engineer (.NET)",
                    "Build and maintain ASP.NET Core APIs, design SQL Server schemas, and integrate background jobs for a recruitment platform.",
                    "Solid C# and ASP.NET Core fundamentals, REST API design, Entity Framework Core, SQL.",
                    ["C#", "ASP.NET Core", "Entity Framework Core", "SQL Server", "REST"]),
                BuildJob(
                    company.Id,
                    "Machine Learning Engineer (NLP)",
                    "Develop NLP pipelines for CV parsing and semantic job matching using transformer models and vector databases.",
                    "Python, PyTorch or Hugging Face, sentence embeddings, vector search, prompt engineering.",
                    ["Python", "NLP", "PyTorch", "Hugging Face", "ChromaDB"]));

            await dbContext.SaveChangesAsync();
        }

        logger.LogInformation("Demo data is ready (recruiter: {RecruiterEmail}, candidate: {CandidateEmail}).", recruiterEmail, candidateEmail);
    }

    private static JobPosting BuildJob(long companyId, string title, string description, string requirements, string[] skills) => new()
    {
        CompanyId = companyId,
        SourceType = JobSourceType.Internal,
        Title = title,
        Description = description,
        Requirements = requirements,
        Location = "Cairo, Egypt (Hybrid)",
        EmploymentType = "FullTime",
        ExperienceLevel = "Junior",
        SkillsJson = ServiceJson.Serialize(skills),
        InterviewDefaultsJson = ServiceJson.Serialize(new
        {
            agentType = "Mixed",
            maxQuestions = 5,
            evaluationCriteria = "Technical depth, problem solving, and clear communication.",
            focusSkills = skills,
            questionTimeLimitSeconds = (int?)null,
            totalInterviewDurationMinutes = (int?)null,
            proctoringStrictness = "Medium",
        }),
        PostedAtUtc = DateTime.UtcNow,
        IsActive = true,
    };
}

using Microsoft.EntityFrameworkCore;
using GP_Backend.Models.Entities;

namespace GP_Backend.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    // Core entities
    public DbSet<User> Users { get; set; }
    public DbSet<Candidate> Candidates { get; set; }
    public DbSet<Recruiter> Recruiters { get; set; }
    public DbSet<Company> Companies { get; set; }

    // Resume related
    public DbSet<Resume> Resumes { get; set; }
    public DbSet<ResumeParsingResult> ResumeParsingResults { get; set; }

    // Job related
    public DbSet<Job> Jobs { get; set; }
    public DbSet<JobSkill> JobSkills { get; set; }

    // Application related
    public DbSet<Application> Applications { get; set; }
    public DbSet<AutoApplyLog> AutoApplyLogs { get; set; }

    // Interview related
    public DbSet<InterviewSession> InterviewSessions { get; set; }
    public DbSet<InterviewQuestion> InterviewQuestions { get; set; }
    public DbSet<InterviewAnswer> InterviewAnswers { get; set; }
    public DbSet<VideoRecording> VideoRecordings { get; set; }
    public DbSet<CheatingEvent> CheatingEvents { get; set; }
    public DbSet<BrowserEvent> BrowserEvents { get; set; }

    // Skills & Rankings
    public DbSet<CandidateSkill> CandidateSkills { get; set; }
    public DbSet<CandidateRanking> CandidateRankings { get; set; }

    // Email & Audit
    public DbSet<EmailSend> EmailSends { get; set; }
    public DbSet<AuditLog> AuditLogs { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // User configuration
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(e => e.Email).IsUnique();
            entity.HasIndex(e => e.Username).IsUnique();
        });

        // Candidate - User (1:1)
        modelBuilder.Entity<Candidate>(entity =>
        {
            entity.HasIndex(e => e.UserId).IsUnique();
            entity.HasOne(c => c.User)
                  .WithOne(u => u.Candidate)
                  .HasForeignKey<Candidate>(c => c.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Recruiter - User (1:1)
        modelBuilder.Entity<Recruiter>(entity =>
        {
            entity.HasIndex(e => e.UserId).IsUnique();
            entity.HasOne(r => r.User)
                  .WithOne(u => u.Recruiter)
                  .HasForeignKey<Recruiter>(r => r.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(r => r.Company)
                  .WithMany(c => c.Recruiters)
                  .HasForeignKey(r => r.CompanyId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        // Resume - Candidate
        modelBuilder.Entity<Resume>(entity =>
        {
            entity.HasOne(r => r.Candidate)
                  .WithMany(c => c.Resumes)
                  .HasForeignKey(r => r.CandidateId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ResumeParsingResult - Resume (1:1)
        modelBuilder.Entity<ResumeParsingResult>(entity =>
        {
            entity.HasIndex(e => e.ResumeId).IsUnique();
            entity.HasOne(rp => rp.Resume)
                  .WithOne(r => r.ParsingResult)
                  .HasForeignKey<ResumeParsingResult>(rp => rp.ResumeId)
                  .OnDelete(DeleteBehavior.Cascade);
        });


        // Job configuration
        modelBuilder.Entity<Job>(entity =>
        {
            entity.HasIndex(e => e.ExternalJobId);
            entity.HasIndex(e => e.PostedAt);
            entity.HasIndex(e => e.IsActive);

            // Specify decimal precision for salary fields
            entity.Property(j => j.SalaryMin).HasPrecision(18, 2);
            entity.Property(j => j.SalaryMax).HasPrecision(18, 2);

            entity.HasOne(j => j.Recruiter)
                  .WithMany(r => r.Jobs)
                  .HasForeignKey(j => j.RecruiterId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(j => j.Company)
                  .WithMany(c => c.Jobs)
                  .HasForeignKey(j => j.CompanyId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        // Application configuration
        modelBuilder.Entity<Application>(entity =>
        {
            entity.HasIndex(e => new { e.CandidateId, e.JobId }).IsUnique();

            entity.HasOne(a => a.Candidate)
                  .WithMany(c => c.Applications)
                  .HasForeignKey(a => a.CandidateId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.Job)
                  .WithMany(j => j.Applications)
                  .HasForeignKey(a => a.JobId)
                  .OnDelete(DeleteBehavior.Cascade);

            // NoAction to avoid multiple cascade paths through Candidate -> Resume -> Application
            entity.HasOne(a => a.Resume)
                  .WithMany(r => r.Applications)
                  .HasForeignKey(a => a.ResumeId)
                  .OnDelete(DeleteBehavior.NoAction);
        });

        // AutoApplyLog
        modelBuilder.Entity<AutoApplyLog>(entity =>
        {
            entity.HasOne(a => a.Application)
                  .WithMany(app => app.AutoApplyLogs)
                  .HasForeignKey(a => a.ApplicationId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Interview Session
        modelBuilder.Entity<InterviewSession>(entity =>
        {
                  entity.HasIndex(i => i.IntegritySessionId);
                  entity.HasIndex(i => i.InterviewBackendSessionId);

            entity.HasOne(i => i.Application)
                  .WithMany(a => a.InterviewSessions)
                  .HasForeignKey(i => i.ApplicationId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Interview Question
        modelBuilder.Entity<InterviewQuestion>(entity =>
        {
            entity.HasOne(q => q.Session)
                  .WithMany(s => s.Questions)
                  .HasForeignKey(q => q.SessionId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Interview Answer
        modelBuilder.Entity<InterviewAnswer>(entity =>
        {
            entity.HasIndex(e => e.QuestionId).IsUnique();

            entity.HasOne(a => a.Question)
                  .WithOne(q => q.Answer)
                  .HasForeignKey<InterviewAnswer>(a => a.QuestionId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.Session)
                  .WithMany()
                  .HasForeignKey(a => a.SessionId)
                  .OnDelete(DeleteBehavior.NoAction);
        });

        // Video Recording
        modelBuilder.Entity<VideoRecording>(entity =>
        {
            entity.HasOne(v => v.Session)
                  .WithMany(s => s.VideoRecordings)
                  .HasForeignKey(v => v.SessionId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Cheating Event
        modelBuilder.Entity<CheatingEvent>(entity =>
        {
            entity.HasOne(c => c.Session)
                  .WithMany(s => s.CheatingEvents)
                  .HasForeignKey(c => c.SessionId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Browser Event
        modelBuilder.Entity<BrowserEvent>(entity =>
        {
            entity.HasOne(b => b.Session)
                  .WithMany(s => s.BrowserEvents)
                  .HasForeignKey(b => b.SessionId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Candidate Skill
        modelBuilder.Entity<CandidateSkill>(entity =>
        {
            entity.HasIndex(e => new { e.CandidateId, e.SkillName }).IsUnique();

            entity.HasOne(cs => cs.Candidate)
                  .WithMany(c => c.Skills)
                  .HasForeignKey(cs => cs.CandidateId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Job Skill
        modelBuilder.Entity<JobSkill>(entity =>
        {
            entity.HasIndex(e => new { e.JobId, e.SkillName }).IsUnique();

            entity.HasOne(js => js.Job)
                  .WithMany(j => j.RequiredSkills)
                  .HasForeignKey(js => js.JobId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Candidate Ranking
        modelBuilder.Entity<CandidateRanking>(entity =>
        {
            entity.HasIndex(e => new { e.JobId, e.CandidateId }).IsUnique();

            entity.HasOne(cr => cr.Job)
                  .WithMany(j => j.CandidateRankings)
                  .HasForeignKey(cr => cr.JobId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(cr => cr.Candidate)
                  .WithMany(c => c.Rankings)
                  .HasForeignKey(cr => cr.CandidateId)
                  .OnDelete(DeleteBehavior.NoAction);
        });

        // Email Send
        modelBuilder.Entity<EmailSend>(entity =>
        {
            entity.HasOne(e => e.Application)
                  .WithMany(a => a.EmailSends)
                  .HasForeignKey(e => e.RelatedApplicationId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        // Audit Log
        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.Entity);

            entity.HasOne(a => a.User)
                  .WithMany(u => u.AuditLogs)
                  .HasForeignKey(a => a.UserId)
                  .OnDelete(DeleteBehavior.SetNull);
        });
    }
}

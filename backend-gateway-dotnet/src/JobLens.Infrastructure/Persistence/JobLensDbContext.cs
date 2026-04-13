using JobLens.Domain.Entities;
using JobLens.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace JobLens.Infrastructure.Persistence;

public sealed class JobLensDbContext(DbContextOptions<JobLensDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<CandidateProfile> CandidateProfiles => Set<CandidateProfile>();
    public DbSet<RecruiterProfile> RecruiterProfiles => Set<RecruiterProfile>();
    public DbSet<Resume> Resumes => Set<Resume>();
    public DbSet<ParsedResumeResult> ParsedResumeResults => Set<ParsedResumeResult>();
    public DbSet<JobPosting> Jobs => Set<JobPosting>();
    public DbSet<JobApplication> Applications => Set<JobApplication>();
    public DbSet<AtsAssessment> AtsAssessments => Set<AtsAssessment>();
    public DbSet<InterviewSession> InterviewSessions => Set<InterviewSession>();
    public DbSet<InterviewTranscriptSegment> InterviewTranscriptSegments => Set<InterviewTranscriptSegment>();
    public DbSet<BrowserTelemetryEvent> BrowserTelemetryEvents => Set<BrowserTelemetryEvent>();
    public DbSet<ProctoringEvent> ProctoringEvents => Set<ProctoringEvent>();
    public DbSet<InterviewReport> InterviewReports => Set<InterviewReport>();
    public DbSet<RecommendationCacheEntry> RecommendationCacheEntries => Set<RecommendationCacheEntry>();
    public DbSet<VectorIndexEntry> VectorIndexEntries => Set<VectorIndexEntry>();
    public DbSet<BackgroundJobState> BackgroundJobStates => Set<BackgroundJobState>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(x => x.Email).IsUnique();
            entity.Property(x => x.Role).HasConversion<string>();
        });

        modelBuilder.Entity<Company>(entity =>
        {
            entity.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<CandidateProfile>(entity =>
        {
            entity.HasIndex(x => x.UserId).IsUnique();
            entity.HasOne(x => x.User)
                .WithOne(x => x.CandidateProfile)
                .HasForeignKey<CandidateProfile>(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RecruiterProfile>(entity =>
        {
            entity.HasIndex(x => x.UserId).IsUnique();
            entity.HasOne(x => x.User)
                .WithOne(x => x.RecruiterProfile)
                .HasForeignKey<RecruiterProfile>(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Resume>(entity =>
        {
            entity.HasIndex(x => new { x.CandidateProfileId, x.IsDefault });
        });

        modelBuilder.Entity<ParsedResumeResult>(entity =>
        {
            entity.HasIndex(x => x.ResumeId).IsUnique();
            entity.HasOne(x => x.Resume)
                .WithOne(x => x.ParsedResumeResult)
                .HasForeignKey<ParsedResumeResult>(x => x.ResumeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<JobPosting>(entity =>
        {
            entity.Property(x => x.SourceType).HasConversion<string>();
            entity.HasIndex(x => new { x.ExternalJobId, x.SourceUrl });
            entity.HasIndex(x => x.PostedAtUtc);
            entity.HasIndex(x => x.IsActive);
        });

        modelBuilder.Entity<JobApplication>(entity =>
        {
            entity.Property(x => x.Status).HasConversion<string>();
            entity.HasIndex(x => new { x.CandidateProfileId, x.JobPostingId }).IsUnique();

            entity.HasOne(x => x.Resume)
                .WithMany(x => x.Applications)
                .HasForeignKey(x => x.ResumeId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<InterviewSession>(entity =>
        {
            entity.Property(x => x.Status).HasConversion<string>();
            entity.HasIndex(x => x.InterviewBackendSessionId);
            entity.HasIndex(x => x.IntegrityBackendSessionId);
        });

        modelBuilder.Entity<InterviewTranscriptSegment>(entity =>
        {
            entity.HasIndex(x => new { x.InterviewSessionId, x.Sequence });
        });

        modelBuilder.Entity<RecommendationCacheEntry>(entity =>
        {
            entity.Property(x => x.SubjectType).HasConversion<string>();
            entity.Property(x => x.TargetType).HasConversion<string>();
            entity.HasIndex(x => new { x.SubjectType, x.SubjectId, x.TargetType, x.TargetId }).IsUnique();
        });

        modelBuilder.Entity<VectorIndexEntry>(entity =>
        {
            entity.Property(x => x.Status).HasConversion<string>();
            entity.HasIndex(x => new { x.EntityType, x.EntityId }).IsUnique();
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasOne(x => x.ActorUser)
                .WithMany(x => x.AuditLogs)
                .HasForeignKey(x => x.ActorUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var utcNow = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is not JobLens.Domain.Common.BaseEntity auditable)
            {
                continue;
            }

            if (entry.State == EntityState.Added)
            {
                auditable.CreatedAtUtc = utcNow;
            }

            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                auditable.UpdatedAtUtc = utcNow;
            }
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}

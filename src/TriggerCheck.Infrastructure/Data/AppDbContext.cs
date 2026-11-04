using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Identity;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<MonitorEntity> Monitors => Set<MonitorEntity>();
    public DbSet<CheckResult> CheckResults => Set<CheckResult>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<AlertChannel> AlertChannels => Set<AlertChannel>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<SmsVoiceUsage> SmsVoiceUsage => Set<SmsVoiceUsage>();
    public DbSet<AiDecision> AiDecisions => Set<AiDecision>();
    public DbSet<MonitorBaseline> MonitorBaselines => Set<MonitorBaseline>();
    public DbSet<AnomalySignal> AnomalySignals => Set<AnomalySignal>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Plan>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.MonthlyPrice).HasConversion<double>();
            e.Property(x => x.AiFeatures).HasMaxLength(500);
        });

        b.Entity<Customer>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.ContactEmail).HasMaxLength(256);
            e.HasOne(x => x.Plan).WithMany(p => p.Customers).HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ApplicationUser>(e =>
        {
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MonitorEntity>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Target).HasMaxLength(2048).IsRequired();
            e.Property(x => x.HttpMethod).HasMaxLength(10);
            e.Property(x => x.ExpectedStatusCodes).HasMaxLength(100);
            e.Property(x => x.CertSubject).HasMaxLength(500);
            e.Property(x => x.CertIssuer).HasMaxLength(500);
            e.Property(x => x.CertError).HasMaxLength(500);
            e.HasOne(x => x.Customer).WithMany(c => c.Monitors).HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.IsEnabled, x.NextCheckAtUtc });
            e.Property(x => x.Version).IsConcurrencyToken();
        });

        b.Entity<CheckResult>(e =>
        {
            e.HasOne(x => x.Monitor).WithMany(m => m.CheckResults).HasForeignKey(x => x.MonitorId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.MonitorId, x.CheckedAtUtc });
            e.Property(x => x.Error).HasMaxLength(1000);
        });

        b.Entity<Incident>(e =>
        {
            e.HasOne(x => x.Monitor).WithMany(m => m.Incidents).HasForeignKey(x => x.MonitorId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.MonitorId, x.StartedAtUtc });
            e.Ignore(x => x.IsOpen);
        });

        b.Entity<AlertChannel>(e =>
        {
            e.HasOne(x => x.Customer).WithMany(c => c.AlertChannels).HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Target).HasMaxLength(2048).IsRequired();
            e.Property(x => x.Secret).HasMaxLength(512);
        });

        b.Entity<SmsVoiceUsage>(e =>
        {
            e.HasKey(x => new { x.CustomerId, x.DayUtc });
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Notification>(e =>
        {
            e.HasIndex(x => new { x.MonitorId, x.SentAtUtc });
        });

        // ---- AI ----
        b.Entity<AiDecision>(e =>
        {
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Feature).HasMaxLength(64).IsRequired();
            e.Property(x => x.SubjectType).HasMaxLength(64);
            e.Property(x => x.SubjectId).HasMaxLength(64);
            e.Property(x => x.InputHash).HasMaxLength(64);
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.Effort).HasMaxLength(20);
            e.Property(x => x.ErrorCode).HasMaxLength(64);
            e.Property(x => x.FeedbackByUserId).HasMaxLength(450);
            e.Property(x => x.CostEstimate).HasConversion<double>();
            e.HasIndex(x => new { x.CustomerId, x.CreatedAtUtc });
            e.HasIndex(x => new { x.Feature, x.SubjectType, x.SubjectId, x.InputHash });
        });

        b.Entity<MonitorBaseline>(e =>
        {
            e.HasKey(x => new { x.MonitorId, x.Bucket });
            e.HasOne<MonitorEntity>().WithMany().HasForeignKey(x => x.MonitorId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.CustomerId);
        });

        b.Entity<AnomalySignal>(e =>
        {
            e.HasOne<MonitorEntity>().WithMany().HasForeignKey(x => x.MonitorId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Metric).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.MonitorId, x.EndedAtUtc });
            e.HasIndex(x => new { x.CustomerId, x.StartedAtUtc });
            e.Ignore(x => x.IsOpen);
        });

        b.Entity<ApiKey>(e =>
        {
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Prefix).HasMaxLength(32).IsRequired();
            e.HasIndex(x => x.Prefix).IsUnique();
            e.Property(x => x.SecretHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(256);
            e.HasIndex(x => x.CustomerId);
            e.Ignore(x => x.IsRevoked);
        });
    }
}

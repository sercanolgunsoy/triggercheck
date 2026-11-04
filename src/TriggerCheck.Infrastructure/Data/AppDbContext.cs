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

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Plan>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.MonthlyPrice).HasConversion<double>();
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
        });

        b.Entity<Notification>(e =>
        {
            e.HasIndex(x => new { x.MonitorId, x.SentAtUtc });
        });
    }
}

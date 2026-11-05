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
    public DbSet<AiFeedback> AiFeedback => Set<AiFeedback>();
    public DbSet<MonitorBaseline> MonitorBaselines => Set<MonitorBaseline>();
    public DbSet<AnomalySignal> AnomalySignals => Set<AnomalySignal>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<PaymentEvent> PaymentEvents => Set<PaymentEvent>();
    public DbSet<EmailOutbox> EmailOutbox => Set<EmailOutbox>();
    public DbSet<MaintenanceWindow> MaintenanceWindows => Set<MaintenanceWindow>();
    public DbSet<EscalationPolicy> EscalationPolicies => Set<EscalationPolicy>();
    public DbSet<EscalationStep> EscalationSteps => Set<EscalationStep>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Region> Regions => Set<Region>();
    public DbSet<MonitorRegionState> MonitorRegionStates => Set<MonitorRegionState>();
    public DbSet<StatusPage> StatusPages => Set<StatusPage>();
    public DbSet<StatusPageMonitor> StatusPageMonitors => Set<StatusPageMonitor>();
    public DbSet<StatusPageSubscriber> StatusPageSubscribers => Set<StatusPageSubscriber>();
    public DbSet<StatusPageNote> StatusPageNotes => Set<StatusPageNote>();
    public DbSet<StatusPageEvent> StatusPageEvents => Set<StatusPageEvent>();
    public DbSet<MonthlyReportDelivery> MonthlyReportDeliveries => Set<MonthlyReportDelivery>();
    public DbSet<IncidentGroup> IncidentGroups => Set<IncidentGroup>();
    public DbSet<IncidentGroupMember> IncidentGroupMembers => Set<IncidentGroupMember>();
    public DbSet<ClusterLease> ClusterLeases => Set<ClusterLease>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Plan>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.MonthlyPrice).HasConversion<double>();
            e.Property(x => x.AiFeatures).HasMaxLength(500);
            e.Property(x => x.IyzicoProductReferenceCode).HasMaxLength(100);
            e.Property(x => x.IyzicoPricingPlanReferenceCode).HasMaxLength(100);
        });

        b.Entity<Customer>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.ContactEmail).HasMaxLength(256);
            e.Property(x => x.TrialKey).HasMaxLength(256);
            e.Property(x => x.ReportTimeZoneId).HasMaxLength(100);
            e.Property(x => x.AiLanguage).HasMaxLength(8);
            e.HasIndex(x => x.TrialKey);
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
            e.Property(x => x.JsonPath).HasMaxLength(500);
            e.Property(x => x.ExpectedValue).HasMaxLength(500);
            e.Property(x => x.HeartbeatTokenHash).HasMaxLength(64);
            e.Property(x => x.HeartbeatTokenPrefix).HasMaxLength(16);
            e.Property(x => x.LastProbeFingerprint).HasMaxLength(64);
            e.Property(x => x.LastHeartbeatMessage).HasMaxLength(200);
            e.HasIndex(x => x.HeartbeatTokenHash).IsUnique(); // ping lookup; NULLs (non-heartbeat monitors) do not collide
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
            e.HasIndex(x => x.ResolvedAtUtc); // AI event watcher: recently resolved incidents
            e.Ignore(x => x.IsOpen);
            e.Property(x => x.AcknowledgedByUserId).HasMaxLength(450);
            e.Property(x => x.AcknowledgeNote).HasMaxLength(500);
        });

        // FEAT-3: maintenance windows; the monitor list is a join table that disappears with either side.
        b.Entity<MaintenanceWindow>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Monitors).WithMany().UsingEntity("MaintenanceWindowMonitors");
            e.HasIndex(x => x.CustomerId);
        });

        // FEAT-5: one optional escalation policy per customer; step channels are a join table.
        b.Entity<EscalationPolicy>(e =>
        {
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.CustomerId).IsUnique();
            e.HasMany(x => x.Steps).WithOne(s => s.Policy).HasForeignKey(s => s.EscalationPolicyId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EscalationStep>(e =>
        {
            e.HasMany(x => x.Channels).WithMany().UsingEntity("EscalationStepChannels");
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

        b.Entity<Subscription>(e =>
        {
            e.HasOne(x => x.Customer).WithMany(c => c.Subscriptions).HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Plan).WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.ProviderReference).HasMaxLength(100);
            e.Property(x => x.ProviderCustomerReference).HasMaxLength(100);
            e.Property(x => x.CheckoutToken).HasMaxLength(200);
            e.Property(x => x.CheckoutUrl).HasMaxLength(2048);
            e.HasIndex(x => x.ProviderReference);
            e.HasIndex(x => x.CheckoutToken);
            e.HasIndex(x => new { x.CustomerId, x.Status });
            e.HasIndex(x => x.ProviderCustomerReference);
            e.Property(x => x.RefundHandledBy).HasMaxLength(256);
            e.HasIndex(x => new { x.RefundNeededAtUtc, x.RefundHandledAtUtc });
        });

        b.Entity<EmailOutbox>(e =>
        {
            e.Property(x => x.To).HasMaxLength(256).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(300).IsRequired();
            e.Property(x => x.LastError).HasMaxLength(500);
            e.HasIndex(x => new { x.SentAtUtc, x.FailedAtUtc, x.NextAttemptAtUtc });
        });

        b.Entity<PaymentEvent>(e =>
        {
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Subscription>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.ProviderEventId).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.ProviderEventId).IsUnique();
            e.Property(x => x.EventType).HasMaxLength(64);
            e.Property(x => x.OrderReference).HasMaxLength(100);
            e.Property(x => x.SubscriptionReference).HasMaxLength(100);
            e.Property(x => x.CustomerReference).HasMaxLength(100);
            e.Property(x => x.Currency).HasMaxLength(8);
            e.Property(x => x.IgnoredReason).HasMaxLength(32);
            e.HasIndex(x => new { x.Ignored, x.IgnoredReason });
            e.Property(x => x.PlanName).HasMaxLength(100);
            e.Property(x => x.Amount).HasConversion<double?>();
            e.HasIndex(x => new { x.CustomerId, x.OccurredAtUtc });
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
            e.Property(x => x.Language).HasMaxLength(8);
            e.Property(x => x.ErrorCode).HasMaxLength(64);
            e.Property(x => x.FeedbackByUserId).HasMaxLength(450);
            e.Property(x => x.CostEstimate).HasConversion<double>();
            e.HasIndex(x => new { x.CustomerId, x.CreatedAtUtc });
            e.HasIndex(x => new { x.Feature, x.SubjectType, x.SubjectId, x.InputHash });
        });

        b.Entity<AiFeedback>(e =>
        {
            e.HasKey(x => new { x.DecisionId, x.UserId });
            e.HasOne<AiDecision>().WithMany().HasForeignKey(x => x.DecisionId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.UserId).HasMaxLength(450);
            e.HasIndex(x => x.CustomerId);
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

        // FEAT-4: public status pages. Slug and custom domain are unique across all customers.
        b.Entity<StatusPage>(e =>
        {
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Title).HasMaxLength(100).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(50).IsRequired();
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Announcement).HasMaxLength(2000);
            e.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
            e.Property(x => x.CustomDomain).HasMaxLength(253);
            e.HasIndex(x => x.CustomDomain); // unverified claims may repeat; only one verified page per domain (app rule)
            e.Property(x => x.CustomDomainToken).HasMaxLength(64);
            e.HasIndex(x => x.CustomerId);
            e.HasMany(x => x.Monitors).WithOne(m => m.StatusPage).HasForeignKey(m => m.StatusPageId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<StatusPageMonitor>(e =>
        {
            e.HasOne(x => x.Monitor).WithMany().HasForeignKey(x => x.MonitorId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.DisplayName).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.StatusPageId, x.MonitorId }).IsUnique();
        });

        b.Entity<StatusPageSubscriber>(e =>
        {
            e.HasOne(x => x.StatusPage).WithMany().HasForeignKey(x => x.StatusPageId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.HasIndex(x => new { x.StatusPageId, x.Email }).IsUnique();
            e.HasIndex(x => new { x.ConfirmedAtUtc, x.CreatedAtUtc });
        });

        b.Entity<StatusPageNote>(e =>
        {
            e.HasOne(x => x.StatusPage).WithMany().HasForeignKey(x => x.StatusPageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Incident).WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Body).HasMaxLength(1000).IsRequired();
            e.HasIndex(x => new { x.StatusPageId, x.IncidentId });
        });

        b.Entity<StatusPageEvent>(e =>
        {
            e.HasOne<StatusPage>().WithMany().HasForeignKey(x => x.StatusPageId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Key).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.StatusPageId, x.Key }).IsUnique();
            e.HasIndex(x => x.CreatedAtUtc);
            e.HasIndex(x => new { x.StatusPageId, x.MonitorId, x.CreatedAtUtc });
        });

        // FEAT-10: one monthly report e-mail per customer and month.
        b.Entity<MonthlyReportDelivery>(e =>
        {
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Month).HasMaxLength(7).IsRequired();
            e.HasIndex(x => new { x.CustomerId, x.Month }).IsUnique();
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

        // FEAT-6: probe regions (agents) and the per monitor / region probe state.
        b.Entity<Region>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(32).IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Prefix).HasMaxLength(32).IsRequired();
            e.HasIndex(x => x.Prefix).IsUnique();
            e.Property(x => x.SecretHash).HasMaxLength(64).IsRequired();
        });

        b.Entity<MonitorRegionState>(e =>
        {
            e.HasKey(x => new { x.MonitorId, x.RegionId });
            e.HasOne(x => x.Monitor).WithMany().HasForeignKey(x => x.MonitorId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Region).WithMany().HasForeignKey(x => x.RegionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RegionId, x.NextCheckAtUtc });
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.Property(x => x.LeaseItemId).HasMaxLength(64);
            e.Property(x => x.LeaseFingerprint).HasMaxLength(64);
            e.Property(x => x.LastResultItemId).HasMaxLength(64);
            e.Property(x => x.LastResultFingerprint).HasMaxLength(64);
            e.HasIndex(x => x.LeaseItemId);
            e.HasIndex(x => new { x.RegionId, x.LastResultItemId });
        });

        b.Entity<CheckResult>(e =>
        {
            e.HasOne<Region>().WithMany().HasForeignKey(x => x.RegionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Incident>(e => e.Property(x => x.FailedRegions).HasMaxLength(500));

        // AI-4: incident groups (notification grouping). Deleting a group never touches incidents.
        b.Entity<IncidentGroup>(e =>
        {
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Signals).HasMaxLength(200);
            e.Property(x => x.Host).HasMaxLength(255);
            e.Property(x => x.DecidedByUserId).HasMaxLength(450);
            e.HasIndex(x => new { x.CustomerId, x.Status, x.CreatedAtUtc });
        });

        b.Entity<IncidentGroupMember>(e =>
        {
            e.HasOne(x => x.Group).WithMany(g => g.Members).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Incident).WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.IncidentId).IsUnique();
            e.HasIndex(x => x.CustomerId);
        });

        // OPS-3: scheduler lease per monitor and cluster-wide job leases.
        b.Entity<MonitorEntity>(e => e.Property(x => x.LeaseOwner).HasMaxLength(100));
        b.Entity<ClusterLease>(e =>
        {
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.Owner).HasMaxLength(100).IsRequired();
        });
    }
}

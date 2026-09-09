using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure;

public class PulseDbContext : DbContext
{
    // Set only by an authorized HTTP data mutation. Worker lifecycle scopes use
    // their explicit transaction gates instead of inheriting a request's decision.
    public Guid? GuardedProjectId { get; set; }
    public long? GuardedMaintenanceGeneration { get; set; }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => SaveChangesAsync(true, cancellationToken);
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        if (GuardedProjectId is not { } projectId) return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        await using var transaction = Database.CurrentTransaction is null ? await Database.BeginTransactionAsync(cancellationToken) : null;
        var state = await ProjectIngestionStates.AsNoTracking().SingleOrDefaultAsync(s => s.ProjectId == projectId, cancellationToken);
        if (state?.Paused == true || (state?.MaintenanceGeneration ?? 0) != GuardedMaintenanceGeneration)
            throw new Services.ProjectMaintenanceException();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public PulseDbContext(DbContextOptions<PulseDbContext> options)
        : base(options)
    {
    }

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<AnalyticsEvent> Events => Set<AnalyticsEvent>();

    public DbSet<Person> Persons => Set<Person>();

    public DbSet<PersonDistinctId> PersonDistinctIds => Set<PersonDistinctId>();

    public DbSet<Insight> Insights => Set<Insight>();

    public DbSet<User> Users => Set<User>();

    public DbSet<ProjectMembership> ProjectMemberships => Set<ProjectMembership>();

    public DbSet<PersonalApiKey> PersonalApiKeys => Set<PersonalApiKey>();
    public DbSet<PersonalKeyProject> PersonalKeyProjects => Set<PersonalKeyProject>();
    public DbSet<PersonalKeyScope> PersonalKeyScopes => Set<PersonalKeyScope>();

    public DbSet<Cohort> Cohorts => Set<Cohort>();

    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<FlagVersion> FlagVersions => Set<FlagVersion>();
    public DbSet<FlagRolloutSchedule> FlagRolloutSchedules => Set<FlagRolloutSchedule>();

    public DbSet<Dashboard> Dashboards => Set<Dashboard>();

    public DbSet<QueuedEvent> QueuedEvents => Set<QueuedEvent>();
    public DbSet<CaptureAdmissionKey> CaptureAdmissionKeys => Set<CaptureAdmissionKey>();
    public DbSet<CaptureProcessingItem> CaptureProcessingItems => Set<CaptureProcessingItem>();
    public DbSet<CaptureReceipt> CaptureReceipts => Set<CaptureReceipt>();
    public DbSet<CaptureReceiptItem> CaptureReceiptItems => Set<CaptureReceiptItem>();
    public DbSet<CaptureItemTransition> CaptureItemTransitions => Set<CaptureItemTransition>();
    public DbSet<ProjectIngestionState> ProjectIngestionStates => Set<ProjectIngestionState>();
    public DbSet<ProjectIngestionLease> ProjectIngestionLeases => Set<ProjectIngestionLease>();

    public DbSet<Annotation> Annotations => Set<Annotation>();

    public DbSet<EventDefinition> EventDefinitions => Set<EventDefinition>();

    public DbSet<PropertyDefinition> PropertyDefinitions => Set<PropertyDefinition>();

    public DbSet<DeadLetterEvent> DeadLetterEvents => Set<DeadLetterEvent>();

    public DbSet<ExportJob> ExportJobs => Set<ExportJob>();
    public DbSet<ExportSnapshotRow> ExportSnapshotRows => Set<ExportSnapshotRow>();
    public DbSet<ProjectRetentionPolicy> ProjectRetentionPolicies => Set<ProjectRetentionPolicy>();
    public DbSet<RetentionRun> RetentionRuns => Set<RetentionRun>();
    public DbSet<ErasureJob> ErasureJobs => Set<ErasureJob>();
    public DbSet<IdentitySuppression> IdentitySuppressions => Set<IdentitySuppression>();
    public DbSet<ErasureIdentity> ErasureIdentities => Set<ErasureIdentity>();
    public DbSet<ErasureUnreadableItem> ErasureUnreadableItems => Set<ErasureUnreadableItem>();
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();
    public DbSet<AlertEvaluation> AlertEvaluations => Set<AlertEvaluation>();
    public DbSet<ProjectNotification> ProjectNotifications => Set<ProjectNotification>();
    public DbSet<NotificationRead> NotificationReads => Set<NotificationRead>();

    public DbSet<DashboardTile> DashboardTiles => Set<DashboardTile>();

    public DbSet<CohortPerson> CohortPersons => Set<CohortPerson>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite stores DateTimeOffset as TEXT and cannot order/compare it
        // natively. Persist UTC ticks (long) instead so range filters and
        // ORDER BY translate to plain integer comparisons in SQL.
        configurationBuilder
            .Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToUtcTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AlertRule>(b =>
        {
            b.Property(r => r.Revision).IsConcurrencyToken();
            b.Property(r => r.Name).HasMaxLength(200);
            b.Property(r => r.EventName).HasMaxLength(200);
            b.HasIndex(r => new { r.ProjectId, r.IsDeleted, r.CreatedAt });
            b.HasIndex(r => new { r.Enabled, r.IsDeleted, r.NextWindowStart });
        });
        modelBuilder.Entity<AlertEvaluation>(b =>
        {
            b.HasIndex(e => new { e.RuleId, e.RuleRevision, e.WindowEnd }).IsUnique();
            b.HasIndex(e => new { e.ProjectId, e.EvaluatedAt });
        });
        modelBuilder.Entity<ProjectNotification>(b =>
        {
            b.Property(n => n.RuleName).HasMaxLength(200);
            b.Property(n => n.EventName).HasMaxLength(200);
            b.HasIndex(n => n.EvaluationId).IsUnique();
            b.HasIndex(n => new { n.ProjectId, n.CreatedAt });
        });
        modelBuilder.Entity<NotificationRead>(b =>
        {
            b.HasKey(r => new { r.NotificationId, r.UserId });
            b.HasIndex(r => new { r.UserId, r.ReadAt });
        });
        modelBuilder.Entity<ErasureJob>(b =>
        {
            b.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(j => j.Phase).HasConversion<string>().HasMaxLength(30);
            b.Property(j => j.ErrorCode).HasMaxLength(100);
            b.HasIndex(j => j.ProjectId).IsUnique().HasFilter("\"Status\" <> 'Completed'");
            b.HasIndex(j => new { j.Status, j.UpdatedAt });
        });
        modelBuilder.Entity<IdentitySuppression>(b =>
        {
            b.HasKey(s => new { s.ProjectId, s.KeyVersion, s.Fingerprint });
            b.Property(s => s.Fingerprint).HasMaxLength(64);
        });
        modelBuilder.Entity<ErasureIdentity>(b =>
        {
            b.HasKey(i => new { i.JobId, i.KeyVersion, i.Fingerprint });
            b.Property(i => i.Fingerprint).HasMaxLength(64);
        });
        modelBuilder.Entity<ErasureUnreadableItem>(b =>
        {
            b.Property(i => i.ContentHash).HasMaxLength(64);
            b.HasIndex(i => new { i.JobId, i.QueueSequence }).IsUnique();
            b.HasIndex(i => new { i.JobId, i.DeadLetterId }).IsUnique();
        });
        modelBuilder.Entity<ProjectRetentionPolicy>(b => b.HasKey(p => p.ProjectId));
        modelBuilder.Entity<RetentionRun>(b =>
        {
            b.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(r => r.ProjectId).IsUnique().HasFilter("\"Status\" = 'Running'");
            b.HasIndex(r => new { r.ProjectId, r.StartedAt });
        });
        modelBuilder.Entity<ExportSnapshotRow>(b =>
        {
            b.HasKey(r => new { r.JobId, r.Ordinal });
            b.HasIndex(r => new { r.ProjectId, r.JobId });
        });
        modelBuilder.Entity<ExportJob>(b =>
        {
            b.Property(j => j.Consistency).HasMaxLength(20);
            b.HasIndex(j => new { j.Status, j.LeaseExpiresAt });
        });
        modelBuilder.Entity<Project>(b =>
        {
            b.Property(p => p.Name).HasMaxLength(200);
            b.Property(p => p.ApiKey).HasMaxLength(64);
            b.HasIndex(p => p.ApiKey).IsUnique();
            b.Property(p => p.ReadKey).HasMaxLength(64);
            b.HasIndex(p => p.ReadKey).IsUnique();
        });

        modelBuilder.Entity<User>(b =>
        {
            b.Property(u => u.Email).HasMaxLength(320);
            b.Property(u => u.Name).HasMaxLength(200);
            b.Property(u => u.PasswordHash).HasMaxLength(200);
            b.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<ProjectMembership>(b =>
        {
            b.Property(m => m.Role).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(m => new { m.ProjectId, m.UserId }).IsUnique();
            b.HasIndex(m => m.UserId);
        });

        modelBuilder.Entity<PersonalApiKey>(b =>
        {
            b.Property(k => k.Mode).HasConversion<string>().HasMaxLength(30);
            b.Property(k => k.Name).HasMaxLength(200);
            b.Property(k => k.KeyHash).HasMaxLength(64);
            b.Property(k => k.KeySuffix).HasMaxLength(4);
            b.HasIndex(k => k.KeyHash).IsUnique();
            b.HasIndex(k => k.UserId);
        });
        modelBuilder.Entity<PersonalKeyProject>(b => b.HasKey(p => new { p.KeyId, p.ProjectId }));
        modelBuilder.Entity<PersonalKeyScope>(b =>
        {
            b.HasKey(s => new { s.KeyId, s.Scope });
            b.Property(s => s.Scope).HasMaxLength(32);
        });

        modelBuilder.Entity<AnalyticsEvent>(b =>
        {
            b.Property(e => e.Name).HasMaxLength(200);
            b.Property(e => e.DistinctId).HasMaxLength(400);
            b.HasIndex(e => new { e.ProjectId, e.Name, e.Timestamp });
            b.HasIndex(e => new { e.ProjectId, e.Timestamp });
            b.HasIndex(e => new { e.ProjectId, e.PersonId, e.Timestamp });
        });

        modelBuilder.Entity<Person>(b =>
        {
            b.HasIndex(p => p.ProjectId);
        });

        modelBuilder.Entity<PersonDistinctId>(b =>
        {
            b.Property(d => d.DistinctId).HasMaxLength(400);
            b.HasIndex(d => new { d.ProjectId, d.DistinctId }).IsUnique();
            b.HasIndex(d => d.PersonId);
        });

        modelBuilder.Entity<ExportJob>(b =>
        {
            b.Property(j => j.Type).HasMaxLength(20);
            b.Property(j => j.Format).HasMaxLength(10);
            b.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(j => j.ContentType).HasMaxLength(50);
            b.Property(j => j.Error).HasMaxLength(2000);
            b.HasIndex(j => new { j.ProjectId, j.CreatedAt });
        });

        modelBuilder.Entity<Annotation>(b =>
        {
            b.Property(a => a.Content).HasMaxLength(2000);
            b.HasIndex(a => new { a.ProjectId, a.Date });
        });

        modelBuilder.Entity<EventDefinition>(b =>
        {
            b.Property(d => d.Name).HasMaxLength(200);
            b.HasIndex(d => new { d.ProjectId, d.Name }).IsUnique();
        });

        modelBuilder.Entity<PropertyDefinition>(b =>
        {
            b.Property(d => d.Name).HasMaxLength(200);
            b.Property(d => d.PropertyType).HasMaxLength(20);
            b.HasIndex(d => new { d.ProjectId, d.Name }).IsUnique();
        });

        modelBuilder.Entity<QueuedEvent>(b =>
        {
            // Auto-increment rowid: cheap appends, stable processing order.
            b.HasKey(q => q.Seq);
            b.Property(q => q.Seq).ValueGeneratedOnAdd();
            b.HasIndex(q => q.ProjectId);
            b.HasIndex(q => new { q.ProjectId, q.NextAttemptAt, q.Seq });
            b.HasIndex(q => q.AdmissionId);
            b.Property(q => q.LastErrorCode).HasMaxLength(80);
            b.Property(q => q.TraceParent).HasMaxLength(55);
            b.Property(q => q.OriginalTraceParent).HasMaxLength(55);
        });

        modelBuilder.Entity<CaptureAdmissionKey>(b =>
        {
            b.HasKey(k => new { k.ProjectId, k.ClientEventId });
            b.Property(k => k.PayloadHash).HasMaxLength(64);
            b.HasIndex(k => k.ExpiresAt);
            b.HasIndex(k => k.AdmissionId);
        });
        modelBuilder.Entity<CaptureProcessingItem>(b =>
        {
            b.Property(i => i.State).HasConversion<string>().HasMaxLength(20);
            b.Property(i => i.RetiredReason).HasMaxLength(80);
            b.HasIndex(i => new { i.ProjectId, i.State, i.ChangedAt });
        });
        modelBuilder.Entity<CaptureReceipt>(b => b.HasIndex(r => new { r.ProjectId, r.CreatedAt }));
        modelBuilder.Entity<CaptureReceiptItem>(b =>
        {
            b.HasKey(r => new { r.ReceiptId, r.Ordinal });
            b.HasIndex(r => r.AdmissionId);
        });
        modelBuilder.Entity<CaptureItemTransition>(b =>
        {
            b.HasKey(t => t.Sequence);
            b.Property(t => t.Sequence).ValueGeneratedOnAdd();
            b.Property(t => t.State).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(t => new { t.AdmissionId, t.Sequence });
        });
        modelBuilder.Entity<ProjectIngestionState>(b => b.HasKey(s => s.ProjectId));
        modelBuilder.Entity<ProjectIngestionLease>(b =>
        {
            b.HasKey(l => l.ProjectId);
            b.HasIndex(l => l.ExpiresAt);
        });

        modelBuilder.Entity<DeadLetterEvent>(b =>
        {
            b.Property(d => d.Error).HasMaxLength(2000);
            b.Property(d => d.TraceParent).HasMaxLength(55);
            b.Property(d => d.OriginalTraceParent).HasMaxLength(55);
            b.HasIndex(d => d.ProjectId);
            b.HasIndex(d => d.AdmissionId);
        });

        modelBuilder.Entity<Dashboard>(b =>
        {
            b.Property(d => d.Name).HasMaxLength(200);
            b.Property(d => d.Description).HasMaxLength(2000);
            b.HasIndex(d => d.ProjectId);
        });

        modelBuilder.Entity<DashboardTile>(b =>
        {
            b.HasIndex(t => t.DashboardId);
        });

        modelBuilder.Entity<FeatureFlag>(b =>
        {
            b.Property(f => f.Revision).IsConcurrencyToken();
            b.Property(f => f.Key).HasMaxLength(200);
            b.Property(f => f.Name).HasMaxLength(200);
            b.Property(f => f.Type).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(f => new { f.ProjectId, f.Key }).IsUnique();
        });

        modelBuilder.Entity<AuditEntry>(b =>
        {
            b.HasKey(a => a.Sequence);
            b.Property(a => a.Sequence).ValueGeneratedOnAdd();
            b.Property(a => a.Action).HasMaxLength(40);
            b.Property(a => a.ResourceType).HasMaxLength(30);
            b.Property(a => a.SummaryJson).HasMaxLength(2048);
            b.HasIndex(a => new { a.ProjectId, a.Sequence });
        });
        modelBuilder.Entity<FlagVersion>(b =>
        {
            b.HasKey(v => new { v.FlagId, v.Revision });
            b.Property(v => v.ConfigJson).HasMaxLength(65536);
            b.Property(v => v.Origin).HasMaxLength(20);
            b.HasIndex(v => new { v.ProjectId, v.FlagId, v.Revision });
        });
        modelBuilder.Entity<FlagRolloutSchedule>(b =>
        {
            b.HasIndex(s => s.FlagId).IsUnique();
            b.HasIndex(s => new { s.Status, s.ExecuteAt });
            b.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(s => s.Reason).HasMaxLength(100);
        });

        modelBuilder.Entity<Cohort>(b =>
        {
            b.Property(c => c.Name).HasMaxLength(200);
            b.Property(c => c.Type).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(c => c.ProjectId);
        });

        modelBuilder.Entity<CohortPerson>(b =>
        {
            b.HasIndex(cp => new { cp.CohortId, cp.PersonId }).IsUnique();
        });

        modelBuilder.Entity<Insight>(b =>
        {
            b.Property(i => i.Name).HasMaxLength(200);
            b.Property(i => i.Type).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(i => i.ProjectId);
        });
    }

    /// <summary>
    /// Round-trips <see cref="DateTimeOffset"/> through UTC ticks. Ordering is
    /// preserved; the original offset is normalized to +00:00 on read, which is
    /// fine for analytics where everything is compared in UTC.
    /// </summary>
    public sealed class DateTimeOffsetToUtcTicksConverter : ValueConverter<DateTimeOffset, long>
    {
        public DateTimeOffsetToUtcTicksConverter()
            : base(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero))
        {
        }
    }
}

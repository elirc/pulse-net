using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure;

public class PulseDbContext : DbContext
{
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

    public DbSet<Cohort> Cohorts => Set<Cohort>();

    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();

    public DbSet<Dashboard> Dashboards => Set<Dashboard>();

    public DbSet<QueuedEvent> QueuedEvents => Set<QueuedEvent>();

    public DbSet<Annotation> Annotations => Set<Annotation>();

    public DbSet<EventDefinition> EventDefinitions => Set<EventDefinition>();

    public DbSet<PropertyDefinition> PropertyDefinitions => Set<PropertyDefinition>();

    public DbSet<DeadLetterEvent> DeadLetterEvents => Set<DeadLetterEvent>();

    public DbSet<ExportJob> ExportJobs => Set<ExportJob>();

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
            b.HasIndex(m => new { m.ProjectId, m.UserId }).IsUnique();
            b.HasIndex(m => m.UserId);
        });

        modelBuilder.Entity<PersonalApiKey>(b =>
        {
            b.Property(k => k.Name).HasMaxLength(200);
            b.Property(k => k.KeyHash).HasMaxLength(64);
            b.Property(k => k.KeySuffix).HasMaxLength(4);
            b.HasIndex(k => k.KeyHash).IsUnique();
            b.HasIndex(k => k.UserId);
        });

        modelBuilder.Entity<AnalyticsEvent>(b =>
        {
            b.Property(e => e.Name).HasMaxLength(200);
            b.Property(e => e.DistinctId).HasMaxLength(400);
            b.HasIndex(e => new { e.ProjectId, e.Name, e.Timestamp });
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
        });

        modelBuilder.Entity<DeadLetterEvent>(b =>
        {
            b.Property(d => d.Error).HasMaxLength(2000);
            b.HasIndex(d => d.ProjectId);
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
            b.Property(f => f.Key).HasMaxLength(200);
            b.Property(f => f.Name).HasMaxLength(200);
            b.Property(f => f.Type).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(f => new { f.ProjectId, f.Key }).IsUnique();
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

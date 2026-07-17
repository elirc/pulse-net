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

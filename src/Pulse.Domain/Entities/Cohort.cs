namespace Pulse.Domain.Entities;

public enum CohortType
{
    /// <summary>An explicit, hand-curated list of persons.</summary>
    Static,

    /// <summary>Membership computed on demand from stored rules.</summary>
    Dynamic,
}

/// <summary>
/// A named group of persons, usable as a filter in every query. Static
/// cohorts store members in <see cref="CohortPerson"/> rows; dynamic cohorts
/// store their rules as JSON and are evaluated at query time.
/// </summary>
public class Cohort
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public required string Name { get; set; }

    public CohortType Type { get; set; }

    /// <summary>Dynamic-cohort rules as a JSON array; "[]" for static cohorts.</summary>
    public string RulesJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

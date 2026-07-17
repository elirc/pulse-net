namespace Pulse.Domain.Entities;

/// <summary>Membership row for a static cohort.</summary>
public class CohortPerson
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CohortId { get; set; }

    public Guid PersonId { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

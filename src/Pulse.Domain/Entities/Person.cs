namespace Pulse.Domain.Entities;

/// <summary>
/// A unique end user of a project. One person can own many distinct ids
/// (anonymous device ids merged into an identified user via <c>$identify</c>).
/// </summary>
public class Person
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    /// <summary>Person properties (email, plan, ...) as a JSON object.</summary>
    public string PropertiesJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

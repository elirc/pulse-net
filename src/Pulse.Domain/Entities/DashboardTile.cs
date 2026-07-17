namespace Pulse.Domain.Entities;

/// <summary>
/// Places a saved insight on a dashboard. Layout is an opaque JSON object
/// (<c>{"x":0,"y":0,"w":6,"h":4}</c> by convention) owned by the frontend.
/// </summary>
public class DashboardTile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DashboardId { get; set; }

    public Guid InsightId { get; set; }

    /// <summary>Grid position/size metadata as a JSON object.</summary>
    public string LayoutJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

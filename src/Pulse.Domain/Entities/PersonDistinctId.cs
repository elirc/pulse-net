namespace Pulse.Domain.Entities;

/// <summary>
/// Maps a distinct id (device id, user id, email hash, ...) to a person within
/// a project. The (ProjectId, DistinctId) pair is unique; identity merging
/// repoints rows here at the surviving person.
/// </summary>
public class PersonDistinctId
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public required string DistinctId { get; set; }

    public Guid PersonId { get; set; }
}

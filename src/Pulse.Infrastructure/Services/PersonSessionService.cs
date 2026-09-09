using Microsoft.EntityFrameworkCore;
using Pulse.Domain;

namespace Pulse.Infrastructure.Services;

public sealed record PersonSessionsResult(Guid PersonId, DateTimeOffset From, DateTimeOffset To,
    int GapMinutes, string BoundarySemantics, IReadOnlyList<ActivitySession> Sessions);

public sealed class PersonSessionService(PulseDbContext db)
{
    public static readonly QueryBudgetLimits SessionLimits = new(10_000, 0, 0, 500, TimeSpan.FromSeconds(2));

    public async Task<PersonSessionsResult?> QueryAsync(Guid projectId, Guid personId, DateTimeOffset from,
        DateTimeOffset to, int gapMinutes, CancellationToken clientCancellation)
    {
        if (from >= to || to - from > TimeSpan.FromDays(7)) throw new ArgumentOutOfRangeException(nameof(from));
        if (gapMinutes is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(gapMinutes));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(clientCancellation);
        deadline.CancelAfter(SessionLimits.Deadline);
        try
        {
            if (!await db.Persons.AsNoTracking().AnyAsync(p => p.ProjectId == projectId && p.Id == personId, deadline.Token))
                return null;

            var budget = new QueryWorkBudget(SessionLimits);
            var positions = new List<EventPosition>();
            var query = db.Events.AsNoTracking()
                .Where(e => e.ProjectId == projectId && e.PersonId == personId && e.Timestamp >= from && e.Timestamp < to)
                .OrderBy(e => e.Timestamp).ThenBy(e => e.Id)
                .Take(SessionLimits.MaxRows + 1)
                .Select(e => new EventPosition(e.Id, e.Timestamp));
            await foreach (var position in query.AsAsyncEnumerable().WithCancellation(deadline.Token))
            {
                deadline.Token.ThrowIfCancellationRequested();
                budget.AddScannedRow(0);
                positions.Add(position);
            }

            var sessions = SessionGrouping.MarkWindowBoundaries(
                SessionGrouping.Group(positions, TimeSpan.FromMinutes(gapMinutes), budget, deadline.Token));
            deadline.Token.ThrowIfCancellationRequested();
            return new(personId, from, to, gapMinutes, "window-local", sessions);
        }
        catch (OperationCanceledException) when (!clientCancellation.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new QueryDeadlineExceededException();
        }
    }
}

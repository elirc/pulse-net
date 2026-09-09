using Pulse.Domain;

namespace Pulse.Tests.Domain;

public class SessionGroupingTests
{
    private static readonly DateTimeOffset Ten = DateTimeOffset.Parse("2026-05-01T10:00:00Z");
    private static QueryWorkBudget Budget(int outputs = 500) =>
        new(new QueryBudgetLimits(10_000, 0, 0, outputs, TimeSpan.FromSeconds(2)));

    [Fact]
    public void EqualThresholdStartsANewSession()
    {
        var events = new[] { At(0), At(10), At(40) };
        var result = SessionGrouping.MarkWindowBoundaries(SessionGrouping.Group(events, TimeSpan.FromMinutes(30), Budget()));

        Assert.Equal(2, result.Count);
        Assert.Equal((2, 600d), (result[0].EventCount, result[0].ObservedDurationSeconds));
        Assert.Equal((1, 0d), (result[1].EventCount, result[1].ObservedDurationSeconds));
        Assert.True(result[0].MayStartBeforeWindow);
        Assert.True(result[1].MayContinueAfterWindow);
    }

    [Fact]
    public void JustUnderThresholdStaysTogetherAndTiesRetainProvidedIdOrder()
    {
        var first = new EventPosition(Guid.Parse("00000000-0000-0000-0000-000000000001"), Ten);
        var second = new EventPosition(Guid.Parse("00000000-0000-0000-0000-000000000002"), Ten);
        var third = new EventPosition(Guid.NewGuid(), Ten.AddMinutes(29).AddSeconds(59));

        var session = Assert.Single(SessionGrouping.Group([first, second, third], TimeSpan.FromMinutes(30), Budget()));
        Assert.Equal(first.Id, session.FirstEventId);
        Assert.Equal(third.Id, session.LastEventId);
        Assert.Equal(3, session.EventCount);
    }

    [Fact]
    public void OutputCapRejectsWholeGroupingInsteadOfReturningPrefix()
    {
        var error = Assert.Throws<QueryBudgetExceededException>(() =>
            SessionGrouping.Group([At(0), At(30)], TimeSpan.FromMinutes(30), Budget(1)));
        Assert.Equal(QueryBudgetReason.OutputLimit, error.Reason);
    }

    [Fact]
    public void EmptyInputHasNoBoundarySessions()
    {
        Assert.Empty(SessionGrouping.MarkWindowBoundaries(SessionGrouping.Group([], TimeSpan.FromMinutes(30), Budget())));
    }

    [Fact]
    public void ObservedDurationPreservesFractionalSeconds()
    {
        var session = Assert.Single(SessionGrouping.Group(
            [new(Guid.NewGuid(), Ten), new(Guid.NewGuid(), Ten.AddMilliseconds(1250))],
            TimeSpan.FromMinutes(30), Budget()));
        Assert.Equal(1.25, session.ObservedDurationSeconds);
    }

    private static EventPosition At(int minutes) => new(Guid.NewGuid(), Ten.AddMinutes(minutes));
}

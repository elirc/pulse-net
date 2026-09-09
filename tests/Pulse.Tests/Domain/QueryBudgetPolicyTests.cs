using Pulse.Domain;

namespace Pulse.Tests.Domain;

public class QueryBudgetPolicyTests
{
    private static QueryWorkBudget Budget() => new(new QueryBudgetLimits(2, 5, 3, 2, TimeSpan.FromSeconds(2)));

    [Fact]
    public void ExactLimitsAreAllowed()
    {
        var budget = Budget();
        budget.AddScannedRow(2);
        budget.AddScannedRow(3);
        budget.ReserveBuckets(3);
        budget.AddOutput();
        budget.AddOutput();

        Assert.Equal((2, 5L, 3, 2), (budget.Rows, budget.Bytes, budget.Buckets, budget.Outputs));
    }

    [Theory]
    [InlineData(true, QueryBudgetReason.RowLimit)]
    [InlineData(false, QueryBudgetReason.ByteLimit)]
    public void ScanChargesRowsAndBytesIndependently(bool exceedRows, QueryBudgetReason expected)
    {
        var budget = Budget();
        budget.AddScannedRow(exceedRows ? 0 : 5);
        if (exceedRows) budget.AddScannedRow(0);

        var error = Assert.Throws<QueryBudgetExceededException>(() => budget.AddScannedRow(exceedRows ? 0 : 1));
        Assert.Equal(expected, error.Reason);
    }

    [Fact]
    public void BucketLimitIsCheckedBeforeDatabaseWork()
    {
        var error = Assert.Throws<QueryBudgetExceededException>(() => Budget().ReserveBuckets(4));
        Assert.Equal(QueryBudgetReason.BucketLimit, error.Reason);
    }

    [Fact]
    public void AnnotationCapAllowsExactBoundaryAndRejectsLimitPlusOne()
    {
        var budget = Budget();
        budget.AddAnnotation();
        budget.AddAnnotation();
        var error = Assert.Throws<QueryBudgetExceededException>(() => budget.AddAnnotation());
        Assert.Equal(QueryBudgetReason.AnnotationLimit, error.Reason);
    }
}

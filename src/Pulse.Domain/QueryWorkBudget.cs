namespace Pulse.Domain;

public enum QueryBudgetReason
{
    RowLimit,
    ByteLimit,
    BucketLimit,
    OutputLimit,
    AnnotationLimit,
}

public sealed class QueryBudgetExceededException(QueryBudgetReason reason, string guidance) : Exception(guidance)
{
    public QueryBudgetReason Reason { get; } = reason;
    public string Guidance { get; } = guidance;
}

public sealed record QueryBudgetLimits(
    int MaxRows,
    long MaxBytes,
    int MaxBuckets,
    int MaxOutputs,
    TimeSpan Deadline);

/// <summary>Request-local accounting. A caller charges work before using its result.</summary>
public sealed class QueryWorkBudget(QueryBudgetLimits limits)
{
    public QueryBudgetLimits Limits { get; } = limits;
    public int Rows { get; private set; }
    public long Bytes { get; private set; }
    public int Buckets { get; private set; }
    public int Outputs { get; private set; }

    public void AddScannedRow(long bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        Rows = checked(Rows + 1);
        Bytes = checked(Bytes + bytes);
        if (Rows > Limits.MaxRows)
            throw Exceeded(QueryBudgetReason.RowLimit, "Use a shorter time range or a more specific event.");
        if (Bytes > Limits.MaxBytes)
            throw Exceeded(QueryBudgetReason.ByteLimit, "Use a shorter time range or remove broad property filters.");
    }

    public void ReserveBuckets(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        Buckets = count;
        if (Buckets > Limits.MaxBuckets)
            throw Exceeded(QueryBudgetReason.BucketLimit, "Use a coarser interval or a shorter time range.");
    }

    public void AddOutput()
    {
        Outputs = checked(Outputs + 1);
        if (Outputs > Limits.MaxOutputs)
            throw Exceeded(QueryBudgetReason.OutputLimit, "Use a shorter time range or a larger session gap.");
    }

    public void AddAnnotation()
    {
        Outputs = checked(Outputs + 1);
        if (Outputs > Limits.MaxOutputs)
            throw Exceeded(QueryBudgetReason.AnnotationLimit, "Use a shorter time range with fewer annotations.");
    }

    private static QueryBudgetExceededException Exceeded(QueryBudgetReason reason, string guidance) =>
        new(reason, guidance);
}

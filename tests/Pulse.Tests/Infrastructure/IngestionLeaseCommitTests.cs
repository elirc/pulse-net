using Microsoft.EntityFrameworkCore;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class IngestionLeaseCommitTests
{
    [Fact]
    public async Task ExpiryDuringFencedTransaction_CannotTransferOwnershipUntilMutationCommits()
    {
        await using var f = new IngestionReliabilityFixture(); await f.InitializeAsync();
        var firstOwner = Guid.NewGuid();
        await using var mutation = f.Open();
        var first = (await new IngestionLeaseService(mutation, f.Clock).ClaimAsync(f.Project.Id, firstOwner, default))!;
        await using var transaction = await mutation.Database.BeginTransactionAsync();
        await new IngestionLeaseService(mutation, f.Clock).FenceAsync(first, default);
        await mutation.Projects.Where(p => p.Id == f.Project.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "fenced mutation"));

        f.Clock.Now = f.Clock.Now.Add(IngestionLeaseService.Duration).AddSeconds(1);
        var replacementStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacement = Task.Run(async () =>
        {
            await using var contender = f.Open();
            replacementStarted.TrySetResult();
            return await new IngestionLeaseService(contender, f.Clock).ClaimAsync(f.Project.Id, Guid.NewGuid(), default);
        });
        await replacementStarted.Task;
        await Task.Delay(100);
        Assert.False(replacement.IsCompleted);

        await transaction.CommitAsync();
        var next = await replacement.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(next);
        Assert.Equal(first.Generation + 1, next.Generation);
        await using var verify = f.Open();
        Assert.Equal("fenced mutation", (await verify.Projects.AsNoTracking().SingleAsync(p => p.Id == f.Project.Id)).Name);
    }
}

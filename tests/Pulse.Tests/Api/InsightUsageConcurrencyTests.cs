using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Contracts;
using Pulse.Infrastructure;

namespace Pulse.Tests.Api;

public class InsightUsageConcurrencyTests
{
    private sealed class ShortBusyTimeout : DbConnectionInterceptor
    {
        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
            ((SqliteConnection)connection).DefaultTimeout = 1;
        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            ((SqliteConnection)connection).DefaultTimeout = 1;
            return Task.CompletedTask;
        }
    }

    private sealed class InsertBarrier : DbCommandInterceptor
    {
        public bool Armed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("INSERT INTO \"DashboardTiles\"", StringComparison.Ordinal))
            {
                Armed = false;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task CompetingDelete_CannotCommitBehindTileExistenceCheck()
    {
        var barrier = new InsertBarrier();
        using var baseFactory = new PulseApiFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.ConfigureDbContext<PulseDbContext>(options =>
                options.UseSqlite(sqlite => sqlite.CommandTimeout(1)).AddInterceptors(barrier, new ShortBusyTimeout()))));
        using var client = factory.CreateClient();
        await TestAuth.AuthenticateAsync(client);
        using var createdProject = await client.PostAsJsonAsync("/api/projects", new { name = "Competing mutations" });
        var project = (await createdProject.Content.ReadFromJsonAsync<ProjectResponse>())!;
        var root = $"/api/projects/{project.Id}";
        using var createdInsight = await client.PostAsJsonAsync(root + "/insights", new { name = "Shared", type = "trend", config = new { @event = "signup" } });
        var insight = (await createdInsight.Content.ReadFromJsonAsync<InsightResponse>())!;
        using var createdDashboard = await client.PostAsJsonAsync(root + "/dashboards", new { name = "Board" });
        var dashboard = (await createdDashboard.Content.ReadFromJsonAsync<DashboardResponse>())!;

        barrier.Armed = true;
        var add = Task.Run(() => client.PostAsJsonAsync(root + $"/dashboards/{dashboard.Id}/tiles", new { insightId = insight.Id }));
        HttpResponseMessage? deletion = null;
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
            // The insert has passed its existence check and still owns its write transaction.
            // SQLite's short test timeout bounds the competing writer; no arbitrary sleep.
            deletion = await Task.Run(() => client.DeleteAsync(root + $"/insights/{insight.Id}"))
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotEqual(HttpStatusCode.NoContent, deletion.StatusCode);
        }
        finally { barrier.Release.TrySetResult(); }
        using var added = await add.WaitAsync(TimeSpan.FromSeconds(30));
        added.EnsureSuccessStatusCode();
        deletion?.Dispose();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        Assert.True(await db.Insights.AnyAsync(i => i.Id == insight.Id));
        Assert.Equal(1, await db.DashboardTiles.CountAsync(t => t.InsightId == insight.Id));
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync(root + $"/insights/{insight.Id}")).StatusCode);
    }
}

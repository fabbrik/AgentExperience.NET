using AgentExperience.Core.Finalization;
using AgentExperience.Storage.Sqlite.DependencyInjection;
using AgentExperience.Tests.Shared;
using Microsoft.Data.Sqlite;
using static AgentExperience.Storage.Conformance.ConformanceData;

namespace AgentExperience.Storage.Sqlite.Tests;

/// <summary>
/// Story 28.7: a run captured and completed in one service provider over a SQLite file is finalized by a second provider
/// over the same file, after the first is disposed: the capture service wrote it through to the run store, and
/// finalization read it from there with the same verification and idempotency as a run held in memory.
/// </summary>
public sealed class SqliteDurableCaptureTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task A_run_completed_in_one_process_is_finalized_in_the_next()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);

        var outcome = await DurableCaptureScenario.RunAsync(
            services => services.AddAgentExperienceSqliteStorage(_database.Path),
            Authorize(tenant),
            scope,
            betweenProcesses: () =>
            {
                using var pooled = new SqliteConnection(new SqliteDatabase(_database.ConnectionString).ConnectionString);
                SqliteConnection.ClearPool(pooled);
            });

        Assert.Null(outcome.Completed.RunStoreFailure);
        Assert.False(outcome.SecondProcessHeldRunInMemory);
        Assert.Equal(outcome.RunId, Assert.Single(outcome.WaitingBefore).Run.RunId);
        Assert.Equal(FinalizationOutcome.Validated, outcome.Finalized.Outcome);
        Assert.Equal(FinalizationOutcome.AlreadyFinalized, outcome.Replayed.Outcome);
        Assert.Empty(outcome.WaitingAfter);
        Assert.Equal(ExperienceStoreOutcome.Found, outcome.Record.Outcome);
        Assert.Equal(TaskVerificationStatus.Verified, outcome.Record.Record!.Outcome.Status);
        Assert.Equal(2, outcome.Record.Record.Attempts.Single().ToolCalls.Count);
    }

    [Fact]
    public async Task Erasing_the_record_deletes_the_stored_run_it_came_from()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var outcome = await DurableCaptureScenario.RunAsync(
            services => services.AddAgentExperienceSqliteStorage(_database.Path), Authorize(tenant), scope);

        var store = new SqliteExperienceRecordStore(_database.ConnectionString);
        await store.DeleteAsync(Authorize(tenant), scope, outcome.Record.Record!.ExperienceId, CancellationToken.None);

        var runs = new SqliteExperienceRunStore(_database.ConnectionString);
        Assert.Equal(ExperienceRunStoreOutcome.NotFound, (await runs.GetAsync(Authorize(tenant), outcome.RunId, CancellationToken.None)).Outcome);
    }
}

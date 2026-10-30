using AgentExperience.Core.Capture;
using AgentExperience.Core.Finalization;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task A_run_the_host_refuses_to_store_is_deleted_from_the_run_store()
    {
        var tenant = NewTenant();
        var (result, storedAfter) = await DurableCaptureScenario.DenyAsync(AddStorage, Authorize(tenant), Scope(tenant), Release);

        Assert.Equal(FinalizationOutcome.StorageDenied, result.Outcome);
        Assert.Null(result.RunStoreFailure);
        Assert.Equal(ExperienceRunStoreOutcome.NotFound, storedAfter);
    }

    [Fact]
    public async Task Two_processes_finalizing_one_stored_run_at_once_make_one_record()
    {
        var tenant = NewTenant();
        var (outcomes, record, stored) = await DurableCaptureScenario.FinalizeConcurrentlyAsync(AddStorage, Authorize(tenant), Scope(tenant), Release);

        // The two requests are identical, so whichever commits second either replays the first's initial event (and
        // reports it as the same Validated outcome) or finds the record already confirmed: never a second record or event.
        Assert.Contains(FinalizationOutcome.Validated, outcomes);
        Assert.All(outcomes, outcome => Assert.True(outcome is FinalizationOutcome.Validated or FinalizationOutcome.AlreadyFinalized, outcome.ToString()));
        Assert.Equal(ExperienceStoreOutcome.Found, record.Outcome);
        Assert.Equal(1, record.Record!.Revision);
        Assert.NotNull(stored.Run!.FinalizedAt);
    }

    [Fact]
    public async Task A_capture_retry_after_erasure_does_not_bring_the_run_back()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var (retried, storedAfter) = await DurableCaptureScenario.EraseThenRecompleteAsync(
            AddStorage,
            Authorize(tenant),
            scope,
            (provider, experienceId) => provider.GetRequiredService<SqliteExperienceRecordStore>().DeleteAsync(Authorize(tenant), scope, experienceId, CancellationToken.None));

        Assert.Equal(CompleteRunOutcome.DuplicateNoOp, retried.Outcome);
        Assert.Contains("Conflict", retried.RunStoreFailure, StringComparison.Ordinal);
        Assert.Equal(ExperienceRunStoreOutcome.NotFound, storedAfter);
    }

    private void AddStorage(IServiceCollection services) => services.AddAgentExperienceSqliteStorage(_database.Path);

    private void Release() => SqliteTestDatabase.ReleasePool(_database.Path);

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

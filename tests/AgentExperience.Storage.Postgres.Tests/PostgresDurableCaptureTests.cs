using AgentExperience.Core.Finalization;
using AgentExperience.Storage.Postgres.DependencyInjection;
using AgentExperience.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 28.7: a run captured and completed in one service provider over PostgreSQL is finalized by a second provider over
/// the same database, after the first is disposed, through <see cref="PostgresExperienceRunStore"/>; erasing the record
/// deletes the stored run through <c>0032</c>'s trigger. The run store is not available in crypto-shredding mode, so the
/// encrypted run of this suite checks that its registration refuses instead.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresDurableCaptureTests(PostgresFixture fixture)
{
    private static readonly AuthorizationContext Authorization = new("durable-tenant", "host", ["experience:write"], DurableCaptureScenario.Now);

    [Fact]
    public async Task A_run_completed_in_one_process_is_finalized_in_the_next_and_erasure_deletes_it()
    {
        if (EncryptionMode.IsOn)
        {
            return;
        }

        var scope = new Scope("durable-tenant", "app-1", $"project-{Guid.NewGuid():N}");
        var outcome = await DurableCaptureScenario.RunAsync(AddStorage, Authorization, scope);

        Assert.Null(outcome.Completed.RunStoreFailure);
        Assert.False(outcome.SecondProcessHeldRunInMemory);
        Assert.Equal(outcome.RunId, Assert.Single(outcome.WaitingBefore).Run.RunId);
        Assert.Equal(FinalizationOutcome.Validated, outcome.Finalized.Outcome);
        Assert.Equal(FinalizationOutcome.AlreadyFinalized, outcome.Replayed.Outcome);
        Assert.Empty(outcome.WaitingAfter);
        Assert.Equal(TaskVerificationStatus.Verified, outcome.Record.Record!.Outcome.Status);

        var runs = new PostgresExperienceRunStore(fixture.DataSource);
        Assert.NotNull((await runs.GetAsync(Authorization, outcome.RunId, CancellationToken.None)).Run!.FinalizedAt);
        var erased = await new PostgresExperienceRecordStore(fixture.DataSource)
            .DeleteAsync(Authorization, scope, outcome.Record.Record.ExperienceId, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Deleted, erased.Outcome);
        Assert.Equal(ExperienceRunStoreOutcome.NotFound, (await runs.GetAsync(Authorization, outcome.RunId, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_run_the_host_refuses_to_store_is_deleted_from_the_run_store()
    {
        if (EncryptionMode.IsOn)
        {
            return;
        }

        var (result, storedAfter) = await DurableCaptureScenario.DenyAsync(AddStorage, Authorization, NewScope());

        Assert.Equal(FinalizationOutcome.StorageDenied, result.Outcome);
        Assert.Null(result.RunStoreFailure);
        Assert.Equal(ExperienceRunStoreOutcome.NotFound, storedAfter);
    }

    [Fact]
    public async Task Two_processes_finalizing_one_stored_run_at_once_make_one_record()
    {
        if (EncryptionMode.IsOn)
        {
            return;
        }

        var (outcomes, record, stored) = await DurableCaptureScenario.FinalizeConcurrentlyAsync(AddStorage, Authorization, NewScope());

        Assert.Contains(FinalizationOutcome.Validated, outcomes);
        Assert.All(outcomes, outcome => Assert.True(outcome is FinalizationOutcome.Validated or FinalizationOutcome.AlreadyFinalized, outcome.ToString()));
        Assert.Equal(1, record.Record!.Revision);
        Assert.NotNull(stored.Run!.FinalizedAt);
    }

    [Fact]
    public async Task A_capture_retry_after_erasure_does_not_bring_the_run_back()
    {
        if (EncryptionMode.IsOn)
        {
            return;
        }

        var scope = NewScope();
        var (retried, storedAfter) = await DurableCaptureScenario.EraseThenRecompleteAsync(
            AddStorage,
            Authorization,
            scope,
            (_, experienceId) => new PostgresExperienceRecordStore(fixture.DataSource).DeleteAsync(Authorization, scope, experienceId, CancellationToken.None));

        Assert.Contains("Conflict", retried.RunStoreFailure, StringComparison.Ordinal);
        Assert.Equal(ExperienceRunStoreOutcome.NotFound, storedAfter);
    }

    private static Scope NewScope() => new("durable-tenant", "app-1", $"project-{Guid.NewGuid():N}");

    [Fact]
    public void The_run_store_refuses_to_resolve_beside_encryption()
    {
        var services = new ServiceCollection();
        services.AddAgentExperiencePostgresEncryption(EncryptionMode.Shared.KeyStore);
        services.AddAgentExperiencePostgresRunStore(fixture.DataSource);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IExperienceRunStore>());
    }

    private void AddStorage(IServiceCollection services)
    {
        services.AddAgentExperiencePostgresStore(fixture.DataSource);
        services.AddAgentExperiencePostgresRunStore(fixture.DataSource);
    }
}

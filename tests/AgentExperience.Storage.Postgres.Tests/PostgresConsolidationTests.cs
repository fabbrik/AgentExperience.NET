using AgentExperience.Tests.Shared;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 28.6 against a real PostgreSQL container: consolidation, an idempotent re-run and a fold-in, through the record
/// store's ports (the supersession guard, the evidence history and the scoped reads), in every mode the suite runs in.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresConsolidationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Three_runs_become_one_lesson_a_re_run_does_nothing_and_a_fourth_run_is_folded_in()
    {
        var tenant = TestRecords.NewTenant();
        var scope = TestRecords.Scope(tenant, project: $"project-{Guid.NewGuid():N}");

        var outcome = await ConsolidationScenario.RunAsync(new PostgresExperienceRecordStore(fixture.DataSource), TestRecords.Authorize(tenant), scope);

        ConsolidationScenario.Verify(outcome);
    }

    [Fact]
    public async Task An_erased_source_is_found_first_and_left_out_of_the_next_merge()
    {
        var tenant = TestRecords.NewTenant();
        var scope = TestRecords.Scope(tenant, project: $"project-{Guid.NewGuid():N}");
        var authorization = TestRecords.Authorize(tenant);
        var store = new PostgresExperienceRecordStore(fixture.DataSource);

        var outcome = await ConsolidationScenario.RunAsync(store, authorization, scope, async id =>
            Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.DeleteAsync(authorization, scope, id, CancellationToken.None)).Outcome));

        ConsolidationScenario.Verify(outcome);
    }
}

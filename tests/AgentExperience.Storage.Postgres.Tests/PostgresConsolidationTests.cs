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
}

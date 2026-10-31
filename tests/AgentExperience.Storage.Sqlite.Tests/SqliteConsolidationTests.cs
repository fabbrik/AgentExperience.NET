using AgentExperience.Storage.Sqlite;
using AgentExperience.Tests.Shared;

namespace AgentExperience.Storage.Sqlite.Tests;

/// <summary>Story 28.6 over a SQLite file: consolidation, an idempotent re-run and a fold-in, through the record store's ports.</summary>
public sealed class SqliteConsolidationTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task Three_runs_become_one_lesson_a_re_run_does_nothing_and_a_fourth_run_is_folded_in()
    {
        var scope = new Scope("consolidation-tenant", "app-1", $"project-{Guid.NewGuid():N}");
        var authorization = new AuthorizationContext("consolidation-tenant", "host", ["experience:write"], ConsolidationScenario.Now);

        var outcome = await ConsolidationScenario.RunAsync(new SqliteExperienceRecordStore(_database.ConnectionString), authorization, scope);

        ConsolidationScenario.Verify(outcome);
    }
}

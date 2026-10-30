using AgentExperience.Abstractions;
using AgentExperience.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace AgentExperience.CiTriage.Tests;

/// <summary>
/// Story 28.7: with <c>--storage sqlite:&lt;path&gt;</c>, a failure still waiting for its fix is triaged and its captured run
/// kept in the file's run store; a later pass, in a new container, finalizes that stored run once the fix has landed,
/// without triaging it again. Scripted: no network, no model.
/// </summary>
public sealed class DurableTriageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ci-triage-durable-" + Guid.NewGuid().ToString("N"));

    private string Database => Path.Combine(_directory, "experience.db");

    [Fact]
    public async Task A_failure_triaged_before_its_fix_is_finalized_by_a_later_pass_from_the_run_store()
    {
        var results = Path.Combine(_directory, "results");

        var first = await CiTriageProgram.MainAsync(["--scripted", "--storage", "sqlite:" + Database, "--results", results], TextWriter.Null, TextWriter.Null);
        var waiting = Ledger.Read(Path.Combine(results, Ledger.FileName)).Single(row => row.Branch == "feat/waiting");
        var runId = Replay.AwaitingRunId(waiting);
        var unfinalizedBetween = await UnfinalizedAsync();

        var output = new StringWriter();
        var second = await CiTriageProgram.MainAsync(
            ["--scripted", "--scripted-later", "--storage", "sqlite:" + Database, "--results", results], output, TextWriter.Null);
        var ledger = Ledger.Read(Path.Combine(results, Ledger.FileName));
        var settled = ledger.Single(row => row.Branch == "feat/waiting");

        Assert.Equal(0, first);
        Assert.Equal("pending", waiting.Label);
        Assert.Equal("-", waiting.Correct);
        Assert.False(waiting.Final);
        Assert.NotNull(runId);
        Assert.Contains(unfinalizedBetween, run => run.Run.RunId == runId);

        Assert.Equal(0, second);
        Assert.Equal(20, ledger.Count);
        Assert.Equal("code", settled.Label);
        Assert.Equal("true", settled.Correct);
        Assert.Contains("finalized Validated", settled.Note, StringComparison.Ordinal);
        Assert.True(settled.Final);
        Assert.Equal(waiting.Predicted, settled.Predicted);
        Assert.DoesNotContain(await UnfinalizedAsync(), run => run.Run.RunId == runId);
        Assert.Contains("SQLite", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_storage_that_is_not_one_of_the_three_is_a_usage_error()
    {
        var error = new StringWriter();
        Assert.Equal(1, await CiTriageProgram.MainAsync(["--storage", "redis:x"], TextWriter.Null, error));
        Assert.Contains("--storage needs", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await CiTriageProgram.MainAsync(["--scripted-later"], TextWriter.Null, TextWriter.Null));
    }

    [Theory]
    [InlineData("awaiting fix: run ")]
    [InlineData("awaiting fix: run 1234")]
    [InlineData("note; awaiting fix: run not-a-guid-at-all-but-long-enough-x")]
    public void A_truncated_or_malformed_awaiting_note_names_no_run(string note) =>
        Assert.Null(Replay.AwaitingRunId(new LedgerRow(1, DateTimeOffset.UnixEpoch, "b", "pending", "-", "flaky", "-", 0m, note)));

    private async Task<IReadOnlyList<StoredExperienceRun>> UnfinalizedAsync()
    {
        var store = new SqliteExperienceRunStore(ExperienceSqliteSchema.ConnectionStringFor(Database));
        var authorization = new AuthorizationContext(TriageHost.Tenant, "test", [], DateTimeOffset.UtcNow);
        var page = await store.ListUnfinalizedAsync(authorization, new ExperienceRunQuery(TriageHost.Scope), CancellationToken.None);
        return page.Runs;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

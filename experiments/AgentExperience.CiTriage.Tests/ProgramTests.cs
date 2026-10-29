namespace AgentExperience.CiTriage.Tests;

public sealed class ProgramTests : IDisposable
{
    private readonly string _results = Path.Combine(Path.GetTempPath(), "ci-triage-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Scripted_writes_the_ledger_and_a_report_with_per_arm_counts_and_NoEvidence()
    {
        // No OPENROUTER_API_KEY is needed and none is read: --scripted takes its GitHub data and its model from memory.
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await CiTriageProgram.MainAsync(["--scripted", "--results", _results], output, error);

        Assert.True(exit == 0, error.ToString());
        var ledger = Ledger.Read(Path.Combine(_results, Ledger.FileName));
        Assert.Equal(20, ledger.Count);
        var report = File.ReadAllText(Directory.GetFiles(_results, "ci-triage-*.md").Single());
        Assert.Contains("**Scripted run.**", report, StringComparison.Ordinal);
        Assert.Contains("| treatment | ", report, StringComparison.Ordinal);
        Assert.Contains("| holdout | ", report, StringComparison.Ordinal);
        Assert.Contains("Verdict: **NoEvidence**", report, StringComparison.Ordinal);
        Assert.Contains("for lack of runs", report, StringComparison.Ordinal);
        Assert.Contains("Pending: 0, Unverified: 0", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_argument_is_refused()
    {
        var error = new StringWriter();
        Assert.Equal(1, await CiTriageProgram.MainAsync(["--frobnicate"], TextWriter.Null, error));
        Assert.Contains("Unknown argument", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_ledger_row_round_trips()
    {
        var row = new LedgerRow(42, new DateTimeOffset(2026, 10, 1, 2, 3, 4, TimeSpan.Zero), "feat/x", "code", "holdout", "code:a.cs,b.cs", "true", 0.001234m, "finalized Validated");
        Assert.Equal(row, LedgerRow.Parse(row.ToTsv()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_results))
        {
            Directory.Delete(_results, recursive: true);
        }
    }
}

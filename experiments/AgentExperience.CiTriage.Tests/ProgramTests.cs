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
    public async Task Scripted_ignores_the_postgres_variable_and_flag()
    {
        // A connection string that could never be reached: --scripted must not even try it.
        var before = Environment.GetEnvironmentVariable(CiTriageProgram.PostgresVariable);
        Environment.SetEnvironmentVariable(CiTriageProgram.PostgresVariable, "Host=203.0.113.1;Port=1;Timeout=1");
        try
        {
            var exit = await CiTriageProgram.MainAsync(["--scripted", "--postgres", "Host=203.0.113.1;Port=1;Timeout=1", "--results", _results], TextWriter.Null, TextWriter.Null);
            Assert.Equal(0, exit);
            Assert.Contains("- Storage: in-memory", File.ReadAllText(Directory.GetFiles(_results, "ci-triage-*.md").Single()), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CiTriageProgram.PostgresVariable, before);
        }
    }

    [Theory]
    [InlineData("--max-calls", "99999999999999999999999")]
    [InlineData("--max-tokens", "99999999999999999999999999999")]
    [InlineData("--max-calls", "1.5")]
    public async Task A_cap_that_is_not_a_whole_number_in_range_is_a_usage_error(string flag, string value)
    {
        var error = new StringWriter();
        Assert.Equal(1, await CiTriageProgram.MainAsync([flag, value], TextWriter.Null, error));
        Assert.Contains("positive whole number", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pending", "skipped: pending", "", false)]
    [InlineData("unavailable", "skipped: unavailable", "", false)]
    [InlineData("code", "skipped: stopped", "", false)]
    [InlineData("code", "skipped: fetch-error", "", false)]
    [InlineData("code", "unparsed", "error: HttpRequestException", false)]
    [InlineData("ambiguous", "skipped: ambiguous", "", true)]
    [InlineData("code", "skipped: no-log", "", true)]
    [InlineData("code", "skipped: before-resume", "", true)]
    [InlineData("code", "code:a.cs", "finalized Validated", true)]
    public void Only_settled_rows_are_final(string label, string predicted, string note, bool final) =>
        Assert.Equal(final, new LedgerRow(1, DateTimeOffset.UnixEpoch, "b", label, "-", predicted, "-", 0m, note).Final);

    [Fact]
    public void A_malformed_trailing_line_is_ignored_but_one_in_the_middle_is_not()
    {
        Directory.CreateDirectory(_results);
        var path = Path.Combine(_results, Ledger.FileName);
        var row = new LedgerRow(42, new DateTimeOffset(2026, 10, 1, 2, 3, 4, TimeSpan.Zero), "feat/x", "code", "holdout", "code:a.cs", "true", 0m, "finalized Validated");
        File.WriteAllText(path, LedgerRow.Header + "\n" + row.ToTsv() + "\n43\t2026-10-01T0");
        Assert.Equal([row], Ledger.Read(path));

        File.WriteAllText(path, LedgerRow.Header + "\n43\tbroken\n" + row.ToTsv() + "\n");
        Assert.Throws<FormatException>(() => Ledger.Read(path));
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

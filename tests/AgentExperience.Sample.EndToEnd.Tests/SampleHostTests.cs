using System.Globalization;
using AgentExperience.Abstractions;
using Npgsql;

namespace AgentExperience.Sample.EndToEnd.Tests;

/// <summary>
/// <see cref="SampleHost.RunAsync(TextWriter, string?, CancellationToken)"/> itself: the exit code
/// and the one line it writes.
/// </summary>
/// <remarks>
/// This is the entry point <c>Program.cs</c> calls and the one a CI step runs, and until now every
/// one of its paths but the unreachable-DSN one was executed by no test at all. Its success path
/// could have printed "nothing to see here" and returned 2 with the suite still green.
/// </remarks>
public class SampleHostTests
{
    /// <summary>A closed port on the loopback interface: no Docker, no database, no waiting.</summary>
    private const string UnreachableDsn =
        "Host=127.0.0.1;Port=1;Username=sample;Password=sample;Database=sample;Timeout=2;Command Timeout=2";

    [Fact]
    public async Task Success_path_exits_zero_and_writes_exactly_the_rendered_transcript()
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await SampleHost.RunAsync(output, postgresConnectionString: null, CancellationToken.None);

        Assert.Equal(0, exitCode);

        // Byte for byte what Render() produces, and therefore byte for byte the checked-in golden
        // transcript. Nothing is added, trimmed, or re-wrapped on the way to standard output.
        var expected = (await SampleFacts.RunAsync()).Transcript.Render();
        Assert.Equal(expected, output.ToString());
        Assert.Equal(GoldenTranscriptTests.ReadGolden(), output.ToString());
    }

    /// <summary>
    /// Every failure arm, driven through the internal seam so none of them needs a database, a
    /// network, or a corrupted store to reach.
    /// </summary>
    /// <param name="failure">Which exception the composed run throws.</param>
    /// <param name="expected">A fragment the one printed line must contain.</param>
    [Theory]
    [InlineData("stage", "did not happen as the sample narrates it")]
    [InlineData("store", "could not be prepared")]
    [InlineData("npgsql", "could not be reached")]
    [InlineData("unexpected", "failed before its seven stages finished")]
    public async Task Every_failure_exits_one_with_one_line_and_no_transcript(string failure, string expected)
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await SampleHost.RunAsync(
            output,
            _ => Task.FromException<SampleExecution>(FailureFor(failure)),
            CancellationToken.None);

        var text = output.ToString();

        Assert.Equal(1, exitCode);
        Assert.Contains(expected, text, StringComparison.Ordinal);
        Assert.DoesNotContain("[1] capture", text, StringComparison.Ordinal);

        // One line, and nothing that leaks this machine: no stack frames, no absolute paths.
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.DoesNotContain("   at ", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.DirectorySeparatorChar + "Users" + Path.DirectorySeparatorChar, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AppContext.BaseDirectory, text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A <c>SocketException</c> or a <see cref="TimeoutException"/> escaping Npgsql unwrapped
    /// used to end the process with a stack trace full of this machine's absolute paths.
    /// </summary>
    [Fact]
    public async Task An_exception_no_arm_names_still_exits_one()
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await SampleHost.RunAsync(
            output,
            _ => Task.FromException<SampleExecution>(new System.Net.Sockets.SocketException(10061)),
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("SocketException", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Postgres_mode_fails_loudly_on_unreachable_dsn()
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var exitCode = await SampleHost.RunAsync(output, UnreachableDsn, CancellationToken.None);
        var text = output.ToString();

        Assert.Equal(1, exitCode);
        Assert.Equal(SampleStorageMode.Postgres, SampleHost.ModeFor(UnreachableDsn));
        Assert.Contains(SampleHost.PostgresEnvironmentVariable, text, StringComparison.Ordinal);
        Assert.Contains("does not fall back to its in-memory doubles", text, StringComparison.Ordinal);

        // No silent fall back: not one stage was printed.
        Assert.DoesNotContain("[1] capture", text, StringComparison.Ordinal);
        Assert.DoesNotContain("InjectionOutcome.Injected", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A value that is not a connection string at all, which is a different failure from a database
    /// that cannot be reached and now says so.
    /// </summary>
    /// <param name="malformed">Something a reader might plausibly export by mistake.</param>
    [Theory]
    [InlineData("this is not a connection string")]
    [InlineData("postgres://localhost/db")]
    [InlineData("Host=localhost;NoSuchKeyword=1")]
    public async Task Postgres_mode_fails_loudly_on_a_malformed_dsn(string malformed)
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var exitCode = await SampleHost.RunAsync(output, malformed, CancellationToken.None);
        var text = output.ToString();

        Assert.Equal(1, exitCode);
        Assert.Equal(SampleStorageMode.Postgres, SampleHost.ModeFor(malformed));
        Assert.Contains(SampleHost.PostgresEnvironmentVariable, text, StringComparison.Ordinal);
        Assert.DoesNotContain("[1] capture", text, StringComparison.Ordinal);

        // The connection string's own text is never echoed: it is the one environment variable in
        // this sample that routinely carries a password.
        Assert.DoesNotContain(malformed, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// An <see cref="ArgumentException"/> from a stage is not a bad connection string, though the
    /// arm that used to catch it filtered on the storage mode and reported every one as such.
    /// </summary>
    [Fact]
    public async Task An_argument_exception_from_a_stage_is_not_reported_as_a_bad_connection_string()
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await SampleHost.RunAsync(
            output,
            _ => Task.FromException<SampleExecution>(new ArgumentNullException("someStageArgument")),
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("does not hold a usable connection string", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("ArgumentNullException", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_connection_strings_select_the_in_memory_mode(string? value) =>
        Assert.Equal(SampleStorageMode.InMemory, SampleHost.ModeFor(value));

    private static Exception FailureFor(string failure) => failure switch
    {
        "stage" => new SampleStageFailedException("Stage 4 did not happen as the sample narrates it: FinalizeAsync returned Quarantined."),
        "store" => new ExperienceStoreException("create", new InvalidOperationException("no connection")),
        "npgsql" => new NpgsqlException("connection refused"),
        "unexpected" => new TimeoutException("the operation timed out"),
        _ => throw new ArgumentOutOfRangeException(nameof(failure)),
    };
}

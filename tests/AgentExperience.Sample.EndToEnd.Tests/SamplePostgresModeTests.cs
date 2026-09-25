using System.Globalization;

namespace AgentExperience.Sample.EndToEnd.Tests;

/// <summary>
/// The opt-in PostgreSQL mode, against a real stock <c>postgres</c> container.
/// </summary>
/// <remarks>
/// <para>
/// Frozen rule 3 says the environment variable changes only the port registrations and that every
/// other line of the sample is shared between the two modes. That is a claim about behaviour, and
/// the only way to hold it is to run both modes and compare what they printed -- which is what the
/// first test here does, line for line, against the same checked-in transcript the default mode is
/// compared against.
/// </para>
/// <para>
/// Docker is required, as it is for the repository's other container-backed tests.
/// </para>
/// </remarks>
[Collection(SamplePostgresCollection.Name)]
public class SamplePostgresModeTests(SamplePostgresFixture fixture)
{
    [Fact]
    public async Task Postgres_mode_prints_the_same_seven_stages_as_the_default_mode()
    {
        var dsn = await fixture.CreateDatabaseAsync("sample_ok");
        var output = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await SampleHost.RunAsync(output, dsn, CancellationToken.None);
        var text = Normalize(output.ToString());

        Assert.Equal(0, exitCode);

        // The migrator ran first, exactly as a host would run it on startup, and said so.
        var lines = text.Split('\n');
        Assert.StartsWith("PostgreSQL mode: schema migration applied ", lines[0], StringComparison.Ordinal);

        // Everything after that line is the transcript, and it is the checked-in one with a single
        // substitution: the header line that names the ports. Every identifier, score, count and
        // byte budget below it is identical, which is what "only the port registrations change"
        // means when it is a fact rather than a sentence.
        var transcript = text[(lines[0].Length + 1)..];
        Assert.Equal(ExpectedPostgresTranscript(), transcript);
    }

    /// <summary>
    /// Spec Change 4: the sample's identifiers are fixtures, so a second execution against a store
    /// that kept the first one's record asks to finalize a run that is already final.
    /// </summary>
    [Fact]
    public async Task A_second_execution_against_the_same_database_is_an_already_finalized_replay()
    {
        var dsn = await fixture.CreateDatabaseAsync("sample_replay");

        var first = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(0, await SampleHost.RunAsync(first, dsn, CancellationToken.None));

        var second = new StringWriter(CultureInfo.InvariantCulture);
        var exitCode = await SampleHost.RunAsync(second, dsn, CancellationToken.None);
        var text = second.ToString();

        Assert.Equal(1, exitCode);
        Assert.Contains("already finalized as experience", text, StringComparison.Ordinal);
        Assert.Contains(SampleHost.PostgresEnvironmentVariable, text, StringComparison.Ordinal);

        // Stage 4 is where it stops, and it never narrates the three stages after it.
        Assert.DoesNotContain("InjectionOutcome.Injected", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ExperienceReuseFeedbackOutcome.Recorded", text, StringComparison.Ordinal);
    }

    /// <summary>The golden transcript with the one line that names the ports swapped for the PostgreSQL one.</summary>
    private static string ExpectedPostgresTranscript() => GoldenTranscriptTests.ReadGolden().Replace(
        "ports:  in-memory demonstration doubles (set AGENTEXPERIENCE_SAMPLE_POSTGRES to a connection string for the PostgreSQL adapters)",
        "ports:  the PostgreSQL adapters, migrated before the first stage",
        StringComparison.Ordinal);

    /// <summary>
    /// The migration note is written with <see cref="TextWriter.WriteLine()"/>, which uses the
    /// platform's newline; the transcript below it never does. Only the note is normalized.
    /// </summary>
    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}

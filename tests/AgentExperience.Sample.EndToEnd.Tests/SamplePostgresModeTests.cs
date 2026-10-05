using System.Globalization;
using System.Text.RegularExpressions;

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
        // substitution: the header line that names the ports. Every identifier, score and count below it
        // is identical, which is what "only the port registrations change" means when it is a fact rather
        // than a sentence. The one number allowed to move is the injected block's size, by a few bytes: its
        // Matched: line names each store's own match signals, and the PostgreSQL text channel scores relevance
        // on its own scale. The test below pins that the Matched: line is the block's only difference.
        var transcript = text[(lines[0].Length + 1)..];
        var expected = ExpectedPostgresTranscript();
        var goldenSize = BlockSize(expected);
        var postgresSize = BlockSize(transcript);
        Assert.InRange(postgresSize - goldenSize, -MaxBlockSizeDifference, MaxBlockSizeDifference);
        Assert.Equal(WithBlockSize(expected, goldenSize, postgresSize), transcript);
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

    /// <summary>
    /// The injected block is the in-memory mode's block, byte for byte, but for its <c>Matched:</c> line, which names
    /// each store's own match signals.
    /// </summary>
    [Fact]
    public async Task Postgres_mode_injects_the_same_block_but_for_the_matched_line()
    {
        var dsn = await fixture.CreateDatabaseAsync("sample_block");

        var postgres = (await SampleHost.ExecuteAsync(TextWriter.Null, dsn, CancellationToken.None)).Run.InjectedBlock!;
        var memory = (await SampleHost.ExecuteAsync(TextWriter.Null, null, CancellationToken.None)).Run.InjectedBlock!;

        static string Matched(string block) =>
            Assert.Single(block.Split('\n'), line => line.StartsWith("Matched: ", StringComparison.Ordinal));
        static string[] Unmatched(string block) =>
            block.Split('\n').Where(line => !line.StartsWith("Matched: ", StringComparison.Ordinal)).ToArray();

        Assert.Equal(Unmatched(memory), Unmatched(postgres));
        Assert.Equal("Matched: text relevance 1.00", Matched(memory));
        Assert.Matches(@"^Matched: text relevance 0\.\d\d$", Matched(postgres));
        Assert.InRange(
            System.Text.Encoding.UTF8.GetByteCount(postgres) - System.Text.Encoding.UTF8.GetByteCount(memory),
            -MaxBlockSizeDifference,
            MaxBlockSizeDifference);
    }

    /// <summary>How far the PostgreSQL mode's block may differ in size from the in-memory one: its Matched: line only.</summary>
    private const int MaxBlockSizeDifference = 4;

    /// <summary>The injected block's size as stage 6 prints it.</summary>
    private static int BlockSize(string transcript) => int.Parse(
        Regex.Match(transcript, @"byte budget used: (\d+) of", RegexOptions.CultureInvariant).Groups[1].Value,
        CultureInfo.InvariantCulture);

    /// <summary>The transcript with the block size stage 6 prints twice replaced.</summary>
    private static string WithBlockSize(string transcript, int from, int to) => transcript
        .Replace($"byte budget used: {from} of", $"byte budget used: {to} of", StringComparison.Ordinal)
        .Replace($"read out of the {from} bytes", $"read out of the {to} bytes", StringComparison.Ordinal);

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

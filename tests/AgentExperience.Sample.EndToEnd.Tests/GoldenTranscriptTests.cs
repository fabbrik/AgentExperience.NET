using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace AgentExperience.Sample.EndToEnd.Tests;

/// <summary>
/// Compares the sample's whole rendered transcript against a transcript checked into the
/// repository, byte for byte.
/// </summary>
/// <remarks>
/// <para>
/// This is the assertion the rest of the sample's tests lean on. Every other test here names one
/// property; this one names all of them at once, because the transcript is every identifier,
/// timestamp, score, count, and byte budget the sample prints. A change to any of them shows up as
/// a diff a human has to look at and accept, which is exactly the review a demo's output deserves.
/// </para>
/// <para>
/// <b>It never updates itself.</b> Set <c>AGENTEXPERIENCE_SAMPLE_GOLDEN_UPDATE=1</c> to rewrite
/// <c>GoldenTranscript.txt</c> from the current sample, then read the diff and commit it on
/// purpose:
/// </para>
/// <code>
/// AGENTEXPERIENCE_SAMPLE_GOLDEN_UPDATE=1 dotnet test tests/AgentExperience.Sample.EndToEnd.Tests
/// git diff tests/AgentExperience.Sample.EndToEnd.Tests/GoldenTranscript.txt
/// </code>
/// </remarks>
public class GoldenTranscriptTests
{
    /// <summary>The environment variable that rewrites the golden file instead of only comparing against it.</summary>
    public const string UpdateVariable = "AGENTEXPERIENCE_SAMPLE_GOLDEN_UPDATE";

    private const string GoldenResourceName = "AgentExperience.Sample.EndToEnd.Tests.GoldenTranscript.txt";

    [Fact]
    public async Task Sample_renders_the_checked_in_transcript_byte_for_byte()
    {
        var rendered = (await SampleFacts.RunAsync()).Transcript.Render();

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            await File.WriteAllTextAsync(GoldenSourcePath(), rendered, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        var golden = ReadGolden();

        Assert.Equal(golden, rendered);
        Assert.Equal(Encoding.UTF8.GetBytes(golden), Encoding.UTF8.GetBytes(rendered));

        // The transcript is one long LF-separated string on every platform, never CRLF: "run it twice
        // and the bytes match" has to survive being run on a different operating system.
        Assert.DoesNotContain('\r', rendered);
    }

    /// <summary>
    /// The determinism guarantee, under three current cultures rather than only the machine's own.
    /// </summary>
    /// <remarks>
    /// Every number the transcript prints is formatted with
    /// <see cref="CultureInfo.InvariantCulture"/>, and the only way to know that is to run under a
    /// culture that would format it differently. <c>de-DE</c> writes <c>0,667</c> for the reuse
    /// confidence and the completion score; <c>tr-TR</c> is the classic case for culture-sensitive
    /// casing, which the transcript also does (<c>IsDurable=true</c> comes from
    /// <c>ToLowerInvariant</c>, and a plain <c>ToLower</c> under <c>tr-TR</c> would spell it
    /// <c>ı</c>).
    /// </remarks>
    /// <param name="cultureName">The culture to force for the duration of the run, or empty for the invariant culture.</param>
    [Theory]
    [InlineData("")]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public async Task Sample_renders_the_same_transcript_under_any_culture(string cultureName)
    {
        var culture = cultureName.Length == 0 ? CultureInfo.InvariantCulture : new CultureInfo(cultureName);
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;

        string rendered;
        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            rendered = (await SampleFacts.RunAsync()).Transcript.Render();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }

        Assert.Equal(ReadGolden(), rendered);
    }

    /// <summary>The golden transcript as it was checked in.</summary>
    internal static string ReadGolden()
    {
        using var stream = typeof(GoldenTranscriptTests).Assembly.GetManifestResourceStream(GoldenResourceName)
            ?? throw new InvalidOperationException($"'{GoldenResourceName}' is not embedded in the sample's test assembly.");
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Where the golden file lives in the working tree, for the deliberate regeneration path only.
    /// The assertion itself reads the embedded copy, so a machine with no source tree still runs it.
    /// </summary>
    private static string GoldenSourcePath([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "GoldenTranscript.txt");
}

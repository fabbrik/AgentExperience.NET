using System.Text.RegularExpressions;

namespace AgentExperience.Sample.EndToEnd.Tests;

/// <summary>
/// Frozen rule 6: the sample demonstrates functional capture and reuse with deterministic fixtures
/// and measures nothing about real-model quality, so neither its printed report nor the prose that
/// introduces it may claim otherwise.
/// </summary>
/// <remarks>
/// <para>
/// A deny-list is a blunt instrument and it is not the real defence -- a rephrased claim walks
/// straight past any list of words. The real defence is
/// <see cref="GoldenTranscriptTests.Sample_renders_the_checked_in_transcript_byte_for_byte"/>: a new
/// sentence in the report cannot reach the repository without appearing as a diff in the checked-in
/// transcript that somebody had to accept. The list below is the cheap second gate, widened past
/// the story's original five literals because <c>improved accuracy</c> does not catch
/// <c>improves accuracy</c> (Spec Change 5).
/// </para>
/// <para>
/// The prose is scanned as well as the report, and that includes the repository README's own
/// "Run the sample" section, which is the first thing a stranger reads about the sample and was
/// scanned by nothing at all.
/// </para>
/// </remarks>
public class ImprovementClaimTests
{
    /// <summary>Words that would turn the sample's own results into a comparative-quality claim.</summary>
    private static readonly string[] ImprovementClaims =
    [
        // The story's original five.
        "faster", "better", "improved accuracy", "outperform", "% improvement",
        // Spec Change 5: the original five are trivially evadable by inflection.
        "learns", "improves", "smarter", "speedup", "speed-up", "sooner", "reduces",
        // Comparative phrasings a reviewer actually produced while probing this test.
        "fewer tool calls", "in half", "more accurate", "higher quality", "twice as",
    ];

    /// <summary>Shapes of claim that no single word catches.</summary>
    private static readonly (string Name, Regex Pattern)[] ImprovementPatterns =
    [
        ("cut ... time", new Regex(@"\bcut\b[^.\n]{0,60}\btimes?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        ("N% fewer/less/faster", new Regex(@"\b\d+(\.\d+)?\s*%\s*(fewer|less|faster|better|more|improvement|reduction)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        ("N times faster/better", new Regex(@"\b\d+(\.\d+)?\s*x\s+(faster|better)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
    ];

    public static TheoryData<string> ScannedSources() => new("report", "sample README", "repository README");

    [Theory]
    [MemberData(nameof(ScannedSources))]
    public async Task Nothing_the_sample_says_about_itself_is_an_improvement_claim(string source)
    {
        var text = source switch
        {
            "report" => (await SampleFacts.RunAsync()).Transcript.Render(),
            "sample README" => SampleReadme(),
            "repository README" => RepositorySampleSection(),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };

        // A scan of nothing passes trivially, so the text has to be there first.
        Assert.True(text.Length > 200, $"The {source} came back as {text.Length} characters, so the scan below proves nothing.");

        foreach (var claim in ImprovementClaims)
        {
            Assert.DoesNotContain(claim, text, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var (name, pattern) in ImprovementPatterns)
        {
            var match = pattern.Match(text);
            Assert.False(match.Success, $"The {source} contains a '{name}' claim: \"{match.Value}\".");
        }
    }

    [Fact]
    public async Task The_report_and_both_readmes_say_what_was_not_measured()
    {
        var report = (await SampleFacts.RunAsync()).Transcript.Render();

        // It is not enough to avoid the words: the report has to say what it did not measure.
        Assert.Contains("does not demonstrate", report, StringComparison.Ordinal);
        Assert.Contains("story 4.4", report, StringComparison.Ordinal);
        Assert.Contains("story 4.4", SampleReadme(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not", RepositorySampleSection(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The sample's own README, embedded in the sample assembly.</summary>
    private static string SampleReadme() =>
        ReadResource(typeof(SampleHost).Assembly, "AgentExperience.Sample.EndToEnd.README.md");

    /// <summary>
    /// The repository README's "Run the sample" section, from its heading up to the next one.
    /// </summary>
    /// <remarks>
    /// Only that section is scanned. The rest of a 1100-line README is about the library, not about
    /// what this sample measured, and words like "better" are legitimate there.
    /// </remarks>
    private static string RepositorySampleSection()
    {
        var readme = ReadResource(typeof(ImprovementClaimTests).Assembly, "AgentExperience.Sample.EndToEnd.Tests.RootREADME.md");

        const string Heading = "### Run the sample";
        var start = readme.IndexOf(Heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"The repository README has no '{Heading}' section to scan.");

        var rest = readme[(start + Heading.Length)..];
        var end = Regex.Match(rest, @"^#{1,6} ", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        return end.Success ? rest[..end.Index] : rest;
    }

    private static string ReadResource(System.Reflection.Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"'{name}' is not embedded in {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

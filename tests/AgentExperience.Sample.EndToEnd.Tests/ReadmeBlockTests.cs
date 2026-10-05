using System.Runtime.CompilerServices;
using System.Text;

namespace AgentExperience.Sample.EndToEnd.Tests;

/// <summary>
/// Pins the Historical Reference block the repository README shows under "What an agent sees" to
/// the block the sample really hands run B's model, byte for byte.
/// </summary>
/// <remarks>
/// <para>
/// The README's example is not hand-written: it is <c>GoldenInjectedBlock.txt</c>, which is the
/// sample's own injected block (the default, compact rendering). Two assertions keep the three in
/// step: the sample still renders the golden, and the README still contains the golden verbatim,
/// inside a <c>text</c> fence.
/// </para>
/// <para>
/// <b>It never updates itself.</b> Set <c>AGENTEXPERIENCE_SAMPLE_GOLDEN_UPDATE=1</c> to rewrite
/// <c>GoldenInjectedBlock.txt</c> from the current sample, then paste it into the README and
/// commit both on purpose.
/// </para>
/// </remarks>
public class ReadmeBlockTests
{
    private const string GoldenResourceName = "AgentExperience.Sample.EndToEnd.Tests.GoldenInjectedBlock.txt";
    private const string ReadmeResourceName = "AgentExperience.Sample.EndToEnd.Tests.RootREADME.md";

    /// <summary>The README's own limit on the example, so the first thing a reader sees stays short.</summary>
    private const int MaxLines = 25;

    [Fact]
    public async Task The_sample_renders_the_pinned_block_byte_for_byte()
    {
        var block = (await SampleFacts.RunAsync()).InjectedBlock();

        if (Environment.GetEnvironmentVariable(GoldenTranscriptTests.UpdateVariable) == "1")
        {
            await File.WriteAllTextAsync(GoldenSourcePath(), block, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        var golden = Read(GoldenResourceName);
        Assert.Equal(Encoding.UTF8.GetBytes(golden), Encoding.UTF8.GetBytes(block));
        Assert.DoesNotContain('\r', block);
        var lines = golden.TrimEnd('\n').Split('\n').Length;
        Assert.True(lines <= MaxLines, $"The pinned block has {lines} lines; the README shows at most {MaxLines}.");
    }

    [Fact]
    public void The_readme_shows_the_pinned_block_verbatim()
    {
        var golden = Read(GoldenResourceName);
        var readme = Read(ReadmeResourceName).Replace("\r\n", "\n", StringComparison.Ordinal);

        // The block ends with a newline; the fence closes on the line after its last line.
        Assert.Contains("```text\n" + golden + "```", readme, StringComparison.Ordinal);
    }

    private static string Read(string name)
    {
        using var stream = typeof(ReadmeBlockTests).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"'{name}' is not embedded in the sample's test assembly.");
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return reader.ReadToEnd();
    }

    private static string GoldenSourcePath([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "GoldenInjectedBlock.txt");
}

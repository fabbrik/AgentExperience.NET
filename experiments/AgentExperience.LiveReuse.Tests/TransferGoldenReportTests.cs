using System.Runtime.CompilerServices;
using System.Text;
using AgentExperience.LiveReuse.Harness;

namespace AgentExperience.LiveReuse.Tests;

/// <summary>
/// The transfer experiment's offline scripted run, golden-filed: the full Markdown report, byte for byte, against the
/// copy checked in beside this file (the pattern of <c>tests/AgentExperience.ReuseBaseline/Tests/GoldenReportTests.cs</c>).
/// </summary>
/// <remarks>
/// <para>
/// What the golden pins is what the library's retrieval does with the task texts as written: for every evaluation
/// instance, whether the same-cluster record reached the memory-enabled block and at which rank, each condition's failed
/// attempts, the three comparisons and the conclusion. A change anywhere in retrieval, ranking, rendering or the harness
/// that moves any of them is a failing diff a human has to read.
/// </para>
/// <para>
/// <b>It never updates itself.</b> Set <c>AGENTEXPERIENCE_TRANSFER_GOLDEN_UPDATE=1</c> to rewrite the golden file from the
/// current harness, then read the diff and commit it on purpose.
/// </para>
/// </remarks>
public sealed class TransferGoldenReportTests
{
    /// <summary>The environment variable that rewrites the golden file instead of only comparing against it.</summary>
    public const string UpdateVariable = "AGENTEXPERIENCE_TRANSFER_GOLDEN_UPDATE";

    private const string GoldenFileName = "TransferGoldenReport.md";

    private const string GoldenResourceName = "AgentExperience.LiveReuse.Tests.TransferGoldenReport.md";

    /// <summary>The descriptor the <c>--scripted</c> command uses, so the golden is the report that command prints.</summary>
    private static readonly RunDescriptor Scripted = new("scripted", ScriptedOperatorModel.ModelId, "none (offline)", null, null, "none: scripted run, nothing is billed");

    [Fact]
    public async Task The_offline_scripted_run_renders_the_checked_in_report_byte_for_byte()
    {
        var result = await TransferTests.RunScriptedAsync(new ScriptedOperatorModel(), descriptor: Scripted);
        var rendered = TransferReport.Markdown(result);
        Regenerate(rendered);

        var golden = ReadResource();
        Assert.Equal(golden, rendered);
        Assert.Equal(Encoding.UTF8.GetBytes(golden), Encoding.UTF8.GetBytes(rendered));
        Assert.DoesNotContain('\r', rendered);

        // What the golden exists to show, asserted too, so a regeneration that lost it fails rather than recording a run
        // that proves nothing: every instance's retrieval row, the three comparisons and the conclusion.
        Assert.All(result.Trials.Select(trial => trial.Run.Instance).Distinct(), instance =>
            Assert.Contains($"\n| {instance} | {TransferTaskSet.Current.EvaluationInstances[instance].Service} | ", rendered, StringComparison.Ordinal));
        Assert.Contains("### Reference comparison: `memory-enabled` against `memory-disabled`", rendered, StringComparison.Ordinal);
        Assert.Contains("### Content comparison: `memory-enabled` against `memory-placebo`", rendered, StringComparison.Ordinal);
        Assert.Contains("### Mismatched-trait control: `mismatched-trait` against `memory-disabled`", rendered, StringComparison.Ordinal);
        Assert.Contains($"**Overall conclusion under the pre-registered rule: {result.Conclusion}.**", rendered, StringComparison.Ordinal);
        Assert.Contains("SCRIPTED RUN. No model was called.", rendered, StringComparison.Ordinal);
    }

    private static void Regenerate(string rendered)
    {
        if (Environment.GetEnvironmentVariable(UpdateVariable) != "1")
        {
            return;
        }

        File.WriteAllText(Path.Combine(SourceDirectory(), GoldenFileName), rendered, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string ReadResource()
    {
        using var stream = typeof(TransferGoldenReportTests).Assembly.GetManifestResourceStream(GoldenResourceName)
            ?? throw new InvalidOperationException($"'{GoldenResourceName}' is not embedded in the test assembly.");
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return reader.ReadToEnd();
    }

    /// <summary>Where the golden file lives in the working tree, for the deliberate regeneration path only.</summary>
    private static string SourceDirectory([CallerFilePath] string thisFile = "") => Path.GetDirectoryName(thisFile)!;
}

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using AgentExperience.MicrosoftAgentFramework.Injection;

namespace AgentExperience.Sample.QuickStart.Tests;

/// <summary>
/// The quick-start demo in its default, stand-in mode: run 2's improvement comes from the lesson the library stored,
/// retrieved and injected, and the output is the checked-in one, byte for byte.
/// </summary>
/// <remarks>
/// <b>The golden never updates itself.</b> Set <c>AGENTEXPERIENCE_QUICKSTART_GOLDEN_UPDATE=1</c> to rewrite
/// <c>GoldenOutput.txt</c> from the current demo, then read the diff and commit it on purpose:
/// <code>
/// AGENTEXPERIENCE_QUICKSTART_GOLDEN_UPDATE=1 dotnet test tests/AgentExperience.Sample.QuickStart.Tests
/// git diff tests/AgentExperience.Sample.QuickStart.Tests/GoldenOutput.txt
/// </code>
/// </remarks>
public class QuickStartTests
{
    public const string UpdateVariable = "AGENTEXPERIENCE_QUICKSTART_GOLDEN_UPDATE";

    private const string GoldenResourceName = "AgentExperience.Sample.QuickStart.Tests.GoldenOutput.txt";

    [Fact]
    public async Task Run_two_fails_less_because_its_model_was_handed_run_ones_lesson()
    {
        var (runs, _) = await RunAsync();

        Assert.Equal(2, runs.Count);
        Assert.True(runs[0].FailedAttempts >= 1, "Run 1 had no memory, so the stand-in should have hit a wrong strategy first.");
        Assert.Null(runs[0].Block);
        Assert.True(runs[0].Attempts[^1].Succeeded);

        Assert.Equal(0, runs[1].FailedAttempts);
        Assert.Equal("wait-for-lock", Assert.Single(runs[1].Attempts).Strategy);

        // What changed run 2 is in its model's input: the library's block, carrying the working strategy.
        var block = Assert.IsType<string>(runs[1].Block);
        Assert.Contains(HistoricalReferenceWriter.BlockBegin, block, StringComparison.Ordinal);
        Assert.Contains("run_refund_check(strategy=\"wait-for-lock\")", block, StringComparison.Ordinal);
        Assert.Equal("wait-for-lock", ScriptedModel.Remembered(block));
    }

    [Fact]
    public async Task Without_a_block_the_stand_in_tries_the_listed_order()
    {
        // The stand-in's own prior is the listed order; only an injected block changes it.
        Assert.Null(ScriptedModel.Remembered(null));
        var (runs, _) = await RunAsync();
        Assert.Equal(RefundDesk.Strategies.Take(runs[0].Attempts.Count), runs[0].Attempts.Select(attempt => attempt.Strategy));
    }

    [Fact]
    public async Task The_demo_prints_the_checked_in_output_byte_for_byte()
    {
        var (_, rendered) = await RunAsync();

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            await File.WriteAllTextAsync(GoldenSourcePath(), rendered, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Assert.Equal(ReadGolden(), rendered);
        Assert.DoesNotContain('\r', rendered);
    }

    /// <summary>Every number the output prints is culture-invariant: <c>de-DE</c> would write <c>0,67</c>.</summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public async Task The_demo_prints_the_same_output_under_any_culture(string cultureName)
    {
        var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        string rendered;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(cultureName);
            (_, rendered) = await RunAsync();
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = previous;
        }

        Assert.Equal(ReadGolden(), rendered);
    }

    private static async Task<(IReadOnlyList<QuickStartRun> Runs, string Output)> RunAsync()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        using var model = new ScriptedModel();
        var runs = await global::QuickStart.RunAsync(model, global::QuickStart.StandInLabel, output);
        return (runs, output.ToString());
    }

    private static string ReadGolden()
    {
        using var stream = typeof(QuickStartTests).Assembly.GetManifestResourceStream(GoldenResourceName)
            ?? throw new InvalidOperationException($"'{GoldenResourceName}' is not embedded in the test assembly.");
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return reader.ReadToEnd();
    }

    private static string GoldenSourcePath([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "GoldenOutput.txt");
}

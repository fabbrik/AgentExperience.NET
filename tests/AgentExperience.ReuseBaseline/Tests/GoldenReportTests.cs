using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using AgentExperience.ReuseBaseline.Experiment;
using AgentExperience.ReuseBaseline.Harness;

namespace AgentExperience.ReuseBaseline.Tests;

/// <summary>
/// The harness's deterministic reports, compared against reports checked into the repository, byte
/// for byte.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes an accidental change to a published number a failing diff rather than a quiet
/// edit. Every identifier, count, mean and standard deviation the report prints is in the file, so
/// any of them moving is something a human has to look at and accept.
/// </para>
/// <para>
/// <b>It never updates itself.</b> Set <c>AGENTEXPERIENCE_REUSEBASELINE_GOLDEN_UPDATE=1</c> to
/// rewrite the golden files from the current harness, then read the diff and commit it on purpose.
/// </para>
/// <para>
/// <b>Three goldens, not one.</b> The reference report, the negative control's report -- the run in
/// which the honest answer is no -- and a deliberately faulted run, which is the only one that
/// exercises the rendering of errors, timeouts, retrieval failures, missing-value placeholders and a
/// statistic that is undefined because its sample has one observation.
/// </para>
/// <para>
/// The elapsed-time section is deliberately outside the comparison: it is a wall-clock measurement
/// of one machine on one run. It is asserted separately, for the things about it that <em>are</em>
/// stable -- that it exists, that it carries numbers, and that it discloses the two asymmetries
/// that bias it.
/// </para>
/// </remarks>
public class GoldenReportTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>The environment variable that rewrites the golden files instead of only comparing against them.</summary>
    public const string UpdateVariable = "AGENTEXPERIENCE_REUSEBASELINE_GOLDEN_UPDATE";

    private const string GoldenResourceName = "AgentExperience.ReuseBaseline.GoldenReport.txt";

    private const string NegativeControlResourceName = "AgentExperience.ReuseBaseline.GoldenNegativeControlReport.txt";

    private const string FailedTrialResourceName = "AgentExperience.ReuseBaseline.GoldenFailedTrialReport.txt";

    [Fact]
    public async Task The_reference_experiment_renders_the_checked_in_report_byte_for_byte()
    {
        var rendered = ReuseBaselineReport.RenderDeterministic(await ExperimentFacts.ReferenceAsync());
        Regenerate("GoldenReport.txt", rendered);

        var golden = ReadResource(GoldenResourceName);

        Assert.Equal(golden, rendered);
        Assert.Equal(Encoding.UTF8.GetBytes(golden), Encoding.UTF8.GetBytes(rendered));
        Assert.DoesNotContain('\r', rendered);
    }

    [Fact]
    public async Task The_negative_control_renders_its_own_checked_in_report_byte_for_byte()
    {
        // Checked in for the same reason as the reference report, and it is the more important of
        // the two: it is the run in which the honest answer is no, and its numbers -- two identical
        // means -- are exactly what somebody tempted to tune this harness would have to change.
        var rendered = ReuseBaselineReport.RenderDeterministic(await ExperimentFacts.NegativeControlAsync());
        Regenerate("GoldenNegativeControlReport.txt", rendered);

        Assert.Equal(ReadResource(NegativeControlResourceName), rendered);
        Assert.Contains("VERDICT: NoDemonstratedBenefit", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// A run with a retrieval failure, two errors, two timeouts and a condition left with one
    /// observation, golden-filed.
    /// </summary>
    /// <remarks>
    /// Neither other golden contains a trial that is not <c>Completed</c>, and every <c>n=</c> in
    /// them is six -- so the failure classifications, the retrieval-failure line, the <c>-</c>
    /// placeholders and the <c>(undefined)</c> render path were asserted only by substring checks
    /// against a report nothing pinned. This pins them.
    /// </remarks>
    [Fact]
    public async Task A_run_with_failed_trials_and_an_undefined_statistic_renders_its_own_checked_in_report()
    {
        var result = await ExperimentFacts.FaultedAsync();
        var rendered = ReuseBaselineReport.RenderDeterministic(result);
        Regenerate("GoldenFailedTrialReport.txt", rendered);

        Assert.Equal(ReadResource(FailedTrialResourceName), rendered);

        // The properties the golden exists to hold, asserted here too so a regeneration that lost
        // them fails rather than quietly recording a run that proves nothing.
        Assert.Equal(1, result.Gate.Enabled.FailedAttempts.Observations);
        Assert.Null(result.Gate.Enabled.FailedAttempts.StandardDeviation);
        Assert.Equal(3, result.Gate.Enabled.Errored);
        Assert.Equal(2, result.Gate.Enabled.TimedOut);
        Assert.Equal(1, result.Gate.Enabled.RetrievalFailures);

        Assert.Contains("n=1 mean 2.000 sd" + ReuseBaselineReport.DispersionMarker + " " + ReuseBaselineReport.Undefined, rendered, StringComparison.Ordinal);
        Assert.Contains("Errored", rendered, StringComparison.Ordinal);
        Assert.Contains("TimedOut", rendered, StringComparison.Ordinal);
        Assert.Contains("retrieval did not complete: InjectionOutcome.RetrievalFailed", rendered, StringComparison.Ordinal);

        // The '-' placeholders a non-completed trial prints, in the failed/tools/denied columns.
        Assert.Contains("-       -      -       -", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_independent_runs_of_the_reference_experiment_produce_the_same_report()
    {
        var first = await ReuseBaselineExperiment.RunAsync(new ExperimentOptions { Arm = ReuseBaselineArms.Reference });
        var second = await ReuseBaselineExperiment.RunAsync(new ExperimentOptions { Arm = ReuseBaselineArms.Reference });

        Assert.Equal(
            ReuseBaselineReport.RenderDeterministic(first),
            ReuseBaselineReport.RenderDeterministic(second));
    }

    /// <summary>
    /// The determinism guarantee under three current cultures rather than only the machine's own.
    /// </summary>
    /// <param name="cultureName">The culture to force for the duration of the render, or empty for the invariant culture.</param>
    [Theory]
    [InlineData("")]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public async Task The_report_renders_the_same_bytes_under_any_culture(string cultureName)
    {
        var culture = cultureName.Length == 0 ? CultureInfo.InvariantCulture : new CultureInfo(cultureName);
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;

        string rendered;
        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            rendered = ReuseBaselineReport.RenderDeterministic(
                await ReuseBaselineExperiment.RunAsync(new ExperimentOptions { Arm = ReuseBaselineArms.Reference }));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }

        Assert.Equal(ReadGolden(), rendered);
    }

    [Fact]
    public async Task The_elapsed_time_section_carries_numbers_and_discloses_what_biases_them()
    {
        var result = await ExperimentFacts.ReferenceAsync();
        var elapsed = ReuseBaselineReport.RenderElapsedTime(result);

        // Frozen rule 5 says elapsed time is reported, so it is written where a CI log will carry it
        // rather than only asserted about. It cannot go in the golden file: it is machine-dependent.
        output.WriteLine(elapsed);

        Assert.Contains("excluded from the gate", elapsed, StringComparison.Ordinal);
        Assert.Contains("ExperienceIndex.cs:231", elapsed, StringComparison.Ordinal);
        Assert.Contains("ExperienceIndexingService.cs:502-511", elapsed, StringComparison.Ordinal);
        Assert.Contains("ExperienceContextProvider.cs:404-422", elapsed, StringComparison.Ordinal);
        Assert.Contains("Stopwatch", elapsed, StringComparison.Ordinal);

        // Every trial's elapsed time is there, and every one of them is a real measurement.
        Assert.All(result.Trials, trial => Assert.True(trial.Metrics.ElapsedMilliseconds > 0d));
        Assert.Equal(result.Trials.Count, result.Gate.Enabled.ElapsedMilliseconds.Observations + result.Gate.Disabled.ElapsedMilliseconds.Observations);

        // And it is genuinely not part of the golden comparison, which is why it is asserted here.
        Assert.DoesNotContain("ELAPSED TIME", ReadGolden(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The elapsed-time section is a public render that publishes numbers, so it refuses under a
    /// changed pre-registration exactly as the golden-filed body does.
    /// </summary>
    [Fact]
    public async Task Every_public_render_refuses_when_the_preregistration_changed()
    {
        var path = Path.Combine(Path.GetTempPath(), "reuse-baseline-render-" + Guid.NewGuid().ToString("N") + ".json");
        File.Copy(PreregistrationSource.DefaultPath(), path);

        try
        {
            var result = await ReuseBaselineExperiment.RunAsync(new ExperimentOptions
            {
                Arm = ReuseBaselineArms.Reference,
                Preregistration = PreregistrationSource.ForFile(path),
            });

            Assert.False(string.IsNullOrWhiteSpace(ReuseBaselineReport.RenderElapsedTime(result)));

            await File.AppendAllTextAsync(path, " ");

            Assert.Throws<PreregistrationTamperedException>(() => ReuseBaselineReport.RenderDeterministic(result));
            Assert.Throws<PreregistrationTamperedException>(() => ReuseBaselineReport.RenderElapsedTime(result));
            Assert.Throws<PreregistrationTamperedException>(() => ReuseBaselineReport.Render(result));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The gate expression the report prints is the file's, and the terms the gate evaluated are
    /// that expression rather than something that merely starts with the same word.
    /// </summary>
    [Fact]
    public async Task The_gate_terms_are_the_expression_the_checked_in_file_declares()
    {
        var result = await ExperimentFacts.ReferenceAsync();
        var report = ReuseBaselineReport.RenderDeterministic(result);
        var design = PreregistrationSource.CheckedIn.Read().Design;

        // The binding, as a whole string: operators, metric names, condition labels and order. An
        // earlier version split on " AND " and compared only the first token of each fragment, which
        // would have accepted a gate on a different metric entirely.
        Assert.Equal(
            design.GateExpression,
            string.Join(" AND ", result.Gate.Terms.Select(term => term.Expression)));

        // And each term is in the report verbatim, on one line, rather than only as a wrapped copy
        // of the whole expression.
        var lines = report.Split(ReuseBaselineReport.LineSeparator);
        foreach (var term in result.Gate.Terms)
        {
            Assert.Contains(lines, line => line.Contains(term.Expression, StringComparison.Ordinal));
        }

        Assert.Contains(result.Preregistration.GitBlobId, report, StringComparison.Ordinal);
        Assert.Contains("git hash-object", report, StringComparison.Ordinal);
    }

    /// <summary>The reference experiment's golden report as it was checked in.</summary>
    internal static string ReadGolden() => ReadResource(GoldenResourceName);

    private static void Regenerate(string fileName, string rendered)
    {
        if (Environment.GetEnvironmentVariable(UpdateVariable) != "1")
        {
            return;
        }

        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(GoldenSourcePath())!, fileName),
            rendered,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(GoldenReportTests).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"'{name}' is not embedded in the harness assembly.");
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return reader.ReadToEnd();
    }

    /// <summary>Where the golden files live in the working tree, for the deliberate regeneration path only.</summary>
    /// <param name="thisFile">Supplied by the compiler; never passed.</param>
    private static string GoldenSourcePath([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!, "GoldenReport.txt");
}

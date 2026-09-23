using System.Globalization;
using System.Text.RegularExpressions;
using AgentExperience.ReuseBaseline.Experiment;
using AgentExperience.ReuseBaseline.Harness;

namespace AgentExperience.ReuseBaseline.Tests;

/// <summary>
/// The numbers the report prints, checked against the numbers the harness computed -- and against
/// each other.
/// </summary>
/// <remarks>
/// <para>
/// Falsifying a rendered number while leaving the computation alone used to be caught by the golden
/// byte comparison and by nothing else, so a maintainer who regenerated the goldens lost the only
/// detector. And the per-condition table and the gate-term explanations are rendered by different
/// code from the same statistics, yet were never compared with one another.
/// </para>
/// <para>
/// Everything below parses the rendered text and compares it against
/// <see cref="ExperimentResult.Gate"/>. It is deliberately not a second renderer: it reads what a
/// human would read off the page.
/// </para>
/// </remarks>
public class RenderedNumbersTests
{
    private static readonly Regex MetricLine = new(
        @"^\s{6}(?<name>\S.*?)\s{2,}n=(?<n>\d+) mean (?<mean>\S+) sd(?<marker>\(\*\))? (?<sd>\S+) min (?<min>\S+) median (?<median>\S+) max (?<max>\S+)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex TermValues = new(
        @"^\s+(?:holds|does not hold): (?<left>\S+) against (?<right>\S+) \((?<metric>\w+)\)",
        RegexOptions.CultureInvariant);

    public static TheoryData<string> Arms() => new("reference", "negative-control", "wrong-strategy", "faulted");

    [Theory]
    [MemberData(nameof(Arms))]
    public async Task The_per_condition_table_prints_the_statistics_the_harness_computed(string arm)
    {
        var result = await ResultAsync(arm);
        var report = ReuseBaselineReport.RenderDeterministic(result);
        var lines = report.Split(ReuseBaselineReport.LineSeparator);

        // The table is two blocks, memory-enabled first, in the order Conditions() renders them.
        var blocks = new[] { result.Gate.Enabled, result.Gate.Disabled };
        var blockIndex = -1;
        var seen = 0;

        for (var line = 0; line < lines.Length; line++)
        {
            if (lines[line].StartsWith("  " + blocks[Math.Min(blockIndex + 1, blocks.Length - 1)].Label + ":", StringComparison.Ordinal)
                && blockIndex + 1 < blocks.Length)
            {
                blockIndex++;
                continue;
            }

            var match = MetricLine.Match(lines[line]);
            if (!match.Success || blockIndex < 0)
            {
                continue;
            }

            var condition = blocks[blockIndex];
            var summary = match.Groups["name"].Value switch
            {
                "failed_attempts (primary)" => condition.FailedAttempts,
                "tool_calls (secondary)" => condition.ToolCalls,
                "unauthorized_tool_executions (guardrail)" => condition.UnauthorizedToolExecutions,
                _ => null,
            };

            if (summary is null)
            {
                continue;
            }

            seen++;

            // Every dispersion figure in this table carries the marker that ties it to the paragraph
            // saying it is between-task variation in a deterministic fixture, not sampling variance.
            Assert.Equal(ReuseBaselineReport.DispersionMarker, match.Groups["marker"].Value);

            Assert.Equal(summary.Observations.ToString(CultureInfo.InvariantCulture), match.Groups["n"].Value);
            Assert.Equal(Printed(summary.Mean), match.Groups["mean"].Value);
            Assert.Equal(Printed(summary.StandardDeviation), match.Groups["sd"].Value);
            Assert.Equal(Printed(summary.Minimum), match.Groups["min"].Value);
            Assert.Equal(Printed(summary.Median), match.Groups["median"].Value);
            Assert.Equal(Printed(summary.Maximum), match.Groups["max"].Value);
        }

        // Three metrics under each of the two conditions. Without this, a renderer that stopped
        // printing the table would pass every assertion above.
        Assert.Equal(6, seen);
    }

    /// <summary>
    /// The per-condition table and the gate-term explanations agree. They are rendered by different
    /// code and were never compared against one another.
    /// </summary>
    [Theory]
    [MemberData(nameof(Arms))]
    public async Task The_gate_terms_and_the_per_condition_table_agree_on_every_shared_number(string arm)
    {
        var result = await ResultAsync(arm);
        var report = ReuseBaselineReport.RenderDeterministic(result);

        var fromTerms = report.Split(ReuseBaselineReport.LineSeparator)
            .Select(line => TermValues.Match(line))
            .Where(match => match.Success)
            .ToDictionary(
                match => match.Groups["metric"].Value,
                match => (Left: match.Groups["left"].Value, Right: match.Groups["right"].Value),
                StringComparer.Ordinal);

        foreach (var term in result.Gate.Terms.Where(term => term.Holds is not null))
        {
            var printed = fromTerms[term.Metric];

            Assert.Equal(Printed(term.EnabledValue), printed.Left);
            Assert.Equal(Printed(term.DisabledValue), printed.Right);

            // And those are the same numbers the table printed for the same metric.
            var (enabled, disabled) = Sides(term.Metric, result);
            Assert.Equal(Printed(enabled), printed.Left);
            Assert.Equal(Printed(disabled), printed.Right);
        }
    }

    /// <summary>
    /// The verdict word and the terms agree: a report cannot print a pass whose terms did not all
    /// hold, or a failure whose terms all did.
    /// </summary>
    [Theory]
    [MemberData(nameof(Arms))]
    public async Task The_printed_verdict_is_the_one_the_terms_imply(string arm)
    {
        var result = await ResultAsync(arm);
        var report = ReuseBaselineReport.RenderDeterministic(result);

        var expected = result.Gate.Terms.All(term => term.Holds == true)
            ? GateVerdict.BenefitDemonstrated
            : GateVerdict.NoDemonstratedBenefit;

        Assert.Equal(expected, result.Gate.Verdict);
        Assert.Contains("VERDICT: " + expected, report, StringComparison.Ordinal);
    }

    /// <summary>
    /// The trial table's rows are the trials, with the values those trials hold.
    /// </summary>
    [Theory]
    [MemberData(nameof(Arms))]
    public async Task Each_trial_row_prints_that_trials_own_metrics(string arm)
    {
        var result = await ResultAsync(arm);
        var report = ReuseBaselineReport.RenderDeterministic(result);
        var lines = report.Split(ReuseBaselineReport.LineSeparator);

        foreach (var trial in result.Trials)
        {
            var row = Assert.Single(lines, line => Regex.IsMatch(
                line,
                @"^\s{2,}" + trial.Index + @"\s{2}" + Regex.Escape(result.Preregistration.Design.LabelFor(trial.Condition))
                    + @"\s+" + Regex.Escape(trial.TaskId) + @"\s+" + trial.Status + @"\s",
                RegexOptions.CultureInvariant));

            var cells = row.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(Cell(trial.Metrics.FailedAttempts), cells[^4]);
            Assert.Equal(Cell(trial.Metrics.ToolCalls), cells[^3]);
            Assert.Equal(Cell(trial.Metrics.UnauthorizedToolExecutions), cells[^2]);
            Assert.Equal(
                trial.Metrics.VerifiedSuccess is { } verified ? (verified ? "yes" : "no") : "-",
                cells[^1]);
        }
    }

    private static (double? Enabled, double? Disabled) Sides(string metric, ExperimentResult result) => metric switch
    {
        "failed_attempts" => (result.Gate.Enabled.FailedAttempts.Mean, result.Gate.Disabled.FailedAttempts.Mean),
        "tool_calls" => (result.Gate.Enabled.ToolCalls.Mean, result.Gate.Disabled.ToolCalls.Mean),
        "unauthorized_tool_executions" => (result.Gate.Enabled.UnauthorizedToolExecutions.Mean, result.Gate.Disabled.UnauthorizedToolExecutions.Mean),
        "verified_success_rate" => (result.Gate.Enabled.VerifiedSuccessRate, result.Gate.Disabled.VerifiedSuccessRate),
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "No such gated metric."),
    };

    private static string Printed(double? value) =>
        value is { } number ? number.ToString("F3", CultureInfo.InvariantCulture) : ReuseBaselineReport.Undefined;

    private static string Cell(int? value) =>
        value is { } number ? number.ToString(CultureInfo.InvariantCulture) : "-";

    private static Task<ExperimentResult> ResultAsync(string arm) => arm switch
    {
        "reference" => ExperimentFacts.ReferenceAsync(),
        "negative-control" => ExperimentFacts.NegativeControlAsync(),
        "wrong-strategy" => ExperimentFacts.WrongStrategyAsync(),
        "faulted" => ExperimentFacts.FaultedAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(arm)),
    };
}

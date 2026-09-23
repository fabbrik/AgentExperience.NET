using AgentExperience.ReuseBaseline.Harness;

namespace AgentExperience.ReuseBaseline.Tests;

/// <summary>
/// The gate's verdict over synthetic trial data with known properties. No agent, no store, no run:
/// numbers in, verdict asserted.
/// </summary>
/// <remarks>
/// This is the part of the story that is testable without a model, and it is the part that matters
/// most: a gate that has never been observed saying no is not evidence when it says yes. The theory
/// below covers a pass, a failure on the primary metric, a failure on each guardrail separately, a
/// condition whose every trial failed, and a single-observation sample.
/// </remarks>
public class GateVerdictTests
{
    private static readonly Preregistration Design = ExperimentFacts.Design();

    public static TheoryData<string, GateVerdict, TrialRecord[]> Cases() => new()
    {
        // Matrix row 2: every term holds.
        {
            "lower mean, guardrails level",
            GateVerdict.BenefitDemonstrated,
            [
                ExperimentFacts.Synthetic(0, TrialCondition.MemoryDisabled, 2),
                ExperimentFacts.Synthetic(1, TrialCondition.MemoryEnabled, 0),
                ExperimentFacts.Synthetic(2, TrialCondition.MemoryDisabled, 3),
                ExperimentFacts.Synthetic(3, TrialCondition.MemoryEnabled, 1),
            ]
        },

        // Matrix row 3: the primary metric alone fails.
        {
            "equal means",
            GateVerdict.NoDemonstratedBenefit,
            [
                ExperimentFacts.Synthetic(0, TrialCondition.MemoryDisabled, 2),
                ExperimentFacts.Synthetic(1, TrialCondition.MemoryEnabled, 2),
                ExperimentFacts.Synthetic(2, TrialCondition.MemoryDisabled, 3),
                ExperimentFacts.Synthetic(3, TrialCondition.MemoryEnabled, 3),
            ]
        },
        {
            "higher mean under memory-enabled",
            GateVerdict.NoDemonstratedBenefit,
            [
                ExperimentFacts.Synthetic(0, TrialCondition.MemoryDisabled, 1),
                ExperimentFacts.Synthetic(1, TrialCondition.MemoryEnabled, 3),
            ]
        },

        // Matrix row 4a: the primary metric holds and verified success drops.
        {
            "verified success drops",
            GateVerdict.NoDemonstratedBenefit,
            [
                ExperimentFacts.Synthetic(0, TrialCondition.MemoryDisabled, 3, verified: true),
                ExperimentFacts.Synthetic(1, TrialCondition.MemoryEnabled, 0, verified: false),
            ]
        },

        // Matrix row 4b: the primary metric holds and denied invocations rise.
        {
            "denied tool invocations rise",
            GateVerdict.NoDemonstratedBenefit,
            [
                ExperimentFacts.Synthetic(0, TrialCondition.MemoryDisabled, 3, denied: 0),
                ExperimentFacts.Synthetic(1, TrialCondition.MemoryEnabled, 0, denied: 1),
            ]
        },

        // Matrix row 13: one condition produced nothing usable. Undefined is not a pass.
        {
            "every memory-enabled trial failed",
            GateVerdict.NoDemonstratedBenefit,
            [
                ExperimentFacts.Synthetic(0, TrialCondition.MemoryDisabled, 3),
                ExperimentFacts.Synthetic(1, TrialCondition.MemoryEnabled, null, verified: null, denied: null, status: TrialStatus.Errored),
                ExperimentFacts.Synthetic(2, TrialCondition.MemoryDisabled, 2),
                ExperimentFacts.Synthetic(3, TrialCondition.MemoryEnabled, null, verified: null, denied: null, status: TrialStatus.TimedOut),
            ]
        },
        {
            "no trials at all",
            GateVerdict.NoDemonstratedBenefit,
            []
        },

        // Matrix row 14: n=1 per condition. The verdict is still reached; the dispersion is not.
        {
            "one observation per condition",
            GateVerdict.BenefitDemonstrated,
            [
                ExperimentFacts.Synthetic(0, TrialCondition.MemoryDisabled, 2),
                ExperimentFacts.Synthetic(1, TrialCondition.MemoryEnabled, 0),
            ]
        },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Gate_reaches_the_expected_verdict(string name, GateVerdict expected, TrialRecord[] trials)
    {
        var result = GateEvaluator.Evaluate(trials, Design);

        Assert.Equal(expected, result.Verdict);
        Assert.Equal(3, result.Terms.Count);

        // A pass means every term held; a failure names at least one that did not.
        if (expected == GateVerdict.BenefitDemonstrated)
        {
            Assert.All(result.Terms, term => Assert.True(term.Holds));
        }
        else
        {
            Assert.Contains(result.Terms, term => term.Holds != true);
        }

        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    [Fact]
    public void A_failing_guardrail_is_named_with_the_numbers_that_produced_it()
    {
        TrialRecord[] trials =
        [
            ExperimentFacts.Synthetic(0, TrialCondition.MemoryDisabled, 3, denied: 0),
            ExperimentFacts.Synthetic(1, TrialCondition.MemoryEnabled, 0, denied: 2),
        ];

        var result = GateEvaluator.Evaluate(trials, Design);

        var failing = Assert.Single(result.Terms, term => term.Holds != true);
        Assert.Equal("unauthorized_tool_executions", failing.Metric);
        Assert.Equal(2d, failing.EnabledValue);
        Assert.Equal(0d, failing.DisabledValue);
        Assert.Contains("+2.000", failing.Explanation, StringComparison.Ordinal);

        // And the primary term, which did hold, is still reported rather than dropped.
        Assert.True(result.Terms.Single(term => term.Metric == "failed_attempts").Holds);
    }

    [Fact]
    public void An_undefined_term_is_explicitly_not_a_pass()
    {
        TrialRecord[] trials =
        [
            ExperimentFacts.Synthetic(0, TrialCondition.MemoryDisabled, 3),
            ExperimentFacts.Synthetic(1, TrialCondition.MemoryEnabled, null, verified: null, denied: null, status: TrialStatus.Errored),
        ];

        var result = GateEvaluator.Evaluate(trials, Design);

        var primary = result.Terms.Single(term => term.Metric == "failed_attempts");
        Assert.Null(primary.Holds);
        Assert.Contains("undefined", primary.Explanation, StringComparison.Ordinal);
        Assert.Equal(GateVerdict.NoDemonstratedBenefit, result.Verdict);
    }

    [Fact]
    public void An_errored_trial_is_counted_in_its_condition_and_excluded_from_no_metric_it_has_a_value_for()
    {
        TrialRecord[] trials =
        [
            ExperimentFacts.Synthetic(0, TrialCondition.MemoryDisabled, 2),
            ExperimentFacts.Synthetic(1, TrialCondition.MemoryEnabled, 0),
            ExperimentFacts.Synthetic(2, TrialCondition.MemoryEnabled, null, verified: null, denied: null, status: TrialStatus.Errored),
        ];

        var result = GateEvaluator.Evaluate(trials, Design);

        Assert.Equal(2, result.Enabled.Trials);
        Assert.Equal(1, result.Enabled.Errored);

        // Excluded from failed_attempts, which it has no value for ...
        Assert.Equal(1, result.Enabled.FailedAttempts.Observations);

        // ... and from the denial guardrail too, because a trial killed part-way through has a
        // truncated denial count and admitting it would dilute that mean towards passing.
        Assert.Equal(1, result.Enabled.UnauthorizedToolExecutions.Observations);
        Assert.Equal(0d, result.Enabled.UnauthorizedToolExecutions.Mean);

        // ... and from nothing else: elapsed time is a complete measurement of what did happen.
        Assert.Equal(2, result.Enabled.ElapsedMilliseconds.Observations);
    }

    /// <summary>
    /// A pass whose margin is smaller than the printed precision says so, rather than rendering as
    /// a tie.
    /// </summary>
    /// <remarks>
    /// Unreachable while both conditions have the same number of integer observations, and reachable
    /// the moment they do not -- which is to say, as soon as one trial errors. 1/46 against 1/45 is
    /// a difference of 0.00048, which rounds to 0.000 at three decimals.
    /// </remarks>
    [Fact]
    public void A_margin_below_the_printed_precision_is_reported_exactly_rather_than_as_a_tie()
    {
        var trials = new List<TrialRecord>();
        var index = 0;

        for (var trial = 0; trial < 46; trial++)
        {
            trials.Add(ExperimentFacts.Synthetic(index++, TrialCondition.MemoryEnabled, trial == 0 ? 1 : 0));
        }

        for (var trial = 0; trial < 45; trial++)
        {
            trials.Add(ExperimentFacts.Synthetic(index++, TrialCondition.MemoryDisabled, trial == 0 ? 1 : 0));
        }

        var result = GateEvaluator.Evaluate(trials, Design);
        var primary = result.Terms.Single(term => term.Metric == "failed_attempts");

        Assert.True(primary.Holds);
        Assert.Equal(1d / 46d, primary.EnabledValue);
        Assert.Equal(1d / 45d, primary.DisabledValue);

        // Both sides print as 0.022 and the difference prints as -0.000, so without the sentence
        // below a reader could not tell this pass from a tie.
        Assert.Contains("0.022 against 0.022", primary.Explanation, StringComparison.Ordinal);
        Assert.Contains("differ below the printed precision", primary.Explanation, StringComparison.Ordinal);
        Assert.Contains("applied no tolerance", primary.Explanation, StringComparison.Ordinal);
        Assert.Contains((1d / 46d).ToString("R", System.Globalization.CultureInfo.InvariantCulture), primary.Explanation, StringComparison.Ordinal);
    }

    /// <summary>And a term whose two sides really are equal says nothing of the kind.</summary>
    [Fact]
    public void A_term_whose_sides_are_exactly_equal_carries_no_precision_note()
    {
        var result = GateEvaluator.Evaluate(
            [
                ExperimentFacts.Synthetic(0, TrialCondition.MemoryDisabled, 2),
                ExperimentFacts.Synthetic(1, TrialCondition.MemoryEnabled, 2),
            ],
            Design);

        var primary = result.Terms.Single(term => term.Metric == "failed_attempts");

        Assert.False(primary.Holds);
        Assert.DoesNotContain("differ below the printed precision", primary.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// The terms are built from the pre-registration's metric fields, in the order it declares them.
    /// </summary>
    [Fact]
    public void The_terms_are_the_primary_metric_then_each_guardrail_in_the_declared_order()
    {
        var result = GateEvaluator.Evaluate([], Design);

        Assert.Equal(
            [Design.PrimaryMetric, .. Design.GuardrailMetrics],
            [.. result.Terms.Select(term => term.Metric)]);

        Assert.Equal([.. result.Terms.Select(term => term.Metric)], GateEvaluator.GatedMetrics(Design));

        // And the metrics the file excludes from the gate are in no term at all.
        Assert.All(
            Design.MetricsExcludedFromGate,
            excluded => Assert.DoesNotContain(result.Terms, term => term.Metric == excluded));
    }

    [Fact]
    public void The_gate_expression_reported_is_the_one_read_from_the_preregistration_file()
    {
        var result = GateEvaluator.Evaluate([], Design);

        Assert.Equal(Design.GateExpression, result.Expression);
        Assert.Equal("NoDemonstratedBenefit", Design.GateFailureVerdict);
        Assert.True(Design.GateEvaluatedOnce);
    }
}

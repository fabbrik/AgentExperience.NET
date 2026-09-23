using System.Globalization;

namespace AgentExperience.ReuseBaseline.Harness;

/// <summary>
/// The verdict the one predeclared gate expression produced. There are two values and there is no
/// code path from the second to the first.
/// </summary>
public enum GateVerdict
{
    /// <summary>
    /// Every term of the predeclared expression held. Always reported with its qualification: this
    /// harness runs a scripted agent against fake model clients, so what is demonstrated is a
    /// property of the harness and the fixture, never of a model.
    /// </summary>
    BenefitDemonstrated,

    /// <summary>
    /// At least one term did not hold, or could not be evaluated. Reported with the same numbers and
    /// the same detail as a pass, because a truthful negative is a successful run.
    /// </summary>
    NoDemonstratedBenefit,
}

/// <summary>One term of the gate expression, and whether it held.</summary>
/// <param name="Metric">The metric the term is about.</param>
/// <param name="Expression">The term, as the pre-registration words it.</param>
/// <param name="Holds">
/// Whether the term held. <see langword="null"/> means it could not be evaluated at all -- a
/// condition with no usable observation -- which is not a pass.
/// </param>
/// <param name="EnabledValue">The memory-enabled side of the comparison, or <see langword="null"/> when undefined.</param>
/// <param name="DisabledValue">The memory-disabled side, or <see langword="null"/> when undefined.</param>
/// <param name="Explanation">What the term came to, in words, including by how much when it failed.</param>
public sealed record GateTerm(
    string Metric,
    string Expression,
    bool? Holds,
    double? EnabledValue,
    double? DisabledValue,
    string Explanation);

/// <summary>Everything one condition's trials came to, per metric.</summary>
/// <param name="Condition">The condition.</param>
/// <param name="Label">Its pre-registered label.</param>
/// <param name="Trials">How many trials ran under it, including errored and timed-out ones.</param>
/// <param name="Completed">How many of them completed.</param>
/// <param name="Errored">How many threw.</param>
/// <param name="TimedOut">How many exceeded their deadline.</param>
/// <param name="RetrievalFailures">How many had a retrieval that did not complete.</param>
/// <param name="FailedAttempts">The primary metric.</param>
/// <param name="ToolCalls">Secondary.</param>
/// <param name="ElapsedMilliseconds">Secondary, and excluded from the gate.</param>
/// <param name="UnauthorizedToolExecutions">Guardrail.</param>
/// <param name="VerifiedTrials">How many trials verified.</param>
/// <param name="TrialsWithVerificationOutcome">How many had a verification outcome at all.</param>
/// <param name="VerifiedSuccessRate">Guardrail, as a rate. <see langword="null"/> when no trial had an outcome.</param>
public sealed record ConditionMetrics(
    TrialCondition Condition,
    string Label,
    int Trials,
    int Completed,
    int Errored,
    int TimedOut,
    int RetrievalFailures,
    MetricSummary FailedAttempts,
    MetricSummary ToolCalls,
    MetricSummary ElapsedMilliseconds,
    MetricSummary UnauthorizedToolExecutions,
    int VerifiedTrials,
    int TrialsWithVerificationOutcome,
    double? VerifiedSuccessRate);

/// <summary>The gate's one evaluation, with everything that produced it.</summary>
/// <param name="Verdict">The verdict.</param>
/// <param name="Expression">The gate expression exactly as the pre-registration file words it.</param>
/// <param name="Terms">Each term and whether it held.</param>
/// <param name="Enabled">The memory-enabled condition's numbers.</param>
/// <param name="Disabled">The memory-disabled condition's numbers.</param>
/// <param name="Notes">Anything a reader needs in order to read the numbers correctly.</param>
public sealed record GateResult(
    GateVerdict Verdict,
    string Expression,
    IReadOnlyList<GateTerm> Terms,
    ConditionMetrics Enabled,
    ConditionMetrics Disabled,
    IReadOnlyList<string> Notes);

/// <summary>
/// Evaluates the one predeclared gate expression, once, over the retained trials.
/// </summary>
/// <remarks>
/// <para>
/// <b>The terms come out of the pre-registration file, not out of this source.</b>
/// <see cref="Evaluate"/> builds one term for <c>primaryMetric</c> and one for each entry of
/// <c>guardrailMetrics</c>, renders each term's expression from the metric's declared shape and the
/// condition labels, and then <em>refuses to run</em> unless the terms it built, joined with
/// <c>" AND "</c>, are character for character the file's <c>gateExpression</c>. Editing
/// <c>primaryMetric</c> or <c>guardrailMetrics</c> therefore changes what is gated on and fails
/// loudly if the expression no longer matches. A pre-registration the code only prints is
/// decoration; this one is read.
/// </para>
/// <para>
/// <b>There is no second gate and no code path that turns a failure into a pass.</b> The verdict is
/// <see cref="GateVerdict.BenefitDemonstrated"/> if and only if every term is
/// <see langword="true"/>. A term that could not be evaluated is not a pass.
/// </para>
/// <para>
/// <b>Comparisons are exact.</b> No tolerance is applied, so two equal means are not a pass on a
/// strict-inequality term. A tolerance would be a magnitude threshold arrived at after seeing the
/// data, which is the thing the pre-registration exists to prevent. The printed values are rounded
/// to three decimals; when a comparison turns on a difference smaller than that, the term says so
/// and prints the difference exactly, so a hair-thin pass never renders as a tie.
/// </para>
/// <para>
/// Every trial reaches this function -- completed, errored, timed out, and regressed alike. A trial
/// with no value for a metric is excluded from that metric's statistics and from nothing else: it
/// still counts towards its condition's trial count, and it still appears in the report.
/// </para>
/// </remarks>
public static class GateEvaluator
{
    /// <summary>How a metric's term is worded and which way it has to go.</summary>
    /// <param name="Rendered">How the metric appears on each side of the term, given a condition label.</param>
    /// <param name="Comparator">The operator, as the pre-registration words it.</param>
    /// <param name="Holds">Whether the term holds for a pair of values.</param>
    /// <param name="Value">The metric's value for one condition, or <see langword="null"/> when undefined.</param>
    /// <param name="Requirement">What the term requires, in words.</param>
    private sealed record GateableMetric(
        Func<string, string, string> Rendered,
        string Comparator,
        Func<double, double, bool> Holds,
        Func<ConditionMetrics, double?> Value,
        string Requirement);

    /// <summary>
    /// Every metric this harness knows how to gate on, and nothing else. A pre-registration naming a
    /// metric that is not here is refused rather than quietly gated on something else.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, GateableMetric> Gateable =
        new Dictionary<string, GateableMetric>(StringComparer.Ordinal)
        {
            ["failed_attempts"] = new(
                (metric, label) => $"mean({metric} | {label})",
                "<",
                (left, right) => left < right,
                condition => condition.FailedAttempts.Mean,
                "a strictly lower mean under the memory-enabled condition"),
            ["tool_calls"] = new(
                (metric, label) => $"mean({metric} | {label})",
                "<",
                (left, right) => left < right,
                condition => condition.ToolCalls.Mean,
                "a strictly lower mean under the memory-enabled condition"),
            ["verified_success_rate"] = new(
                (metric, label) => $"{metric}({label})",
                ">=",
                (left, right) => left >= right,
                condition => condition.VerifiedSuccessRate,
                "verified success must not decrease"),
            ["unauthorized_tool_executions"] = new(
                (metric, label) => $"mean({metric} | {label})",
                "<=",
                (left, right) => left <= right,
                condition => condition.UnauthorizedToolExecutions.Mean,
                "denied tool invocations must not increase"),
        };

    /// <summary>Evaluates the gate over <paramref name="trials"/> under <paramref name="preregistration"/>.</summary>
    /// <param name="trials">Every trial the run produced, in plan order.</param>
    /// <param name="preregistration">The design, which supplies the metrics, the labels and the gate expression.</param>
    /// <exception cref="PreregistrationException">
    /// The design names a metric this harness cannot gate on, names one twice, or the terms built
    /// from it are not the expression the file declares.
    /// </exception>
    public static GateResult Evaluate(IReadOnlyList<TrialRecord> trials, Preregistration preregistration)
    {
        ArgumentNullException.ThrowIfNull(trials);
        ArgumentNullException.ThrowIfNull(preregistration);

        var enabled = Summarize(trials, TrialCondition.MemoryEnabled, preregistration);
        var disabled = Summarize(trials, TrialCondition.MemoryDisabled, preregistration);

        var terms = BuildTerms(preregistration, enabled, disabled);

        // All of them, or nothing. There is deliberately no branch below this line.
        var verdict = terms.All(term => term.Holds == true)
            ? GateVerdict.BenefitDemonstrated
            : GateVerdict.NoDemonstratedBenefit;

        return new GateResult(verdict, preregistration.GateExpression, terms, enabled, disabled, Notes(enabled, disabled, terms));
    }

    /// <summary>
    /// The metrics the gate is built from, in term order: the primary metric, then each guardrail.
    /// </summary>
    /// <param name="preregistration">The design.</param>
    /// <exception cref="PreregistrationException">A metric is named twice, or is not one this harness can gate on.</exception>
    public static IReadOnlyList<string> GatedMetrics(Preregistration preregistration)
    {
        ArgumentNullException.ThrowIfNull(preregistration);

        var metrics = new List<string> { preregistration.PrimaryMetric };
        metrics.AddRange(preregistration.GuardrailMetrics);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var metric in metrics)
        {
            if (!seen.Add(metric))
            {
                throw new PreregistrationException(
                    $"The pre-registration names metric '{metric}' twice across primaryMetric and guardrailMetrics, so the gate would "
                    + "carry the same term twice and the expression could not be checked against it.");
            }

            if (!Gateable.ContainsKey(metric))
            {
                throw new PreregistrationException(
                    $"The pre-registration gates on '{metric}', which this harness has no measurement for. Gateable metrics: "
                    + $"[{string.Join(", ", Gateable.Keys.OrderBy(key => key, StringComparer.Ordinal))}]. A metric the harness cannot "
                    + "measure cannot be gated on, and silently gating on a different one is the failure the pre-registration exists to prevent.");
            }
        }

        return metrics;
    }

    private static IReadOnlyList<GateTerm> BuildTerms(
        Preregistration preregistration,
        ConditionMetrics enabled,
        ConditionMetrics disabled)
    {
        var terms = new List<GateTerm>();

        foreach (var metric in GatedMetrics(preregistration))
        {
            var gateable = Gateable[metric];

            terms.Add(Term(
                metric,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} {1} {2}",
                    gateable.Rendered(metric, enabled.Label),
                    gateable.Comparator,
                    gateable.Rendered(metric, disabled.Label)),
                gateable.Value(enabled),
                gateable.Value(disabled),
                gateable.Holds,
                gateable.Requirement));
        }

        // The whole point of reading the file: the terms the code built have to be the expression the
        // file declares, character for character. If they are not, one of the two was edited without
        // the other and the gate is not the pre-registered gate.
        var built = string.Join(" AND ", terms.Select(term => term.Expression));
        if (!string.Equals(built, preregistration.GateExpression, StringComparison.Ordinal))
        {
            throw new PreregistrationException(
                "The gate terms built from the pre-registration's primaryMetric, guardrailMetrics and condition labels are:"
                + Environment.NewLine + "  " + built + Environment.NewLine
                + "and the pre-registration's gateExpression is:" + Environment.NewLine + "  " + preregistration.GateExpression
                + Environment.NewLine
                + "A gate expression that is only printed is decoration. The two must agree, so that editing what is gated on "
                + "cannot leave the published expression describing something else.");
        }

        return terms;
    }

    private static GateTerm Term(
        string metric,
        string expression,
        double? enabled,
        double? disabled,
        Func<double, double, bool> holds,
        string what)
    {
        if (enabled is not { } left || disabled is not { } right)
        {
            return new GateTerm(
                metric,
                expression,
                Holds: null,
                enabled,
                disabled,
                $"undefined: {Side(enabled, "memory-enabled")}{Side(disabled, "memory-disabled")}"
                    + "no comparison is possible, and an undefined term is not a pass.");
        }

        var held = holds(left, right);
        var delta = left - right;

        return new GateTerm(
            metric,
            expression,
            held,
            left,
            right,
            string.Format(
                CultureInfo.InvariantCulture,
                "{0}: {1} against {2} ({3}), a difference of {4}.{5} Required: {6}.",
                held ? "holds" : "does not hold",
                Number(left),
                Number(right),
                metric,
                Signed(delta),
                BelowPrintedPrecision(left, right, delta),
                what));
    }

    /// <summary>
    /// The sentence a term adds when the comparison turns on a difference too small to see at the
    /// printed precision, so a hair-thin pass never renders as a tie.
    /// </summary>
    /// <remarks>
    /// Unreachable while both conditions have the same number of integer observations, and reachable
    /// the moment they do not -- which is to say, as soon as one trial errors. The values are printed
    /// round-trippable here rather than everywhere, because three decimals is what a reader wants in
    /// every other case.
    /// </remarks>
    private static string BelowPrintedPrecision(double left, double right, double delta)
    {
        if (left.Equals(right) || !string.Equals(Number(left), Number(right), StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            " The two sides differ below the printed precision: exactly {0} against {1}, a difference of {2}."
                + " The comparison the gate performed is exact and applied no tolerance.",
            left.ToString("R", CultureInfo.InvariantCulture),
            right.ToString("R", CultureInfo.InvariantCulture),
            delta.ToString("R", CultureInfo.InvariantCulture));
    }

    private static string Side(double? value, string label) =>
        value is null ? $"the {label} condition produced no usable observation; " : string.Empty;

    private static ConditionMetrics Summarize(
        IReadOnlyList<TrialRecord> trials,
        TrialCondition condition,
        Preregistration preregistration)
    {
        var inCondition = trials.Where(trial => trial.Condition == condition).ToList();
        var count = inCondition.Count;

        var withOutcome = inCondition.Where(trial => trial.Metrics.VerifiedSuccess.HasValue).ToList();
        var verified = withOutcome.Count(trial => trial.Metrics.VerifiedSuccess == true);

        return new ConditionMetrics(
            condition,
            preregistration.LabelFor(condition),
            count,
            inCondition.Count(trial => trial.Status == TrialStatus.Completed),
            inCondition.Count(trial => trial.Status == TrialStatus.Errored),
            inCondition.Count(trial => trial.Status == TrialStatus.TimedOut),
            inCondition.Count(trial => trial.RetrievalFailure is not null),
            Statistics.Summarize(inCondition.Select(trial => (double?)trial.Metrics.FailedAttempts), count),
            Statistics.Summarize(inCondition.Select(trial => (double?)trial.Metrics.ToolCalls), count),
            Statistics.Summarize(inCondition.Select(trial => trial.Metrics.ElapsedMilliseconds), count),
            Statistics.Summarize(inCondition.Select(trial => (double?)trial.Metrics.UnauthorizedToolExecutions), count),
            verified,
            withOutcome.Count,
            withOutcome.Count == 0 ? null : (double)verified / withOutcome.Count);
    }

    private static IReadOnlyList<string> Notes(ConditionMetrics enabled, ConditionMetrics disabled, IReadOnlyList<GateTerm> terms)
    {
        var notes = new List<string>();

        foreach (var condition in new[] { enabled, disabled })
        {
            if (condition.Trials == 0)
            {
                notes.Add($"The {condition.Label} condition ran no trials at all.");
                continue;
            }

            if (condition.FailedAttempts.Observations == 0)
            {
                notes.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "All {0} trial(s) under {1} failed to produce a value for failed_attempts ({2} errored, {3} timed out). Its statistics are undefined rather than zero, and the gate cannot pass.",
                    condition.Trials,
                    condition.Label,
                    condition.Errored,
                    condition.TimedOut));
            }
            else if (condition.FailedAttempts.Observations < condition.Trials)
            {
                notes.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} of the {1} trial(s) under {2} produced no value for failed_attempts and are excluded from that metric only; they are still counted, still reported, and still listed below.",
                    condition.Trials - condition.FailedAttempts.Observations,
                    condition.Trials,
                    condition.Label));
            }

            if (condition.RetrievalFailures > 0)
            {
                notes.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} trial(s) under {1} had a retrieval that did not complete. They keep their condition: a memory-enabled trial that got nothing is not a memory-disabled trial.",
                    condition.RetrievalFailures,
                    condition.Label));
            }
        }

        foreach (var term in terms.Where(term => term.Holds != true))
        {
            notes.Add($"Gate term on {term.Metric} {term.Explanation}");
        }

        return notes;
    }

    /// <summary>A number as the report prints it, at a fixed precision under any current culture.</summary>
    /// <param name="value">The number.</param>
    internal static string Number(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

    private static string Signed(double value) =>
        (value > 0 ? "+" : string.Empty) + Number(value);
}

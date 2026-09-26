using System.Numerics;

namespace AgentExperience.LiveReuse.Harness;

/// <summary>What one comparison concluded under the pre-registered gate.</summary>
public enum ComparisonVerdict
{
    /// <summary>Every gate term held.</summary>
    BenefitDemonstrated,

    /// <summary>At least one gate term failed.</summary>
    NoDemonstratedBenefit,

    /// <summary>Too many instances were excluded for the comparison to be evaluated.</summary>
    Inconclusive,

    /// <summary>The run stopped before every trial ran, so no gate was evaluated.</summary>
    NotEvaluated,
}

/// <summary>What the whole experiment concluded, from the three comparisons, by the pre-registered rule.</summary>
public enum OverallConclusion
{
    ReuseBenefitAttributableToContent,
    BenefitNotAttributableToContent,
    NoDemonstratedBenefit,
    Inconclusive,
    NotEvaluated,
}

/// <summary>One instance's pair of evaluation trials in one comparison.</summary>
public sealed record PairedObservation(
    int Instance,
    int TreatmentFailedAttempts,
    int ControlFailedAttempts,
    bool TreatmentVerified,
    bool ControlVerified,
    int TreatmentUnauthorized,
    int ControlUnauthorized);

/// <summary>An exact sign test's inputs and result.</summary>
/// <param name="Wins">Pairs in which the treatment failed fewer attempts.</param>
/// <param name="Losses">Pairs in which it failed more.</param>
/// <param name="Ties">Pairs that failed the same number, dropped from the test.</param>
/// <param name="PValue">P(at least <paramref name="Wins"/> wins of <c>Wins + Losses</c> | p = 1/2). 1 when no pair differs.</param>
public sealed record SignTestResult(int Wins, int Losses, int Ties, double PValue);

/// <summary>One gate term, evaluated.</summary>
public sealed record GateTerm(string Expression, bool Holds, string Observed);

/// <summary>One comparison: its pairs, its terms, and its verdict.</summary>
public sealed record ComparisonResult(
    string Treatment,
    string Control,
    int Pairs,
    int ExcludedInstances,
    double? TreatmentMeanFailedAttempts,
    double? ControlMeanFailedAttempts,
    double? TreatmentSuccessRate,
    double? ControlSuccessRate,
    int TreatmentUnauthorized,
    int ControlUnauthorized,
    SignTestResult? SignTest,
    IReadOnlyList<GateTerm> Terms,
    ComparisonVerdict Verdict);

/// <summary>The exact sign test.</summary>
public static class SignTest
{
    /// <summary>
    /// The one-sided exact p-value: the probability, under a fair coin, of at least <paramref name="wins"/> heads in
    /// <c>wins + losses</c> tosses. Computed exactly in integers, then divided once.
    /// </summary>
    public static double OneSidedP(int wins, int losses)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(wins);
        ArgumentOutOfRangeException.ThrowIfNegative(losses);

        var n = wins + losses;
        if (n == 0)
        {
            return 1d;
        }

        var tail = BigInteger.Zero;
        var coefficient = BigInteger.One; // C(n, 0)
        for (var k = 0; k <= n; k++)
        {
            if (k >= wins)
            {
                tail += coefficient;
            }

            coefficient = coefficient * (n - k) / (k + 1);
        }

        return (double)tail / (double)BigInteger.Pow(2, n);
    }

    /// <summary>Counts wins, losses and ties on the primary metric, and tests them.</summary>
    public static SignTestResult Run(IReadOnlyList<PairedObservation> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var wins = pairs.Count(pair => pair.TreatmentFailedAttempts < pair.ControlFailedAttempts);
        var losses = pairs.Count(pair => pair.TreatmentFailedAttempts > pair.ControlFailedAttempts);
        return new SignTestResult(wins, losses, pairs.Count - wins - losses, OneSidedP(wins, losses));
    }
}

/// <summary>
/// The pre-registered gate: four terms, all required, evaluated once per comparison. There is no second gate and no
/// path that turns a failed term into a pass.
/// </summary>
public static class LiveGate
{
    public static ComparisonResult Evaluate(
        string treatment,
        string control,
        IReadOnlyList<PairedObservation> pairs,
        int excludedInstances,
        LivePreregistration design,
        bool runComplete)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(design);

        double? Mean(Func<PairedObservation, double> select) => pairs.Count == 0 ? null : pairs.Average(select);

        var treatmentMean = Mean(pair => pair.TreatmentFailedAttempts);
        var controlMean = Mean(pair => pair.ControlFailedAttempts);
        var treatmentSuccess = Mean(pair => pair.TreatmentVerified ? 1 : 0);
        var controlSuccess = Mean(pair => pair.ControlVerified ? 1 : 0);
        var treatmentUnauthorized = pairs.Sum(pair => pair.TreatmentUnauthorized);
        var controlUnauthorized = pairs.Sum(pair => pair.ControlUnauthorized);

        if (!runComplete)
        {
            return new ComparisonResult(treatment, control, pairs.Count, excludedInstances, treatmentMean, controlMean, treatmentSuccess, controlSuccess,
                treatmentUnauthorized, controlUnauthorized, null, [], ComparisonVerdict.NotEvaluated);
        }

        if (excludedInstances > design.MaxExcludedInstances || pairs.Count == 0)
        {
            return new ComparisonResult(treatment, control, pairs.Count, excludedInstances, treatmentMean, controlMean, treatmentSuccess, controlSuccess,
                treatmentUnauthorized, controlUnauthorized, null, [], ComparisonVerdict.Inconclusive);
        }

        var sign = SignTest.Run(pairs);

        var terms = new List<GateTerm>
        {
            new(
                $"mean(failed_attempts | {treatment}) < mean(failed_attempts | {control})",
                treatmentMean < controlMean,
                $"{Format(treatmentMean)} against {Format(controlMean)}"),
            new(
                $"sign_test_p(failed_attempts: {treatment} < {control}) <= {design.Alpha.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}",
                sign.PValue <= design.Alpha,
                $"p = {sign.PValue.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)} ({sign.Wins} fewer, {sign.Losses} more, {sign.Ties} tied, of {pairs.Count} pairs)"),
            new(
                $"success_rate({treatment}) >= success_rate({control})",
                treatmentSuccess >= controlSuccess,
                $"{Format(treatmentSuccess)} against {Format(controlSuccess)}"),
            new(
                $"sum(unauthorized_tool_requests | {treatment}) <= sum(unauthorized_tool_requests | {control})",
                treatmentUnauthorized <= controlUnauthorized,
                $"{treatmentUnauthorized} against {controlUnauthorized}"),
        };

        var verdict = terms.All(term => term.Holds) ? ComparisonVerdict.BenefitDemonstrated : ComparisonVerdict.NoDemonstratedBenefit;

        return new ComparisonResult(treatment, control, pairs.Count, excludedInstances, treatmentMean, controlMean, treatmentSuccess, controlSuccess,
            treatmentUnauthorized, controlUnauthorized, sign, terms, verdict);
    }

    /// <summary>
    /// The pre-registered rule that combines the three comparisons: memory-enabled against memory-disabled (the
    /// reference), memory-enabled against the placebo (content: the same block with the strategy withheld), and the
    /// negative control against memory-disabled. A benefit is credited to what the record says only if the reference
    /// and the content comparison both pass and the negative control does not.
    /// </summary>
    public static OverallConclusion Conclude(ComparisonVerdict reference, ComparisonVerdict content, ComparisonVerdict negativeControl)
    {
        ComparisonVerdict[] all = [reference, content, negativeControl];
        if (all.Contains(ComparisonVerdict.NotEvaluated))
        {
            return OverallConclusion.NotEvaluated;
        }

        if (all.Contains(ComparisonVerdict.Inconclusive))
        {
            return OverallConclusion.Inconclusive;
        }

        if (reference == ComparisonVerdict.NoDemonstratedBenefit)
        {
            return OverallConclusion.NoDemonstratedBenefit;
        }

        return content == ComparisonVerdict.BenefitDemonstrated && negativeControl == ComparisonVerdict.NoDemonstratedBenefit
            ? OverallConclusion.ReuseBenefitAttributableToContent
            : OverallConclusion.BenefitNotAttributableToContent;
    }

    internal static string Format(double? value) =>
        value is { } number ? number.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) : "undefined";
}

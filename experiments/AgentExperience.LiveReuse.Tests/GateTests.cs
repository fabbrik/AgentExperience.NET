using AgentExperience.LiveReuse.Harness;

namespace AgentExperience.LiveReuse.Tests;

public sealed class GateTests
{
    private static readonly LivePreregistration Design = LivePreregistration.ReadEmbedded();

    [Theory]
    [InlineData(0, 0, 1.0)]
    [InlineData(1, 0, 0.5)]
    [InlineData(10, 0, 1.0 / 1024)]
    [InlineData(9, 1, 11.0 / 1024)]
    [InlineData(8, 2, 56.0 / 1024)]
    [InlineData(12, 0, 1.0 / 4096)]
    [InlineData(10, 2, 79.0 / 4096)]
    [InlineData(9, 3, 299.0 / 4096)]
    [InlineData(0, 12, 1.0)]
    [InlineData(5, 5, 638.0 / 1024)]
    public void The_sign_test_is_the_exact_binomial_upper_tail(int wins, int losses, double expected)
    {
        Assert.Equal(expected, SignTest.OneSidedP(wins, losses), 12);
    }

    /// <summary>What the pre-registration's justification says: with 12 untied pairs, 10 wins pass at 0.05 and 9 do not.</summary>
    [Fact]
    public void Twelve_untied_pairs_need_ten_wins()
    {
        Assert.True(SignTest.OneSidedP(10, 2) <= Design.Alpha);
        Assert.False(SignTest.OneSidedP(9, 3) <= Design.Alpha);
    }

    [Fact]
    public void Ties_are_dropped_from_the_test_but_counted()
    {
        var pairs = new[] { Pair(0, 2), Pair(0, 0), Pair(1, 1), Pair(3, 1) };
        Assert.Equal(new SignTestResult(1, 1, 2, 0.75), SignTest.Run(pairs));
    }

    [Fact]
    public void All_four_terms_holding_is_a_demonstrated_benefit()
    {
        var result = Evaluate(Enumerable.Range(0, 12).Select(i => Pair(0, 3)).ToArray());
        Assert.Equal(ComparisonVerdict.BenefitDemonstrated, result.Verdict);
        Assert.All(result.Terms, term => Assert.True(term.Holds));
    }

    [Fact]
    public void A_lower_mean_that_is_not_significant_is_no_demonstrated_benefit()
    {
        // 9 wins, 3 losses: the mean is lower, p = 0.073.
        var pairs = Enumerable.Range(0, 9).Select(i => Pair(0, 3)).Concat(Enumerable.Range(0, 3).Select(i => Pair(1, 0))).ToArray();
        var result = Evaluate(pairs);
        Assert.True(result.Terms[0].Holds);
        Assert.False(result.Terms[1].Holds);
        Assert.Equal(ComparisonVerdict.NoDemonstratedBenefit, result.Verdict);
    }

    [Fact]
    public void A_significant_drop_in_failures_that_costs_success_is_no_demonstrated_benefit()
    {
        var pairs = Enumerable.Range(0, 12).Select(i => Pair(0, 3, treatmentVerified: i != 0)).ToArray();
        var result = Evaluate(pairs);
        Assert.False(result.Terms[2].Holds);
        Assert.Equal(ComparisonVerdict.NoDemonstratedBenefit, result.Verdict);
    }

    [Fact]
    public void One_more_unauthorized_request_under_treatment_is_no_demonstrated_benefit()
    {
        var pairs = Enumerable.Range(0, 12).Select(i => Pair(0, 3, treatmentUnauthorized: i == 0 ? 1 : 0)).ToArray();
        var result = Evaluate(pairs);
        Assert.False(result.Terms[3].Holds);
        Assert.Equal(ComparisonVerdict.NoDemonstratedBenefit, result.Verdict);
    }

    [Fact]
    public void Equal_means_are_no_demonstrated_benefit()
    {
        Assert.Equal(ComparisonVerdict.NoDemonstratedBenefit, Evaluate(Enumerable.Range(0, 12).Select(i => Pair(2, 2)).ToArray()).Verdict);
    }

    [Fact]
    public void More_exclusions_than_the_limit_are_inconclusive_and_a_partial_run_is_not_evaluated()
    {
        var pairs = Enumerable.Range(0, 8).Select(i => Pair(0, 3)).ToArray();
        Assert.Equal(ComparisonVerdict.Inconclusive, LiveGate.Evaluate("t", "c", pairs, Design.MaxExcludedInstances + 1, Design, runComplete: true).Verdict);
        Assert.Equal(ComparisonVerdict.BenefitDemonstrated, LiveGate.Evaluate("t", "c", pairs, Design.MaxExcludedInstances, Design, runComplete: true).Verdict);
        Assert.Equal(ComparisonVerdict.NotEvaluated, LiveGate.Evaluate("t", "c", pairs, 0, Design, runComplete: false).Verdict);
    }

    [Theory]
    [InlineData(ComparisonVerdict.BenefitDemonstrated, ComparisonVerdict.BenefitDemonstrated, ComparisonVerdict.NoDemonstratedBenefit, OverallConclusion.ReuseBenefitAttributableToContent)]
    [InlineData(ComparisonVerdict.BenefitDemonstrated, ComparisonVerdict.NoDemonstratedBenefit, ComparisonVerdict.NoDemonstratedBenefit, OverallConclusion.BenefitNotAttributableToContent)]
    [InlineData(ComparisonVerdict.BenefitDemonstrated, ComparisonVerdict.BenefitDemonstrated, ComparisonVerdict.BenefitDemonstrated, OverallConclusion.BenefitNotAttributableToContent)]
    [InlineData(ComparisonVerdict.NoDemonstratedBenefit, ComparisonVerdict.BenefitDemonstrated, ComparisonVerdict.NoDemonstratedBenefit, OverallConclusion.NoDemonstratedBenefit)]
    [InlineData(ComparisonVerdict.NoDemonstratedBenefit, ComparisonVerdict.NoDemonstratedBenefit, ComparisonVerdict.BenefitDemonstrated, OverallConclusion.NoDemonstratedBenefit)]
    [InlineData(ComparisonVerdict.BenefitDemonstrated, ComparisonVerdict.Inconclusive, ComparisonVerdict.NoDemonstratedBenefit, OverallConclusion.Inconclusive)]
    [InlineData(ComparisonVerdict.NotEvaluated, ComparisonVerdict.NotEvaluated, ComparisonVerdict.NotEvaluated, OverallConclusion.NotEvaluated)]
    public void The_overall_conclusion_follows_the_pre_registered_table(ComparisonVerdict reference, ComparisonVerdict content, ComparisonVerdict negative, OverallConclusion expected)
    {
        Assert.Equal(expected, LiveGate.Conclude(reference, content, negative));
    }

    /// <summary>The terms the code evaluates are, word for word, the expression the file registered.</summary>
    [Fact]
    public void The_evaluated_terms_are_the_registered_gate_expression()
    {
        var result = Evaluate(Enumerable.Range(0, 12).Select(i => Pair(0, 3)).ToArray());
        Assert.Equal(Design.GateExpression, string.Join(" AND ", result.Terms.Select(term => term.Expression)));
    }

    private static ComparisonResult Evaluate(PairedObservation[] pairs) => LiveGate.Evaluate("treatment", "control", pairs, 0, Design, runComplete: true);

    private static PairedObservation Pair(int treatment, int control, bool treatmentVerified = true, int treatmentUnauthorized = 0) =>
        new(0, treatment, control, treatmentVerified, true, treatmentUnauthorized, 0);
}

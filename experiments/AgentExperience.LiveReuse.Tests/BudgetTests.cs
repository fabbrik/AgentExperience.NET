using AgentExperience.LiveReuse.Harness;

namespace AgentExperience.LiveReuse.Tests;

public sealed class BudgetTests
{
    [Fact]
    public async Task The_call_cap_stops_the_run_cleanly_before_the_next_call_and_no_verdict_is_evaluated()
    {
        var model = new ScriptedOperatorModel();
        var result = await TestSupport.RunScriptedAsync(model, new LiveBudget(MaxModelCalls: 25, MaxTotalTokens: 10_000_000));

        Assert.False(result.Complete);
        Assert.Equal(25, result.Total.ModelCalls);
        Assert.Equal(25, model.Calls.Count);
        Assert.Contains("learning phase", result.StopReason, StringComparison.Ordinal);
        Assert.Equal(RunStatus.BudgetExhausted, result.Learning[^1].Status);
        Assert.Empty(result.Trials);
        Assert.Equal(ComparisonVerdict.NotEvaluated, result.Reference.Verdict);
        Assert.Equal(ComparisonVerdict.NotEvaluated, result.NegativeControl.Verdict);
        Assert.Equal(OverallConclusion.NotEvaluated, result.Conclusion);

        var report = LiveReuseReport.Markdown(result);
        Assert.Contains("STOPPED", report, StringComparison.Ordinal);
        Assert.Contains("support no conclusion", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_token_cap_stops_the_run_during_evaluation_too()
    {
        var full = await TestSupport.RunScriptedAsync(new ScriptedOperatorModel());
        var learningTokens = full.Learning.Sum(run => run.Usage.TotalTokens);

        var model = new ScriptedOperatorModel();
        var result = await TestSupport.RunScriptedAsync(model, new LiveBudget(MaxModelCalls: 10_000, MaxTotalTokens: learningTokens + 500));

        Assert.False(result.Complete);
        Assert.Contains("evaluation phase", result.StopReason, StringComparison.Ordinal);
        Assert.Equal(RunStatus.BudgetExhausted, result.Trials[^1].Status);
        Assert.True(result.Trials.Count < 48);

        // Stops before the first call that would START at or past the cap: the overshoot is at most one call.
        Assert.True(result.Total.TotalTokens < learningTokens + 500 + 5_000);
        Assert.Equal(OverallConclusion.NotEvaluated, result.Conclusion);
    }

    [Fact]
    public async Task The_pre_registered_defaults_apply_when_no_budget_is_given()
    {
        var result = await TestSupport.RunScriptedAsync(new ScriptedOperatorModel());
        Assert.Equal(new LiveBudget(result.Design.DefaultMaxModelCalls, result.Design.DefaultMaxTotalTokens), result.Budget);
        Assert.True(result.Complete);
    }
}

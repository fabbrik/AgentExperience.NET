using AgentExperience.MicrosoftAgentFramework.Injection;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Contract 1.10's checks that need no database: every benefit-select fixture is what the .NET context provider selects
/// and its writer keeps, with every omission's reason and detail.
/// </summary>
public sealed partial class ContractFixtureTests
{
    [Fact]
    public void Every_benefit_select_fixture_is_what_the_context_provider_selects()
    {
        var drift = new List<string>();
        foreach (var (name, definition) in BenefitSelectContract.Cases)
        {
            var directory = $"fixtures/{BenefitSelectContract.Family}/{name}";
            var recordsJson = ContractJson.Write(InjectContract.ToRecordsJson(definition.Records));
            var estimateJson = ContractJson.Write(BenefitSelectContract.EstimateJson(definition.Estimate));
            var optionsJson = ContractJson.Write(definition.Options);
            ContractFiles.Verify($"{directory}/records.json", recordsJson, drift);
            ContractFiles.Verify($"{directory}/estimate.json", estimateJson, drift);
            ContractFiles.Verify($"{directory}/options.json", optionsJson, drift);

            // The records and options as checked in, as a port reads them; the estimate is checked byte for byte above.
            var records = InjectContract.FromRecordsJson(ContractFiles.Read($"{directory}/records.json") ?? recordsJson);
            var options = ContractJson.Read<BenefitSelectOptions>(ContractFiles.Read($"{directory}/options.json") ?? optionsJson);
            var (selected, injected, omitted) = BenefitSelectContract.Run(records, definition.Estimate, options);
            ContractFiles.Verify($"{directory}/expected.json", ContractJson.Write(BenefitSelectContract.ExpectedJson(selected, injected, omitted)), drift);
        }

        ContractFiles.VerifyNoStray(
            BenefitSelectContract.Family,
            BenefitSelectContract.Cases.Select(entry => entry.Name),
            ["records.json", "estimate.json", "options.json", "expected.json"],
            drift);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }

    [Fact]
    public void The_benefit_select_cases_cover_every_rule()
    {
        var results = BenefitSelectContract.Cases.ToDictionary(
            entry => entry.Name,
            entry => BenefitSelectContract.Run(entry.Case.Records, entry.Case.Estimate, entry.Case.Options));

        string? Detail(string name, InjectionOmissionReason reason) =>
            results[name].Omitted.First(entry => entry.Reason == reason).Detail;

        Assert.Equal(ExperienceContextProvider.MeasuredHarmfulDetail, Detail("one-lesson-hurts", InjectionOmissionReason.MeasuredHarmful));
        Assert.Empty(results["scope-hurts"].Injected);
        Assert.All(results["scope-hurts"].Omitted, entry => Assert.Equal(ExperienceContextProvider.ScopeMeasuredHarmfulDetail, entry.Detail));
        Assert.All(results["per-model-scope-hurts"].Omitted, entry => Assert.Equal(ExperienceContextProvider.ScopeMeasuredHarmfulDetail, entry.Detail));
        Assert.Empty(results["per-model-scope-hurts"].Injected);

        // Reordering: the helpful lesson leads, inside the host's window only.
        var g = BenefitSelectContract.Cases.Single(entry => entry.Name == "prefer-helpful").Case.Records[4].Record.ExperienceId;
        Assert.Equal(g, results["prefer-helpful"].Selected[0]);
        Assert.Equal(g, results["prefer-helpful-within-the-request-limit"].Selected[0]);
        Assert.Equal(g, results["prefer-helpful-past-the-request-limit"].Selected[1]);
        Assert.Single(results["prefer-helpful-within-the-request-limit"].Omitted);

        // Per model: each model sees its own verdicts, and an unknown, unmeasured or absent model the pooled ones.
        Assert.NotEqual(results["per-model"].Selected, results["per-model-other-model"].Selected);
        Assert.Equal(results["per-model-no-model"].Selected, results["per-model-unknown"].Selected);
        Assert.Equal(results["per-model-no-model"].Selected, results["per-model-unmeasured"].Selected);
        Assert.Equal(results["per-model-no-model"].Selected, results["pooled-ignores-the-model"].Selected);
        Assert.NotEmpty(results["per-model-unknown"].Injected);

        // Nothing changes with the policy off or no evidence.
        Assert.DoesNotContain(results["policy-disabled"].Omitted, entry => entry.Reason == InjectionOmissionReason.MeasuredHarmful);
        Assert.DoesNotContain(results["no-evidence"].Omitted, entry => entry.Reason == InjectionOmissionReason.MeasuredHarmful);
        Assert.Equal(results["policy-disabled"].Selected, results["no-evidence"].Selected);

        // Every existing omission, in .NET's order.
        Assert.Equal(
            new[]
            {
                InjectionOmissionReason.ModelAuthored,
                InjectionOmissionReason.MeasuredHarmful,
                InjectionOmissionReason.OverRecordLimit,
                InjectionOmissionReason.OverByteBudget,
            },
            results["existing-omissions"].Omitted.Select(entry => entry.Reason));
        Assert.Null(results["existing-omissions"].Omitted[0].Detail);
    }
}

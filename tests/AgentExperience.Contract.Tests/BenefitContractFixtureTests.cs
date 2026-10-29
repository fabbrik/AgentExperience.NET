using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Holdout;
using AgentExperience.Storage.Postgres;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Contract 1.9's checks that need no database: every benefit-estimate fixture is what the .NET estimator (and its
/// accumulator, page by page) gives, the model-metadata fixture is what capture records, the published statements are the
/// .NET store's, line for line, and the model key changes nothing that the finalize and inject fixtures pin.
/// </summary>
public sealed partial class ContractFixtureTests
{
    [Fact]
    public void Every_benefit_estimate_fixture_is_what_the_estimator_gives()
    {
        var drift = new List<string>();
        foreach (var (name, definition) in BenefitEstimateContract.Cases)
        {
            var directory = $"fixtures/{BenefitEstimateContract.Family}/{name}";
            var rowsJson = ContractJson.Write(BenefitEstimateContract.ToRowsJson(definition));
            var optionsJson = ContractJson.Write(definition.Options);
            ContractFiles.Verify($"{directory}/rows.json", rowsJson, drift);
            ContractFiles.Verify($"{directory}/options.json", optionsJson, drift);

            var rows = BenefitEstimateContract.ReadRows(JsonNode.Parse(ContractFiles.Read($"{directory}/rows.json") ?? rowsJson)!);
            var options = BenefitEstimateContract.ToOptions(ContractJson.Read<BenefitEstimateOptions>(ContractFiles.Read($"{directory}/options.json") ?? optionsJson));
            var estimate = BenefitEstimateContract.EstimateJson(ExperienceBenefitEstimator.Estimate(rows, options));
            ContractFiles.Verify($"{directory}/expected.json", ContractJson.Write(estimate), drift);

            // The paged form gives the same estimate, whatever the page size, and so does the pure form over reversed rows.
            foreach (var pageSize in new[] { 1, 3, 1000 })
            {
                var accumulator = new ExperienceBenefitAccumulator(options);
                foreach (var page in rows.Chunk(pageSize))
                {
                    accumulator.AddRange(page);
                }

                if (!JsonNode.DeepEquals(estimate, BenefitEstimateContract.EstimateJson(accumulator.ToEstimate())))
                {
                    drift.Add($"{directory}: the accumulator, {pageSize} row(s) a page, gives another estimate.");
                }
            }

            if (!JsonNode.DeepEquals(estimate, BenefitEstimateContract.EstimateJson(ExperienceBenefitEstimator.Estimate(rows.Reverse(), options))))
            {
                drift.Add($"{directory}: the estimate depends on the row order.");
            }
        }

        ContractFiles.VerifyNoStray(BenefitEstimateContract.Family, BenefitEstimateContract.Cases.Select(entry => entry.Name), ["rows.json", "options.json", "expected.json"], drift);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }

    [Fact]
    public void The_benefit_estimate_cases_cover_every_verdict_and_count()
    {
        var estimates = BenefitEstimateContract.Cases.ToDictionary(
            entry => entry.Name,
            entry => ExperienceBenefitEstimator.Estimate(entry.Case.Rows, BenefitEstimateContract.ToOptions(entry.Case.Options)));
        Assert.Equal(ExperienceBenefitVerdict.NoEvidence, estimates["below-the-run-minimum"].Overall.Verdict);
        Assert.True(estimates["below-the-run-minimum"].Overall.Lower > 0);
        Assert.Equal(ExperienceBenefitVerdict.Helps, estimates["helps"].Overall.Verdict);
        Assert.Equal(ExperienceBenefitVerdict.Hurts, estimates["hurts"].Overall.Verdict);
        Assert.Equal(ExperienceBenefitVerdict.NoEvidence, estimates["spans-zero"].Overall.Verdict);
        Assert.Equal(ExperienceBenefitVerdict.Helps, estimates["rates-of-one-and-zero"].Overall.Verdict);
        Assert.Null(estimates["one-arm-empty"].Overall.Difference);
        Assert.True(estimates["mixed-arm"].Overall.MixedArm > 0);
        Assert.True(estimates["pending"].Overall.Pending > 0);
        Assert.True(estimates["unverified"].Overall.Unverified > 0);
        Assert.Equal(3, estimates["per-lesson"].Lessons.Count);
        Assert.Contains(estimates["split-by-model"].ByConsumingModel!, model => model.Model == ExperienceBenefitEstimate.UnknownModel);
        Assert.Equal(ExperienceBenefitVerdict.Helps, estimates["split-by-model"].ByConsumingModel!.Single(model => model.Model == "model-a").Comparison.Verdict);
        Assert.Equal(ExperienceBenefitVerdict.Hurts, estimates["split-by-model"].ByConsumingModel!.Single(model => model.Model == "model-b").Comparison.Verdict);
        Assert.Contains(estimates["split-by-model"].Lessons.SelectMany(lesson => lesson.ByModelPair!), pair => pair.LessonModel == ExperienceBenefitEstimate.UnknownModel);
        Assert.Equal(ExperienceBenefitVerdict.Helps, estimates["custom-minimum"].Overall.Verdict);
        Assert.Equal(
            ExperienceBenefitVerdict.NoEvidence,
            ExperienceBenefitEstimator.Estimate(BenefitEstimateContract.Cases.Single(entry => entry.Name == "custom-minimum").Case.Rows).Overall.Verdict);
    }

    [Fact]
    public void The_model_metadata_fixture_is_what_capture_records()
    {
        var drift = new List<string>();
        var path = $"fixtures/{ModelMetadataContract.Family}/{ModelMetadataContract.FileName}";
        ContractFiles.Verify(path, ContractJson.Write(ModelMetadataContract.Run()), drift);
        if (ContractFiles.Read(path) is { } text)
        {
            ModelMetadataContract.Recheck(path, text, drift);
        }

        ContractFiles.VerifyNoStray(ModelMetadataContract.Family, [], [], drift, ModelMetadataContract.FileName);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }

    [Fact]
    public void The_holdout_outcome_statements_are_the_ones_the_store_runs()
    {
        // Contract 1.9: the first page and every later page, each the .NET store's statement line for line.
        var statements = VectorStatements.Read(BenefitStatements.QueryFile);
        Assert.Equal(new[] { "first-page", "next-page" }, statements.Keys.ToArray());
        Assert.Equal(
            PostgresExperienceRecordStore.QueryHoldoutOutcomesPrefix + PostgresExperienceRecordStore.QueryHoldoutOutcomesSuffix,
            statements["first-page"]);
        Assert.Equal(
            PostgresExperienceRecordStore.QueryHoldoutOutcomesPrefix + PostgresExperienceRecordStore.QueryHoldoutOutcomesAfterPredicate + PostgresExperienceRecordStore.QueryHoldoutOutcomesSuffix,
            statements["next-page"]);
    }

    [Fact]
    public async Task The_model_key_changes_no_finalize_fixture_but_the_environment_it_is_recorded_in()
    {
        // Contract 1.9: the model is bookkeeping. A run whose environment carries it stores the record the fixture pins,
        // with the key in its environment metadata and nowhere else: not as a precondition, not in the lesson.
        foreach (var (name, _) in FinalizeContract.Cases)
        {
            var directory = $"fixtures/{FinalizeContract.Family}/{name}";
            var input = ContractJson.Read<FinalizeInput>(ContractFiles.Read($"{directory}/input.json")!);
            var metadata = new Dictionary<string, string>(input.Run.Environment.Metadata, StringComparer.Ordinal) { [HoldoutOutcomeRow.ModelMetadataKey] = "contract-model" };
            var withModel = input with { Run = input.Run with { Environment = input.Run.Environment with { Metadata = metadata } } };
            var (record, lifecycleEvent) = await FinalizeContract.RunAsync(withModel);

            var expected = JsonNode.Parse(ContractFiles.Read($"{directory}/expected-record.json")!)!;
            expected["payload"]!["environment"]!["metadata"]![HoldoutOutcomeRow.ModelMetadataKey] = "contract-model";
            Assert.True(JsonNode.DeepEquals(expected, record), $"{directory}: the model key changed the record beyond its environment.");
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ContractFiles.Read($"{directory}/expected-event.json")!), lifecycleEvent), $"{directory}: the model key changed the event.");
        }
    }

    [Fact]
    public async Task The_model_key_changes_no_inject_fixture()
    {
        // Contract 1.9: the block never shows the model, in either rendering.
        foreach (var (name, _) in await InjectContract.CasesAsync())
        {
            var directory = $"fixtures/{InjectContract.Family}/{name}";
            var records = InjectContract.FromRecordsJson(ContractFiles.Read($"{directory}/records.json")!)
                .Select(entry => (entry.Record with
                {
                    Environment = entry.Record.Environment with
                    {
                        Metadata = new Dictionary<string, string>(entry.Record.Environment.Metadata, StringComparer.Ordinal) { [HoldoutOutcomeRow.ModelMetadataKey] = "contract-model" },
                    },
                }, entry.Ranking))
                .ToList();
            var options = ContractJson.Read<InjectOptions>(ContractFiles.Read($"{directory}/options.json")!);
            Assert.Equal(ContractFiles.Read($"{directory}/expected-block.txt"), InjectContract.Render(records, options));
        }
    }
}

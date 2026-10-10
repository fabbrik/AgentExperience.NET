using System.Text;
using System.Text.Json.Nodes;
using AgentExperience.LiveReuse.Harness;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Extensions.AI;

namespace AgentExperience.LiveReuse.Tests;

/// <summary>
/// The transfer experiment (story 20.4), offline: the task set and its digest lock, the pre-registration, the integrity
/// guard, and the I/O matrix rows, all against scripted models. The library's capture, verification, reflection,
/// in-memory store, candidate source, retrieval and injection are real; only the model is a script.
/// </summary>
public sealed class TransferTests : IDisposable
{
    /// <summary>
    /// The git blob id of <c>preregistration.transfer.json</c> as registered, and its amendment count. Any change to the
    /// file fails this test until both are updated, so editing the registration is always a visible act in review.
    /// </summary>
    private const string RegisteredBlobId = "f568bcd660433abe6d472271b918ae804c0226d9";

    private const int RegisteredAmendments = 0;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "transfer-tests-" + Guid.NewGuid().ToString("N"));

    private static string OnDisk => Path.Combine(TestSupport.ExperimentDirectory(), TransferPreregistration.FileName);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    internal static Task<TransferExperimentResult> RunScriptedAsync(IChatClient model, LiveBudget? budget = null, TransferTaskSet? taskSet = null, RunDescriptor? descriptor = null) =>
        TransferExperiment.RunAsync(new TransferExperimentOptions
        {
            Model = model,
            Descriptor = descriptor ?? TestSupport.ScriptedDescriptor,
            Budget = budget,
            Clock = new SteppingClock(),
            TaskSet = taskSet ?? TransferTaskSet.Current,
        });

    // ---- task set and digest ---------------------------------------------------------------------------------------

    [Fact]
    public void The_task_set_validates_and_its_trait_is_a_bijection_onto_the_strategies()
    {
        var taskSet = TransferTaskSet.Current;
        taskSet.Validate();

        Assert.Equal(["halyard", "keel", "mizzen", "bowsprit", "capstan", "taffrail"], taskSet.Clusters);
        Assert.Equal(RolloutStrategies.All.Order(StringComparer.Ordinal), taskSet.StrategyByCluster.Values.Order(StringComparer.Ordinal));
        Assert.Equal(6, taskSet.LearningServices.Count);
        Assert.Equal(12, taskSet.EvaluationInstances.Count);
        Assert.Equal(24, taskSet.Distractors.Count);
        Assert.All(taskSet.Clusters, cluster => Assert.Equal(2, taskSet.EvaluationInstances.Count(instance => instance.Cluster == cluster)));
        Assert.All(taskSet.Clusters, cluster => Assert.Equal(2, taskSet.Distractors.Count(distractor => distractor.Cluster == cluster && distractor.Family == DistractorFamily.CacheFlush)));
        Assert.All(taskSet.Clusters, cluster => Assert.Equal(2, taskSet.Distractors.Count(distractor => distractor.Cluster == cluster && distractor.Family == DistractorFamily.ConfigRollout)));
        Assert.StartsWith("Roll out config change `2026_09_checkoutapi_pool_limit` on `checkoutapi` (cluster `halyard`).", taskSet.Distractors[12].Text, StringComparison.Ordinal);

        // Every text names its service and cluster in the shape the story fixes, and describe_service reports the cluster.
        var instance = taskSet.EvaluationInstances[1];
        Assert.Contains($"roll out migration `{instance.Migration}` on `{instance.Service}` (cluster `{instance.Cluster}`)", TransferTaskSet.EvaluationText(instance), StringComparison.Ordinal);
        Assert.EndsWith("; cluster=" + instance.Cluster, taskSet.Describe(instance), StringComparison.Ordinal);
        Assert.Contains("(cluster `keel`)", taskSet.Distractors[1].Text, StringComparison.Ordinal);

        // Learning and evaluation texts are written with different words.
        Assert.NotEqual(
            TransferTaskSet.LearningText(taskSet.LearningServices[0]).Split(' ')[0],
            TransferTaskSet.EvaluationText(taskSet.EvaluationInstances[0]).Split(' ')[0]);
    }

    [Fact]
    public void The_reuse_experiments_describe_service_output_is_unchanged()
    {
        var instance = MigrationTaskSet.Current.Instances[0];
        Assert.Equal(MigrationEnvironment.Describe(instance), MigrationEnvironment.Describe(instance, cluster: null));
        Assert.DoesNotContain("cluster", MigrationEnvironment.Describe(instance), StringComparison.Ordinal);
    }

    [Fact]
    public void A_task_set_that_names_a_strategy_or_reuses_a_learning_service_is_refused()
    {
        var current = TransferTaskSet.Current;

        var leaky = current.EvaluationInstances.ToList();
        leaky[0] = leaky[0] with { Description = "introduces a reason_code via blue-green" };
        var refused = Assert.Throws<TaskSetException>(() => Rebuild(current, evaluation: leaky).Validate());
        Assert.Contains("names the strategy 'blue-green'", refused.Message, StringComparison.Ordinal);

        var seen = current.EvaluationInstances.ToList();
        seen[0] = seen[0] with { Service = current.LearningServices[0].Service };
        Assert.Contains("share a service", Assert.Throws<TaskSetException>(() => Rebuild(current, evaluation: seen).Validate()).Message, StringComparison.Ordinal);

        var notBijective = current.StrategyByCluster.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        notBijective["keel"] = notBijective["halyard"];
        Assert.Contains("bijection", Assert.Throws<TaskSetException>(() => Rebuild(current, strategies: notBijective).Validate()).Message, StringComparison.Ordinal);

        // An unknown cluster is refused here, not met later as a missing key in the middle of a paid run.
        var lost = current.EvaluationInstances.ToList();
        lost[0] = lost[0] with { Cluster = "jib" };
        Assert.Contains("'jib'", Assert.Throws<TaskSetException>(() => Rebuild(current, evaluation: lost).Validate()).Message, StringComparison.Ordinal);

        var strayDistractor = current.Distractors.ToList();
        strayDistractor[0] = strayDistractor[0] with { Cluster = "jib" };
        Assert.Contains("'jib'", Assert.Throws<TaskSetException>(() => Rebuild(current, distractors: strayDistractor).Validate()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_task_set_whose_texts_changed_is_refused_before_any_model_call()
    {
        var current = TransferTaskSet.Current;
        var reworded = current.Distractors.ToList();
        reworded[12] = reworded[12] with { Description = "raises the connection pool limit from 200 to 400" };
        var changed = Rebuild(current, distractors: reworded);
        changed.Validate();
        Assert.Equal(current.TraitDigest(), changed.TraitDigest());

        var model = new ScriptedOperatorModel();
        var refused = await Assert.ThrowsAsync<PreregistrationException>(() => RunScriptedAsync(model, taskSet: changed));
        Assert.Contains("texts digest to", refused.Message, StringComparison.Ordinal);
        Assert.Empty(model.Calls);
    }

    [Fact]
    public async Task A_task_set_whose_trait_changed_is_refused_before_any_model_call()
    {
        var current = TransferTaskSet.Current;
        var swapped = current.StrategyByCluster.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        (swapped["halyard"], swapped["keel"]) = (swapped["keel"], swapped["halyard"]);
        var changed = Rebuild(current, strategies: swapped);
        changed.Validate();

        var model = new ScriptedOperatorModel();
        var refused = await Assert.ThrowsAsync<PreregistrationException>(() => RunScriptedAsync(model, taskSet: changed));
        Assert.Contains("digests to", refused.Message, StringComparison.Ordinal);
        Assert.Empty(model.Calls);
    }

    // ---- pre-registration -------------------------------------------------------------------------------------------

    [Fact]
    public void The_embedded_transfer_pre_registration_is_the_checked_in_file_and_is_registered()
    {
        var bytes = File.ReadAllBytes(OnDisk);
        var design = TransferPreregistration.ReadEmbedded();
        Assert.Equal(LivePreregistration.ComputeGitBlobId(bytes), design.GitBlobId);
        Assert.Equal(bytes.Length, design.ByteCount);

        Assert.True(
            design.GitBlobId == RegisteredBlobId,
            $"{TransferPreregistration.FileName} changed (blob {design.GitBlobId}, registered {RegisteredBlobId}). Record an amendment and update RegisteredBlobId and RegisteredAmendments.");
        Assert.True(design.Amendments.Count == RegisteredAmendments, $"{design.Amendments.Count} amendments recorded; {RegisteredAmendments} registered.");

        Assert.False(design.LiveResultsExistedAtRegistration);
        Assert.Contains("offline scripted results are part of the design", design.ResultsNote, StringComparison.Ordinal);
        Assert.Equal("registered in the commit that adds this file (parent c3dcb37)", design.RegisteredAgainstCommit);
        design.Check(TransferTaskSet.Current);
        Assert.Equal(TransferTaskSet.Current.TraitDigest(), design.TraitAssignmentSha256);
        Assert.Equal(TransferTaskSet.Current.TaskTextDigest(), design.TaskTextSha256);
        Assert.Equal(24, design.Distractors);
        Assert.Equal(4, design.MaxExcludedInstances);
        Assert.Equal(0.05, design.Alpha);
        Assert.Equal(["memory-disabled", "memory-enabled", "memory-placebo", "mismatched-trait"], [design.ControlLabel, design.TreatmentLabel, design.PlaceboLabel, design.MismatchedLabel]);
        Assert.Equal(Enum.GetNames<TransferConclusion>().Order(StringComparer.Ordinal), design.OverallConclusion.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("transfer-ledger.tsv", design.ConfirmatoryRunRule, StringComparison.Ordinal);

        // The gate is 9.1's, word for word.
        Assert.Equal(LivePreregistration.ReadEmbedded().GateExpression, design.GateExpression);
    }

    [Theory]
    [InlineData("primaryMetric", "tool_calls", "primary metric")]
    [InlineData("taskSetVersion", "transfer-clusters@2", "task set")]
    [InlineData("evaluationInstances", 11, "instances")]
    [InlineData("taskTextSha256", "0000", "texts digest to")]
    public void A_transfer_pre_registration_the_harness_cannot_execute_is_refused(string field, object value, string expected)
    {
        var node = JsonNode.Parse(File.ReadAllText(OnDisk))!;
        node[field] = JsonValue.Create(value);
        var design = TransferPreregistration.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()));

        var refused = Assert.Throws<PreregistrationException>(() => design.Check(TransferTaskSet.Current));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_field_or_an_unregistered_verdict_is_refused()
    {
        var node = JsonNode.Parse(File.ReadAllText(OnDisk))!.AsObject();
        node.Remove("exclusions");
        Assert.Throws<PreregistrationException>(() => TransferPreregistration.Parse(Encoding.UTF8.GetBytes(node.ToJsonString())));

        var renamed = JsonNode.Parse(File.ReadAllText(OnDisk))!.AsObject();
        renamed["overallConclusion"]!.AsObject().Remove("TransferInconclusive");
        var design = TransferPreregistration.Parse(Encoding.UTF8.GetBytes(renamed.ToJsonString()));
        Assert.Throws<PreregistrationException>(() => design.Check(TransferTaskSet.Current));

        // A field of the wrong JSON kind is refused as a pre-registration problem, not an InvalidOperationException.
        var wrongKind = JsonNode.Parse(File.ReadAllText(OnDisk))!.AsObject();
        wrongKind["clusters"] = "six";
        Assert.Throws<PreregistrationException>(() => TransferPreregistration.Parse(Encoding.UTF8.GetBytes(wrongKind.ToJsonString())));
    }

    [Fact]
    public void Repeated_condition_labels_and_a_zero_budget_are_refused()
    {
        var labels = JsonNode.Parse(File.ReadAllText(OnDisk))!.AsObject();
        labels["conditions"]!["placebo"] = "memory-enabled";
        var design = TransferPreregistration.Parse(Encoding.UTF8.GetBytes(labels.ToJsonString()));
        Assert.Contains("distinct", Assert.Throws<PreregistrationException>(() => design.Check(TransferTaskSet.Current)).Message, StringComparison.Ordinal);

        var budget = JsonNode.Parse(File.ReadAllText(OnDisk))!.AsObject();
        budget["budgetDefaults"]!["maxModelCalls"] = 0;
        design = TransferPreregistration.Parse(Encoding.UTF8.GetBytes(budget.ToJsonString()));
        Assert.Contains("budget", Assert.Throws<PreregistrationException>(() => design.Check(TransferTaskSet.Current)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ComparisonVerdict.BenefitDemonstrated, ComparisonVerdict.BenefitDemonstrated, ComparisonVerdict.NoDemonstratedBenefit, TransferConclusion.TransferBenefitAttributableToContent)]
    [InlineData(ComparisonVerdict.BenefitDemonstrated, ComparisonVerdict.NoDemonstratedBenefit, ComparisonVerdict.NoDemonstratedBenefit, TransferConclusion.TransferBenefitNotAttributableToContent)]
    [InlineData(ComparisonVerdict.BenefitDemonstrated, ComparisonVerdict.BenefitDemonstrated, ComparisonVerdict.BenefitDemonstrated, TransferConclusion.TransferBenefitNotAttributableToContent)]
    [InlineData(ComparisonVerdict.NoDemonstratedBenefit, ComparisonVerdict.BenefitDemonstrated, ComparisonVerdict.NoDemonstratedBenefit, TransferConclusion.TransferNoDemonstratedBenefit)]
    [InlineData(ComparisonVerdict.BenefitDemonstrated, ComparisonVerdict.Inconclusive, ComparisonVerdict.NoDemonstratedBenefit, TransferConclusion.TransferInconclusive)]
    [InlineData(ComparisonVerdict.NotEvaluated, ComparisonVerdict.NotEvaluated, ComparisonVerdict.NotEvaluated, TransferConclusion.TransferNotEvaluated)]
    public void The_transfer_conclusion_follows_the_registered_rule(ComparisonVerdict reference, ComparisonVerdict content, ComparisonVerdict mismatched, TransferConclusion expected) =>
        Assert.Equal(expected, LiveGate.ConcludeTransfer(reference, content, mismatched));

    // ---- the offline scripted run -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_scripted_run_goes_through_the_shared_store_and_every_condition_runs()
    {
        var model = new ScriptedOperatorModel();
        var result = await RunScriptedAsync(model);

        Assert.True(result.Complete);
        Assert.Equal(24, result.Distractors.Count);
        Assert.Equal(6, result.Learning.Count);
        Assert.All(result.Learning, learning => Assert.Equal(TransferTaskSet.Current.StrategyByCluster[learning.Cluster], learning.Run.StoredStrategy));
        Assert.Equal(48, result.Trials.Count);
        Assert.Empty(result.Excluded);

        // The distractors cost no model call: every call the model received belongs to a learning run or a trial.
        Assert.Equal(model.Calls.Count, result.Learning.Select(learning => learning.Run).Concat(result.Trials.Select(trial => trial.Run)).Sum(run => run.Usage.ModelCalls));

        // The store holds 30 records against a cap of 8, so retrieval chooses; every block is full.
        var enabled = result.Trials.Where(trial => trial.Run.Condition == "memory-enabled").ToList();
        Assert.All(enabled, trial => Assert.True(trial.Run.BlockSeen));
        Assert.All(enabled, trial => Assert.Equal(8, trial.BlockRecords.Count));

        // The mismatched store never holds the same-cluster record, and its first working line never names the trait.
        Assert.All(result.Trials.Where(trial => trial.Run.Condition == "mismatched-trait"), trial =>
        {
            Assert.False(trial.SameClusterInjected);
            Assert.NotEqual(trial.HiddenStrategy, trial.Run.BlockStrategy);
        });

        // The placebo's block holds the same records and names no strategy.
        Assert.All(result.Trials.Where(trial => trial.Run.Condition == "memory-placebo"), trial =>
        {
            Assert.Equal(0, trial.StrategyMentions);
            Assert.Equal(enabled.Single(other => other.Run.Instance == trial.Run.Instance).BlockRecords, trial.BlockRecords);
        });

        // The recorded offline result with the texts as written (see the golden): the config-rollout tickets, worded like
        // the migration tickets, fill every block, no same-cluster lesson is injected, and a script that follows the block
        // gains nothing. Reported, not fixed.
        Assert.All(enabled, trial => Assert.False(trial.SameClusterInjected));
        Assert.Equal(TransferConclusion.TransferNoDemonstratedBenefit, result.Conclusion);
        Assert.Equal(new HarmComparison(12, 0, 0, 12, 2.5, 2.5), result.Harm);
    }

    [Fact]
    public async Task The_conditions_differ_by_the_injected_block_and_nothing_else()
    {
        var model = new ScriptedOperatorModel();
        var result = await RunScriptedAsync(model);

        static string Render(IReadOnlyList<ChatMessage> messages) =>
            string.Join("\n---\n", messages
                .Where(message => !message.Text.Contains(HistoricalReferenceWriter.BlockBegin, StringComparison.Ordinal))
                .Select(message => message.Role + ": " + message.Text));

        var offset = result.Learning.Sum(learning => learning.Run.Usage.ModelCalls);
        var first = new Dictionary<(int, string), IReadOnlyList<ChatMessage>>();
        foreach (var trial in result.Trials)
        {
            first[(trial.Run.Instance, trial.Run.Condition)] = model.Calls[offset];
            offset += trial.Run.Usage.ModelCalls;
        }

        foreach (var instance in TransferTaskSet.Current.EvaluationInstances)
        {
            var control = Render(first[(instance.Index, "memory-disabled")]);
            Assert.Equal(control, Render(first[(instance.Index, "memory-enabled")]));
            Assert.Equal(control, Render(first[(instance.Index, "memory-placebo")]));
            Assert.Equal(control, Render(first[(instance.Index, "mismatched-trait")]));
            Assert.DoesNotContain(first[(instance.Index, "memory-disabled")], message => message.Text.Contains(HistoricalReferenceWriter.BlockBegin, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task The_evaluation_order_rotates_the_conditions_by_instance()
    {
        var result = await RunScriptedAsync(new ScriptedOperatorModel());

        Assert.Equal(["memory-disabled", "memory-enabled", "mismatched-trait", "memory-placebo"], result.Trials.Where(t => t.Run.Instance == 0).Select(t => t.Run.Condition));
        Assert.Equal(["memory-enabled", "mismatched-trait", "memory-placebo", "memory-disabled"], result.Trials.Where(t => t.Run.Instance == 1).Select(t => t.Run.Condition));
        Assert.All(result.Trials, trial => Assert.Equal(TransferTaskSet.Current.StrategyByCluster[trial.Cluster], trial.Run.AcceptedStrategy));
    }

    /// <summary>A model that never reads the block scores the same in every condition: the harness plants no answer.</summary>
    [Fact]
    public async Task A_model_that_ignores_the_block_shows_no_transfer_benefit()
    {
        var result = await RunScriptedAsync(new ScriptedOperatorModel(ScriptedBehavior.IgnoresBlock));

        Assert.Equal(TransferConclusion.TransferNoDemonstratedBenefit, result.Conclusion);
        foreach (var group in result.Trials.GroupBy(trial => trial.Run.Instance))
        {
            Assert.Single(group.Select(trial => trial.Run.FailedAttempts).Distinct());
        }
    }

    // ---- the I/O matrix ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_cluster_whose_learning_run_does_not_verify_has_its_two_instances_excluded_and_counted()
    {
        var result = await RunScriptedAsync(new NeverActsOnLearning(new ScriptedOperatorModel(), "couponvault"));

        var failed = Assert.Single(result.Learning, learning => learning.Run.Verified == false);
        Assert.Equal("couponvault", failed.Run.Service);
        Assert.Equal("mizzen", failed.Cluster);
        Assert.Null(failed.Run.StoredStrategy);

        Assert.Equal([2, 8], result.Excluded.Select(exclusion => exclusion.Instance));
        Assert.All(result.Excluded, exclusion => Assert.Contains("learning run of cluster mizzen did not verify", exclusion.Reason, StringComparison.Ordinal));
        Assert.Equal(10, result.Reference.Pairs);
        Assert.Equal(2, result.Reference.ExcludedInstances);
        Assert.NotEqual(TransferConclusion.TransferInconclusive, result.Conclusion);

        // Excluded before evaluation, they ran no trial and spent no budget; the run is still complete.
        Assert.True(result.Complete);
        Assert.Equal(40, result.Trials.Count);
        Assert.DoesNotContain(result.Trials, trial => trial.Cluster == "mizzen");
        Assert.Contains("did not verify", TransferReport.Markdown(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task More_than_four_exclusions_make_the_run_inconclusive()
    {
        var result = await RunScriptedAsync(new NeverActsOnLearning(new ScriptedOperatorModel(), "couponvault", "stockpile", "pricebook"));

        Assert.Equal(6, result.Excluded.Count);
        Assert.Equal(ComparisonVerdict.Inconclusive, result.Reference.Verdict);
        Assert.Equal(ComparisonVerdict.Inconclusive, result.Content.Verdict);
        Assert.Equal(ComparisonVerdict.Inconclusive, result.MismatchedControl.Verdict);
        Assert.Equal(TransferConclusion.TransferInconclusive, result.Conclusion);
    }

    [Fact]
    public async Task A_model_that_never_acts_stores_nothing_and_the_run_is_inconclusive()
    {
        var result = await RunScriptedAsync(new ScriptedOperatorModel(ScriptedBehavior.NeverActs));

        Assert.All(result.Learning, learning => Assert.Null(learning.Run.StoredStrategy));
        Assert.Equal(12, result.Excluded.Count);
        Assert.Equal(TransferConclusion.TransferInconclusive, result.Conclusion);

        // Every instance was excluded before evaluation, so no trial ran; the distractors were finalized by the script.
        Assert.Empty(result.Trials);
        Assert.Equal(24, result.Distractors.Count);
    }

    [Fact]
    public async Task The_budget_cap_stops_the_run_writes_the_report_and_evaluates_no_verdict()
    {
        var model = new ScriptedOperatorModel();
        var result = await RunScriptedAsync(model, new LiveBudget(MaxModelCalls: 40, MaxTotalTokens: 10_000_000));

        Assert.False(result.Complete);
        Assert.Equal(40, model.Calls.Count);
        Assert.Contains("evaluation phase", result.StopReason, StringComparison.Ordinal);
        Assert.Equal(RunStatus.BudgetExhausted, result.Trials[^1].Run.Status);
        Assert.Empty(result.Excluded);
        Assert.Equal(ComparisonVerdict.NotEvaluated, result.Reference.Verdict);
        Assert.Equal(TransferConclusion.TransferNotEvaluated, result.Conclusion);

        var report = TransferReport.Markdown(result);
        Assert.Contains("STOPPED", report, StringComparison.Ordinal);
        Assert.Contains("support no conclusion", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provider_failure_in_an_evaluation_trial_excludes_its_instance_and_publishes_no_message()
    {
        // The learning phase makes 27 calls with this script, so call 40 falls in an evaluation trial.
        var result = await RunScriptedAsync(new FailingOnCall(new ScriptedOperatorModel(), failOnCall: 40));

        var errored = Assert.Single(result.Trials, trial => trial.Run.Status == RunStatus.Errored);
        Assert.Equal(nameof(InvalidOperationException), errored.Run.Classification);
        var exclusion = Assert.Single(result.Excluded);
        Assert.Equal(errored.Run.Instance, exclusion.Instance);
        Assert.Equal("an evaluation trial errored", exclusion.Reason);
        Assert.Equal(11, result.Reference.Pairs);
        Assert.Equal(11, result.Content.Pairs);
        Assert.Equal(11, result.MismatchedControl.Pairs);
        Assert.Equal(11, result.Harm.Pairs);
        Assert.DoesNotContain("secret detail", TransferReport.Markdown(result), StringComparison.Ordinal);
        Assert.DoesNotContain("secret detail", TransferReport.Json(result), StringComparison.Ordinal);
    }

    // ---- the integrity guard ----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_integrity_guard_refuses_a_mismatched_block_with_the_same_cluster_record()
    {
        var (result, design, learned, full, mismatched) = await GuardInputsAsync();
        TransferExperiment.RequireIntegrity(design, Runs(result), result.Trials, learned, full, mismatched);

        var trials = result.Trials.ToList();
        var at = trials.FindIndex(trial => trial.Run.Condition == design.MismatchedLabel);
        trials[at] = trials[at] with { BlockRecords = [trials[at].SameClusterTaskId, .. trials[at].BlockRecords] };

        var refused = Assert.Throws<HarnessIntegrityException>(() => TransferExperiment.RequireIntegrity(design, Runs(result), trials, learned, full, mismatched));
        Assert.Contains("own cluster's learning record", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_integrity_guard_refuses_a_mismatched_store_that_holds_the_same_cluster_record()
    {
        var (result, design, learned, full, _) = await GuardInputsAsync();
        var leaky = TransferTaskSet.Current.Clusters.ToDictionary(cluster => cluster, _ => (IReadOnlySet<string>)full, StringComparer.Ordinal);

        var refused = Assert.Throws<HarnessIntegrityException>(() => TransferExperiment.RequireIntegrity(design, Runs(result), result.Trials, learned, full, leaky));
        Assert.Contains("mismatched store", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_integrity_guard_refuses_a_placebo_that_names_a_strategy_a_record_its_store_lacks_and_a_disabled_trial_that_saw_a_block()
    {
        var (result, design, learned, full, mismatched) = await GuardInputsAsync();

        HarnessIntegrityException Refuse(Func<TransferTrial, bool> which, Func<TransferTrial, TransferTrial> plant)
        {
            var trials = result.Trials.ToList();
            var at = trials.FindIndex(trial => which(trial));
            trials[at] = plant(trials[at]);
            return Assert.Throws<HarnessIntegrityException>(() => TransferExperiment.RequireIntegrity(design, Runs(result), trials, learned, full, mismatched));
        }

        Assert.Contains("names a strategy", Refuse(trial => trial.Run.Condition == design.PlaceboLabel, trial => trial with { StrategyMentions = 1 }).Message, StringComparison.Ordinal);
        Assert.Contains("does not hold", Refuse(trial => trial.Run.Condition == design.TreatmentLabel, trial => trial with { BlockRecords = [.. trial.BlockRecords, "nowhere/learning"] }).Message, StringComparison.Ordinal);
        Assert.Contains("memory disabled", Refuse(trial => trial.Run.Condition == design.ControlLabel, trial => trial with { Run = trial.Run with { BlockSeen = true } }).Message, StringComparison.Ordinal);
        Assert.Contains("first record naming", Refuse(trial => trial.Run.Condition == design.TreatmentLabel, trial => trial with { Run = trial.Run with { BlockStrategies = [.. trial.Run.BlockStrategies, RolloutStrategies.InPlace] } }).Message, StringComparison.Ordinal);
        Assert.Contains("working line names", Refuse(trial => trial.Run.Condition == design.MismatchedLabel, trial => trial with { Run = trial.Run with { BlockStrategy = trial.HiddenStrategy } }).Message, StringComparison.Ordinal);
        Assert.Contains("different records", Refuse(trial => trial.Run.Condition == design.PlaceboLabel, trial => trial with { BlockRecords = [.. trial.BlockRecords.Reverse()] }).Message, StringComparison.Ordinal);
    }

    private static List<RunRecord> Runs(TransferExperimentResult result) => [.. result.Learning.Select(learning => learning.Run)];

    private static async Task<(TransferExperimentResult Result, TransferPreregistration Design, Dictionary<string, string> Learned, HashSet<string> Full, Dictionary<string, IReadOnlySet<string>> Mismatched)> GuardInputsAsync()
    {
        var result = await RunScriptedAsync(new ScriptedOperatorModel());
        var taskSet = TransferTaskSet.Current;
        var learned = taskSet.LearningServices.ToDictionary(service => service.Cluster, service => service.Service + TransferExperiment.LearningTaskSuffix, StringComparer.Ordinal);
        var full = result.Distractors.Select(distractor => distractor.TaskId).Concat(learned.Values).ToHashSet(StringComparer.Ordinal);
        var mismatched = taskSet.Clusters.ToDictionary(
            cluster => cluster,
            cluster => (IReadOnlySet<string>)full.Where(taskId => taskId != learned[cluster]).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        return (result, result.Design, learned, full, mismatched);
    }

    // ---- the host ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_scripted_transfer_command_prints_a_scripted_report_and_writes_nothing()
    {
        var output = new StringWriter();
        var exit = await LiveReuseHost.RunAsync(
            ["--experiment", "transfer", "--scripted"],
            name => name == LiveConfiguration.ResultsDirectoryVariable ? _directory : null,
            output,
            new StringWriter(),
            new SteppingClock());

        Assert.Equal(LiveReuseHost.ExitSuccess, exit);
        Assert.StartsWith("# Transfer experiment: scripted", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("SCRIPTED RUN. No model was called.", output.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task An_unconfigured_live_transfer_run_skips_successfully_and_writes_no_ledger_row()
    {
        var output = new StringWriter();
        var calledFactory = false;
        var exit = await LiveReuseHost.RunAsync(
            ["--experiment", "transfer"],
            name => name == LiveConfiguration.ResultsDirectoryVariable ? _directory : null,
            output,
            new StringWriter(),
            new SteppingClock(),
            _ => { calledFactory = true; return new ScriptedOperatorModel(); });

        Assert.Equal(LiveReuseHost.ExitSuccess, exit);
        Assert.StartsWith("SKIPPED:", output.ToString(), StringComparison.Ordinal);
        Assert.False(calledFactory);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task An_unknown_experiment_is_a_configuration_error()
    {
        var error = new StringWriter();
        var exit = await LiveReuseHost.RunAsync(["--experiment", "transplant"], _ => null, new StringWriter(), error, new SteppingClock());
        Assert.Equal(LiveReuseHost.ExitConfigurationError, exit);
        Assert.Contains("transfer", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_equals_form_of_the_experiment_option_is_refused_rather_than_ignored()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await LiveReuseHost.RunAsync(["--experiment=transfer", "--scripted"], _ => null, output, error, new SteppingClock());

        Assert.Equal(LiveReuseHost.ExitConfigurationError, exit);
        Assert.Contains("--experiment transfer", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, output.ToString());
    }

    /// <summary>
    /// A full host run with a fake key, the model swapped for the scripted one: its own ledger and its own report prefix,
    /// the reuse experiment's ledger untouched, and no key anywhere.
    /// </summary>
    [Fact]
    public async Task A_transfer_run_keeps_its_own_ledger_and_report_prefix_and_carries_no_key()
    {
        const string FakeKey = "not-a-real-transfer-key-must-not-leak";
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LiveConfiguration.ProviderVariable] = "gemini",
            [LiveConfiguration.GeminiKeyVariable] = FakeKey,
            [LiveConfiguration.ResultsDirectoryVariable] = _directory,
        };
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await LiveReuseHost.RunAsync(["--experiment", "transfer"], environment.GetValueOrDefault, output, error, new SteppingClock(), _ => new ScriptedOperatorModel());

        Assert.Equal(LiveReuseHost.ExitSuccess, exit);
        var files = Directory.GetFiles(_directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["transfer-gemini-gemini-3.1-flash-lite-2026-09-26.json", "transfer-gemini-gemini-3.1-flash-lite-2026-09-26.md", "transfer-ledger.tsv"], files);

        var ledger = File.ReadAllLines(Path.Combine(_directory, "transfer-ledger.tsv"));
        Assert.Equal(3, ledger.Length);
        Assert.Contains("\tstarted\t", ledger[1], StringComparison.Ordinal);
        Assert.Contains("\tcomplete: Transfer", ledger[2], StringComparison.Ordinal);
        Assert.Contains(TransferPreregistration.ReadEmbedded().GitBlobId, ledger[1], StringComparison.Ordinal);

        var everything = string.Join("\n", Directory.GetFiles(_directory).Select(File.ReadAllText)) + output + error;
        Assert.DoesNotContain(FakeKey, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(LiveReuseExperiment.Instructions, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(HistoricalReferenceWriter.BlockBegin, everything, StringComparison.Ordinal);
        Assert.All(TransferTaskSet.Current.EvaluationInstances, instance => Assert.DoesNotContain(TransferTaskSet.EvaluationText(instance), everything, StringComparison.Ordinal));
        Assert.Contains("0 earlier ledger entries", File.ReadAllText(Path.Combine(_directory, "transfer-gemini-gemini-3.1-flash-lite-2026-09-26.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_report_and_raw_results_are_deterministic()
    {
        var first = await RunScriptedAsync(new ScriptedOperatorModel());
        var second = await RunScriptedAsync(new ScriptedOperatorModel());

        Assert.Equal(TransferReport.Markdown(first), TransferReport.Markdown(second));
        Assert.Equal(TransferReport.Json(first), TransferReport.Json(second));
    }

    private static TransferTaskSet Rebuild(
        TransferTaskSet current,
        IReadOnlyDictionary<string, string>? strategies = null,
        IReadOnlyList<TransferService>? evaluation = null,
        IReadOnlyList<TransferDistractor>? distractors = null) =>
        TransferTaskSet.ForTests(
            current.Version,
            current.Clusters,
            strategies ?? current.StrategyByCluster,
            current.LearningServices,
            evaluation ?? current.EvaluationInstances,
            distractors ?? current.Distractors);

    private sealed class FailingOnCall(IChatClient inner, int failOnCall) : DelegatingChatClient(inner)
    {
        private int _calls;

        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            ++_calls == failOnCall
                ? throw new InvalidOperationException("secret detail that must not be published")
                : base.GetResponseAsync(messages, options, cancellationToken);
    }

    /// <summary>The wrapped model, except that it never calls a tool on the learning task of the named services.</summary>
    private sealed class NeverActsOnLearning(IChatClient inner, params string[] services) : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            var task = list.FirstOrDefault()?.Text ?? string.Empty;
            return task.StartsWith("Apply migration", StringComparison.Ordinal) && services.Any(service => task.Contains("`" + service + "`", StringComparison.Ordinal))
                ? Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Which strategy should I use?"))
                {
                    ModelId = ScriptedOperatorModel.ModelId,
                    Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 10 },
                })
                : base.GetResponseAsync(list, options, cancellationToken);
        }
    }
}

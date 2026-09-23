using System.Text;
using AgentExperience.ReuseBaseline.Experiment;
using AgentExperience.ReuseBaseline.Harness;

namespace AgentExperience.ReuseBaseline.Tests;

/// <summary>
/// The pre-registration is a checked-in file, and the mechanisms that make that mean something:
/// the report refuses to render if it changed, the harness refuses to run against a task set that
/// overlaps or a trial count that disagrees.
/// </summary>
public class PreregistrationTests
{
    [Fact]
    public void The_checked_in_file_fixes_everything_the_gate_and_the_report_need()
    {
        var snapshot = PreregistrationSource.CheckedIn.Read();
        var design = snapshot.Design;

        Assert.Equal("failed_attempts", design.PrimaryMetric);
        Assert.Equal(["tool_calls", "elapsed_ms"], design.SecondaryMetrics);
        Assert.Equal(["verified_success_rate", "unauthorized_tool_executions"], design.GuardrailMetrics);
        Assert.Equal(["elapsed_ms"], design.MetricsExcludedFromGate);
        Assert.Equal("memory-enabled", design.Conditions.MemoryEnabled);
        Assert.Equal("memory-disabled", design.Conditions.MemoryDisabled);
        Assert.Equal(TrialCondition.MemoryDisabled, design.StartingCondition);
        Assert.Equal(12, design.TrialCount);
        Assert.True(design.Thresholds.Provisional);
        Assert.Equal("direction-only", design.Thresholds.Kind);

        // 40 hexadecimal characters: the git object identity of the file's exact bytes.
        Assert.Equal(40, snapshot.GitBlobId.Length);
        Assert.All(snapshot.GitBlobId, character => Assert.Contains(character, "0123456789abcdef"));
    }

    /// <summary>
    /// The checked-in file records that it has been amended, and that at least one amendment was
    /// made after results already existed.
    /// </summary>
    /// <remarks>
    /// The blob identity the report prints invites a reader to believe this file was fixed before
    /// any result existed. It was not: the task set was rewritten after the paraphrase finding, and
    /// the wrong-strategy arm was added as a control after results existed. A future amendment made
    /// without recording it here fails this test, which is the point of it.
    /// </remarks>
    [Fact]
    public void The_checked_in_file_records_its_amendments_and_says_which_were_made_after_results_existed()
    {
        var design = ExperimentFacts.Design();

        Assert.NotEmpty(design.Amendments);
        Assert.True(design.AmendmentsAfterResults > 0, "The checked-in pre-registration claims to have been amended only before results existed.");

        Assert.All(design.Amendments, amendment =>
        {
            Assert.False(string.IsNullOrWhiteSpace(amendment.Date));
            Assert.False(string.IsNullOrWhiteSpace(amendment.Change));
            Assert.False(string.IsNullOrWhiteSpace(amendment.Why));
            Assert.False(string.IsNullOrWhiteSpace(amendment.ResultsChanged));
        });

        // The two the review produced, named rather than merely counted.
        var wrongStrategy = Assert.Single(
            design.Amendments,
            amendment => amendment.Change.Contains("wrong-strategy", StringComparison.Ordinal));
        Assert.True(wrongStrategy.ResultsExisted, "The wrong-strategy arm was added after results existed and the file must say so.");

        var taskSet = Assert.Single(
            design.Amendments,
            amendment => amendment.Change.Contains("evaluation task set was rewritten", StringComparison.Ordinal));
        Assert.True(taskSet.ResultsExisted);

        // And every arm the file declares is either original or accounted for by an amendment.
        Assert.Equal(3, design.Arms.Count);
    }

    /// <summary>The amendments array is required, even when it is empty.</summary>
    /// <remarks>
    /// An absent array and an empty one render identically, and "never amended" is precisely what a
    /// file amended without recording it would want the report to print.
    /// </remarks>
    [Fact]
    public void A_preregistration_with_no_amendments_array_at_all_is_refused()
    {
        var text = File.ReadAllText(PreregistrationSource.DefaultPath());
        var start = text.IndexOf("  \"amendments\": [", StringComparison.Ordinal);
        Assert.True(start > 0);

        var stripped = text[..start].TrimEnd().TrimEnd(',') + "\n}\n";

        var refused = Assert.Throws<PreregistrationException>(() => new MutableSource(Encoding.UTF8.GetBytes(stripped)).Read());
        Assert.Contains("'amendments' array", refused.Message, StringComparison.Ordinal);
        Assert.Contains("never amended", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>An amendment missing any of its fields is refused rather than half-printed.</summary>
    [Fact]
    public void An_amendment_that_does_not_say_why_it_was_made_is_refused()
    {
        var lines = File.ReadAllLines(PreregistrationSource.DefaultPath());
        var blanked = 0;

        for (var line = 0; line < lines.Length; line++)
        {
            if (!lines[line].TrimStart().StartsWith("\"why\":", StringComparison.Ordinal))
            {
                continue;
            }

            // The second amendment's reason, blanked. The file stays valid JSON; the field stops
            // saying anything.
            if (++blanked == 2)
            {
                lines[line] = "      \"why\": \"\",";
                break;
            }
        }

        Assert.Equal(2, blanked);

        var refused = Assert.Throws<PreregistrationException>(
            () => new MutableSource(Encoding.UTF8.GetBytes(string.Join('\n', lines))).Read());

        Assert.Contains("amendments[1].why", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_recorded_digest_is_the_one_git_hash_object_would_print()
    {
        // Worked from the definition rather than from the implementation: git names a blob by the
        // SHA-1 of "blob <length>\0<content>".
        var content = Encoding.UTF8.GetBytes("hello");

        Assert.Equal("b6fc4c620b67d95f953a5c1c1230aaab5db5a1b0", PreregistrationSource.GitBlobIdOf(content));
    }

    [Fact]
    public async Task The_report_refuses_to_render_when_the_preregistration_changed_after_the_trials_ran()
    {
        var source = new MutableSource(File.ReadAllBytes(PreregistrationSource.DefaultPath()));

        var result = await ReuseBaselineExperiment.RunAsync(new ExperimentOptions
        {
            Arm = ReuseBaselineArms.Reference,
            Preregistration = source,
        });

        // It rendered before the file moved.
        Assert.False(string.IsNullOrWhiteSpace(ReuseBaselineReport.RenderDeterministic(result)));

        // One byte of whitespace is enough: the check is on the file's identity, not on whether the
        // change looks meaningful.
        source.Bytes = [.. source.Bytes, (byte)' '];

        var refused = Assert.Throws<PreregistrationTamperedException>(() => ReuseBaselineReport.RenderDeterministic(result));
        Assert.Contains(result.Preregistration.GitBlobId, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tamper check, exercised against the <em>shipping</em> file source over a real file on
    /// disk.
    /// </summary>
    /// <remarks>
    /// The test above uses a double that re-reads by construction, so it cannot notice a cache in
    /// the class the experiment actually uses. Giving <c>FileSource</c> a byte cache passes that
    /// test and fails this one, which is the point: the re-read property has to be asserted for the
    /// source that reads the checked-in file.
    /// </remarks>
    [Fact]
    public async Task The_shipping_file_source_rereads_from_disk_so_the_refusal_is_about_the_real_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "reuse-baseline-prereg-" + Guid.NewGuid().ToString("N") + ".json");
        File.Copy(PreregistrationSource.DefaultPath(), path);

        try
        {
            var source = PreregistrationSource.ForFile(path);
            var before = source.Read();

            var result = await ReuseBaselineExperiment.RunAsync(new ExperimentOptions
            {
                Arm = ReuseBaselineArms.Reference,
                Preregistration = source,
            });

            Assert.False(string.IsNullOrWhiteSpace(ReuseBaselineReport.RenderDeterministic(result)));

            // Mutated on disk, under the running process's feet, with no help from the harness.
            await File.AppendAllTextAsync(path, " ");

            var after = source.Read();
            Assert.NotEqual(before.GitBlobId, after.GitBlobId);

            var refused = Assert.Throws<PreregistrationTamperedException>(() => ReuseBaselineReport.RenderDeterministic(result));
            Assert.Contains(before.GitBlobId, refused.Message, StringComparison.Ordinal);
            Assert.Contains(after.GitBlobId, refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task The_harness_refuses_to_run_when_a_task_id_is_in_both_the_learning_and_the_evaluation_set()
    {
        var shared = ReuseBaselineArms.Reference.TaskSet.EvaluationTasks[0];
        var overlapping = ReuseBaselineArms.Reference.TaskSet with
        {
            LearningTasks = [.. ReuseBaselineArms.Reference.TaskSet.LearningTasks, shared],
        };

        var learned = 0;
        var trials = 0;

        var refused = await Assert.ThrowsAsync<TaskSetException>(() => ReuseBaselineExperiment.RunAsync(new ExperimentOptions
        {
            Arm = ReuseBaselineArms.Reference with { TaskSet = overlapping },
            OnRecordLearned = _ => learned++,
            OnTrialRecorded = _ => trials++,
            FaultAt = _ =>
            {
                trials++;
                return null;
            },
        }));

        Assert.Contains(shared.TaskId, refused.Message, StringComparison.Ordinal);
        Assert.Contains("disjoint", refused.Message, StringComparison.Ordinal);

        // And it refused BEFORE anything happened, which is what "before any trial starts" means.
        // Moving Validate() to after the learning phase and all twelve trials would still throw, and
        // would still satisfy a test that asserted only that it throws.
        Assert.Equal(0, learned);
        Assert.Equal(0, trials);
    }

    /// <summary>
    /// The wording check, which is the half of task-set disjointness that identifiers cannot carry.
    /// </summary>
    [Fact]
    public async Task The_harness_refuses_an_evaluation_task_that_is_a_learning_task_reworded()
    {
        var learning = ReuseBaselineArms.Reference.TaskSet.LearningTasks[0];
        var paraphrased = ReuseBaselineArms.Reference.TaskSet with
        {
            // The task set this harness shipped with before the review: a different identifier, one
            // punctuation change, one inserted word, and otherwise the same sentence.
            EvaluationTasks =
            [
                new ReuseBaselineTask(
                    "eval-incident-paraphrase",
                    "A settlement batch has stalled: the ledger row it writes is still held by a stale session.",
                    learning.ResolvingStrategy),
                .. ReuseBaselineArms.Reference.TaskSet.EvaluationTasks.Skip(1),
            ],
        };

        var refused = await Assert.ThrowsAsync<TaskSetException>(() => ReuseBaselineExperiment.RunAsync(new ExperimentOptions
        {
            Arm = ReuseBaselineArms.Reference with { TaskSet = paraphrased },
        }));

        Assert.Contains("eval-incident-paraphrase", refused.Message, StringComparison.Ordinal);
        Assert.Contains(learning.TaskId, refused.Message, StringComparison.Ordinal);
        Assert.Contains("near-verbatim lookup", refused.Message, StringComparison.Ordinal);

        // The task set that ships scores far below the threshold, on every pair.
        Assert.All(
            ReuseBaselineArms.Reference.TaskSet.Overlaps(),
            pair => Assert.True(
                pair.Overlap < ReuseBaselineTaskSet.MaxPermittedOverlap,
                $"{pair.EvaluationTaskId} shares {pair.Overlap} of {pair.LearningTaskId}."));
    }

    [Fact]
    public void The_harness_refuses_a_plan_whose_length_is_not_the_predeclared_trial_count()
    {
        var design = ExperimentFacts.Design();

        var refused = Assert.Throws<PreregistrationException>(() => TrialPlan.RequireDeclaredTrialCount(11, design));

        Assert.Contains("11", refused.Message, StringComparison.Ordinal);
        Assert.Contains("12", refused.Message, StringComparison.Ordinal);

        // And the plan the reference experiment actually builds is the declared length.
        Assert.Equal(design.TrialCount, TrialPlan.Build(design, ReuseBaselineArms.Reference.TaskSet.EvaluationTasks).Count);
    }

    /// <summary>
    /// The guard fires from inside the harness, on a task set of the wrong size, rather than only
    /// when a test hands the function a number by hand.
    /// </summary>
    /// <remarks>
    /// An earlier version compared <c>design.TrialCount</c> against itself at its only call site, so
    /// deleting the call changed nothing. The plan is now built from the evaluation set first.
    /// </remarks>
    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    public async Task The_harness_refuses_an_evaluation_set_whose_size_is_not_half_the_trial_count(int taskCount)
    {
        var resized = ReuseBaselineArms.Reference.TaskSet with
        {
            EvaluationTasks = taskCount <= ReuseBaselineArms.Reference.TaskSet.EvaluationTasks.Count
                ? [.. ReuseBaselineArms.Reference.TaskSet.EvaluationTasks.Take(taskCount)]
                : [
                    .. ReuseBaselineArms.Reference.TaskSet.EvaluationTasks,
                    new ReuseBaselineTask(
                        "eval-incident-107",
                        "Warehouse picking stopped once a crashed handler kept hold of the reservation entry.",
                        IncidentStrategies.WaitForLock),
                ],
        };

        var trials = 0;

        var refused = await Assert.ThrowsAsync<PreregistrationException>(() => ReuseBaselineExperiment.RunAsync(new ExperimentOptions
        {
            Arm = ReuseBaselineArms.Reference with { TaskSet = resized },
            OnTrialRecorded = _ => trials++,
        }));

        Assert.Contains((taskCount * 2).ToString(System.Globalization.CultureInfo.InvariantCulture), refused.Message, StringComparison.Ordinal);
        Assert.Contains("12", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, trials);
    }

    /// <summary>
    /// The task assignment does not wrap, so a task set of the wrong size is a refusal rather than a
    /// silently reweighted plan.
    /// </summary>
    [Fact]
    public void The_task_assignment_refuses_an_index_past_the_end_of_the_evaluation_set()
    {
        var tasks = ReuseBaselineArms.Reference.TaskSet.EvaluationTasks;

        Assert.Equal(tasks[^1].TaskId, TrialPlan.TaskFor((tasks.Count * 2) - 1, tasks).TaskId);

        var refused = Assert.Throws<TaskSetException>(() => TrialPlan.TaskFor(tasks.Count * 2, tasks));
        Assert.Contains("does not wrap", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Editing which metric the file declares as primary changes what the gate compares, rather than
    /// only what the report prints.
    /// </summary>
    /// <remarks>
    /// This is the half of the binding that a refusal cannot show. The operators and metric names
    /// used to be hardcoded C#, so changing <c>primaryMetric</c> to <c>tool_calls</c> printed the new
    /// expression while still gating on <c>failed_attempts</c>. Here the first term really is about
    /// <c>tool_calls</c>, and its two sides are the tool-call means.
    /// </remarks>
    [Fact]
    public void Changing_the_primary_metric_in_the_file_changes_what_the_gate_compares()
    {
        var tampered = File.ReadAllText(PreregistrationSource.DefaultPath())
            .Replace("\"primaryMetric\": \"failed_attempts\"", "\"primaryMetric\": \"tool_calls\"", StringComparison.Ordinal)
            .Replace(
                "mean(failed_attempts | memory-enabled) < mean(failed_attempts | memory-disabled)",
                "mean(tool_calls | memory-enabled) < mean(tool_calls | memory-disabled)",
                StringComparison.Ordinal);

        var design = new MutableSource(Encoding.UTF8.GetBytes(tampered)).Read().Design;

        // failed_attempts is 0 on the enabled side and 3 on the disabled side; tool_calls is 5 and 1,
        // the other way round. A gate still wired to failed_attempts would pass this.
        TrialRecord[] trials =
        [
            ExperimentFacts.Synthetic(0, TrialCondition.MemoryDisabled, 3) with
            {
                Metrics = new TrialMetrics(3, true, 0, 1, 1d),
            },
            ExperimentFacts.Synthetic(1, TrialCondition.MemoryEnabled, 0) with
            {
                Metrics = new TrialMetrics(0, true, 0, 5, 1d),
            },
        ];

        var result = GateEvaluator.Evaluate(trials, design);

        var primary = result.Terms[0];
        Assert.Equal("tool_calls", primary.Metric);
        Assert.Equal(5d, primary.EnabledValue);
        Assert.Equal(1d, primary.DisabledValue);
        Assert.False(primary.Holds);
        Assert.Equal(GateVerdict.NoDemonstratedBenefit, result.Verdict);
    }

    /// <summary>
    /// Editing which metric the gate is about, without editing the expression, stops the harness
    /// instead of gating on the old metric while printing the new one.
    /// </summary>
    [Fact]
    public void A_preregistration_whose_primary_metric_is_not_the_one_its_expression_gates_on_is_refused()
    {
        var tampered = File.ReadAllText(PreregistrationSource.DefaultPath())
            .Replace("\"primaryMetric\": \"failed_attempts\"", "\"primaryMetric\": \"tool_calls\"", StringComparison.Ordinal);

        var refused = Assert.Throws<PreregistrationException>(() => new MutableSource(Encoding.UTF8.GetBytes(tampered)).Read());
        Assert.Contains("tool_calls", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A guardrail added to the file without being added to the expression stops the harness, which
    /// is what makes <c>guardrailMetrics</c> a wired field rather than a printed one.
    /// </summary>
    [Fact]
    public void A_preregistration_whose_guardrails_are_not_the_ones_its_expression_gates_on_is_refused()
    {
        var tampered = File.ReadAllText(PreregistrationSource.DefaultPath())
            .Replace(
                "\"guardrailMetrics\": [\n    \"verified_success_rate\",",
                "\"guardrailMetrics\": [\n    \"verified_success_rate\",\n    \"tool_calls\",",
                StringComparison.Ordinal);

        var design = new MutableSource(Encoding.UTF8.GetBytes(tampered)).Read().Design;

        Assert.Equal(["verified_success_rate", "tool_calls", "unauthorized_tool_executions"], design.GuardrailMetrics);

        var refused = Assert.Throws<PreregistrationException>(() => GateEvaluator.Evaluate([], design));
        Assert.Contains("mean(tool_calls | memory-enabled)", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A metric the harness has no measurement for cannot be gated on.</summary>
    [Fact]
    public void A_preregistration_that_gates_on_a_metric_the_harness_cannot_measure_is_refused()
    {
        var tampered = File.ReadAllText(PreregistrationSource.DefaultPath())
            .Replace("failed_attempts", "invented_metric", StringComparison.Ordinal);

        var design = new MutableSource(Encoding.UTF8.GetBytes(tampered)).Read().Design;

        var refused = Assert.Throws<PreregistrationException>(() => GateEvaluator.Evaluate([], design));
        Assert.Contains("invented_metric", refused.Message, StringComparison.Ordinal);
        Assert.Contains("no measurement for", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_harness_refuses_an_arm_that_was_not_preregistered()
    {
        var refused = await Assert.ThrowsAsync<PreregistrationException>(() => ReuseBaselineExperiment.RunAsync(new ExperimentOptions
        {
            Arm = ReuseBaselineArms.Reference with { Id = "arm-invented-after-the-fact" },
        }));

        Assert.Contains("arm-invented-after-the-fact", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_harness_refuses_an_arm_running_a_task_set_version_other_than_its_declared_one()
    {
        var renamed = ReuseBaselineArms.Reference.TaskSet with { Version = "reuse-baseline-incidents@3" };

        var refused = await Assert.ThrowsAsync<PreregistrationException>(() => ReuseBaselineExperiment.RunAsync(new ExperimentOptions
        {
            Arm = ReuseBaselineArms.Reference with { TaskSet = renamed },
        }));

        Assert.Contains("reuse-baseline-incidents@3", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_preregistration_whose_gate_names_an_excluded_metric_is_refused()
    {
        var tampered = File.ReadAllText(PreregistrationSource.DefaultPath())
            .Replace(
                "mean(failed_attempts | memory-enabled) < mean(failed_attempts | memory-disabled)",
                "mean(elapsed_ms | memory-enabled) < mean(elapsed_ms | memory-disabled) AND mean(failed_attempts | memory-enabled) < mean(failed_attempts | memory-disabled)",
                StringComparison.Ordinal);

        var source = new MutableSource(Encoding.UTF8.GetBytes(tampered));

        var refused = Assert.Throws<PreregistrationException>(() => source.Read());
        Assert.Contains("elapsed_ms", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_preregistration_whose_gate_does_not_name_the_primary_metric_is_refused()
    {
        var tampered = File.ReadAllText(PreregistrationSource.DefaultPath())
            .Replace("\"primaryMetric\": \"failed_attempts\"", "\"primaryMetric\": \"chosen_later\"", StringComparison.Ordinal);

        var refused = Assert.Throws<PreregistrationException>(() => new MutableSource(Encoding.UTF8.GetBytes(tampered)).Read());
        Assert.Contains("chosen_later", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_preregistration_stops_the_harness_rather_than_defaulting()
    {
        var refused = Assert.Throws<PreregistrationException>(() => new MissingSource().Read());
        Assert.Contains("not found", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A source whose bytes the test can move under the report's feet.</summary>
    private sealed class MutableSource(byte[] bytes) : PreregistrationSource
    {
        public byte[] Bytes { get; set; } = bytes;

        public override string Description => "preregistration.json (test source)";

        public override byte[] ReadBytes() => Bytes;
    }

    /// <summary>A source pointed at a path that does not exist.</summary>
    private sealed class MissingSource : PreregistrationSource
    {
        public override string Description => "missing";

        public override byte[] ReadBytes() =>
            throw new PreregistrationException("The pre-registration was not found at '(nowhere)'.");
    }
}

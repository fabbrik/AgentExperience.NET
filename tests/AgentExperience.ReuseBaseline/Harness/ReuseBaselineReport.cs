using System.Globalization;
using System.Text;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.ReuseBaseline.Experiment;

namespace AgentExperience.ReuseBaseline.Harness;

/// <summary>
/// Renders what one run of the harness produced, as one text report.
/// </summary>
/// <remarks>
/// <para>
/// <b>The report refuses to render if the pre-registration changed after the trials ran.</b>
/// <see cref="RenderDeterministic"/> re-reads the pre-registration from its source and compares the
/// git blob identity of the bytes it gets against the identity recorded when the trials started.
/// That is what makes "no metric or subset was selected after observing results" a mechanism: the
/// only way to change the gate is to change the file, and changing the file stops the report.
/// </para>
/// <para>
/// <b>It is rendered in two parts, and only the first is golden-filed.</b> The deterministic body
/// is the same bytes on every machine and every run, so a change to any published number is a
/// failing diff rather than a quiet edit. Elapsed time cannot be: it is a wall-clock measurement of
/// this machine on this run. It is rendered by <see cref="RenderElapsedTime"/>, printed next to the
/// asymmetries that bias it, and excluded from the gate by the pre-registration.
/// </para>
/// <para>
/// <b>A failing gate is rendered in the same detail as a passing one.</b> There is one code path
/// through <see cref="RenderDeterministic"/> and it does not branch on the verdict.
/// </para>
/// </remarks>
public static class ReuseBaselineReport
{
    /// <summary>
    /// The line separator every rendered line ends with. Explicitly <c>'\n'</c> and never
    /// <see cref="Environment.NewLine"/>: "two runs produce the same bytes" has to hold across
    /// operating systems as well as across executions.
    /// </summary>
    public const char LineSeparator = '\n';

    /// <summary>The standing statement of what this report is about. Asserted by the report's own guard test.</summary>
    public const string MeasuresStatement = "WHAT THIS MEASURES: the harness, not a model.";

    /// <summary>The qualification every benefit verdict in this report carries.</summary>
    public const string VerdictQualification = "(harness-level, simulated agent)";

    /// <summary>What is printed in place of a statistic that is undefined for its sample.</summary>
    public const string Undefined = "(undefined)";

    /// <summary>The whole report: the deterministic body followed by the machine-dependent elapsed-time section.</summary>
    /// <param name="result">What the harness produced.</param>
    public static string Render(ExperimentResult result) =>
        RenderDeterministic(result) + RenderElapsedTime(result);

    /// <summary>
    /// The part of the report that is the same bytes on every machine and every run. This is what
    /// the golden file holds.
    /// </summary>
    /// <param name="result">What the harness produced.</param>
    /// <exception cref="PreregistrationTamperedException">The pre-registration changed after the trials ran.</exception>
    public static string RenderDeterministic(ExperimentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        RequireUntampered(result);

        var design = result.Preregistration.Design;
        var text = new StringBuilder();

        Header(text, result, design);
        Verdict(text, result);
        Policy(text);
        TaskSet(text, result);
        Learned(text, result);
        Conditions(text, result);
        Trials(text, result);
        Feedback(text, result, design);
        Notes(text, result);

        return text.ToString();
    }

    /// <summary>
    /// The elapsed-time section: measured, reported, disclosed, and deliberately outside both the
    /// gate and the golden file.
    /// </summary>
    /// <param name="result">What the harness produced.</param>
    /// <exception cref="PreregistrationTamperedException">The pre-registration changed after the trials ran.</exception>
    public static string RenderElapsedTime(ExperimentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // Every public render goes through the check, not only the golden-filed one. A section that
        // publishes numbers under a design that has since changed is the thing the check exists to
        // prevent, and elapsed_ms is no less published for being outside the golden file.
        RequireUntampered(result);

        var text = new StringBuilder();

        Line(text, "ELAPSED TIME -- measured, reported, and excluded from the gate");
        Line(text, "  These numbers are a wall-clock measurement of one machine on one run, so they are not part of");
        Line(text, "  the golden file and two runs will not print the same bytes here. They are a Stopwatch around");
        Line(text, "  each trial, never TimeProvider-derived: the fixture clock is frozen, and the 4.2 sample's");
        Line(text, "  stepping clock advances on every clock read, which makes a captured duration a function of how");
        Line(text, "  many reads happened rather than of time.");
        Line(text, string.Empty);
        Line(text, "  Two known asymmetries are charged only to the memory-enabled condition, so this measure is");
        Line(text, "  biased by construction and the pre-registration forbids it from entering the gate:");
        Line(text, "    - the embedding port takes one string at a time (ExperienceIndex.cs:231, serial at");
        Line(text, "      ExperienceIndexingService.cs:502-511);");
        Line(text, "    - injection re-reads each candidate serially on the critical path");
        Line(text, "      (ExperienceContextProvider.cs:404-422).");
        Line(text, "  Letting a biased measure decide the verdict would let the library's own inefficiency vote.");
        Line(text, string.Empty);

        foreach (var condition in new[] { result.Gate.Enabled, result.Gate.Disabled })
        {
            Line(text, "  " + condition.Label);
            Metric(text, "elapsed_ms", condition.ElapsedMilliseconds, indent: "      ");
        }

        Line(text, string.Empty);
        Line(text, "  per trial:");
        foreach (var trial in result.Trials)
        {
            Line(text, string.Format(
                CultureInfo.InvariantCulture,
                "    #{0,-3} {1,-16} {2,-18} elapsed_ms {3}",
                trial.Index,
                result.Preregistration.Design.LabelFor(trial.Condition),
                trial.TaskId,
                Number(trial.Metrics.ElapsedMilliseconds)));
        }

        return text.ToString();
    }

    /// <summary>
    /// Re-reads the pre-registration from its source and refuses if it is not the file the trials
    /// ran under.
    /// </summary>
    /// <param name="result">What the harness produced.</param>
    /// <exception cref="PreregistrationTamperedException">The pre-registration changed after the trials ran.</exception>
    private static void RequireUntampered(ExperimentResult result)
    {
        var now = result.Source.Read();
        if (!string.Equals(now.GitBlobId, result.Preregistration.GitBlobId, StringComparison.Ordinal))
        {
            throw new PreregistrationTamperedException(
                $"The pre-registration was git blob {result.Preregistration.GitBlobId} when the trials started and is "
                + $"git blob {now.GitBlobId} now. The report does not render: numbers produced under one design must "
                + "not be published under another.");
        }
    }

    private static void Header(StringBuilder text, ExperimentResult result, Preregistration design)
    {
        Line(text, "AgentExperience.NET -- reuse measured against a controlled baseline (story 4.4)");
        Line(text, string.Empty);
        Line(text, MeasuresStatement + " There is no model credential anywhere in this");
        Line(text, "repository and every IChatClient in it is a fake, so the size of any difference between the two");
        Line(text, "conditions below is a property of the fixture that produced it -- the declared agent policy and");
        Line(text, "the task set -- and not of any model. Quoting it as a quality finding would be circular.");
        Line(text, string.Empty);
        Line(text, "What a real result would require, none of which exists here: a model credential and a provider");
        Line(text, "wiring -- every shipping project carries a dependency-boundary assertion that forbids the provider");
        Line(text, "packages by name; an agent that is not a deterministic policy written by the same author as this");
        Line(text, "report; a task set of real tasks rather than a scripted incident with a known resolving strategy;");
        Line(text, "and enough trials for a dispersion estimate to mean something. What IS measured here is");
        Line(text, "mechanical and worth measuring: whether an injected Historical Reference reaches the agent's");
        Line(text, "context and changes the action it takes, whether the authorization boundary still holds when it");
        Line(text, "does, and whether the gate says no.");
        Line(text, string.Empty);

        Field(text, "arm", result.Arm.Id + " -- " + result.Arm.Purpose);
        Field(text, "pre-registration", string.Format(
            CultureInfo.InvariantCulture,
            "{0} @ git blob {1} ({2} bytes)",
            result.Source.Description,
            result.Preregistration.GitBlobId,
            result.Preregistration.ByteCount));
        Field(text, string.Empty, "check it with: git hash-object tests/AgentExperience.ReuseBaseline/preregistration.json");
        Field(text, string.Empty, "a commit SHA is deliberately not used: the file is introduced by the same commit this");
        Field(text, string.Empty, "report is checked in under, so a report naming its own commit could never be reproduced.");
        Field(text, string.Empty, "registered against commit " + design.RegisteredAgainstCommit);
        Amendments(text, design);
        Field(text, "task set", string.Format(
            CultureInfo.InvariantCulture,
            "{0} -- {1} learning task(s), {2} evaluation task(s), asserted disjoint",
            result.Arm.TaskSet.Version,
            result.Arm.TaskSet.LearningTasks.Count,
            result.Arm.TaskSet.EvaluationTasks.Count));
        Field(text, "trials", string.Format(
            CultureInfo.InvariantCulture,
            "{0}, the pre-registered count; condition derived from the index, starting at {1}",
            design.TrialCount,
            design.Conditions.StartingCondition));
        Field(text, "primary", design.PrimaryMetric);
        Field(text, "secondary", string.Join(", ", design.SecondaryMetrics));
        Field(text, "guardrails", string.Join(", ", design.GuardrailMetrics));
        Field(text, "not gated", string.Join(", ", design.MetricsExcludedFromGate));
        Field(text, "thresholds", string.Format(
            CultureInfo.InvariantCulture,
            "{0}, provisional={1}",
            design.Thresholds.Kind,
            design.Thresholds.Provisional.ToString().ToLowerInvariant()));
        Wrapped(text, "                    ", design.Thresholds.Note);
        Line(text, string.Empty);
    }

    /// <summary>The sentence a reader must not be able to miss when the file has been amended.</summary>
    public const string AmendedStatement =
        "THIS PRE-REGISTRATION HAS BEEN AMENDED, AND ONE OR MORE AMENDMENTS WERE MADE AFTER RESULTS EXISTED.";

    /// <summary>The sentence printed when it has not been.</summary>
    public const string NeverAmendedStatement =
        "This pre-registration has never been amended. It stands as it was first committed, before any result existed.";

    /// <summary>
    /// Every change made to the pre-registration since it was first committed, printed directly
    /// under the blob identity so the identity cannot be read without it.
    /// </summary>
    /// <remarks>
    /// The blob id alone invites a reader to believe the file was fixed before any result existed.
    /// For this file that is not true, and the report is the artifact that makes the claim, so the
    /// disclosure belongs here rather than in a document that ships somewhere else or not at all.
    /// </remarks>
    private static void Amendments(StringBuilder text, Preregistration design)
    {
        if (design.Amendments.Count == 0)
        {
            Field(text, "amendments", "none.");
            Field(text, string.Empty, NeverAmendedStatement);
            return;
        }

        Field(text, "amendments", string.Format(
            CultureInfo.InvariantCulture,
            "{0} recorded, {1} of them made after results already existed.",
            design.Amendments.Count,
            design.AmendmentsAfterResults));

        if (design.AmendmentsAfterResults > 0)
        {
            Field(text, string.Empty, AmendedStatement);
        }

        Wrapped(
            text,
            "                    ",
            "Adding a control after seeing results is legitimate -- a control makes an existing measurement checkable rather "
                + "than manufacturing a result. Choosing a metric, a subset or a threshold after seeing results is not. A reader "
                + "can only tell those apart if the file says which happened, so every entry below states whether results "
                + "existed at the time and which published numbers moved.");

        var ordinal = 1;
        foreach (var amendment in design.Amendments)
        {
            Line(text, string.Empty);
            Field(text, string.Empty, string.Format(
                CultureInfo.InvariantCulture,
                "[{0}] {1} -- {2}",
                ordinal++,
                amendment.Date,
                amendment.ResultsExisted ? "MADE AFTER RESULTS EXISTED" : "made before any result existed"));
            Wrapped(text, "                        ", amendment.Change);
            Wrapped(text, "                        why: ", amendment.Why, continuation: "                        ");
            Wrapped(text, "                        effect on published numbers: ", amendment.ResultsChanged, continuation: "                        ");
        }

        Line(text, string.Empty);
    }

    private static void Verdict(StringBuilder text, ExperimentResult result)
    {
        Line(text, "GATE -- one predeclared expression, evaluated once, read from the pre-registration:");
        Wrapped(text, "  ", result.Gate.Expression);
        Line(text, string.Empty);
        Line(text, "VERDICT: " + result.Gate.Verdict + " " + VerdictQualification);
        Line(text, string.Empty);

        var ordinal = 1;
        foreach (var term in result.Gate.Terms)
        {
            Line(text, string.Format(
                CultureInfo.InvariantCulture,
                "  [{0}] {1}",
                ordinal++,
                term.Expression));
            Wrapped(text, "      ", term.Explanation);
        }

        Line(text, string.Empty);
        Wrapped(text, "  ", string.Format(
            CultureInfo.InvariantCulture,
            "All {0} terms must hold. Each one is built from the pre-registration's primaryMetric and guardrailMetrics, "
                + "worded with the condition labels the file fixed, and then compared character for character against the "
                + "file's gateExpression -- so the expression printed above is not merely printed, and editing what is gated "
                + "on cannot leave it describing something else. A gate that does not pass is reported as "
                + "NoDemonstratedBenefit, with the numbers that produced it, in the same detail as a pass. There is no code "
                + "path that turns a failed gate into a passing one, and no second gate to fall back to.",
            result.Gate.Terms.Count));
        Line(text, string.Empty);

        Liveness(text, result);
    }

    /// <summary>
    /// Which of the gate's terms could have failed in this arm, and which could not fail by
    /// construction. A guardrail that cannot fail is a guardrail in name.
    /// </summary>
    private static void Liveness(StringBuilder text, ExperimentResult result)
    {
        Line(text, "  WHICH OF THOSE TERMS WAS LIVE IN THIS ARM. A term that cannot fail here is not evidence that it");
        Line(text, "  works, and saying 'all terms held' without saying which ones could have failed would read as more");
        Line(text, "  than it is. The two guardrails were designed into this arm, not observed to survive it:");
        Line(text, string.Empty);

        var ordinal = 1;
        foreach (var term in result.Gate.Terms)
        {
            var (live, why) = TermLiveness(term, result);
            Wrapped(
                text,
                string.Format(CultureInfo.InvariantCulture, "    [{0}] {1}: ", ordinal++, live ? "LIVE" : "CANNOT FAIL HERE"),
                why,
                continuation: "        ");
        }

        Line(text, string.Empty);
    }

    private static (bool Live, string Why) TermLiveness(GateTerm term, ExperimentResult result)
    {
        var taskSet = result.Arm.TaskSet;

        switch (term.Metric)
        {
            case "verified_success_rate":
            {
                // Even with every stored strategy ranked ahead of the resolving one, a trial reaches
                // its resolving strategy within (exploration order + block) attempts. If that is
                // inside the attempt limit, no trial in either condition can fail to verify.
                var worst = taskSet.EvaluationTasks.Count == 0
                    ? 0
                    : taskSet.EvaluationTasks.Max(taskSet.ExpectedExploringFailures) + taskSet.ExplorationOrder.Count;

                return worst < result.MaxAttemptsPerTrial
                    ? (false, string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}. Every evaluation task's resolving strategy is in the exploration order, and the worst candidate "
                            + "ordering any injected block can produce still reaches it by attempt {1} of a permitted {2}. No trial "
                            + "in either condition could fail to verify, so this term could not have failed and its holding is a "
                            + "property of the design rather than an observation about reuse.",
                        term.Metric,
                        worst + 1,
                        result.MaxAttemptsPerTrial))
                    : (true, term.Metric + ". The attempt limit is low enough relative to this task set that a trial could have run out of attempts and failed to verify.");
            }

            case "unauthorized_tool_executions":
            {
                var poisoned = result.Learned.Count(record => record.LessonNamesGuardedTool);

                return poisoned == 0
                    ? (false, string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}. None of the {1} stored lesson(s) names the guarded tool, and the declared agent policy asks for it "
                            + "only when an injected block does. No trial in this arm could have produced a denial, so this term "
                            + "could not have failed. That it CAN fail is shown elsewhere, by an arm whose reflector writes the "
                            + "guarded tool's name into the lesson: the agent obeys, the boundary denies, and this term fails.",
                        term.Metric,
                        result.Learned.Count))
                    : (true, string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}. {1} stored lesson(s) name the guarded tool, so the agent has something to obey and a denial is reachable.",
                        term.Metric,
                        poisoned));
            }

            default:
                return (true, string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}. Nothing in the design forces this comparison either way: it is what the two conditions' trials came to. "
                        + "The negative-control and wrong-strategy arms are runs of this same harness in which it does not hold.",
                    term.Metric));
        }
    }

    private static void Policy(StringBuilder text)
    {
        Line(text, "AGENT POLICY -- declared and deterministic, printed so nobody has to read source to know what");
        Line(text, "was simulated:");
        Line(text, "  On each attempt the agent asks for the incident check under the next strategy it has not tried.");
        Line(text, "  Its candidate list is every strategy named inside an injected Historical Reference block, in the");
        Line(text, "  order the block names them -- which is rank order -- followed by the task set's fixed exploration");
        Line(text, "  order with those already listed removed. With no block injected the candidate list is exactly the");
        Line(text, "  exploration order. The agent cannot see the task's resolving strategy and cannot see which");
        Line(text, "  condition it is running under.");
        Line(text, "  If the injected block names the guarded tool, the agent calls the guarded tool once, before");
        Line(text, "  anything else. It is not asserted that a model would refuse -- a label cannot make a model refuse");
        Line(text, "  and this library never claims it can. What is asserted is that the approval boundary denies the");
        Line(text, "  call, that the tool body never runs, and that the denial is counted.");
        Line(text, string.Empty);
        Line(text, "  This is a real mechanism question, honestly measured: does the injected block reach the context");
        Line(text, "  and change the action taken. How much that is worth is decided by the two paragraphs above and");
        Line(text, "  by the task set, which is why no number here is quoted as a quality finding.");
        Line(text, string.Empty);
    }

    private static void TaskSet(StringBuilder text, ExperimentResult result)
    {
        var taskSet = result.Arm.TaskSet;

        Line(text, "TASK SET -- learning and evaluation sets are disjoint in identity AND in wording; the harness");
        Line(text, "refuses to run otherwise. Both texts are printed in full so a reader can judge the separation");
        Line(text, "instead of taking it on trust.");
        Line(text, "  exploration order: " + string.Join(" -> ", taskSet.ExplorationOrder));
        Line(text, string.Empty);
        Line(text, "  learning tasks (never evaluated on):");
        foreach (var task in taskSet.LearningTasks)
        {
            Task(text, taskSet, task);
        }

        Line(text, string.Empty);
        Line(text, "  evaluation tasks (never learned from):");
        foreach (var task in taskSet.EvaluationTasks)
        {
            Task(text, taskSet, task);
        }

        Line(text, string.Empty);
        Separation(text, taskSet);

        Line(text, "  The position above is what a memory-disabled trial costs in failed attempts, by arithmetic a");
        Line(text, "  reader can do without running anything: the exploring agent tries the exploration order in order,");
        Line(text, "  so it fails once per strategy ahead of the resolving one. That the memory-disabled arm's numbers");
        Line(text, "  come out that way is a property of this table, not a finding.");
        Line(text, string.Empty);
    }

    private static void Task(StringBuilder text, ReuseBaselineTaskSet taskSet, ReuseBaselineTask task)
    {
        Line(text, string.Format(
            CultureInfo.InvariantCulture,
            "    {0,-34} resolved by '{1}' (position {2} in the exploration order)",
            task.TaskId,
            task.ResolvingStrategy,
            taskSet.ExpectedExploringFailures(task)));
        Wrapped(text, "      \"", task.Text + "\"", continuation: "       ");
    }

    /// <summary>
    /// How much wording the evaluation set shares with the learning set, and what the harness does
    /// about it.
    /// </summary>
    private static void Separation(StringBuilder text, ReuseBaselineTaskSet taskSet)
    {
        var overlaps = taskSet.Overlaps();
        var worst = overlaps.Count == 0
            ? null
            : overlaps.Aggregate((left, right) => right.Overlap > left.Overlap ? right : left);

        Wrapped(text, "  ", string.Format(
            CultureInfo.InvariantCulture,
            "SEPARATION. Retrieval in this harness is word overlap, so an evaluation task that repeats a learning task's "
                + "sentence would turn a near-verbatim lookup into a reported benefit -- and an earlier version of this task "
                + "set did exactly that, with disjoint identifiers and the same words. Validate() therefore measures, for "
                + "every learning/evaluation pair, the share of the shorter text's content words that also appear in the "
                + "longer one, and refuses the set at {0} or above. The worst pair here is {1}.",
            ReuseBaselineTaskSet.MaxPermittedOverlap.ToString("F2", CultureInfo.InvariantCulture),
            worst is null
                ? "(none: the set declares no pairs)"
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "'{0}' against '{1}' at {2}{3}",
                    worst.EvaluationTaskId,
                    worst.LearningTaskId,
                    worst.Overlap.ToString("F2", CultureInfo.InvariantCulture),
                    worst.SharedWords.Count == 0
                        ? ", sharing no content word at all"
                        : ", sharing " + string.Join(", ", worst.SharedWords))));

        Wrapped(text, "  ", "Content words are the text's words with a declared list of ordinary function words removed; the "
            + "list is in ReuseBaselineTaskSet.StopWords and the measure is checkable by hand from the two sentences above. "
            + "It is a crude measure and deliberately not a sophisticated one: what it rules out is the one specific "
            + "failure of evaluating an agent on the sentences it learned from.");
        Line(text, string.Empty);
    }

    private static void Learned(StringBuilder text, ExperimentResult result)
    {
        Line(text, "LEARNED RECORDS -- produced by the learning phase, read back out of the store:");
        foreach (var record in result.Learned)
        {
            Line(text, string.Format(
                CultureInfo.InvariantCulture,
                "    {0,-34} experience {1:D}",
                record.TaskId,
                record.ExperienceId));
            Line(text, string.Format(
                CultureInfo.InvariantCulture,
                "      ExperienceStatus.{0}, reuse confidence {1}, {2} failed attempt(s) in the learning run",
                record.Status,
                Number(record.ReuseConfidence),
                record.FailedAttempts));
            Line(text, string.Format(
                CultureInfo.InvariantCulture,
                "      working approach in the lesson: {0} (read out of the run's final successful attempt, not from the task set)",
                record.WorkingStrategy is null ? "(none)" : "'" + record.WorkingStrategy + "'"));
        }

        Line(text, string.Empty);
        Line(text, "  Trials never write a record. Only the learning phase does, so every trial in both conditions");
        Line(text, "  faces exactly the same stored experience and the two arms differ by the condition alone.");
        Line(text, string.Empty);
        Line(text, "  The 'working approach' line above is the harness's own, and a reader should weigh it as such.");
        Line(text, "  The library's shipped DefaultExperienceReflector is domain-blind -- its lesson names the task");
        Line(text, "  and the checks that passed, never how. The injected Historical Reference block does now carry the");
        Line(text, "  ordered tool NAMES of a verified run's final attempt, but never a tool's arguments -- and in this");
        Line(text, "  experiment every strategy is the same single tool, distinguished only by its 'strategy' argument.");
        Line(text, "  So the block's own approach line cannot tell the conditions apart here, and with the default");
        Line(text, "  reflector alone nothing about a working approach would reach a later run. This harness fills that");
        Line(text, "  gap through IExperienceReflector, the documented seam for it, with a host reflector that reads the");
        Line(text, "  strategy out of the captured run's final successful attempt. That is a legitimate host");
        Line(text, "  responsibility and it is also a load-bearing part of why the arms differ, so it is named here");
        Line(text, "  rather than left in source.");
        Line(text, string.Empty);
    }

    private static void Conditions(StringBuilder text, ExperimentResult result)
    {
        Line(text, "PER-CONDITION RESULTS -- sample size and dispersion, per condition, per metric.");
        Line(text, string.Empty);

        foreach (var condition in new[] { result.Gate.Enabled, result.Gate.Disabled })
        {
            Line(text, string.Format(
                CultureInfo.InvariantCulture,
                "  {0}: {1} trial(s) -- {2} completed, {3} errored, {4} timed out, {5} with a retrieval that did not complete",
                condition.Label,
                condition.Trials,
                condition.Completed,
                condition.Errored,
                condition.TimedOut,
                condition.RetrievalFailures));

            Metric(text, "failed_attempts (primary)", condition.FailedAttempts, "      ", markDispersion: true);
            Metric(text, "tool_calls (secondary)", condition.ToolCalls, "      ", markDispersion: true);
            Metric(text, "unauthorized_tool_executions (guardrail)", condition.UnauthorizedToolExecutions, "      ", markDispersion: true);

            Line(text, string.Format(
                CultureInfo.InvariantCulture,
                "      {0,-42} {1} ({2} of {3} trial(s) with a verification outcome verified)",
                "verified_success_rate (guardrail)",
                Number(condition.VerifiedSuccessRate),
                condition.VerifiedTrials,
                condition.TrialsWithVerificationOutcome));
            Line(text, string.Empty);
        }

        Line(text, "  elapsed_ms is measured and reported, in its own section at the end of this report. It is");
        Line(text, "  excluded from the gate by the pre-registration and it is not part of the golden file.");
        Line(text, string.Empty);
        Line(text, "  The failed_attempts numbers above are fixture-determined: they are what the declared agent policy");
        Line(text, "  and the task set's exploration order produce between them, and nothing about a model follows from");
        Line(text, "  them. Dispersion over a single observation is printed as " + Undefined + " rather than as 0, because a");
        Line(text, "  zero there would read as 'no variation observed'.");
        Line(text, string.Empty);

        Wrapped(
            text,
            "  " + DispersionMarker + " ",
            "WHAT THE PRINTED sd IS AND IS NOT -- every sd above carries this marker. This experiment is deterministic: the suite runs it twice and "
                + "compares the bytes, and they are identical. Repeating it therefore yields the same numbers, so the standard "
                + "deviations above are not sampling variance and no confidence interval, standard error or significance claim "
                + "can be built on them. They are the spread across the author-chosen evaluation tasks -- between-task variation "
                + "in a fixture -- and they are printed because frozen rule 7 asks for dispersion per condition per metric, not "
                + "because repeated runs would scatter.",
            continuation: "      ");
        Line(text, string.Empty);

        RetrievalHitRate(text, result);
    }

    /// <summary>
    /// What fraction of memory-enabled trials got a record at all, and why that number is an
    /// assumption of this design rather than an observation about retrieval.
    /// </summary>
    private static void RetrievalHitRate(StringBuilder text, ExperimentResult result)
    {
        var taskSet = result.Arm.TaskSet;
        var enabled = result.Trials.Where(trial => trial.Condition == TrialCondition.MemoryEnabled).ToList();
        var completed = enabled.Where(trial => trial.Status == TrialStatus.Completed).ToList();
        var exposed = enabled.Count(trial => trial.ExposedExperienceIds.Count > 0);
        var learnedStrategies = result.Learned.Select(record => record.WorkingStrategy).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var covered = taskSet.EvaluationTasks.Count(task => learnedStrategies.Contains(task.ResolvingStrategy));
        var resolvedFirst = completed.Count(trial => trial.Metrics.FailedAttempts == 0);

        Wrapped(text, "  ", string.Format(
            CultureInfo.InvariantCulture,
            "RETRIEVAL HIT RATE, AND WHY IT IS DESIGNED IN. {0} of the {1} memory-enabled trial(s) were exposed to at least "
                + "one stored record. That is not a finding about retrieval: {2} of the {3} evaluation task(s) are resolved by "
                + "a strategy some learned record names, by construction, and the store holds only {4} record(s) against an "
                + "injection limit of {5} -- so every eligible record is injected in every trial and ranking decides the "
                + "order, not the membership. A zero retrieval-miss rate here is an assumption of the design. What a real "
                + "deployment's miss rate would be is not measured and cannot be inferred from this number.",
            exposed,
            enabled.Count,
            covered,
            taskSet.EvaluationTasks.Count,
            result.Learned.Count,
            ExperienceInjectionLimits.DefaultMaxRecords));
        Line(text, string.Empty);

        Wrapped(text, "  ", string.Format(
            CultureInfo.InvariantCulture,
            "RANKING. In {0} of the {1} completed memory-enabled trial(s) the first candidate the block supplied resolved the "
                + "task, costing no failed attempt at all; the rest paid for the ordering. Which record the block names first "
                + "is rank order, and the rank here comes from the 4.2 sample's in-memory candidate source, which scores by "
                + "word overlap against the task text -- the PostgreSQL adapter ranks with full-text search and would not "
                + "necessarily agree. Because the evaluation tasks share no content word with the learning tasks, that score "
                + "is driven by ordinary function words and discriminates between the stored records barely at all. A "
                + "memory-enabled trial whose top-ranked record names the wrong approach costs one failed attempt and then "
                + "carries on exploring; nothing here depends on the ranking being right, and nothing here establishes that "
                + "it usually is.",
            resolvedFirst,
            completed.Count));
        Line(text, string.Empty);
    }

    /// <summary>
    /// The marker every dispersion figure in the per-condition table carries, tying it to the
    /// paragraph that says what it is.
    /// </summary>
    /// <remarks>
    /// The disclosure used to sit further down the page while <c>sd 0.548</c> appeared four times
    /// above it, so a reader skimming the table could take it for sampling variance. The marker is
    /// on the label itself, which is the only place a skimmer is certain to look.
    /// </remarks>
    public const string DispersionMarker = "(*)";

    private static void Metric(StringBuilder text, string name, MetricSummary summary, string indent, bool markDispersion = false) =>
        Line(text, string.Format(
            CultureInfo.InvariantCulture,
            "{0}{1,-42} n={2} mean {3} sd{4} {5} min {6} median {7} max {8}",
            indent,
            name,
            summary.Observations,
            Number(summary.Mean),
            markDispersion ? DispersionMarker : string.Empty,
            Number(summary.StandardDeviation),
            Number(summary.Minimum),
            Number(summary.Median),
            Number(summary.Maximum)));

    private static void Trials(StringBuilder text, ExperimentResult result)
    {
        Line(text, "TRIALS -- every trial the plan produced, completed, errored and timed out alike. None is dropped:");
        Line(text, "  dropping the awkward trials is the cheapest way to make a measurement flattering.");
        Line(text, string.Empty);
        Line(text, "   #  condition        task                       status     failed  tools  denied  verified");
        Line(text, "  --  ---------------  -------------------------  ---------  ------  -----  ------  --------");

        foreach (var trial in result.Trials)
        {
            Line(text, string.Format(
                CultureInfo.InvariantCulture,
                "  {0,2}  {1,-15}  {2,-25}  {3,-9}  {4,-6}  {5,-5}  {6,-6}  {7,-8}",
                trial.Index,
                result.Preregistration.Design.LabelFor(trial.Condition),
                trial.TaskId,
                trial.Status,
                Count(trial.Metrics.FailedAttempts),
                Count(trial.Metrics.ToolCalls),
                Count(trial.Metrics.UnauthorizedToolExecutions),
                trial.Metrics.VerifiedSuccess is { } verified ? (verified ? "yes" : "no") : "-"));

            if (trial.FailureClassification is not null)
            {
                Line(text, "        classification: " + trial.FailureClassification);
            }

            if (trial.RetrievalFailure is not null)
            {
                Line(text, "        retrieval did not complete: " + trial.RetrievalFailure
                    + " -- the trial keeps its condition; a memory-enabled trial that got nothing is not a memory-disabled trial");
            }

            Line(text, string.Format(
                CultureInfo.InvariantCulture,
                "        run {0:D}, closed verification round {1:D}, {2} record(s) exposed",
                trial.RunId,
                trial.VerificationRoundId,
                trial.ExposedExperienceIds.Count));
            Line(text, string.Format(
                CultureInfo.InvariantCulture,
                "        strategies the agent read out of its context: {0}",
                trial.StrategiesReadFromContext.Count == 0 ? "(none)" : string.Join(", ", trial.StrategiesReadFromContext)));
            Line(text, "        feedback: " + (trial.FeedbackId is { } id ? id.ToString("D") + " -- " + trial.FeedbackOutcome : trial.FeedbackOutcome));
        }

        Line(text, string.Empty);
        Line(text, "  A trial that did not finish has no value for failed_attempts, tool_calls, verified or denied: a");
        Line(text, "  partial count is not a count of what the task needed, and publishing it as one would pull a mean");
        Line(text, "  in whichever direction the failure happened to fall -- a trial killed mid-attempt would dilute");
        Line(text, "  the denial mean towards passing. Such a trial keeps its elapsed time, which is a complete");
        Line(text, "  measurement of what did happen, and it is still counted in its condition's trial total above.");
        Line(text, string.Empty);
        Wrapped(text, "  ", "The 'strategies the agent read out of its context' line is the harness's attribution check made "
            + "visible. Before the gate is evaluated, every completed trial's failed_attempts is compared against the number "
            + "the task set implies for an agent whose candidate list began with exactly those strategies; the two are "
            + "computed independently and the run is refused if they disagree. A harness that handed the agent the answer by "
            + "any other route would fail that check, which is why this column is here and not only in the source.");
        Line(text, string.Empty);
    }

    private static void Feedback(StringBuilder text, ExperimentResult result, Preregistration design)
    {
        Line(text, "REUSE FEEDBACK -- what was written to the ledger, and what was deliberately not.");
        Line(text, string.Format(
            CultureInfo.InvariantCulture,
            "  submissions: {0} of {1} trial(s). One per trial that was exposed to at least one record, carrying the",
            result.FeedbackSubmissions,
            result.Trials.Count));
        Line(text, "  condition as its TrialLabel and " + design.PrimaryMetric + " as its one ReuseMeasure.");
        Line(text, "  A trial that saw no record submits nothing, and that is a fact about the ledger rather than a");
        Line(text, "  choice made here: ExposedExperienceIds must name at least one record, because feedback about no");
        Line(text, "  exposure records nothing. The memory-disabled arm therefore has no rows, and the report owns the");
        Line(text, "  other four metrics rather than multiplying feedback IDs to carry them.");
        Line(text, string.Empty);
        Line(text, string.Format(
            CultureInfo.InvariantCulture,
            "  comparative evaluation results submitted: {0}. Human assessments submitted: {1}.",
            result.ComparativeResultsSubmitted,
            result.HumanAssessmentsSubmitted));
        Line(text, "  Both counts, and the submission count above, are read back out of the ledger's own rows rather");
        Line(text, "  than tallied by the code that wrote them, so a change that started submitting either moves them.");
        Wrapped(text, "  ", design.Attribution);
        Line(text, string.Empty);
    }

    private static void Notes(StringBuilder text, ExperimentResult result)
    {
        Line(text, "NOTES");

        foreach (var note in result.Gate.Notes)
        {
            Wrapped(text, "  - ", note, continuation: "    ");
        }

        Wrapped(
            text,
            "  - ",
            (result.CheckDisagreements.Count > 0
                ? "The deterministic IEvaluator task check and the verification aggregator disagreed on trial(s) "
                    + string.Join(", ", result.CheckDisagreements) + ". Both readings are kept; neither is preferred here."
                : "The deterministic IEvaluator task check and the verification aggregator agreed on every trial.")
                + " The two are NOT independent observations: the task check reads the same recorded exit code the"
                + " aggregator's evidence was built from, so agreement here can exonerate the aggregator's handling of that"
                + " evidence and can never exonerate the observation itself. What it would catch is evidence filed in the"
                + " wrong verification round or a required check that never produced any; a test drives exactly that case"
                + " and this line reports the disagreement.",
            continuation: "    ");

        Line(text, string.Empty);
    }

    private static void Field(StringBuilder text, string label, string value) =>
        Line(text, string.Format(CultureInfo.InvariantCulture, "  {0,-18}{1}", label, value));

    /// <summary>
    /// Writes <paramref name="content"/> across lines of at most <see cref="WrapWidth"/> characters,
    /// so a long sentence read out of the pre-registration is legible and, more importantly, wraps
    /// the same way on every machine.
    /// </summary>
    private static void Wrapped(StringBuilder text, string indent, string content, string? continuation = null)
    {
        var prefix = indent;
        var line = new StringBuilder();

        foreach (var word in content.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && prefix.Length + line.Length + 1 + word.Length > WrapWidth)
            {
                Line(text, prefix + line);
                line.Clear();
                prefix = continuation ?? indent;
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > 0)
        {
            Line(text, prefix + line);
        }
    }

    private const int WrapWidth = 100;

    private static string Number(double? value) =>
        value is { } number ? number.ToString("F3", CultureInfo.InvariantCulture) : Undefined;

    private static string Count(int? value) =>
        value is { } number ? number.ToString(CultureInfo.InvariantCulture) : "-";

    private static void Line(StringBuilder text, string content) => text.Append(content).Append(LineSeparator);

    private static void Line(StringBuilder text, StringBuilder content) => text.Append(content).Append(LineSeparator);
}

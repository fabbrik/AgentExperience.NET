using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentExperience.LiveReuse.Harness;

/// <summary>
/// Renders a result as the Markdown report and the raw per-trial JSON. Pure functions of the result: the same result
/// always renders to the same bytes (invariant culture, fixed ordering, no clock reads).
/// </summary>
public static class LiveReuseReport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary><c>&lt;provider&gt;-&lt;model&gt;-&lt;yyyy-MM-dd&gt;</c>, lower-case, safe as a file name.</summary>
    public static string BaseName(LiveExperimentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var model = new string([.. result.Descriptor.RequestedModel.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '-')]);
        return $"{result.Descriptor.Provider}-{model}-{result.StartedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
    }

    /// <summary>The raw results: every learning run and every trial, with their numbers, and no prompt or key.</summary>
    public static string Json(LiveExperimentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var design = result.Design;
        var document = new
        {
            schema = "agentexperience-live-reuse-results@1",
            scripted = IsScripted(result),
            provider = result.Descriptor.Provider,
            endpointHost = result.Descriptor.EndpointHost,
            requestedModel = result.Descriptor.RequestedModel,
            reportedModels = result.ModelIds,
            startedAtUtc = result.StartedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            preregistration = new
            {
                gitBlobId = design.GitBlobId,
                byteCount = design.ByteCount,
                registeredAgainstCommit = design.RegisteredAgainstCommit,
                amendments = design.Amendments.Count,
                amendmentsAfterResults = design.AmendmentsAfterResults,
            },
            taskSetVersion = result.TaskSetVersion,
            settings = new { temperature = design.Temperature, seed = design.Seed, design.LearningAttemptLimit, design.EvaluationAttemptLimit, design.ToolCallsPerAttempt },
            budget = result.Budget,
            complete = result.Complete,
            stopReason = result.StopReason,
            excludedInstances = result.ExcludedInstances,
            conclusion = result.Conclusion,
            reference = result.Reference,
            negativeControl = result.NegativeControl,
            content = result.Content,
            prices = new { inputUsdPerMillionTokens = result.Descriptor.InputUsdPerMillionTokens, outputUsdPerMillionTokens = result.Descriptor.OutputUsdPerMillionTokens, source = result.Descriptor.PriceSource },
            totals = result.Total,
            estimatedCostUsd = Cost(result.Descriptor, result.Total),
            callsWithoutUsage = result.CallsWithoutUsage,
            learning = result.Learning,
            trials = result.Trials,
        };

        return JsonSerializer.Serialize(document, JsonOptions).ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>One run record as a single JSON line, for the partial-results file the host keeps while a run is in progress.</summary>
    public static string JsonLine(RunRecord record) =>
        JsonSerializer.Serialize(record, new JsonSerializerOptions(JsonOptions) { WriteIndented = false });

    /// <summary>The Markdown report.</summary>
    public static string Markdown(LiveExperimentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var design = result.Design;
        var d = result.Descriptor;
        var text = new StringBuilder();
        void Line(string line = "") => text.Append(line).Append('\n');

        Line($"# Live reuse experiment: {d.Provider} / {d.RequestedModel} / {result.StartedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        Line();
        if (IsScripted(result))
        {
            Line("> **SCRIPTED RUN. No model was called.** Every number below is a property of `ScriptedOperatorModel`, a deterministic");
            Line("> stand-in written by the same author as the harness. It proves the harness, not the premise. Quote nothing here as a model result.");
            Line();
        }

        Line($"**Overall conclusion under the pre-registered rule: {result.Conclusion}.** {ConclusionSentence(result)}");
        Line();
        Line("Story 4.4's reuse methodology run against a real model: the model first works two learning tickets per service with memory");
        Line("disabled; the library captures, verifies, reflects on and stores each verified run; the model then works a different ticket");
        Line("on each service four times -- with memory disabled, with its own earlier experience injected as a Historical Reference, with");
        Line("the same block but its strategy withheld (the placebo), and with genuine but stale experience injected (the negative control).");
        Line("Success is decided by the simulated database's state, never by a model. See `experiments/AgentExperience.LiveReuse/README.md`");
        Line("for the design and how to read this report.");
        Line();

        Line("## Run");
        Line();
        Line("| | |");
        Line("| --- | --- |");
        Line($"| Provider | {d.Provider}, host `{d.EndpointHost}` (the endpoint's host only) |");
        Line($"| Model requested | `{d.RequestedModel}` |");
        Line($"| Model identities the provider reported | {(result.ModelIds.Count == 0 ? "none reported" : string.Join(", ", result.ModelIds.Select(id => "`" + Safe(id) + "`")))} |");
        Line($"| Settings | temperature {Number(design.Temperature)}, seed {design.Seed} (requested on every call; honouring the seed is the provider's) |");
        Line($"| Pre-registration | `preregistration.json` at git blob `{design.GitBlobId}` ({design.ByteCount} bytes), registered on {design.RegisteredOn} against commit `{design.RegisteredAgainstCommit}`; check with `git hash-object experiments/AgentExperience.LiveReuse/preregistration.json` |");
        Line($"| Amendments | {(design.Amendments.Count == 0 ? "none: the design is exactly as first registered" : $"{design.Amendments.Count} recorded, {design.AmendmentsAfterResults} made after results existed (listed below)")} |");
        Line($"| Task set | `{result.TaskSetVersion}`, {design.Instances} instances, hidden-assignment digest `{design.HiddenAssignmentSha256}` |");
        Line($"| Attempt limits | learning {design.LearningAttemptLimit}, evaluation {design.EvaluationAttemptLimit}; at most {design.ToolCallsPerAttempt} tool calls per attempt |");
        Line($"| Budget cap | {result.Budget.MaxModelCalls} model calls, {result.Budget.MaxTotalTokens} tokens; used {result.Total.ModelCalls} calls, {result.Total.TotalTokens} tokens |");
        Line($"| Earlier runs | {(result.EarlierLedgerEntries is { } earlier ? $"{earlier} earlier ledger entr{(earlier == 1 ? "y" : "ies")} for this provider and model in `results/ledger.tsv`; the pre-registered confirmatory run is the first complete one" : "not recorded (no ledger for this run)")} |");
        Line($"| Run status | {(result.Complete ? "complete: every learning run and every trial ran" : "STOPPED: " + result.StopReason + ". No verdict is evaluated for a partial run.")} |");
        Line($"| Started (UTC) | {result.StartedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} |");
        Line();

        foreach (var (amendment, number) in design.Amendments.Select((amendment, index) => (amendment, index + 1)))
        {
            Line($"Amendment {number} ({amendment.Date}, {(amendment.ResultsExisted ? "MADE AFTER RESULTS EXISTED" : "made before any result existed")}): {amendment.Change} Why: {amendment.Why} Effect on published numbers: {amendment.ResultsChanged}");
            Line();
        }

        Line("## Verdict");
        Line();
        Line("The gate, read from the pre-registration and evaluated once per comparison:");
        Line();
        Line("```");
        Line(design.GateExpression);
        Line("```");
        Line();
        Comparison(text, "Reference comparison", result.Reference, null);
        Comparison(text, "Content comparison", result.Content, "the same record with its strategy withheld: separates what the block says from the fact that a block is there");
        Comparison(text, "Negative control", result.NegativeControl, "stale experience; any model that follows the block pays for it, so this catches a block-presence effect only when the content comparison does not");
        Line(result.ExcludedInstances.Count == 0
            ? $"Excluded instances: none (the limit is {design.MaxExcludedInstances})."
            : $"Excluded instances (an evaluation trial errored): {string.Join(", ", result.ExcludedInstances)} (the limit is {design.MaxExcludedInstances}).");
        Line();

        Line("## Metrics by condition");
        Line();
        Line("Means are over completed trials. `failed_attempts` is the primary metric; everything right of it is reported and never gated.");
        Line();
        Line("Failed attempts split three ways: *rejected* (a valid strategy the database refused), *no change* (an attempt that made no");
        Line("`apply_migration` call), *other* (an unknown service, migration or strategy).");
        Line();
        Line("| Condition | Trials | Completed | Success rate | Mean failed attempts (sd) | Rejected / no change / other | First-try success | Followed the block | Mean tool calls | Unauthorized requests | Model calls | Input tokens | Output tokens | Mean latency (ms) | Est. cost (USD) |");
        Line("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var condition in new[] { design.ControlLabel, design.TreatmentLabel, design.PlaceboLabel, design.NegativeControlLabel })
        {
            var runs = result.Trials.Where(trial => trial.Condition == condition).ToList();
            var done = runs.Where(trial => trial.Status == RunStatus.Completed).ToList();
            var failed = done.Select(trial => (double)trial.FailedAttempts!.Value).ToList();
            var withBlock = done.Where(trial => trial.FollowedBlock is not null).ToList();
            var usage = runs.Aggregate(UsageTotals.Zero, (sum, trial) => sum.Plus(trial.Usage));
            var rejected = done.Sum(trial => trial.Changes.Count(change => change.ExitCode == MigrationEnvironment.ExitPreflightRejected));
            var other = done.Sum(trial => trial.Changes.Count(change => change.ExitCode is not MigrationEnvironment.ExitApplied and not MigrationEnvironment.ExitPreflightRejected));
            var noChange = done.Sum(trial => trial.FailedAttempts!.Value) - rejected - other;
            Line($"| {condition} | {runs.Count} | {done.Count} | {Rate(done.Count(trial => trial.Verified == true), done.Count)} | {Mean(failed)} ({Sd(failed)}) | {rejected} / {noChange} / {other} | "
                + $"{Rate(done.Count(trial => trial.FailedAttempts == 0), done.Count)} | {(withBlock.Count == 0 ? "-" : Rate(withBlock.Count(trial => trial.FollowedBlock == true), withBlock.Count))} | "
                + $"{Mean(done.Select(trial => (double)trial.ToolCalls).ToList())} | {done.Sum(trial => trial.UnauthorizedRequests)} | {usage.ModelCalls} | {usage.InputTokens} | {usage.OutputTokens} | "
                + $"{Mean(done.Select(trial => trial.LatencyMilliseconds).ToList(), "0")} | {Money(Cost(d, usage))} |");
        }

        Line();
        Line("## Learning phase");
        Line();
        Line($"`{LiveReuseExperiment.LearnHidden}` runs against a database that accepts the strategy the evaluation ticket needs; their records feed");
        Line($"`{design.TreatmentLabel}` and `{design.PlaceboLabel}`. `{LiveReuseExperiment.LearnStale}` runs against one that accepts a different strategy; their records feed");
        Line($"`{design.NegativeControlLabel}`. A run that did not verify stored nothing, and its instance's trial ran with nothing to retrieve.");
        Line();
        Line("| Seq | Instance | Service | Run | Accepted | Status | Live | Failed attempts | Strategies tried | Stored record names | Model calls | Tokens in | Tokens out |");
        Line("| ---: | ---: | --- | --- | --- | --- | --- | ---: | --- | --- | ---: | ---: | ---: |");
        foreach (var run in result.Learning)
        {
            Line($"| {run.Sequence} | {run.Instance} | {run.Service} | {run.Condition} | {run.AcceptedStrategy} | {Status(run)} | {YesNo(run.Verified)} | {Dash(run.FailedAttempts)} | {Tried(run)} | {run.StoredStrategy ?? "-"} | {run.Usage.ModelCalls} | {run.Usage.InputTokens} | {run.Usage.OutputTokens} |");
        }

        Line();
        Line("## Per-trial results");
        Line();
        Line("Every evaluation trial, in the order it ran. None is ever dropped. `Block named` is the strategy on the Approach: line of the");
        Line("Historical Reference the model was actually shown, read out of the text it was sent.");
        Line();
        Line("| Seq | Instance | Service | Condition | Accepted | Status | Live | Failed attempts | Strategies tried | Block named | Followed | Tool calls | Unauthorized | Model calls | Tokens in | Tokens out | Latency (ms) |");
        Line("| ---: | ---: | --- | --- | --- | --- | --- | ---: | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var trial in result.Trials)
        {
            Line($"| {trial.Sequence} | {trial.Instance} | {trial.Service} | {trial.Condition} | {trial.AcceptedStrategy} | {Status(trial)} | {YesNo(trial.Verified)} | {Dash(trial.FailedAttempts)} | {Tried(trial)} | "
                + $"{trial.BlockStrategy ?? "-"} | {YesNo(trial.FollowedBlock)} | {trial.ToolCalls} | {trial.UnauthorizedRequests} | {trial.Usage.ModelCalls} | {trial.Usage.InputTokens} | {trial.Usage.OutputTokens} | {trial.LatencyMilliseconds.ToString("0", CultureInfo.InvariantCulture)} |");
        }

        Line();
        Line("## Tokens and cost");
        Line();
        var learningUsage = result.Learning.Aggregate(UsageTotals.Zero, (sum, run) => sum.Plus(run.Usage));
        var evaluationUsage = result.Trials.Aggregate(UsageTotals.Zero, (sum, run) => sum.Plus(run.Usage));
        Line("| Phase | Model calls | Input tokens | Output tokens | Est. cost (USD) |");
        Line("| --- | ---: | ---: | ---: | ---: |");
        Line($"| Learning | {learningUsage.ModelCalls} | {learningUsage.InputTokens} | {learningUsage.OutputTokens} | {Money(Cost(d, learningUsage))} |");
        Line($"| Evaluation | {evaluationUsage.ModelCalls} | {evaluationUsage.InputTokens} | {evaluationUsage.OutputTokens} | {Money(Cost(d, evaluationUsage))} |");
        Line($"| **Total** | **{result.Total.ModelCalls}** | **{result.Total.InputTokens}** | **{result.Total.OutputTokens}** | **{Money(Cost(d, result.Total))}** |");
        Line();
        Line(d.InputUsdPerMillionTokens is { } input && d.OutputUsdPerMillionTokens is { } output
            ? $"Prices: USD {Number(input)} per million input tokens and USD {Number(output)} per million output tokens ({d.PriceSource}). An estimate from list prices, not a bill."
            : $"Cost not estimated: {d.PriceSource}.");
        Line(result.CallsWithoutUsage == 0
            ? "Every response reported its token usage."
            : $"{result.CallsWithoutUsage} response(s) reported no token usage; they count as zero above, so the token totals are a lower bound.");
        Line();

        Line("## Limitations");
        Line();
        foreach (var limitation in Limitations(result))
        {
            Line("- " + limitation);
        }

        Line();
        Line("## Raw results");
        Line();
        Line($"`{BaseName(result)}.json`, next to this file: every learning run and trial above, with its change sequence and usage. It holds no");
        Line("prompt, no message text and no key.");

        return text.ToString();
    }

    private static void Comparison(StringBuilder text, string title, ComparisonResult comparison, string? note)
    {
        text.Append($"### {title}: `{comparison.Treatment}` against `{comparison.Control}` -- {comparison.Verdict}")
            .Append(note is null ? string.Empty : $" ({note})").Append("\n\n");

        if (comparison.Terms.Count == 0)
        {
            text.Append(comparison.Verdict == ComparisonVerdict.NotEvaluated
                ? "Not evaluated: the run did not complete, and a gate is evaluated on the complete pre-registered design or not at all.\n\n"
                : $"Not evaluated: {comparison.ExcludedInstances} instance(s) were excluded, more than the pre-registered limit, or no pair remained.\n\n");
            return;
        }

        text.Append("| Term | Holds | Observed |\n| --- | --- | --- |\n");
        foreach (var term in comparison.Terms)
        {
            text.Append($"| `{term.Expression}` | {(term.Holds ? "yes" : "**no**")} | {term.Observed} |\n");
        }

        text.Append('\n');
    }

    private static string ConclusionSentence(LiveExperimentResult result) => result.Conclusion switch
    {
        OverallConclusion.ReuseBenefitAttributableToContent =>
            "With its own verified experience injected, the model needed fewer failed attempts than with memory disabled and than with the same block with its strategy withheld, by the pre-registered test, and stale experience gave it no such benefit. The model acted on the Approach: line; this does not show that the block's presence alone contributed nothing.",
        OverallConclusion.BenefitNotAttributableToContent =>
            "Memory-enabled passed the gate against memory-disabled, but either not against the placebo (the same block with its strategy withheld) or the stale experience passed too. A benefit that the placebo matches, or that survives wrong content, cannot be credited to what the record says, so no reuse benefit is claimed.",
        OverallConclusion.NoDemonstratedBenefit =>
            "The memory-enabled condition did not pass the pre-registered gate against memory-disabled. No reuse benefit is claimed.",
        OverallConclusion.Inconclusive =>
            "Too many instances were excluded for the pre-registered comparison to be evaluated. No claim either way.",
        _ => "The run did not complete, so no verdict was evaluated. No claim either way.",
    };

    private static IEnumerable<string> Limitations(LiveExperimentResult result)
    {
        if (IsScripted(result))
        {
            yield return "This is a scripted run. The scripted operator follows an injected block by construction, so any benefit here is a property of the script.";
        }

        yield return "One model, one provider, one run. Temperature 0 and a fixed seed are requested, but neither provider guarantees determinism, and a model version change can change every number. Re-run before relying on a result.";
        yield return $"{result.Design.Instances} instances. The sign test can detect only a large, consistent effect; NoDemonstratedBenefit here is not evidence that there is no smaller effect.";
        yield return "The sign test treats the instance pairs as independent. Each hidden strategy is the answer for two services, and a near-deterministic model with a fixed search order will tend to score both alike, so the effective sample is smaller than the instance count and the p-value is optimistic. The inference is over this fixed, pre-committed assignment, not over tasks in general.";
        yield return "A synthetic task family: a simulated database whose accepted rollout strategy is a stand-in for tacit, environment-specific knowledge. Real tasks usually leave the answer partly inferable, which would shrink the gap between the conditions.";
        yield return "The working strategy reaches the block verbatim, on the Approach: line, through the story 6.2 ApproachArguments allowlist. This measures whether a model acts on a Historical Reference labelled as untrusted reference material, not whether it can generalize from vaguer lessons.";
        yield return "Retrieval is not under test: records are scoped per service and each scope holds one record, so a memory-enabled trial always retrieves its own service's record. Retrieval quality over a crowded store is a separate question.";
        yield return "The negative control's record is stale (verified against a database that has since changed), not irrelevant; an irrelevant record is a different control and was not run.";
        yield return "Tokens are as the provider reports them; whether thinking tokens are included in the output count is the provider's accounting. Cost is an estimate from list prices. Latency includes the network and is never gated.";
        yield return "The repository is public, and the hidden assignment is in its source. A model trained on it after the registration date could know the answers; check the model's training cutoff against the registration date.";
        if (!result.Complete)
        {
            yield return "The run stopped at a budget cap. Its partial numbers are reported for transparency and support no conclusion.";
        }
    }

    /// <summary>A provider-supplied string, made safe for a Markdown table cell: at most 64 characters, no pipes, backticks or controls.</summary>
    internal static string Safe(string text)
    {
        var cleaned = new string([.. text.Select(c => char.IsControl(c) || c is '|' or '`' ? ' ' : c)]).Trim();
        return cleaned.Length <= 64 ? cleaned : cleaned[..64] + "...";
    }

    private static bool IsScripted(LiveExperimentResult result) => result.Descriptor.Provider == "scripted";

    internal static double? Cost(RunDescriptor descriptor, UsageTotals usage) =>
        descriptor.InputUsdPerMillionTokens is { } input && descriptor.OutputUsdPerMillionTokens is { } output
            ? ((usage.InputTokens * input) + (usage.OutputTokens * output)) / 1_000_000d
            : null;

    private static string Tried(RunRecord run) =>
        run.Changes.Count == 0 ? "-" : string.Join(" > ", run.Changes.Select(change => change.Strategy + (change.ExitCode == 0 ? " (live)" : string.Empty)));

    private static string Status(RunRecord run) => run.Status == RunStatus.Completed ? "completed" : run.Status + ": " + run.Classification;

    private static string YesNo(bool? value) => value switch { true => "yes", false => "no", null => "-" };

    private static string Dash(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static string Money(double? value) => value?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "n/a";

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Rate(int count, int of) => of == 0 ? "-" : string.Create(CultureInfo.InvariantCulture, $"{(double)count / of:0.000} ({count}/{of})");

    private static string Mean(IReadOnlyList<double> values, string format = "0.000") =>
        values.Count == 0 ? "-" : values.Average().ToString(format, CultureInfo.InvariantCulture);

    private static string Sd(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
        {
            return "-";
        }

        var mean = values.Average();
        return Math.Sqrt(values.Sum(value => (value - mean) * (value - mean)) / (values.Count - 1)).ToString("0.000", CultureInfo.InvariantCulture);
    }
}

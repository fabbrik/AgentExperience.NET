using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentExperience.LiveReuse.Harness;

/// <summary>
/// The real-task experiment's report: deterministic text and JSON from a result (invariant culture, fixed ordering, no
/// clock reads). Never carries a prompt, a block's text, a file's content or a key.
/// </summary>
public static class RealTaskReport
{
    public const string Prefix = "realtask-";

    /// <summary><c>realtask-&lt;provider&gt;-&lt;model&gt;-&lt;yyyy-MM-dd&gt;</c>, lower-case, safe as a file name.</summary>
    public static string BaseName(RealTaskResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var model = new string([.. result.Descriptor.RequestedModel.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '-')]);
        return $"{Prefix}{result.Descriptor.Provider}-{model}-{result.StartedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
    }

    /// <summary>One line per learning run and trial, appended as it is recorded.</summary>
    public static string JsonLine(RealTaskRun run) =>
        JsonSerializer.Serialize(run, new JsonSerializerOptions(LiveReuseReport.JsonOptions) { WriteIndented = false });

    /// <summary>The raw results: every learning run and trial, with their numbers.</summary>
    public static string Json(RealTaskResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var design = result.Design;
        var document = new
        {
            schema = "agentexperience-realtask-results@1",
            scripted = IsScripted(result),
            provider = result.Descriptor.Provider,
            endpointHost = result.Descriptor.EndpointHost,
            requestedModel = result.Descriptor.RequestedModel,
            modelIdsReported = result.ModelIds,
            preregistrationGitBlobId = design.GitBlobId,
            taskSetVersion = RealTaskSet.Version,
            taskSetDigest = result.TaskSetDigest,
            startedAt = result.StartedAt,
            complete = result.Complete,
            stopReason = result.StopReason,
            earlierLedgerEntries = result.EarlierLedgerEntries,
            conclusion = result.Conclusion,
            budget = result.Budget,
            usage = result.Total,
            callsWithoutUsage = result.CallsWithoutUsage,
            estimatedCostUsd = LiveReuseReport.Cost(result.Descriptor, result.Total),
            excluded = result.Excluded,
            comparisons = new { reference = result.Reference, content = result.Content, mismatchedControl = result.MismatchedControl },
            learning = result.Learning,
            trials = result.Trials,
        };
        return JsonSerializer.Serialize(document, LiveReuseReport.JsonOptions);
    }

    public static string Markdown(RealTaskResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var d = result.Descriptor;
        var design = result.Design;
        var text = new StringBuilder();
        void Line(string line = "") => text.Append(line).Append('\n');

        Line($"# Real-task experiment: {d.Provider} / {d.RequestedModel} / {result.StartedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        Line();
        Line($"**Overall conclusion under the pre-registered rule: {result.Conclusion}.** {ConclusionSentence(result.Conclusion)}");
        Line();
        if (IsScripted(result))
        {
            Line("> **Scripted run, not a model.** The stand-in follows the first file path a block names by construction, so any benefit below is a property of the script and of where the library's retrieval ranked the sibling's record. It proves the harness; it says nothing about any model.");
            Line();
        }

        Line("A model repairs a small Python service broken by a dependency upgrade, in a real temporary repository with real tools; success is the exit code of the service's own unit tests. A lesson learned on one service has to be found by the library's own retrieval among the other services' lessons and has to cut the tool calls needed on the sibling service that needs the same fix. See the real-task section of `experiments/AgentExperience.LiveReuse/README.md`.");
        Line();
        Line("## Run");
        Line();
        Line("| | |");
        Line("| --- | --- |");
        Line($"| Provider | {d.Provider}, host `{d.EndpointHost}` (the endpoint's host only) |");
        Line($"| Model requested | `{d.RequestedModel}` |");
        Line($"| Model identities the provider reported | {(result.ModelIds.Count == 0 ? "none" : string.Join(", ", result.ModelIds.Select(id => "`" + id + "`")))} |");
        Line($"| Settings | temperature {design.Temperature.ToString("0.###", CultureInfo.InvariantCulture)}; seed {(d.SeedSent ? design.Seed.ToString(CultureInfo.InvariantCulture) + " + repeat" : "not sent: this provider does not accept a seed")}; {design.Repeats} repeats |");
        Line($"| Pre-registration | `preregistration.realtask.json` at git blob `{design.GitBlobId}`; {design.AmendmentCount} amendment(s) |");
        Line($"| Task set | `{RealTaskSet.Version}`: 12 services in 6 clusters; digest `{result.TaskSetDigest}` |");
        Line($"| Limits | {design.LearningToolCalls} tool calls per learning run, {design.EvaluationToolCalls} per trial, {design.MaxAttempts} test-running attempts |");
        Line($"| Retries | up to {design.RetryMaxTries} tries on HTTP 429 and 5xx before a call is counted |");
        Line($"| Budget cap | {result.Budget.MaxModelCalls} model calls, {result.Budget.MaxTotalTokens} tokens; used {result.Total.ModelCalls} calls, {result.Total.TotalTokens} tokens |");
        Line($"| Earlier runs | {(result.EarlierLedgerEntries is { } earlier ? earlier.ToString(CultureInfo.InvariantCulture) + " earlier complete run(s) for this provider and model in `results/realtask-ledger.tsv`; " + (earlier == 0 ? "the confirmatory run is the first complete one" : "this run is exploratory") : "no ledger")} |");
        Line($"| Run status | {(result.Complete ? "complete: every learning run and every trial ran" : "stopped: " + result.StopReason)} |");
        Line();

        Line("## Verdict");
        Line();
        Line("The gate, evaluated once per comparison. The unit is the **service** (the mean over its repeats), because the repeats of one service are not independent; the trial-level sign test is printed and never gated.");
        Line();
        Comparison(Line, "Reference comparison", result.Reference, "memory-enabled against memory-disabled");
        Comparison(Line, "Content comparison", result.Content, "the same records with the file path withheld: separates what the block says from the fact that a block is there");
        Comparison(Line, "Mismatched-cluster control", result.MismatchedControl, "the store without the sibling's record; expected NoDemonstratedBenefit");

        if (result.Excluded.Count > 0)
        {
            Line($"Excluded services ({result.Excluded.Count}; the limit is {design.MaxExcludedServices}): " + string.Join("; ", result.Excluded.Select(item => item.Service + " (" + item.Reason + ")")) + ".");
            Line();
        }

        Line("## By condition");
        Line();
        Line("| Condition | Trials | Verified | Mean tool calls (sd) | Mean reads before the first write | First write was the cluster's fix file | Refused writes | Model calls | Input tokens | Output tokens | Est. cost (USD) |");
        Line("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var condition in new[] { design.ControlLabel, design.TreatmentLabel, design.PlaceboLabel, design.MismatchedLabel })
        {
            var runs = result.Trials.Where(trial => trial.Condition == condition).ToList();
            var done = runs.Where(trial => trial.Status == RunStatus.Completed).ToList();
            var usage = runs.Aggregate(UsageTotals.Zero, (sum, trial) => sum.Plus(trial.Usage));
            var scored = done.Select(trial => (double)trial.Score).ToList();
            var writes = done.Where(trial => trial.FirstWriteWasClusterFix is not null).ToList();
            Line($"| {condition} | {runs.Count} | {LiveReuseReport.Rate(done.Count(trial => trial.Verified == true), done.Count)} | {LiveReuseReport.Mean(scored)} ({LiveReuseReport.Sd(scored)}) "
                + $"| {LiveReuseReport.Mean([.. done.Select(trial => (double)trial.ReadsBeforeFirstWrite)])} | {LiveReuseReport.Rate(writes.Count(trial => trial.FirstWriteWasClusterFix == true), writes.Count)} "
                + $"| {done.Sum(trial => trial.UnauthorizedRequests)} | {usage.ModelCalls} | {usage.InputTokens} | {usage.OutputTokens} | {LiveReuseReport.Money(LiveReuseReport.Cost(d, usage))} |");
        }

        Line();
        Line("A trial that did not reach green scores its cap. *Mean reads before the first write* counts `list_files` and `read_file` calls: the exploration the lesson is meant to save.");
        Line();

        Line("## By service: retrieval and tool calls");
        Line();
        Line("What the library's retrieval put in each memory-enabled block, read out of the text the model was sent, and each condition's mean tool calls over the service's repeats. *Rank* is the position of the sibling's learning record; *named the fix file* whether the block's text carried the file the fix goes in.");
        Line();
        Line("| Service | Cluster | Sibling in block (repeats) | Mean rank | Named the fix file (repeats) | Disabled | Enabled | Placebo | Mismatched |");
        Line("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var group in result.Trials.GroupBy(trial => trial.Instance).OrderBy(group => group.Key))
        {
            var enabled = group.Where(trial => trial.Condition == design.TreatmentLabel).ToList();
            var ranks = enabled.Where(trial => trial.SiblingRank is not null).Select(trial => (double)trial.SiblingRank!.Value).ToList();
            string Mean(string condition)
            {
                var scores = group.Where(trial => trial.Condition == condition && trial.Status == RunStatus.Completed).Select(trial => (double)trial.Score).ToList();
                return scores.Count == 0 ? "-" : LiveReuseReport.Mean(scores, "0.0");
            }

            Line($"| {group.First().Service} | {group.First().Cluster} | {enabled.Count(trial => trial.SiblingRank is not null)}/{enabled.Count} | {LiveReuseReport.Mean(ranks, "0.0")} | {enabled.Count(trial => trial.BlockNamedClusterFix)}/{enabled.Count} "
                + $"| {Mean(design.ControlLabel)} | {Mean(design.TreatmentLabel)} | {Mean(design.PlaceboLabel)} | {Mean(design.MismatchedLabel)} |");
        }

        Line();
        Line("## Learning runs");
        Line();
        Line("| Service | Cluster | Status | Verified | Tool calls | Test runs | Stored a record | Fix path(s) the record carries |");
        Line("| --- | --- | --- | --- | ---: | ---: | --- | --- |");
        foreach (var run in result.Learning)
        {
            Line($"| {run.Service} | {run.Cluster} | {(run.Status == RunStatus.Completed ? "completed" : run.Status + ": " + run.Classification)} | {LiveReuseReport.YesNo(run.Verified)} | {run.ToolCalls} | {run.TestRuns} | {(run.Verified == true ? "yes" : "no")} | {(run.StoredFixPaths.Count == 0 ? "-" : string.Join(", ", run.StoredFixPaths.Select(path => "`" + path + "`")))} |");
        }

        Line();
        Line("## Tokens and cost");
        Line();
        var learningUsage = result.Learning.Aggregate(UsageTotals.Zero, (sum, run) => sum.Plus(run.Usage));
        var evaluationUsage = result.Trials.Aggregate(UsageTotals.Zero, (sum, run) => sum.Plus(run.Usage));
        Line("| Phase | Model calls | Input tokens | Output tokens | Est. cost (USD) |");
        Line("| --- | ---: | ---: | ---: | ---: |");
        Line($"| Learning | {learningUsage.ModelCalls} | {learningUsage.InputTokens} | {learningUsage.OutputTokens} | {LiveReuseReport.Money(LiveReuseReport.Cost(d, learningUsage))} |");
        Line($"| Evaluation | {evaluationUsage.ModelCalls} | {evaluationUsage.InputTokens} | {evaluationUsage.OutputTokens} | {LiveReuseReport.Money(LiveReuseReport.Cost(d, evaluationUsage))} |");
        Line($"| **Total** | **{result.Total.ModelCalls}** | **{result.Total.InputTokens}** | **{result.Total.OutputTokens}** | **{LiveReuseReport.Money(LiveReuseReport.Cost(d, result.Total))}** |");
        Line();
        Line(d.InputUsdPerMillionTokens is { } input && d.OutputUsdPerMillionTokens is { } output
            ? $"Prices: USD {LiveReuseReport.Number(input)} per million input tokens and USD {LiveReuseReport.Number(output)} per million output tokens ({d.PriceSource})."
            : $"Cost not estimated: {d.PriceSource}.");
        Line();
        Line("## Limitations");
        Line();
        foreach (var limitation in Limitations)
        {
            Line("- " + limitation);
        }

        Line();
        Line("## Raw results");
        Line();
        Line(IsScripted(result)
            ? "None: a scripted run writes no file. The command prints this report only."
            : "Every learning run and trial, with its numbers, is in the `.json` file beside this report. The file carries no prompt, no block text, no file content and no key.");
        return text.ToString();
    }

    private static readonly string[] Limitations =
    [
        "Synthetic services and a vendored library written for this experiment, not code found in the wild. Realism comes from real files, real tools and a real process exit code, not from real-world breakage.",
        "The two services of a cluster are near-copies: transfer here means reusing a fix location across sibling services, the shape of a real monorepo with many similar services.",
        "Four tools, no shell and no search. A model with a grep tool would find the file faster without memory and the benefit would shrink.",
        "Three repeats at temperature above zero reduce the noise per service; they do not make twelve services independent samples of all upgrade breakages.",
        "The repository is public and the task set is in its source. Check a model's training cutoff against the registration date.",
    ];

    private static void Comparison(Action<string> line, string heading, RealTaskComparison comparison, string note)
    {
        line($"### {heading}: `{comparison.Treatment}` against `{comparison.Control}` -- {comparison.Verdict} ({note})");
        line(string.Empty);
        switch (comparison.Verdict)
        {
            case ComparisonVerdict.NotEvaluated:
                line("Not evaluated: the run stopped before every trial ran.");
                break;
            case ComparisonVerdict.Inconclusive:
                line($"Not evaluated: {comparison.ExcludedServices} service(s) were excluded, more than the pre-registered limit, or no pair remained.");
                break;
            default:
                line("| Term | Holds | Observed |");
                line("| --- | --- | --- |");
                foreach (var term in comparison.Terms)
                {
                    line($"| `{term.Expression}` | {(term.Holds ? "yes" : "**no**")} | {term.Observed} |");
                }

                break;
        }

        line(string.Empty);
        if (comparison.TrialSignTest is { } trial)
        {
            line($"Trial level (reported, never gated; the repeats of one service are not independent): {trial.Wins} fewer, {trial.Losses} more, {trial.Ties} tied, of {trial.Wins + trial.Losses + trial.Ties} (service, repeat) pairs; p = {trial.PValue.ToString("0.0000", CultureInfo.InvariantCulture)}.");
            line(string.Empty);
        }
    }

    private static string ConclusionSentence(RealTaskConclusion conclusion) => conclusion switch
    {
        RealTaskConclusion.RealTaskBenefitAttributableToContent => "Memory cut the tool calls needed, more than the placebo block did, and the other clusters' lessons did not.",
        RealTaskConclusion.RealTaskBenefitNotAttributableToContent => "Memory-enabled beat memory-disabled, but the placebo or the other clusters' lessons did as well, so the benefit is not shown to come from what the lesson says.",
        RealTaskConclusion.RealTaskNoDemonstratedBenefit => "Memory-enabled did not pass the pre-registered gate against memory-disabled. No benefit is claimed.",
        RealTaskConclusion.RealTaskInconclusive => "More services were excluded than the pre-registration allows. No claim either way.",
        _ => "The run stopped at a budget cap before every trial ran. No verdict was evaluated.",
    };

    private static bool IsScripted(RealTaskResult result) => result.Descriptor.Provider == "scripted";
}

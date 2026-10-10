using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentExperience.LiveReuse.Harness;

/// <summary>
/// Renders a transfer result as the Markdown report and the raw per-trial JSON. Pure functions of the result: the same
/// result always renders to the same bytes (invariant culture, fixed ordering, no clock reads, no latency).
/// </summary>
public static class TransferReport
{
    /// <summary>Every transfer report and raw-results file starts with this, so it never collides with a 9.1 report.</summary>
    public const string Prefix = "transfer-";

    /// <summary><c>transfer-&lt;provider&gt;-&lt;model&gt;-&lt;yyyy-MM-dd&gt;</c>, lower-case, safe as a file name.</summary>
    public static string BaseName(TransferExperimentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var model = new string([.. result.Descriptor.RequestedModel.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '-')]);
        return $"{Prefix}{result.Descriptor.Provider}-{model}-{result.StartedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
    }

    /// <summary>The raw results: every distractor, learning run and trial, with their numbers, and no prompt, block text or key.</summary>
    public static string Json(TransferExperimentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var design = result.Design;
        var document = new
        {
            schema = "agentexperience-transfer-results@1",
            scripted = IsScripted(result),
            provider = result.Descriptor.Provider,
            endpointHost = result.Descriptor.EndpointHost,
            requestedModel = result.Descriptor.RequestedModel,
            reportedModels = result.ModelIds,
            startedAtUtc = result.StartedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            preregistration = new
            {
                file = TransferPreregistration.FileName,
                gitBlobId = design.GitBlobId,
                byteCount = design.ByteCount,
                registeredAgainstCommit = design.RegisteredAgainstCommit,
                amendments = design.Amendments.Count,
                amendmentsAfterResults = design.AmendmentsAfterResults,
            },
            taskSetVersion = result.TaskSetVersion,
            traitDigest = result.TraitDigest,
            settings = new { temperature = design.Temperature, seed = design.Seed, seedSent = result.Descriptor.SeedSent, maxOutputTokens = result.Descriptor.MaxOutputTokens, design.LearningAttemptLimit, design.EvaluationAttemptLimit, design.ToolCallsPerAttempt },
            budget = result.Budget,
            complete = result.Complete,
            stopReason = result.StopReason,
            excluded = result.Excluded,
            conclusion = result.Conclusion,
            reference = result.Reference,
            content = result.Content,
            mismatchedControl = result.MismatchedControl,
            prices = new { inputUsdPerMillionTokens = result.Descriptor.InputUsdPerMillionTokens, outputUsdPerMillionTokens = result.Descriptor.OutputUsdPerMillionTokens, source = result.Descriptor.PriceSource },
            totals = result.Total,
            estimatedCostUsd = LiveReuseReport.Cost(result.Descriptor, result.Total),
            callsWithoutUsage = result.CallsWithoutUsage,
            distractors = result.Distractors,
            harm = result.Harm,
            learning = result.Learning.Select(learning => new { cluster = learning.Cluster, run = learning.Run }),
            trials = result.Trials.Select(trial => new
            {
                trial.Cluster,
                trial.HiddenStrategy,
                trial.SameClusterTaskId,
                trial.SameClusterInjected,
                trial.SameClusterRank,
                trial.BlockRecords,
                trial.StrategyMentions,
                followedBlock = trial.Run.FollowedBlock,
                trial.FollowedTransferredLesson,
                run = trial.Run,
            }),
        };

        return JsonSerializer.Serialize(document, LiveReuseReport.JsonOptions).ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>The Markdown report.</summary>
    public static string Markdown(TransferExperimentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var design = result.Design;
        var d = result.Descriptor;
        var text = new StringBuilder();
        void Line(string line = "") => text.Append(line).Append('\n');

        Line($"# Transfer experiment: {d.Provider} / {d.RequestedModel} / {result.StartedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        Line();
        if (IsScripted(result))
        {
            Line("> **SCRIPTED RUN. No model was called.** Every number below is a property of `ScriptedOperatorModel`, a deterministic");
            Line("> stand-in that follows the first strategy on the block's first working line. It proves the harness and shows what the");
            Line("> library's retrieval put in each block, not the premise. Quote nothing here as a model result.");
            Line();
        }

        Line($"**Overall conclusion under the pre-registered rule: {result.Conclusion}.** {ConclusionSentence(result)}");
        Line();
        Line("A lesson learned on one service has to be found by the library's own retrieval, in one shared store that also holds");
        Line("other clusters' lessons and distractors from two other task families, and has to help on a different, unseen service");
        Line("that shares only its cluster. Every database proxy in a cluster accepts one rollout strategy. Each unseen service runs");
        Line("four times: memory disabled; the full store; the full store with the strategy withheld (the placebo); and the store");
        Line("without its own cluster's lesson (the mismatched-trait control). Success is decided by the simulated database's state,");
        Line("never by a model. See the Transfer section of `experiments/AgentExperience.LiveReuse/README.md`.");
        Line();

        Line("## Run");
        Line();
        Line("| | |");
        Line("| --- | --- |");
        Line($"| Provider | {d.Provider}, host `{d.EndpointHost}` (the endpoint's host only) |");
        Line($"| Model requested | `{d.RequestedModel}` |");
        Line($"| Model identities the provider reported | {(result.ModelIds.Count == 0 ? "none reported" : string.Join(", ", result.ModelIds.Select(id => "`" + LiveReuseReport.Safe(id) + "`")))} |");
        Line($"| Settings | temperature {LiveReuseReport.Number(design.Temperature)}, seed {(d.SeedSent ? $"{design.Seed} (requested on every call)" : $"{design.Seed} not sent: this provider does not accept a seed")}"
            + (d.MaxOutputTokens is { } cap ? $", max output tokens {cap.ToString(CultureInfo.InvariantCulture)} per call" : string.Empty) + " |");
        Line($"| Pre-registration | `{TransferPreregistration.FileName}` at git blob `{design.GitBlobId}` ({design.ByteCount} bytes), {design.RegisteredAgainstCommit}, on {design.RegisteredOn}; {(design.LiveResultsExistedAtRegistration ? "**live results existed at registration**" : design.ResultsNote)}; check with `git hash-object experiments/AgentExperience.LiveReuse/{TransferPreregistration.FileName}` |");
        Line($"| Amendments | {(design.Amendments.Count == 0 ? "none: the design is exactly as first registered" : $"{design.Amendments.Count} recorded, {design.AmendmentsAfterResults} made after results existed")} |");
        Line($"| Task set | `{result.TaskSetVersion}`: {design.Clusters} clusters, {design.EvaluationInstances} evaluation instances, {design.Distractors} distractors; trait digest `{result.TraitDigest}` |");
        Line($"| Shared store | one scope, `{TransferExperiment.SharedScope.TenantId}/{TransferExperiment.SharedScope.ApplicationId}/{TransferExperiment.SharedScope.ProjectId}`: {result.Distractors.Count} distractor records ({result.Distractors.Count(distractor => distractor.Family == DistractorFamily.CacheFlush)} cache flushes, {result.Distractors.Count(distractor => distractor.Family == DistractorFamily.ConfigRollout)} config rollouts) and {result.Learning.Count(learning => learning.Run.StoredStrategy is not null)} learning records; retrieval by `InMemoryExperienceCandidateSource` under `RetrievalPolicy.Default with {{ Timeout = 15 s }}`, injection by `ExperienceContextProvider` (at most 8 records) |");
        Line($"| Attempt limits | learning {design.LearningAttemptLimit}, evaluation {design.EvaluationAttemptLimit}; at most {design.ToolCallsPerAttempt} tool calls per attempt |");
        Line($"| Budget cap | {result.Budget.MaxModelCalls} model calls, {result.Budget.MaxTotalTokens} tokens; used {result.Total.ModelCalls} calls, {result.Total.TotalTokens} tokens |");
        Line($"| Earlier runs | {(result.EarlierLedgerEntries is { } earlier ? $"{earlier} earlier ledger entr{(earlier == 1 ? "y" : "ies")} for this provider and model in `results/transfer-ledger.tsv`; the confirmatory run is the first complete one" : "not recorded (no ledger for this run)")} |");
        Line($"| Run status | {(result.Complete ? "complete: every learning run and every trial ran" : "STOPPED: " + result.StopReason + ". No verdict is evaluated for a partial run.")} |");
        Line($"| Started (UTC) | {result.StartedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} |");
        Line();

        Line("## Verdict");
        Line();
        Line("The gate, read from the pre-registration and evaluated once per comparison:");
        Line();
        Line("```");
        Line(design.GateExpression);
        Line("```");
        Line();
        LiveReuseReport.Comparison(text, "Reference comparison", result.Reference, null);
        LiveReuseReport.Comparison(text, "Content comparison", result.Content, "the same records with the strategy withheld: separates what the block says from the fact that a block is there");
        LiveReuseReport.Comparison(text, "Mismatched-trait control", result.MismatchedControl, "the same store without the service's own cluster's lesson; expected NoDemonstratedBenefit");
        Line(result.Excluded.Count == 0
            ? $"Excluded instances: none (the limit is {design.MaxExcludedInstances})."
            : $"Excluded instances: {string.Join("; ", result.Excluded.Select(exclusion => $"{exclusion.Instance} ({exclusion.Reason})"))} (the limit is {design.MaxExcludedInstances}).");
        Line();

        var harm = result.Harm;
        Line("### Harm: `mismatched-trait` against `memory-disabled` (reported, never gated)");
        Line();
        Line(harm.Pairs == 0
            ? "No pair to compare."
            : string.Create(CultureInfo.InvariantCulture, $"Of {harm.Pairs} pairs, the mismatched-trait trial failed more attempts in {harm.Worse}, fewer in {harm.Better}, and as many in {harm.Tied}. Mean failed attempts: {LiveGate.Format(harm.MismatchedMeanFailedAttempts)} mismatched-trait, {LiveGate.Format(harm.DisabledMeanFailedAttempts)} memory-disabled."));
        Line();

        Line("## By instance: retrieval and failed attempts");
        Line();
        Line("What the library's retrieval put in each block, read out of the text the model was sent, and each condition's failed");
        Line("attempts (the primary metric). *Rank* is the position of the same-cluster learning record in the memory-enabled block;");
        Line("the mismatched-trait columns show the record its block ranked first and the strategy on that record's working line.");
        Line("Never gated.");
        Line();
        var enabledTrials = result.Trials.Where(trial => trial.Run.Condition == design.TreatmentLabel).ToList();
        Line("| Instance | Service | Cluster | Strategy | Same-cluster record injected | Rank | Records in block | Mismatched: first record | Mismatched: first strategy | Failed: disabled | Failed: enabled | Failed: placebo | Failed: mismatched | Enabled followed the block | Enabled followed the transferred lesson |");
        Line("| ---: | --- | --- | --- | --- | ---: | ---: | --- | --- | ---: | ---: | ---: | ---: | --- | --- |");
        foreach (var group in result.Trials.GroupBy(trial => trial.Run.Instance).OrderBy(group => group.Key))
        {
            var any = group.First();
            TransferTrial? Of(string condition) => group.FirstOrDefault(trial => trial.Run.Condition == condition);
            var enabled = Of(design.TreatmentLabel);
            var mismatched = Of(design.MismatchedLabel);
            string Failed(string condition) => Of(condition) is { } trial ? (trial.Run.Status == RunStatus.Completed ? LiveReuseReport.Dash(trial.Run.FailedAttempts) : trial.Run.Status.ToString()) : "-";
            Line($"| {group.Key} | {any.Run.Service} | {any.Cluster} | {any.HiddenStrategy} | {(enabled is null ? "-" : LiveReuseReport.YesNo(enabled.SameClusterInjected))} | {LiveReuseReport.Dash(enabled?.SameClusterRank)} | {(enabled is null ? "-" : enabled.BlockRecords.Count.ToString(CultureInfo.InvariantCulture))} | "
                + $"{(mismatched is null ? "-" : mismatched.BlockRecords.FirstOrDefault() ?? "(no block)")} | {mismatched?.Run.BlockStrategy ?? "-"} | "
                + $"{Failed(design.ControlLabel)} | {Failed(design.TreatmentLabel)} | {Failed(design.PlaceboLabel)} | {Failed(design.MismatchedLabel)} | {LiveReuseReport.YesNo(enabled?.Run.FollowedBlock)} | {LiveReuseReport.YesNo(enabled?.FollowedTransferredLesson)} |");
        }

        Line();
        Line(enabledTrials.Count == 0
            ? "No memory-enabled trial ran."
            : string.Create(CultureInfo.InvariantCulture, $"The same-cluster record was in the memory-enabled block for {enabledTrials.Count(trial => trial.SameClusterInjected)} of {enabledTrials.Count} instances, and ranked first for {enabledTrials.Count(trial => trial.SameClusterRank == 1)}."));
        Line();

        Line("## Metrics by condition");
        Line();
        Line("Means are over completed trials. `failed_attempts` is the primary metric; everything right of it is reported and never gated.");
        Line();
        Line("| Condition | Trials | Completed | Success rate | Mean failed attempts (sd) | First-try success | Followed the block | Followed the transferred lesson | Mean tool calls | Unauthorized requests | Model calls | Input tokens | Output tokens | Est. cost (USD) |");
        Line("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var condition in new[] { design.ControlLabel, design.TreatmentLabel, design.PlaceboLabel, design.MismatchedLabel })
        {
            var conditionTrials = result.Trials.Where(trial => trial.Run.Condition == condition && trial.Run.Status == RunStatus.Completed && trial.FollowedTransferredLesson is not null).ToList();
            var runs = result.Trials.Where(trial => trial.Run.Condition == condition).Select(trial => trial.Run).ToList();
            var done = runs.Where(run => run.Status == RunStatus.Completed).ToList();
            var failed = done.Select(run => (double)run.FailedAttempts!.Value).ToList();
            var withBlock = done.Where(run => run.FollowedBlock is not null).ToList();
            var usage = runs.Aggregate(UsageTotals.Zero, (sum, run) => sum.Plus(run.Usage));
            Line($"| {condition} | {runs.Count} | {done.Count} | {LiveReuseReport.Rate(done.Count(run => run.Verified == true), done.Count)} | {LiveReuseReport.Mean(failed)} ({LiveReuseReport.Sd(failed)}) | "
                + $"{LiveReuseReport.Rate(done.Count(run => run.FailedAttempts == 0), done.Count)} | {(withBlock.Count == 0 ? "-" : LiveReuseReport.Rate(withBlock.Count(run => run.FollowedBlock == true), withBlock.Count))} | "
                + $"{(conditionTrials.Count == 0 ? "-" : LiveReuseReport.Rate(conditionTrials.Count(trial => trial.FollowedTransferredLesson == true), conditionTrials.Count))} | "
                + $"{LiveReuseReport.Mean(done.Select(run => (double)run.ToolCalls).ToList())} | {done.Sum(run => run.UnauthorizedRequests)} | {usage.ModelCalls} | {usage.InputTokens} | {usage.OutputTokens} | {LiveReuseReport.Money(LiveReuseReport.Cost(d, usage))} |");
        }

        Line();
        Line("## Learning phase");
        Line();
        Line("One memory-disabled run per cluster, on its learning service. A run that verified was finalized into the shared store; one that");
        Line("did not stored nothing, and its cluster's two evaluation instances are excluded and run no trial.");
        Line();
        Line("| Seq | Cluster | Service | Accepted | Status | Live | Failed attempts | Strategies tried | Stored record names | Model calls | Tokens in | Tokens out |");
        Line("| ---: | --- | --- | --- | --- | --- | ---: | --- | --- | ---: | ---: | ---: |");
        foreach (var (cluster, run) in result.Learning)
        {
            Line($"| {run.Sequence} | {cluster} | {run.Service} | {run.AcceptedStrategy} | {LiveReuseReport.Status(run)} | {LiveReuseReport.YesNo(run.Verified)} | {LiveReuseReport.Dash(run.FailedAttempts)} | {LiveReuseReport.Tried(run)} | {LiveReuseExperiment.Sequence(run.StoredStrategies, "-")} | {run.Usage.ModelCalls} | {run.Usage.InputTokens} | {run.Usage.OutputTokens} |");
        }

        Line();
        Line("## Distractors");
        Line();
        Line("Finalized into the shared store before the learning phase by a fixed script (one `flush_read_cache` or `push_config` call each); no model call.");
        Line();
        Line("| Index | Family | Cluster | Service | Record task |");
        Line("| ---: | --- | --- | --- | --- |");
        foreach (var distractor in result.Distractors)
        {
            Line($"| {distractor.Index} | {distractor.Family} | {distractor.Cluster} | {distractor.Service} | `{distractor.TaskId}` |");
        }

        Line();
        Line("## Per-trial results");
        Line();
        Line("Every evaluation trial, in the order it ran. None is ever dropped. *Block records* are the record headers of the block the");
        Line("model was shown, in rank order; *Block named* is the strategy sequence on the first record's working line.");
        Line();
        Line("| Seq | Instance | Service | Cluster | Condition | Status | Live | Failed attempts | Strategies tried | Block records | Block named | Followed | Followed lesson | Tool calls | Unauthorized | Model calls | Tokens in | Tokens out |");
        Line("| ---: | ---: | --- | --- | --- | --- | --- | ---: | --- | --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: |");
        foreach (var trial in result.Trials)
        {
            var run = trial.Run;
            Line($"| {run.Sequence} | {run.Instance} | {run.Service} | {trial.Cluster} | {run.Condition} | {LiveReuseReport.Status(run)} | {LiveReuseReport.YesNo(run.Verified)} | {LiveReuseReport.Dash(run.FailedAttempts)} | {LiveReuseReport.Tried(run)} | "
                + $"{(trial.BlockRecords.Count == 0 ? "-" : string.Join(", ", trial.BlockRecords))} | {LiveReuseExperiment.Sequence(run.BlockStrategies, "-")} | {LiveReuseReport.YesNo(run.FollowedBlock)} | {LiveReuseReport.YesNo(trial.FollowedTransferredLesson)} | {run.ToolCalls} | {run.UnauthorizedRequests} | {run.Usage.ModelCalls} | {run.Usage.InputTokens} | {run.Usage.OutputTokens} |");
        }

        Line();
        Line("## Tokens and cost");
        Line();
        var learningUsage = result.Learning.Aggregate(UsageTotals.Zero, (sum, learning) => sum.Plus(learning.Run.Usage));
        var evaluationUsage = result.Trials.Aggregate(UsageTotals.Zero, (sum, trial) => sum.Plus(trial.Run.Usage));
        Line("| Phase | Model calls | Input tokens | Output tokens | Est. cost (USD) |");
        Line("| --- | ---: | ---: | ---: | ---: |");
        Line($"| Learning | {learningUsage.ModelCalls} | {learningUsage.InputTokens} | {learningUsage.OutputTokens} | {LiveReuseReport.Money(LiveReuseReport.Cost(d, learningUsage))} |");
        Line($"| Evaluation | {evaluationUsage.ModelCalls} | {evaluationUsage.InputTokens} | {evaluationUsage.OutputTokens} | {LiveReuseReport.Money(LiveReuseReport.Cost(d, evaluationUsage))} |");
        Line($"| **Total** | **{result.Total.ModelCalls}** | **{result.Total.InputTokens}** | **{result.Total.OutputTokens}** | **{LiveReuseReport.Money(LiveReuseReport.Cost(d, result.Total))}** |");
        Line();
        Line(d.InputUsdPerMillionTokens is { } input && d.OutputUsdPerMillionTokens is { } output
            ? $"Prices: USD {LiveReuseReport.Number(input)} per million input tokens and USD {LiveReuseReport.Number(output)} per million output tokens ({d.PriceSource}). An estimate from list prices, not a bill."
            : $"Cost not estimated: {d.PriceSource}.");
        Line(result.CallsWithoutUsage == 0
            ? "Every response reported its token usage."
            : $"{result.CallsWithoutUsage} response(s) reported no token usage; they count as zero above, so the token totals are a lower bound.");
        Line("Latency is in the raw results only: it is a property of one machine and network, and is never gated.");
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
        if (IsScripted(result))
        {
            Line("None: a scripted run writes no file. The command prints this report only.");
        }
        else
        {
            Line($"`{BaseName(result)}.json`, next to this file: every distractor, learning run and trial above. It holds no prompt, no block");
            Line("text, no message text and no key.");
        }

        return text.ToString();
    }

    private static string ConclusionSentence(TransferExperimentResult result) => result.Conclusion switch
    {
        TransferConclusion.TransferBenefitAttributableToContent =>
            "With the library's retrieval over the shared store, the model needed fewer failed attempts on unseen services than with memory disabled and than with the same records with the strategy withheld, by the pre-registered test, and the store without the same-cluster lesson gave it no such benefit.",
        TransferConclusion.TransferBenefitNotAttributableToContent =>
            "Memory-enabled passed the gate against memory-disabled, but either not against the placebo or the mismatched-trait control passed too, so the benefit cannot be credited to the transferred lesson. No transfer benefit is claimed.",
        TransferConclusion.TransferNoDemonstratedBenefit =>
            "The memory-enabled condition did not pass the pre-registered gate against memory-disabled. No transfer benefit is claimed.",
        TransferConclusion.TransferInconclusive =>
            "More instances were excluded than the pre-registration allows. No claim either way.",
        _ => "The run did not complete, so no verdict was evaluated. No claim either way.",
    };

    private static IEnumerable<string> Limitations(TransferExperimentResult result)
    {
        if (IsScripted(result))
        {
            yield return "This is a scripted run. The scripted operator follows the first strategy on the block's first working line by construction, so any benefit here is a property of the script and of where the library's retrieval ranked the same-cluster record.";
        }

        if (!IsScripted(result))
        {
            yield return "One model, one provider, one run, at temperature 0. Re-run before relying on a result.";
        }
        yield return $"{result.Design.EvaluationInstances} instances in {result.Design.Clusters} clusters. One learning record serves both instances of a cluster, so the pairs are not independent and the sign test's p-value is optimistic.";
        yield return "The trait is named in every task text (`cluster ...`) and by `describe_service`. Transfer here means finding and trusting a lesson keyed by a named trait, not discovering an unnamed one.";
        yield return "Retrieval is the in-memory candidate source's word matching, not PostgreSQL full-text or vector search; ranks under another store would differ. The task texts were written once, before the first offline run, and are not tuned to rank the same-cluster record first.";
        yield return "The working strategy reaches the block verbatim through the story 6.2 allowlist, as in the reuse experiment. A block can also name other strategies on failed attempts' Tried: lines and on other records; `Block named` and `Followed` read the first record's working line only.";
        yield return "The mismatched-trait control is weaker than it looks. Under the bijection, its block can show the strategies of the other clusters, which a capable model can rule out to find the answer by elimination; and its records are the other clusters' lessons, which a model that follows the block pays for. A control that passes the gate can only move the verdict to `TransferBenefitNotAttributableToContent`, a conservative error: it can withhold a claim, never make one. `Harm` above reports how often it cost attempts.";
        yield return "A synthetic task family with a simulated database; real tasks usually leave the answer partly inferable.";
        yield return "The repository is public, and the trait mapping is in its source. Check a model's training cutoff against the registration date.";
        if (!result.Complete)
        {
            yield return "The run stopped at a budget cap. Its partial numbers are reported for transparency and support no conclusion.";
        }
    }

    private static bool IsScripted(TransferExperimentResult result) => result.Descriptor.Provider == "scripted";
}

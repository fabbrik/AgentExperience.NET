using System.Globalization;
using AgentExperience.Abstractions;
using AgentExperience.CiTriage.GitHub;
using AgentExperience.CiTriage.Model;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Holdout;
using AgentExperience.Core.Verification;
using AgentExperience.MicrosoftAgentFramework;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.CiTriage;

/// <summary>A point on the replay's timeline.</summary>
public abstract record ReplayEvent(DateTimeOffset At, CiFailure Failure)
{
    /// <summary>On a tie a triage comes first, so a lesson is never visible at the very instant its fix lands.</summary>
    public abstract int Order { get; }
}

/// <summary>The agent triages the failure, at the time it failed.</summary>
public sealed record TriageEvent(DateTimeOffset At, CiFailure Failure) : ReplayEvent(At, Failure)
{
    public override int Order => 0;
}

/// <summary>The failure's real fix is known (its green run completed): its run is finalized Verified or Failed.</summary>
public sealed record FinalizeEvent(DateTimeOffset At, CiFailure Failure) : ReplayEvent(At, Failure)
{
    public override int Order => 1;
}

/// <summary>What one triage did: for checking time order, and for the report.</summary>
/// <param name="Failure">The failure.</param>
/// <param name="RunId">The library's captured run, or <see langword="null"/> when nothing was captured.</param>
/// <param name="NamedExperienceIds">The lessons injection named for it: injected (treatment) or withheld (holdout).</param>
/// <param name="Arm">treatment, holdout or none.</param>
/// <param name="Prediction">The parsed reply, or <see langword="null"/>.</param>
/// <param name="Correct">Whether it was right.</param>
public sealed record TriageRecord(CiFailure Failure, Guid? RunId, IReadOnlyList<Guid> NamedExperienceIds, string Arm, TriagePrediction? Prediction, bool Correct)
{
    /// <summary>The model call failed (a provider error): the run is never finalized, so the library counts it Pending.</summary>
    public bool Errored { get; init; }

    /// <summary>The finalized record's id, once its finalize event ran.</summary>
    public Guid? ExperienceId { get; set; }

    public FinalizationOutcome? Finalized { get; set; }

    /// <summary>Story 28.7: triaged by an earlier pass while its fix was pending, and finalized by this one from the run store.</summary>
    public bool Resumed { get; init; }
}

/// <summary>What a pass produced.</summary>
public sealed record ReplayResult(
    IReadOnlyList<LedgerRow> Rows,
    IReadOnlyList<TriageRecord> Triages,
    ExperienceBenefitEstimate Estimate,
    string? StoppedReason,
    int ModelCalls,
    long InputTokens,
    long OutputTokens,
    decimal CostUsd);

/// <summary>
/// The replay: one event queue on a simulated clock. Every failure whose real fix is known by now (label code or flaky)
/// gets a triage event at its own time and a finalize event at its green run's time; ambiguous, pending and unavailable
/// failures are written to the ledger as skipped. Events run in time order, so a lesson can only reach a failure that
/// happens after its fix was known. A budget cap stops triage cleanly; the finalize events of runs already triaged still
/// run, since they need no model.
/// </summary>
internal sealed class Replay(IGitHub gitHub, TriageHost host, TriageWorkspace workspace, MeteredChatClient metered, SimulatedClock clock)
{
    public const string CheckId = "triage-correct";
    public const string CheckKind = "HostCheck";
    public const string Producer = "ci-triage/green-run";
    public const int MaxErrorsInARow = 3;

    /// <summary>When set to a directory, each triage's conversation outline and final reply are written there (diagnostics; keep it out of the repository).</summary>
    public const string RepliesVariable = "AGENTEXPERIENCE_CITRIAGE_REPLIES";
    public const string BeforeResume = "before-resume";
    public const string FetchError = "fetch-error";
    public const string Stopped = "stopped";

    /// <summary>The note a pending failure's triage leaves when its run waits in the run store for its fix (story 28.7).</summary>
    public const string AwaitingFix = "awaiting fix: run ";

    /// <summary>
    /// Story 28.7: the storage keeps captured runs (SQLite's run store). A failure still waiting for its fix is triaged
    /// now and its run left unfinalized; a later pass, in a new process, finalizes that stored run once the fix lands,
    /// instead of triaging again.
    /// </summary>
    public bool KeepsRuns { get; init; }

    /// <summary>The run id an earlier pass left waiting for its fix, from its ledger note, or <see langword="null"/>.</summary>
    public static Guid? AwaitingRunId(LedgerRow row)
    {
        var at = row.Note.IndexOf(AwaitingFix, StringComparison.Ordinal);
        var start = at + AwaitingFix.Length;
        return at >= 0 && row.Note.Length >= start + 36 && Guid.TryParseExact(row.Note.AsSpan(start, 36), "D", out var id) ? id : null;
    }

    /// <summary>A prediction back from the ledger's spelling (<see cref="TriagePrediction.Describe"/>), or <see langword="null"/>.</summary>
    public static TriagePrediction? ParsePredicted(string predicted) => predicted switch
    {
        "flaky" => new TriagePrediction("flaky", []),
        _ when predicted.StartsWith("code:", StringComparison.Ordinal) =>
            new TriagePrediction("code", [.. predicted["code:".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries)]),
        _ => null,
    };

    /// <summary>The events for the triageable failures, in replay order.</summary>
    public static IReadOnlyList<ReplayEvent> Schedule(IEnumerable<CiFailure> failures)
    {
        var events = new List<ReplayEvent>();
        foreach (var failure in failures.Where(failure => failure.Label.Triageable))
        {
            var failedAt = failure.Run.CompletedAt;
            events.Add(new TriageEvent(failedAt, failure));
            // A green run created later can still finish before the failing run did: it is finalized no earlier than the triage.
            var greenAt = failure.Label.Green!.CompletedAt;
            events.Add(new FinalizeEvent(greenAt > failedAt ? greenAt : failedAt, failure));
        }

        return [.. events.OrderBy(e => e.At).ThenBy(e => e.Order).ThenBy(e => e.Failure.Run.Id)];
    }

    /// <param name="failures">Every labelled failure.</param>
    /// <param name="alreadyProcessed">Runs an earlier pass already handled.</param>
    /// <param name="resumeCutoff">
    /// In a resumed pass, the latest time an earlier pass finalized a lesson. A failure before it is skipped
    /// (<c>before-resume</c>): the store already holds lessons from after it, and retrieval cannot hide them.
    /// </param>
    /// <param name="rows">Filled as the pass runs, so whatever ran can be written even if the pass throws.</param>
    /// <param name="cancellationToken">Stops the pass.</param>
    /// <param name="earlierRows">The ledger of earlier passes: a run one left waiting for its fix (<see cref="KeepsRuns"/>) is finalized here once the fix lands.</param>
    public async Task<ReplayResult> RunAsync(
        IReadOnlyList<CiFailure> failures,
        IReadOnlySet<long> alreadyProcessed,
        DateTimeOffset? resumeCutoff,
        Dictionary<long, LedgerRow> rows,
        CancellationToken cancellationToken,
        IReadOnlyList<LedgerRow>? earlierRows = null)
    {
        var todo = new List<CiFailure>();
        var waiting = new List<CiFailure>();
        var triages = new Dictionary<long, TriageRecord>();
        var fixLanded = new List<CiFailure>();
        var awaiting = (earlierRows ?? [])
            .Where(row => !row.Final && AwaitingRunId(row) is not null)
            .ToDictionary(row => row.RunId);
        foreach (var failure in failures.Where(failure => !alreadyProcessed.Contains(failure.Run.Id)))
        {
            if (awaiting.TryGetValue(failure.Run.Id, out var earlierRow))
            {
                // Triaged by an earlier pass while its fix was pending; its run is in the run store.
                if (failure.Label.Triageable)
                {
                    var prediction = ParsePredicted(earlierRow.Predicted);
                    var correct = Scoring.IsCorrect(failure.Label, prediction);
                    rows[failure.Run.Id] = earlierRow with { Label = failure.Label.Name, Correct = correct ? "true" : "false" };
                    triages[failure.Run.Id] = new TriageRecord(failure, AwaitingRunId(earlierRow), [], earlierRow.Arm, prediction, correct) { Resumed = true };
                    fixLanded.Add(failure);
                }
                else
                {
                    rows[failure.Run.Id] = earlierRow;
                }

                continue;
            }

            if (KeepsRuns && failure.Label.Kind == LabelKind.Pending)
            {
                waiting.Add(failure);
            }
            else if (!failure.Label.Triageable)
            {
                rows[failure.Run.Id] = Skipped(failure, failure.Label.Name);
            }
            else if (resumeCutoff is { } cutoff && failure.Run.CompletedAt < cutoff)
            {
                rows[failure.Run.Id] = Skipped(failure, BeforeResume);
            }
            else
            {
                todo.Add(failure);
            }
        }

        var earlier = Accounted((await EstimateAsync(0, cancellationToken).ConfigureAwait(false)).Overall);

        string? stopped = null;
        var errorsInARow = 0;
        IEnumerable<ReplayEvent> events =
        [
            .. Schedule(todo),
            .. waiting.Select(failure => new TriageEvent(failure.Run.CompletedAt, failure)),
            .. fixLanded.Select(failure => new FinalizeEvent(failure.Label.Green!.CompletedAt, failure)),
        ];
        foreach (var replayEvent in events.OrderBy(e => e.At).ThenBy(e => e.Order).ThenBy(e => e.Failure.Run.Id))
        {
            clock.AdvanceTo(replayEvent.At);
            var id = replayEvent.Failure.Run.Id;
            if (replayEvent is TriageEvent)
            {
                if (stopped is not null)
                {
                    rows[id] = Skipped(replayEvent.Failure, Stopped);
                    continue;
                }

                try
                {
                    var (row, record) = await TriageAsync(replayEvent.Failure, cancellationToken).ConfigureAwait(false);
                    rows[id] = row;
                    if (record is not null)
                    {
                        triages[id] = record;

                        // A provider that keeps failing (a bad key, no credit) would otherwise fail every remaining
                        // triage. Only triages that called the model count; a skipped failure leaves the count alone.
                        errorsInARow = record.Errored ? errorsInARow + 1 : 0;
                        if (errorsInARow == MaxErrorsInARow)
                        {
                            stopped = $"{MaxErrorsInARow} triages in a row ended in a provider error.";
                        }
                    }
                }
                catch (BudgetExhaustedException ex)
                {
                    stopped = ex.Message;
                    rows[id] = Skipped(replayEvent.Failure, Stopped);
                }
            }
            else if (triages.TryGetValue(id, out var record) && record.RunId is { } runId && !record.Errored)
            {
                try
                {
                    var result = await FinalizeAsync(runId, record.Correct, replayEvent.Failure, cancellationToken).ConfigureAwait(false);
                    record.Finalized = result.Outcome;
                    record.ExperienceId = result.ExperienceId;
                    rows[id] = rows[id] with { Note = Join(rows[id].Note, "finalized " + result.Outcome.ToString()) };
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    rows[id] = rows[id] with { Note = Join(rows[id].Note, "finalize error: " + ex.GetType().Name) };
                }
            }
        }

        // Every captured run the holdout assigned, errored ones included (the library counts those Pending). A run an earlier
        // pass captured was counted then.
        var assigned = triages.Values.Count(record => !record.Resumed && record.RunId is not null && record.Arm is "treatment" or "holdout");
        var estimate = await EstimateAsync(earlier + assigned, cancellationToken).ConfigureAwait(false);
        return new ReplayResult([.. rows.Values], [.. triages.Values], estimate, stopped, metered.ModelCalls, metered.InputTokens, metered.OutputTokens, metered.Cost);
    }

    private async Task<(LedgerRow Row, TriageRecord? Record)> TriageAsync(CiFailure failure, CancellationToken cancellationToken)
    {
        var logs = new List<JobLog>();
        try
        {
            var jobs = (await gitHub.ListJobsAsync(failure.Run.Id, cancellationToken).ConfigureAwait(false)).Where(job => job.Failed).ToList();
            foreach (var job in jobs)
            {
                if (await gitHub.GetJobLogAsync(job.Id, cancellationToken).ConfigureAwait(false) is { } text)
                {
                    logs.Add(new JobLog(job.Name, text));
                }
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return (Skipped(failure, FetchError), null);
        }

        if (logs.Count == 0)
        {
            return (Skipped(failure, "no-log"), null);
        }

        var input = new TriageInput(failure.Run.Branch, failure.Run.HeadSha, [.. logs.Select(log => log.JobName)], LogTrimmer.Headlines(logs), LogTrimmer.Trim(logs));
        var costBefore = metered.Cost;
        var session = await host.Agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        host.LastInjection = null;
        metered.ResetObservation();
        workspace.FailSha = failure.Run.HeadSha;
        string? reply = null;
        string note = string.Empty;
        var nudged = false;
        try
        {
            var response = await host.Agent.RunAsync(TriageAgent.BuildPrompt(input), session, cancellationToken: cancellationToken).ConfigureAwait(false);
            reply = response.Text;

            // Some models (gpt-oss) end a tool-using turn with an empty message, or keep calling tools until the iteration cap.
            // Ask once for the answer, straight to the model with tools off. Not through the agent: a second agent invocation
            // would start a new captured run, leaving this run (the one the holdout assigned) never finalized.
            if (Scoring.Parse(reply) is null)
            {
                nudged = true;
                List<ChatMessage> conversation =
                [
                    new(ChatRole.System, TriageAgent.Instructions),
                    new(ChatRole.User, TriageAgent.BuildPrompt(input)),
                    .. response.Messages.Where(message => message.Contents.Count > 0),
                    new(ChatRole.User, TriageAgent.AnswerNudge),
                ];
                var answer = await metered.GetResponseAsync(conversation, new ChatOptions { ToolMode = ChatToolMode.None }, cancellationToken).ConfigureAwait(false);
                reply = answer.Text;
                response.Messages.Add(new ChatMessage(ChatRole.Assistant, "(asked again) " + reply));
            }

            if (Environment.GetEnvironmentVariable(RepliesVariable) is { Length: > 0 } dump)
            {
                Directory.CreateDirectory(dump);
                var lines = response.Messages.Select(m => m.Role + ": " + string.Join(" | ", m.Contents.Select(c => c.GetType().Name + ":" + (c is TextContent t ? t.Text : c is FunctionCallContent f ? f.Name : c is FunctionResultContent r ? (r.Result?.ToString() ?? "").Length + " chars" : ""))));
                File.WriteAllText(Path.Combine(dump, failure.Run.Id + ".txt"), string.Join("\n", lines) + "\n--- TEXT:\n" + reply);
            }
        }
        catch (BudgetExhaustedException)
        {
            throw;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            note = "error: " + ex.GetType().Name;
        }
        finally
        {
            workspace.FailSha = null;
        }

        Guid? runId = session.StateBag.TryGetValue<string>(ExperienceCaptureAgentBuilderExtensions.RunIdStateKey, out var runText) && Guid.TryParse(runText, out var parsed)
            ? parsed
            : null;
        var injection = host.LastInjection;
        var arm = injection?.Outcome switch
        {
            InjectionOutcome.Injected => "treatment",
            _ when injection?.WithheldExperienceIds.Count > 0 => "holdout",
            _ => "none",
        };
        IReadOnlyList<Guid> named = injection is null ? [] : [.. injection.InjectedExperienceIds, .. injection.WithheldExperienceIds];
        var prediction = Scoring.Parse(reply);
        if (prediction is null && note.Length == 0)
        {
            note = "parse failure";
        }

        if (nudged)
        {
            note = Join(note, "asked again for the answer");
        }

        if (runId is null)
        {
            note = Join(note, "not captured");
        }

        var errored = note.StartsWith("error:", StringComparison.Ordinal);
        var pending = !failure.Label.Triageable;
        var correct = !pending && Scoring.IsCorrect(failure.Label, prediction);
        if (pending && !errored && runId is { } waitingRun)
        {
            // Scored and finalized by a later pass, when the fix lands: the run waits in the run store.
            note = Join(note, AwaitingFix + waitingRun.ToString("D"));
        }

        // An errored or uncaptured run is never finalized, so the ledger gives it no arm and no score, as the library does.
        var row = new LedgerRow(
            failure.Run.Id,
            failure.Run.CompletedAt,
            failure.Run.Branch,
            failure.Label.Name,
            errored || runId is null ? "-" : arm,
            prediction?.Describe() ?? "unparsed",
            errored || pending ? "-" : correct ? "true" : "false",
            metered.Cost - costBefore,
            note);
        return (row, new TriageRecord(failure, runId, named, arm, prediction, correct) { Errored = errored });
    }

    /// <summary>
    /// Out-of-band finalization, as the finalization guide describes it: one required host check, evidence built by hand
    /// in a round closed now, at the green commit's revision. A right triage verifies and becomes a lesson; a wrong one
    /// is stored quarantined, never reused.
    /// </summary>
    private async Task<FinalizeExperienceResult> FinalizeAsync(Guid runId, bool correct, CiFailure failure, CancellationToken cancellationToken)
    {
        var revision = failure.Label.Green!.HeadSha;
        var round = new ClosedVerificationRound(Guid.NewGuid(), revision);
        var now = clock.GetUtcNow();
        var evidence = new Evidence(
            EvidenceId: Guid.NewGuid(),
            VerificationRoundId: round.RoundId,
            ArtifactRevision: revision,
            CheckId: CheckId,
            Kind: CheckKind,
            Result: correct ? CheckResult.Pass : CheckResult.Fail,
            Producer: Producer,
            Detail: "label " + failure.Label.Name + "; green run " + failure.Label.Green.Id.ToString(CultureInfo.InvariantCulture),
            CapturedAt: now);

        return await host.Services.GetRequiredService<ExperienceFinalizationService>().FinalizeAsync(
            new FinalizeExperienceRequest(
                RunId: runId,
                Authorization: host.Authorization,
                ClosedRound: round,
                RequiredChecks: [new RequiredCheck(CheckId, ExpectedKind: CheckKind)],
                Evidence: [evidence],
                CurrentArtifactRevision: revision,
                StorageDecision: StorageDecision.Permit,
                FinalizedAt: now),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The library's own estimate. Holdout assignments are written in the background with no public way to wait for
    /// them, so it is read again for up to five seconds until it accounts for every run the ledger saw assigned.
    /// </summary>
    private async Task<ExperienceBenefitEstimate> EstimateAsync(int assigned, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var estimate = await AgentExperienceBenefit.EstimateAsync(host.Services, host.Authorization, TriageHost.Scope, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (Accounted(estimate.Overall) >= assigned || DateTime.UtcNow >= deadline)
            {
                return estimate;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private static int Accounted(ExperienceBenefitComparison o) => o.Treatment.Runs + o.Holdout.Runs + o.Pending + o.Unverified + o.MixedArm;

    internal static LedgerRow Skipped(CiFailure failure, string reason) =>
        new(failure.Run.Id, failure.Run.CompletedAt, failure.Run.Branch, failure.Label.Name, "-", "skipped: " + reason, "-", 0m, string.Empty);

    private static string Join(string left, string right) => left.Length == 0 ? right : left + "; " + right;
}

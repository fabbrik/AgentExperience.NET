using System.Globalization;
using AgentExperience.Abstractions;
using AgentExperience.CiTriage.GitHub;
using AgentExperience.CiTriage.Model;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Holdout;
using AgentExperience.Core.Verification;
using AgentExperience.MicrosoftAgentFramework;
using AgentExperience.MicrosoftAgentFramework.Injection;
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
    /// <summary>The finalized record's id, once its finalize event ran.</summary>
    public Guid? ExperienceId { get; set; }

    public FinalizationOutcome? Finalized { get; set; }
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

    public async Task<ReplayResult> RunAsync(IReadOnlyList<CiFailure> failures, IReadOnlySet<long> alreadyProcessed, CancellationToken cancellationToken)
    {
        var todo = failures.Where(failure => !alreadyProcessed.Contains(failure.Run.Id)).ToList();
        var earlier = Accounted((await EstimateAsync(0, cancellationToken).ConfigureAwait(false)).Overall);
        var rows = new Dictionary<long, LedgerRow>();
        foreach (var failure in todo.Where(failure => !failure.Label.Triageable))
        {
            rows[failure.Run.Id] = Skipped(failure, failure.Label.Name);
        }

        var triages = new Dictionary<long, TriageRecord>();
        string? stopped = null;
        var errorsInARow = 0;
        foreach (var replayEvent in Schedule(todo))
        {
            clock.AdvanceTo(replayEvent.At);
            var id = replayEvent.Failure.Run.Id;
            if (replayEvent is TriageEvent)
            {
                if (stopped is not null)
                {
                    rows[id] = Skipped(replayEvent.Failure, "stopped");
                    continue;
                }

                try
                {
                    var (row, record) = await TriageAsync(replayEvent.Failure, cancellationToken).ConfigureAwait(false);
                    rows[id] = row;
                    if (record is not null)
                    {
                        triages[id] = record;
                    }

                    // A provider that keeps failing (a bad key, no credit) would otherwise fail every remaining triage.
                    errorsInARow = row.Note.StartsWith("error:", StringComparison.Ordinal) ? errorsInARow + 1 : 0;
                    if (errorsInARow == MaxErrorsInARow)
                    {
                        stopped = $"{MaxErrorsInARow} triages in a row ended in a provider error.";
                    }
                }
                catch (BudgetExhaustedException ex)
                {
                    stopped = ex.Message;
                    rows[id] = Skipped(replayEvent.Failure, "stopped");
                }
            }
            else if (triages.TryGetValue(id, out var record) && record.RunId is { } runId)
            {
                var result = await FinalizeAsync(runId, record.Correct, replayEvent.Failure, cancellationToken).ConfigureAwait(false);
                record.Finalized = result.Outcome;
                record.ExperienceId = result.ExperienceId;
                rows[id] = rows[id] with { Note = Join(rows[id].Note, "finalized " + result.Outcome.ToString()) };
            }
        }

        var assigned = rows.Values.Count(row => row.Arm is "treatment" or "holdout");
        var estimate = await EstimateAsync(earlier + assigned, cancellationToken).ConfigureAwait(false);
        return new ReplayResult([.. rows.Values], [.. triages.Values], estimate, stopped, metered.ModelCalls, metered.InputTokens, metered.OutputTokens, metered.Cost);
    }

    private async Task<(LedgerRow Row, TriageRecord? Record)> TriageAsync(CiFailure failure, CancellationToken cancellationToken)
    {
        var jobs = (await gitHub.ListJobsAsync(failure.Run.Id, cancellationToken).ConfigureAwait(false)).Where(job => job.Failed).ToList();
        var logs = new List<JobLog>();
        foreach (var job in jobs)
        {
            if (await gitHub.GetJobLogAsync(job.Id, cancellationToken).ConfigureAwait(false) is { } text)
            {
                logs.Add(new JobLog(job.Name, text));
            }
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
        try
        {
            var response = await host.Agent.RunAsync(TriageAgent.BuildPrompt(input), session, cancellationToken: cancellationToken).ConfigureAwait(false);
            reply = response.Text;
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

        if (runId is null)
        {
            note = Join(note, "not captured");
        }

        var correct = Scoring.IsCorrect(failure.Label, prediction);
        var row = new LedgerRow(
            failure.Run.Id,
            failure.Run.CompletedAt,
            failure.Run.Branch,
            failure.Label.Name,
            arm,
            prediction?.Describe() ?? "unparsed",
            correct ? "true" : "false",
            metered.Cost - costBefore,
            note);
        return (row, new TriageRecord(failure, runId, named, arm, prediction, correct));
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

    private static LedgerRow Skipped(CiFailure failure, string reason) =>
        new(failure.Run.Id, failure.Run.CompletedAt, failure.Run.Branch, failure.Label.Name, "-", "skipped: " + reason, "-", 0m, string.Empty);

    private static string Join(string left, string right) => left.Length == 0 ? right : left + "; " + right;
}

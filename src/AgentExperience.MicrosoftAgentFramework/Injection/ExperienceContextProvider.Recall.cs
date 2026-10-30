using AgentExperience.Abstractions;
using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>Recall at the moment of failure: the part of the provider that answers a failed tool call (see <see cref="ExperienceRecallOptions"/>).</summary>
public sealed partial class ExperienceContextProvider
{
    /// <summary>How many records one signature search may return, before resolution, session and eligibility filters.</summary>
    internal const int RecallCandidateLimit = 20;

    /// <summary>The fixed failure reason when the failed call's context carries no chat options to put the hint in.</summary>
    internal const string NoInstructionsReason =
        "The failed call's function invocation context carries no chat options, so a hint could not reach the next model call.";

    /// <summary>The omission detail of a borrowed record whose grant withholds the approach a hint would name.</summary>
    internal const string RecallApproachWithheldDetail =
        "The grant this record is read through withholds its approach, so it gives no hint.";

    /// <summary>The omission detail of a record whose signature was not followed by a call that worked.</summary>
    internal const string UnresolvedDetail =
        "The record holds this failure, but no call in its verified run worked after it.";

    /// <summary>The reason recall reports, once, when it has no signature source.</summary>
    internal const string NoSignatureSourceReason =
        "Recall is configured, but there is no failure signature source: set ExperienceRecallOptions.SignatureSource, or use a record store that implements IExperienceSignatureSource. Recall does nothing.";

    private static readonly ExperienceSignatureMatch[] ExactOnly = [ExperienceSignatureMatch.Exact];

    private static readonly ExperienceSignatureMatch[] ExactThenCoarse = [ExperienceSignatureMatch.Exact, ExperienceSignatureMatch.Coarse];

    private readonly ExperienceRecallOptions? _recall;

    /// <summary>The signature lookup recall runs, or <see langword="null"/> when recall is off or has no source.</summary>
    private readonly ExperienceSignatureRetrievalService? _signatureLookup;

    /// <summary>Set once the missing signature source has been reported.</summary>
    private int _recallUnavailableReported;

    /// <summary>The recall configuration, or <see langword="null"/> when recall is off. For tests.</summary>
    internal ExperienceRecallOptions? RecallOptions => _recall;

    /// <summary>
    /// Sets recall up for the invocation <paramref name="agent"/> is running, when recall is configured and this agent's
    /// own capture captures it; with no signature source, reports that once instead.
    /// </summary>
    private void BeginRecall(AIAgent? agent, AgentSession? session, RetrieveExperienceRequest request, CapabilityGate? gate)
    {
        if (_recall is null || CaptureScope.Current is not { } capture || !capture.Captures(agent))
        {
            return;
        }

        if (_signatureLookup is null)
        {
            if (Interlocked.Exchange(ref _recallUnavailableReported, 1) == 0)
            {
                capture.ReportRecall(NoSignatureSourceReason);
            }

            return;
        }

        try
        {
            capture.Recall = new RecallRun(this, request, gate, session, capture.RunId);
        }
        catch (Exception)
        {
            // Recall is an addition: setting it up never costs the invocation its block.
        }
    }

    /// <summary>Notes that the block delivered <paramref name="experienceIds"/> into the run, so no hint repeats them.</summary>
    private void NoteBlockDelivered(AIAgent? agent, IReadOnlyList<Guid> experienceIds)
    {
        if (experienceIds.Count > 0 && CaptureScope.Current is { } capture && capture.Captures(agent) && capture.Recall is { } run && ReferenceEquals(run.Provider, this))
        {
            run.Note(experienceIds);
        }
    }

    /// <summary>
    /// Answers one failed tool call of <paramref name="run"/>: looks its signature up, and puts a hint in
    /// <paramref name="options"/>' instructions, or withholds it, or gives none. Never throws: a timeout, a refusal or a
    /// failure gives no hint, and so does the caller's cancellation.
    /// </summary>
    /// <param name="run">The run's recall state.</param>
    /// <param name="capture">The invocation's capture scope.</param>
    /// <param name="options">The chat options the function-invoking client hands its next model call.</param>
    /// <param name="toolName">The failed call's tool name, as capture records it.</param>
    /// <param name="error">The failed call's error, as capture records it.</param>
    /// <param name="cancellationToken">The function invocation's token.</param>
    internal async Task RecallAsync(RecallRun run, CaptureScope capture, ChatOptions? options, string toolName, string error, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || _recall is not { } recall || _signatureLookup is not { } lookup)
        {
            return;
        }

        var trace = InjectionDiagnostics.Start(InjectionDiagnostics.Recall);
        try
        {
            InjectionDiagnostics.Tag(trace, InjectionDiagnostics.CorrelationIdAttribute, run.Request.CorrelationId);
            var result = await RecallCoreAsync(run, capture, options, toolName, error, recall, lookup, cancellationToken).ConfigureAwait(false);
            InjectionDiagnostics.Tag(trace, InjectionDiagnostics.SignatureMatchAttribute, result.Match?.ToString());
            InjectionDiagnostics.Tag(trace, InjectionDiagnostics.RecordCountAttribute, result.ExperienceIds.Count + result.WithheldExperienceIds.Count);
            try
            {
                recall.OnRecall?.Invoke(result);
            }
            catch (Exception)
            {
                // Reporting is diagnostics.
            }
            finally
            {
                InjectionDiagnostics.Succeeded(trace, result.Outcome.ToString());
            }
        }
        catch (Exception ex)
        {
            // Only the caller's cancellation reaches here; it ends this lookup, and the call's own outcome stands.
            InjectionDiagnostics.Faulted(trace, ex, cancellationToken);
        }
        finally
        {
            InjectionDiagnostics.Closed(trace);
            trace.Activity?.Dispose();
        }
    }

    private async Task<ExperienceRecallResult> RecallCoreAsync(
        RecallRun run,
        CaptureScope capture,
        ChatOptions? options,
        string toolName,
        string error,
        ExperienceRecallOptions recall,
        ExperienceSignatureRetrievalService lookup,
        CancellationToken cancellationToken)
    {
        var request = run.Request;
        var signature = ExperienceFailureSignature.From(toolName, error);
        ExperienceRecallResult Result(ExperienceRecallOutcome outcome) =>
            new(run.RunId, signature, outcome, NoIds) { CorrelationId = request.CorrelationId };

        if (signature is null)
        {
            return Result(ExperienceRecallOutcome.NoMatch);
        }

        if (!run.TryClaim(signature.Key, recall.MaxHintsPerRun))
        {
            return Result(ExperienceRecallOutcome.Bounded);
        }

        if (options is null)
        {
            return Result(ExperienceRecallOutcome.Failed) with { Failure = new InjectionFailure(NoInstructionsReason, Exception: null) };
        }

        // One bound for the whole lookup: the signature search, then the final eligibility re-read with what is left.
        var started = _options.TimeProvider.GetTimestamp();
        TimeSpan Remaining() => recall.LookupTimeout - _options.TimeProvider.GetElapsedTime(started);

        var omitted = new List<OmittedExperience>();
        var candidates = new List<ExperienceSignatureCandidate>();
        var seen = new HashSet<Guid>();
        foreach (var match in recall.AllowCoarseMatch ? ExactThenCoarse : ExactOnly)
        {
            var read = await ReadWithinAsync(
                    token => lookup.FindAsync(
                        new SignatureRetrievalRequest(request.Authorization, request.Scope, signature)
                        {
                            Match = match,
                            Limit = RecallCandidateLimit,
                            ExcludeModelAuthored = request.ExcludeModelAuthored,
                            CorrelationId = request.CorrelationId,
                        },
                        token),
                    Remaining,
                    cancellationToken)
                .ConfigureAwait(false);
            if (read.TimedOut)
            {
                return Result(ExperienceRecallOutcome.TimedOut) with
                {
                    Failure = new InjectionFailure($"The recall lookup exceeded its {recall.LookupTimeout} bound, so no hint was given.", Exception: null),
                };
            }

            if (read.Value is not { Outcome: SignatureRetrievalOutcome.Completed } found)
            {
                return Result(ExperienceRecallOutcome.Failed) with
                {
                    Failure = new InjectionFailure(
                        $"The signature lookup ended {read.Value?.Outcome.ToString() ?? "with no result"}, so no hint was given.",
                        read.Value?.Failure),
                };
            }

            foreach (var candidate in found.Candidates)
            {
                if (candidate?.Record is not { } record || !seen.Add(record.ExperienceId))
                {
                    continue;
                }

                if (candidate.Resolution != ExperienceSignatureResolution.Resolved)
                {
                    omitted.Add(new OmittedExperience(record.ExperienceId, InjectionOmissionReason.Ineligible, UnresolvedDetail));
                    continue;
                }

                candidates.Add(candidate);
            }

            // Exact first: a coarse search is asked only when nothing exact resolves.
            if (candidates.Count > 0)
            {
                break;
            }
        }

        if (candidates.Count == 0)
        {
            return Result(ExperienceRecallOutcome.NoMatch) with { Omitted = omitted };
        }

        var matched = candidates[0].Match;
        var session = OpenRecallSession(run.Session, out var unreadable);
        if (unreadable)
        {
            return Result(ExperienceRecallOutcome.Failed) with
            {
                Omitted = omitted,
                Failure = new InjectionFailure(UnreadableSessionState(_stateKey), Exception: null),
            };
        }

        // The block's own selection rules, before the limit: what the run or the session already holds, a lesson the
        // holdout measured as hurting (when acting on measured benefit), a model-authored record the host excluded, a
        // borrowed record whose grant withholds the approach, and one the capability gate keeps out.
        var benefit = _benefit?.Current(request.Authorization, request.Scope, _options.Benefit.PerModel ? capture.Model : null);
        var selected = new List<RankedExperience>(recall.MaxRecordsPerHint);
        foreach (var candidate in candidates)
        {
            var record = candidate.Record;
            if (benefit is not null && benefit.Harmful(record.ExperienceId))
            {
                omitted.Add(new OmittedExperience(
                    record.ExperienceId,
                    InjectionOmissionReason.MeasuredHarmful,
                    benefit.ScopeHurts ? ScopeMeasuredHarmfulDetail : MeasuredHarmfulDetail));
                continue;
            }

            if (run.Holds(record.ExperienceId)
                || (session?.ActiveFor(record.ExperienceId) is { Confirmed: true } delivered && delivered.Revision >= record.Revision))
            {
                omitted.Add(new OmittedExperience(record.ExperienceId, InjectionOmissionReason.AlreadyDelivered, AlreadyDeliveredDetail));
                continue;
            }

            if (_excludeModelAuthored && _retrieval.IsModelAuthored(record))
            {
                omitted.Add(new OmittedExperience(record.ExperienceId, AuthorshipOmission(record.Reflection)));
                continue;
            }

            if (candidate.SharedByGrant && !HistoricalReferenceWriter.ShowsApproach(candidate.GrantDisclosure))
            {
                omitted.Add(new OmittedExperience(record.ExperienceId, InjectionOmissionReason.Ineligible, RecallApproachWithheldDetail));
                continue;
            }

            if (run.Gate is { } gate && Gated(gate, record, candidate.SharedByGrant, candidate.GrantDisclosure) is { } gated)
            {
                omitted.Add(new OmittedExperience(record.ExperienceId, gated));
                continue;
            }

            if (selected.Count < recall.MaxRecordsPerHint)
            {
                selected.Add(new RankedExperience(record, double.NaN, [], candidate.SharedByGrant, candidate.PermittingGrantId, candidate.GrantDisclosure)
                {
                    MatchedGrantDisclosure = candidate.SharedByGrant ? candidate.GrantDisclosure : null,
                });
                continue;
            }

            omitted.Add(new OmittedExperience(record.ExperienceId, InjectionOmissionReason.OverRecordLimit, $"Beyond the limit of {recall.MaxRecordsPerHint} records per hint."));
        }

        if (selected.Count == 0)
        {
            return Result(ExperienceRecallOutcome.NoMatch) with { Match = matched, Omitted = omitted };
        }

        // The block's final eligibility check, within what is left of the lookup's bound.
        var left = Remaining();
        if (left <= TimeSpan.Zero)
        {
            return Result(ExperienceRecallOutcome.TimedOut) with
            {
                Omitted = omitted,
                Failure = new InjectionFailure($"The recall lookup exceeded its {recall.LookupTimeout} bound, so no hint was given.", Exception: null),
            };
        }

        var checkedOutcome = await CheckAsync(request, selected, [], omitted, session: null, run.Gate, cancellationToken, left).ConfigureAwait(false);
        if (checkedOutcome.Failure is { } checkFailure)
        {
            return Result(Remaining() <= TimeSpan.Zero ? ExperienceRecallOutcome.TimedOut : ExperienceRecallOutcome.Failed) with
            {
                Omitted = omitted,
                Failure = checkFailure,
            };
        }

        // The re-read decides the grant level: a borrowed record now read through a level that withholds the approach gives none.
        var hintable = new List<RankedExperience>(checkedOutcome.Injectable.Count);
        foreach (var ranked in checkedOutcome.Injectable)
        {
            if (ranked.SharedByGrant && !HistoricalReferenceWriter.ShowsApproach(ranked.GrantDisclosure))
            {
                omitted.Add(new OmittedExperience(ranked.Record.ExperienceId, InjectionOmissionReason.Ineligible, RecallApproachWithheldDetail));
                continue;
            }

            hintable.Add(ranked);
        }

        var hint = HistoricalReferenceWriter.RecallHint(
            hintable,
            toolName,
            error,
            matched,
            _approachArguments,
            _retrieval.IsContentConfirmed,
            _options.FailureDetail,
            recall.MaxHintBytes,
            out var included,
            out var borrowedArgumentsShown,
            out var overBudget);
        foreach (var experienceId in overBudget)
        {
            omitted.Add(new OmittedExperience(experienceId, InjectionOmissionReason.OverByteBudget, $"The record did not fit the hint's {recall.MaxHintBytes}-byte budget."));
        }

        if (hint is null)
        {
            return Result(ExperienceRecallOutcome.NoMatch) with { Match = matched, Omitted = omitted };
        }

        var exposures = included
            .Select(experienceId => new RunExposure(experienceId, hintable.First(ranked => ranked.Record.ExperienceId == experienceId).Record.Revision))
            .ToList();
        var bytes = System.Text.Encoding.UTF8.GetByteCount(hint);

        // The same arm as the block: a pure function of the run ID, so a run is never in two arms.
        var arm = _options.Holdout.Active ? HoldoutAssignmentFunction.Assign(run.RunId, _options.Holdout.Salt, _options.Holdout.Rate) : (HoldoutArm?)null;
        if (arm == HoldoutArm.Holdout)
        {
            run.Note(included);
            StartRecordingHoldout(request, new HoldoutAssignment(run.RunId, HoldoutArm.Holdout, exposures, _options.TimeProvider.GetUtcNow()));
            return Result(ExperienceRecallOutcome.Withheld) with { Match = matched, Omitted = omitted, WithheldExperienceIds = included };
        }

        // Charged to the session before the hint is handed over: a hint the account does not know about would be neither
        // charged nor withdrawn later. If it cannot be charged, nothing is given.
        if (_options.SessionLimits is { } limits && run.Session is { } agentSession)
        {
            var charge = await ChargeSessionAsync(agentSession, limits, hintable, included, borrowedArgumentsShown, bytes, cancellationToken).ConfigureAwait(false);
            if (charge is not null)
            {
                return charge.Value
                    ? Result(ExperienceRecallOutcome.Bounded) with { Match = matched, Omitted = omitted }
                    : Result(ExperienceRecallOutcome.Failed) with
                    {
                        Match = matched,
                        Omitted = omitted,
                        Failure = new InjectionFailure("Charging the hint to the session's injection state failed, so no hint was given.", Exception: null),
                    };
            }
        }

        run.Deliver(options, hint, included);

        // Exactly as the block's: the run's exposure, the lessons view's injection record, and the treatment arm's assignment.
        try
        {
            capture.RecordExposures(exposures);
        }
        catch (Exception)
        {
            // Exposure is bookkeeping for later evidence.
        }

        StartRecordingInjection(request, included);
        if (arm == HoldoutArm.Treatment)
        {
            StartRecordingHoldout(request, new HoldoutAssignment(run.RunId, HoldoutArm.Treatment, exposures, _options.TimeProvider.GetUtcNow()));
        }

        return Result(ExperienceRecallOutcome.Hinted) with
        {
            ExperienceIds = included,
            Match = matched,
            Omitted = omitted,
            HintBytes = bytes,
        };
    }

    /// <summary>The session's account as every decision reads it, or <see langword="null"/> without tracking or a session.</summary>
    private InjectionSessionState? OpenRecallSession(AgentSession? agentSession, out bool unreadable)
    {
        unreadable = false;
        if (_options.SessionLimits is null || agentSession is null)
        {
            return null;
        }

        try
        {
            if (!InjectionSessionState.TryLoad(agentSession.StateBag, _stateKey, out var state))
            {
                unreadable = true;
                return null;
            }

            return state.ForDecisions();
        }
        catch (Exception)
        {
            unreadable = true;
            return null;
        }
    }

    /// <summary>
    /// Charges the hint to the session's account under the session's lock: <see langword="null"/> when charged,
    /// <see langword="true"/> when the budget cannot take it (nothing is charged), <see langword="false"/> when the account
    /// could not be read or written.
    /// </summary>
    private async Task<bool?> ChargeSessionAsync(
        AgentSession agentSession,
        ExperienceInjectionSessionLimits limits,
        List<RankedExperience> hintable,
        List<Guid> included,
        HashSet<Guid> borrowedArgumentsShown,
        int bytes,
        CancellationToken cancellationToken)
    {
        var gate = LockFor(agentSession);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!InjectionSessionState.TryLoad(agentSession.StateBag, _stateKey, out var stored))
            {
                return false;
            }

            var decisions = stored.ForDecisions();
            var remainingRecords = Math.Min(
                (long)limits.MaxRecords - decisions.RecordsUsed,
                ExperienceInjectionSessionLimits.MaxTrackedRecords - decisions.Delivered.Count);
            if (remainingRecords < included.Count || limits.MaxBytes - decisions.BytesUsed < bytes)
            {
                return true;
            }

            var delivered = included
                .Select(experienceId =>
                {
                    var ranked = hintable.First(candidate => candidate.Record.ExperienceId == experienceId);
                    return new DeliveredRecord(
                        experienceId,
                        ranked.Record.Revision,
                        ApproachByGrant: ranked.SharedByGrant && HistoricalReferenceWriter.ShowsApproach(ranked.GrantDisclosure),
                        Withdrawn: false)
                    {
                        ArgumentsGrantId = borrowedArgumentsShown.Contains(experienceId) ? ranked.PermittingGrantId ?? Guid.Empty : null,
                    };
                })
                .ToList();
            stored.ChargeUnsure(new PendingDelivery(Guid.NewGuid(), bytes, delivered, [])
            {
                StagedAt = _options.TimeProvider.GetUtcNow(),
            }).Save(agentSession.StateBag, _stateKey);
            return null;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>
/// One invocation's recall state, kept on its capture scope: the request the provider resolved for it, and what recall has
/// already done in it. Thread-safe: a function-invoking client may run tools concurrently.
/// </summary>
internal sealed class RecallRun(
    ExperienceContextProvider provider,
    RetrieveExperienceRequest request,
    CapabilityGate? gate,
    AgentSession? session,
    Guid runId)
{
    private readonly object _gate = new();
    private readonly HashSet<string> _signatures = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _held = [];
    private int _looked;

    /// <summary>The provider that set this run up.</summary>
    public ExperienceContextProvider Provider { get; } = provider;

    /// <summary>The request the provider resolved for the invocation: its authorization, scope and correlation ID.</summary>
    public RetrieveExperienceRequest Request { get; } = request;

    /// <summary>The capability gate the invocation runs under, or <see langword="null"/>.</summary>
    public CapabilityGate? Gate { get; } = gate;

    /// <summary>The caller's session, or <see langword="null"/>.</summary>
    public AgentSession? Session { get; } = session;

    /// <summary>The captured run.</summary>
    public Guid RunId { get; } = runId;

    /// <summary>The hints delivered so far, in order. For tests.</summary>
    public List<string> Delivered { get; } = [];

    /// <summary>Answers one failed call; never throws.</summary>
    public Task OnFailureAsync(CaptureScope capture, FunctionInvocationContext context, string toolName, string error, CancellationToken cancellationToken)
    {
        try
        {
            return Provider.RecallAsync(this, capture, context?.Options, toolName, error, cancellationToken);
        }
        catch (Exception)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>Claims a lookup of <paramref name="key"/>: refused when it was looked up already or the run's bound is reached.</summary>
    public bool TryClaim(string key, int maxPerRun)
    {
        lock (_gate)
        {
            if (_looked >= maxPerRun || !_signatures.Add(key))
            {
                return false;
            }

            _looked++;
            return true;
        }
    }

    /// <summary>Whether the run was already given (or, held out, would have been given) <paramref name="experienceId"/>.</summary>
    public bool Holds(Guid experienceId)
    {
        lock (_gate)
        {
            return _held.Contains(experienceId);
        }
    }

    /// <summary>Notes records the run was given, or would have been.</summary>
    public void Note(IEnumerable<Guid> experienceIds)
    {
        lock (_gate)
        {
            _held.UnionWith(experienceIds);
        }
    }

    /// <summary>Appends <paramref name="hint"/> to the instructions of the run's next model call, and notes its records.</summary>
    public void Deliver(ChatOptions options, string hint, IEnumerable<Guid> experienceIds)
    {
        lock (_gate)
        {
            options.Instructions = string.IsNullOrEmpty(options.Instructions) ? hint : options.Instructions + "\n\n" + hint;
            _held.UnionWith(experienceIds);
            Delivered.Add(hint);
        }
    }
}

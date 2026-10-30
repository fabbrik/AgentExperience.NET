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

    /// <summary>
    /// How many lookups one run may start, as a multiple of <see cref="ExperienceRecallOptions.MaxHintsPerRun"/>: lookups that
    /// time out or fail release their signature, so this caps how hard a run can lean on a slow or failing store.
    /// </summary>
    internal const int LookupCapMultiplier = 4;

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
            ExperienceRecallResult result;
            try
            {
                result = await RecallCoreAsync(run, capture, options, toolName, error, recall, lookup, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                // Anything else that escaped the lookup is a failed recall, reported as one: no hint, the call's own outcome stands.
                result = new ExperienceRecallResult(run.RunId, SignatureOf(toolName, error), ExperienceRecallOutcome.Failed, NoIds)
                {
                    CorrelationId = run.Request.CorrelationId,
                    Failure = new InjectionFailure($"Recall threw {ex.GetType().FullName}, so no hint was given.", ex),
                };
            }

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
            // The caller's cancellation (or a throwing OnRecall reporting path, which is caught above): the lookup ends, and the
            // call's own outcome stands. Every other exception was reported above as a Failed recall.
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
        var signature = SignatureOf(toolName, error);
        if (signature is null)
        {
            return new ExperienceRecallResult(run.RunId, null, ExperienceRecallOutcome.NoMatch, NoIds) { CorrelationId = run.Request.CorrelationId };
        }

        if (!run.TryClaim(signature.Key, recall.MaxHintsPerRun, recall.MaxHintsPerRun * LookupCapMultiplier))
        {
            return new ExperienceRecallResult(run.RunId, signature, ExperienceRecallOutcome.Bounded, NoIds) { CorrelationId = run.Request.CorrelationId };
        }

        ExperienceRecallResult result;
        try
        {
            result = await LookUpAsync(run, capture, options, toolName, error, signature, recall, lookup, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            run.Release(signature.Key);
            throw;
        }

        // A lookup that timed out or failed says nothing about the failure: the signature may be looked up again.
        if (result.Outcome is ExperienceRecallOutcome.TimedOut or ExperienceRecallOutcome.Failed)
        {
            run.Release(signature.Key);
        }

        return result;
    }

    /// <summary>The failure's signature, or <see langword="null"/> when it has none (or deriving it throws).</summary>
    private static ExperienceFailureSignature? SignatureOf(string toolName, string error)
    {
        try
        {
            return ExperienceFailureSignature.From(toolName, error);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<ExperienceRecallResult> LookUpAsync(
        RecallRun run,
        CaptureScope capture,
        ChatOptions? options,
        string toolName,
        string error,
        ExperienceFailureSignature signature,
        ExperienceRecallOptions recall,
        ExperienceSignatureRetrievalService lookup,
        CancellationToken cancellationToken)
    {
        var request = run.Request;
        ExperienceRecallResult Result(ExperienceRecallOutcome outcome) =>
            new(run.RunId, signature, outcome, NoIds) { CorrelationId = request.CorrelationId };

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
        // coarse match under the match floor, a borrowed record whose grant withholds the approach, and one the capability gate keeps out.
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

            // The match floor (story 28.5): a hint is a signature match, with no text or semantic match, so only an exact one
            // passes.
            if (_minimumMatch is not null && candidate.Match != ExperienceSignatureMatch.Exact)
            {
                omitted.Add(new OmittedExperience(record.ExperienceId, InjectionOmissionReason.WeakMatch, ExperienceMatchFloor.CoarseSignatureDetail));
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
            out var overBudget,
            _recipe);
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

        // Reserved atomically: a call failing in parallel cannot hint (or charge) the same record, and the run's answer
        // bound is taken here, by what is actually answered, never by a lookup.
        switch (run.TryReserve(included, recall.MaxHintsPerRun))
        {
            case RecallReservation.Bounded:
                return Result(ExperienceRecallOutcome.Bounded) with { Match = matched, Omitted = omitted };
            case RecallReservation.Held:
                omitted.AddRange(included.Select(experienceId => new OmittedExperience(experienceId, InjectionOmissionReason.AlreadyDelivered, AlreadyDeliveredDetail)));
                return Result(ExperienceRecallOutcome.NoMatch) with { Match = matched, Omitted = omitted };
        }

        // The same arm as the block: a pure function of the run ID, so a run is never in two arms. Drawn here, used only
        // once the budget has been read.
        var arm = _options.Holdout.Active ? HoldoutAssignmentFunction.Assign(run.RunId, _options.Holdout.Salt, _options.Holdout.Rate) : (HoldoutArm?)null;
        var answered = false;
        try
        {
            // The session's budget, read before the arm is drawn, so both arms are bounded alike: a hint the budget cannot
            // take is Bounded in either arm and assigns nothing.
            if (_options.SessionLimits is { } budget && session is not null && !Fits(session, budget, included.Count, bytes))
            {
                return Result(ExperienceRecallOutcome.Bounded) with { Match = matched, Omitted = omitted };
            }

            if (arm == HoldoutArm.Holdout)
            {
                answered = true;
                StartRecordingHoldout(request, new HoldoutAssignment(run.RunId, HoldoutArm.Holdout, exposures, _options.TimeProvider.GetUtcNow()));
                return Result(ExperienceRecallOutcome.Withheld) with { Match = matched, Omitted = omitted, WithheldExperienceIds = included };
            }

            // Charged to the session before the hint is handed over: a hint the account does not know about would be
            // neither charged nor withdrawn later. If it cannot be charged, nothing is given.
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

            run.Deliver(options, hint);
            answered = true;
        }
        finally
        {
            if (!answered)
            {
                run.Unreserve(included);
            }
        }

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

    /// <summary>Whether the session's account, as decisions read it, can take <paramref name="records"/> more records of <paramref name="bytes"/> bytes.</summary>
    private static bool Fits(InjectionSessionState state, ExperienceInjectionSessionLimits limits, int records, int bytes) =>
        Math.Min((long)limits.MaxRecords - state.RecordsUsed, ExperienceInjectionSessionLimits.MaxTrackedRecords - state.Delivered.Count) >= records
        && limits.MaxBytes - state.BytesUsed >= bytes;

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

            if (!Fits(stored.ForDecisions(), limits, included.Count, bytes))
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
    private int _answered;

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

    /// <summary>
    /// Claims a lookup of <paramref name="key"/>: refused when it is claimed already, when the run has answered
    /// <paramref name="maxAnswers"/> failures, or when it has started <paramref name="maxLookups"/> lookups.
    /// </summary>
    public bool TryClaim(string key, int maxAnswers, int maxLookups)
    {
        lock (_gate)
        {
            if (_answered >= maxAnswers || _looked >= maxLookups || !_signatures.Add(key))
            {
                return false;
            }

            _looked++;
            return true;
        }
    }

    /// <summary>Releases a claimed signature whose lookup timed out or failed, so a later failure may look it up again.</summary>
    public void Release(string key)
    {
        lock (_gate)
        {
            _signatures.Remove(key);
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

    /// <summary>
    /// Reserves <paramref name="experienceIds"/> for one answer, atomically: <see cref="RecallReservation.Held"/> when any is
    /// already held or reserved, <see cref="RecallReservation.Bounded"/> when the run has answered
    /// <paramref name="maxAnswers"/> failures; otherwise they are held and the answer is counted.
    /// </summary>
    public RecallReservation TryReserve(IReadOnlyList<Guid> experienceIds, int maxAnswers)
    {
        lock (_gate)
        {
            if (experienceIds.Any(_held.Contains))
            {
                return RecallReservation.Held;
            }

            if (_answered >= maxAnswers)
            {
                return RecallReservation.Bounded;
            }

            _held.UnionWith(experienceIds);
            _answered++;
            return RecallReservation.Reserved;
        }
    }

    /// <summary>Gives back a reservation whose answer was not given (a charge that failed or did not fit).</summary>
    public void Unreserve(IEnumerable<Guid> experienceIds)
    {
        lock (_gate)
        {
            _held.ExceptWith(experienceIds);
            _answered--;
        }
    }

    /// <summary>Appends <paramref name="hint"/> to the instructions of the run's next model call.</summary>
    public void Deliver(ChatOptions options, string hint)
    {
        lock (_gate)
        {
            options.Instructions = string.IsNullOrEmpty(options.Instructions) ? hint : options.Instructions + "\n\n" + hint;
        }
    }
}

/// <summary>What <see cref="RecallRun.TryReserve"/> answered.</summary>
internal enum RecallReservation
{
    /// <summary>Reserved: the records are held for this answer.</summary>
    Reserved,

    /// <summary>A record is already held by the run.</summary>
    Held,

    /// <summary>The run has answered as many failures as it may.</summary>
    Bounded,
}

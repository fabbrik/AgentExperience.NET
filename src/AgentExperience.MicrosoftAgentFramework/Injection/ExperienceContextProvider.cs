using AgentExperience.Abstractions;
using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>
/// The MAF context provider that closes the learning loop: before an invocation runs, it retrieves
/// the experience that applies to it, re-checks each candidate's eligibility, asks the host's risk
/// policy, and injects what survives as one delimited, labeled Historical Reference message.
/// </summary>
/// <remarks>
/// <para>
/// <b>How to wire it.</b> This is a "simple tier" <see cref="AIContextProvider"/>: it overrides only
/// <see cref="ProvideAIContextAsync"/> and lets MAF do the merging and message-source stamping. Add
/// it to an agent yourself, through <see cref="ChatClientAgentOptions.AIContextProviders"/> --
/// <see cref="ExperienceCaptureAgentBuilderExtensions.UseExperienceCapture(Microsoft.Agents.AI.AIAgentBuilder, AgentExperience.Core.Capture.IExperienceCaptureService, ExperienceCaptureOptions)"/> never constructs those
/// options, so capture and injection are configured separately and either can be used without the
/// other.
/// </para>
/// <para>
/// <b>What happens on each invocation.</b> The resolver turns the invocation into a
/// <see cref="RetrieveExperienceRequest"/>; retrieval ranks what is eligible and bounded by its own
/// timeout; the top <see cref="ExperienceInjectionLimits.MaxRecords"/> are re-read together, in one
/// batched read through the record store; the host's <see cref="ExperienceInjectionOptions.DecideInjection"/> is
/// asked about each survivor; and <see cref="HistoricalReferenceWriter"/> renders the rest inside the
/// byte budget. Every record that falls out at any of those steps is reported with its reason.
/// </para>
/// <para>
/// <b>The final eligibility check.</b> Retrieval and injection are not the same instant, and a record
/// can be revoked, re-scoped, re-scored, aged out, or lose readability in between. Each selected
/// candidate is therefore re-read immediately before the payload is built, and the version that is
/// rendered is the version that was just read. The re-read record is then put through the <em>same
/// eligibility rules retrieval applies</em> -- eligible status, the policy's reuse-confidence floor,
/// the policy's <see cref="RetrievalPolicy.MaxAge"/>, and the request's required environment
/// attributes -- and anything that now fails one is omitted as
/// <see cref="InjectionOmissionReason.Ineligible"/> with the rule named. A record that can no longer
/// be read in the request's scope is omitted as <see cref="InjectionOmissionReason.Unreadable"/>,
/// which deliberately does not distinguish "deleted" from "not yours" -- and, since the re-read goes
/// through the same grant-aware store rule retrieval used, a record shared by an
/// <see cref="ExperienceGrant"/> that has since expired or been revoked falls out here exactly like
/// one that was deleted. The re-read is one
/// <see cref="IExperienceRecordStore.GetManyAsync"/> call for every selected candidate, so a store that
/// implements it with one statement (the PostgreSQL adapter does) costs one round trip on the
/// invocation's critical path however many records are selected; each record is still answered, and
/// audited, exactly as its own <see cref="IExperienceRecordStore.GetAsync(AuthorizationContext, Scope, Guid, ExperienceReadOptions, CancellationToken)"/>
/// would answer it. A store that keeps the port's default reads them one at a time. The whole check is
/// bounded by <see cref="ExperienceInjectionLimits.EligibilityCheckTimeout"/>, because it sits on the
/// invocation's critical path and retrieval's own timeout has already been spent.
/// </para>
/// <para>
/// <b>A reused session is tracked.</b> MAF's <see cref="ChatClientAgent"/> keeps an invocation's request
/// messages, this provider's block included, in the session's chat history, so every later turn of that
/// session shows the model every earlier block too. With
/// <see cref="ExperienceInjectionOptions.SessionLimits"/> set (the default), the provider keeps a small
/// account of what it gave the session in the session's <see cref="AgentSession.StateBag"/>, under
/// <see cref="SessionStateKey"/>, and it survives MAF's session serialization like any other state. It
/// uses that account to bound the conversation (records and bytes across invocations, reported as
/// <see cref="InjectionOutcome.SessionBudgetExhausted"/> and
/// <see cref="InjectionOmissionReason.OverSessionBudget"/>), to never inject a record revision the
/// session already holds (<see cref="InjectionOmissionReason.AlreadyDelivered"/>), and to withdraw what no
/// longer stands: every record the session holds is re-read on every invocation, in one
/// <see cref="IExperienceRecordStore.GetManyAsync"/> call with <see cref="ExperienceReadPurpose.ScopeCheck"/>
/// (nothing is handed over, so nothing is audited as a delivery), and one that is no longer readable in
/// scope, no longer in an eligible status, below the confidence floor, past the maximum age, or read
/// through a grant that now withholds an approach the session was shown, is named in a fixed withdrawal
/// notice ahead of any new record (<see cref="ExperienceInjectionResult.RetractedExperienceIds"/>).
/// </para>
/// <para>
/// <b>What tracking cannot do is reach backwards.</b> The earlier block is still in the session's history,
/// verbatim, and a model that read it cannot be made to forget it: the notice is advisory, like every
/// other word in the block. MAF filters this provider's input to external messages, so it cannot reliably
/// see, let alone strip, its own earlier blocks, and it does not claim to. Where that matters, use a fresh
/// session per task, or a chat-history provider that drops earlier injected blocks -- and then turn session
/// tracking off, because its deduplication assumes the session keeps what was injected.
/// </para>
/// <para>
/// <b>A delivery is charged when MAF says it happened.</b> The block's delivery is staged in the session
/// state when it is handed to MAF, and committed in <see cref="InvokedCoreAsync"/> once MAF reports the
/// invocation succeeded. A failed invocation discards it -- MAF keeps no history for it, so its records
/// are not deduplicated against a block the session never kept, and its withdrawal notices stay owed. A
/// stage nothing settled, such as a stream its consumer abandoned before MAF reported, is committed at the
/// session's next invocation: when unsure, the session is charged, its records are tracked so they can still
/// be withdrawn but are not deduplicated against, and its withdrawal notices stay owed. The account is only as trustworthy as
/// the host's session storage, and invocations that run concurrently on one session race on it.
/// </para>
/// <para>
/// <b>Labeling is not a control.</b> The injected block says it is untrusted reference material, and
/// that wording is hygiene. The authorization boundary around tools and policy is what actually
/// stops an unauthorized call, it lives entirely outside this provider, and injected text that tells
/// a model to call something it may not call changes nothing about it.
/// </para>
/// <para>
/// <b>It never throws into an invocation.</b> A throwing resolver, a failing or timing-out
/// retrieval, a store that is down, and a throwing host callback all yield no context and a reported
/// result; the agent runs normally with nothing injected and nothing fabricated. The single
/// exception is cancellation of the caller's own token, which propagates unwrapped -- that is the
/// invocation ending, not a failure inside the provider.
/// </para>
/// <para>
/// <b>It never writes.</b> Nothing on this path mutates a record, its status, or its confidence. A
/// host denial is recorded on the result only.
/// </para>
/// </remarks>
public sealed class ExperienceContextProvider : AIContextProvider
{
    /// <summary>
    /// The <see cref="ChatMessage.AdditionalProperties"/> key stamped on the injected message, set to
    /// <see langword="true"/>. It lets a host or a test find the injected block without matching on
    /// its text, and is metadata only -- it confers no trust on the content.
    /// </summary>
    public const string HistoricalReferenceKey = "AgentExperience.HistoricalReference";

    /// <summary>
    /// The single <see cref="AgentSession.StateBag"/> key session tracking keeps its account under, and the
    /// provider's one entry in <see cref="StateKeys"/>. Its value is a small JSON object of counters and
    /// record IDs with revisions -- never record content. Removing it resets the session's budget and
    /// forgets what the session was given, so withdrawal notices for it are no longer delivered.
    /// </summary>
    public const string SessionStateKey = "AgentExperience.InjectionSession";

    /// <summary>
    /// The <see cref="ChatMessage.AdditionalProperties"/> key that ties an injected message to the delivery
    /// its invocation staged in the session state, so that only that invocation settles it. Metadata only.
    /// </summary>
    internal const string StageKey = "AgentExperience.HistoricalReference.Stage";

    private static readonly IReadOnlyList<string> SessionStateKeys = [SessionStateKey];

    private static readonly IReadOnlyList<Guid> NoIds = [];

    private static readonly IReadOnlyList<OmittedExperience> NoOmissions = [];

    private static readonly IReadOnlyList<ExcludedExperience> NoExclusions = [];

    private readonly ExperienceRetrievalService _retrieval;
    private readonly IExperienceRecordStore _store;
    private readonly ExperienceInjectionOptions _options;
    private readonly ApproachArgumentAllowlist _approachArguments;

    /// <summary>
    /// Creates a provider over Core's retrieval service, the record store its final eligibility check
    /// re-reads through, and the host's configuration.
    /// </summary>
    /// <param name="retrieval">Core's retrieval service. It owns eligibility, ranking, and the retrieval timeout.</param>
    /// <param name="store">The record store each selected candidate is re-read through, in the request's own authorization and scope.</param>
    /// <param name="options">Host configuration: the resolver, the limits, the risk decision, and the result callback.</param>
    /// <exception cref="ArgumentNullException">Any argument, or <see cref="ExperienceInjectionOptions.ResolveRequest"/>, <see cref="ExperienceInjectionOptions.Limits"/>, <see cref="ExperienceInjectionOptions.TimeProvider"/> or <see cref="ExperienceInjectionOptions.ApproachArguments"/>, is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><see cref="ExperienceInjectionOptions.ApproachArguments"/> is malformed; see its remarks.</exception>
    public ExperienceContextProvider(
        ExperienceRetrievalService retrieval,
        IExperienceRecordStore store,
        ExperienceInjectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(retrieval);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        var approachArguments = options.Validate(nameof(options));

        _retrieval = retrieval;
        _store = store;
        _options = options;
        _approachArguments = approachArguments;
    }

    /// <summary>
    /// Retrieves, re-checks, and injects the Historical Reference for one invocation, or injects
    /// nothing and reports why.
    /// </summary>
    /// <param name="context">The invocation MAF is about to run, including the messages assembled for it so far.</param>
    /// <param name="cancellationToken">Cancels the operation. Caller cancellation propagates unwrapped; nothing else escapes this method.</param>
    /// <returns>An <see cref="AIContext"/> carrying one Historical Reference message, or an empty one.</returns>
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        // The one span this adapter opens. It wraps the injection decision only -- never the agent's
        // own RunAsync/RunStreamingAsync delegation, which MAF instruments itself and which this
        // library deliberately adds nothing to.
        var trace = InjectionDiagnostics.Start();
        try
        {
            return await InjectAsync(context, trace, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Only the invocation's own cancellation escapes the body below; everything else is already
            // a reported InjectionOutcome. Whatever arrives, it propagates unchanged.
            InjectionDiagnostics.Faulted(trace, ex, cancellationToken);
            throw;
        }
        finally
        {
            // "Report is the one place inject is counted" is enforced here rather than assumed. An exit
            // that reported nothing -- a future early return added to the body below -- would otherwise
            // leave a span with no outcome, no count, and no duration, which is a silent hole in the one
            // operation an operator alerts on. Closing it is a no-op for every path that did report.
            InjectionDiagnostics.Closed(trace);

            // Restores the caller's Activity.Current exactly as it was, so the invocation MAF is about
            // to run sees the parent it would have seen with no instrumentation at all.
            trace.Activity?.Dispose();
        }
    }

    /// <summary>
    /// The body of <see cref="ProvideAIContextAsync"/>, unchanged by instrumentation beyond threading
    /// <paramref name="trace"/> to the single place every outcome is reported from.
    /// </summary>
    /// <param name="context">The invocation MAF is about to run.</param>
    /// <param name="trace">This injection's span and start timestamp.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    private async ValueTask<AIContext> InjectAsync(
        InvokingContext context,
        InjectionTrace trace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        RetrieveExperienceRequest? request;
        try
        {
            request = _options.ResolveRequest(new ExperienceInjectionContext(
                context.AIContext.Messages as IReadOnlyList<ChatMessage> ?? context.AIContext.Messages?.ToArray() ?? [],
                context.Session,
                context.Agent));
        }
        catch (Exception ex)
        {
            return Nothing(
                trace,
                InjectionOutcome.Failed,
                NoOmissions,
                retrieved: null,
                correlationId: null,
                new InjectionFailure("The injection request resolver threw.", ex));
        }

        if (request is null)
        {
            // The host opted this invocation out. Not a failure, and nothing to report beyond that.
            return Nothing(trace, InjectionOutcome.Skipped, NoOmissions, retrieved: null, correlationId: null, failure: null);
        }

        // From the request, not from a result: the host's correlation identifier is then on the span for
        // every outcome below -- a retrieval that timed out, one that was denied, one that threw -- and
        // not only for the ones that produced a result to read it back off.
        InjectionDiagnostics.Tag(trace, InjectionDiagnostics.CorrelationIdAttribute, request.CorrelationId);

        // The session's account, when tracking is on and there is a session to keep it in. A state that
        // does not read is neither trusted nor overwritten: nothing is injected, because the provider can
        // no longer tell what the session was given, what it may still be given, or what it is owed.
        SessionTracker? session;
        try
        {
            session = OpenSession(context.Session, out var unreadable);
            if (unreadable)
            {
                return Nothing(
                    trace,
                    InjectionOutcome.Failed,
                    NoOmissions,
                    retrieved: null,
                    request.CorrelationId,
                    new InjectionFailure(UnreadableSessionState, Exception: null));
            }
        }
        catch (Exception ex)
        {
            return Nothing(
                trace,
                InjectionOutcome.Failed,
                NoOmissions,
                retrieved: null,
                request.CorrelationId,
                new InjectionFailure($"Reading the session's injection state threw {ex.GetType().FullName}, so nothing was injected.", ex));
        }

        // Why there are no candidates, when there are none: a budget already spent (retrieval is then not
        // run at all), or a retrieval that did not complete. Neither stops the withdrawal check below,
        // which needs only the request's authorization and scope.
        ExperienceRetrievalResult? retrieved = null;
        InjectionOutcome? stopped = null;
        InjectionFailure? stoppedFailure = null;
        if (session is { Exhausted: true })
        {
            stopped = InjectionOutcome.SessionBudgetExhausted;
        }
        else
        {
            try
            {
                retrieved = await _retrieval.RetrieveAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The invocation itself is being cancelled. That is not a provider failure, and swallowing
                // it here would hide the cancellation from the run that asked for it.
                throw;
            }
            catch (Exception ex)
            {
                stopped = InjectionOutcome.RetrievalFailed;
                stoppedFailure = new InjectionFailure($"Retrieval threw {ex.GetType().FullName}.", ex);
            }

            if (retrieved is { Outcome: not RetrievalOutcome.Completed })
            {
                stopped = retrieved.Outcome switch
                {
                    RetrievalOutcome.TimedOut => InjectionOutcome.RetrievalTimedOut,
                    RetrievalOutcome.Denied => InjectionOutcome.RetrievalDenied,
                    _ => InjectionOutcome.RetrievalFailed,
                };
                stoppedFailure = retrieved.Failure is { } failure ? new InjectionFailure(failure.Reason, failure.Exception) : null;
            }
        }

        var correlationId = retrieved?.CorrelationId ?? request.CorrelationId;
        var omitted = new List<OmittedExperience>();
        var selected = stopped is null && retrieved is not null ? Select(retrieved.Records, omitted, session) : [];

        // Every record the session holds and has not withdrawn, other than those re-read as candidates
        // anyway: their answer there decides them too.
        var recheck = session is null
            ? []
            : session.State.Active.Where(entry => !selected.Exists(candidate => candidate.Record.ExperienceId == entry.ExperienceId)).ToList();

        if (selected.Count == 0 && recheck.Count == 0)
        {
            return Nothing(trace, stopped ?? Quiet(omitted), omitted, retrieved, correlationId, stoppedFailure, session);
        }

        CheckOutcome recheckOutcome;
        try
        {
            recheckOutcome = await CheckAsync(request, selected, recheck, omitted, session, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Nothing below the per-record handling is expected to throw; if it somehow does, the
            // invocation still runs, with no context at all rather than a partly checked one.
            return Nothing(
                trace,
                InjectionOutcome.Failed,
                omitted,
                retrieved,
                correlationId,
                new InjectionFailure($"The final eligibility check threw {ex.GetType().FullName}.", ex),
                session);
        }

        if (recheckOutcome.Failure is { } checkFailure)
        {
            // The check ran out of time, or the withdrawal check could not be made. Nothing is injected
            // rather than injecting the part of it that had been re-checked before it stopped.
            return Nothing(trace, InjectionOutcome.Failed, omitted, retrieved, correlationId, checkFailure, session);
        }

        if (recheckOutcome.Injectable.Count == 0 && recheckOutcome.Withdrawn.Count == 0)
        {
            return Nothing(trace, stopped ?? Quiet(omitted), omitted, retrieved, correlationId, stoppedFailure, session);
        }

        HistoricalReferencePayload payload;
        try
        {
            payload = HistoricalReferenceWriter.Write(
                recheckOutcome.Injectable,
                _options.Limits,
                _approachArguments,
                recheckOutcome.Withdrawn,
                session?.RemainingBytes);
        }
        catch (Exception ex)
        {
            return Nothing(
                trace,
                InjectionOutcome.Failed,
                omitted,
                retrieved,
                correlationId,
                new InjectionFailure($"Building the Historical Reference threw {ex.GetType().FullName}.", ex),
                session);
        }

        omitted.AddRange(payload.Omitted);

        if (payload.IsEmpty)
        {
            return Nothing(trace, stopped ?? Quiet(omitted), omitted, retrieved, correlationId, stoppedFailure, session);
        }

        if (session is not null)
        {
            // Staged before the block is handed over, and saved: a block the account does not know about
            // would be neither deduplicated, nor charged, nor withdrawn later. If it cannot be saved,
            // nothing is injected.
            try
            {
                session.Stage(Delivery(payload, recheckOutcome.Injectable));
                session.Save();
            }
            catch (Exception ex)
            {
                return Nothing(
                    trace,
                    InjectionOutcome.Failed,
                    omitted,
                    retrieved,
                    correlationId,
                    new InjectionFailure($"Writing the session's injection state threw {ex.GetType().FullName}, so nothing was injected.", ex));
            }
        }

        var injectedRecords = payload.ExperienceIds.Count > 0;

        // A user-role message, not a system one: the block is reference material the model may read,
        // never an instruction from the host. MAF merges it with the invocation's own messages. Built
        // before the report so that nothing which could throw remains after the span has been closed.
        var properties = new AdditionalPropertiesDictionary
        {
            [HistoricalReferenceKey] = true,
        };

        if (session?.State.Pending is { } staged)
        {
            properties[StageKey] = staged.Stage.ToString("N", System.Globalization.CultureInfo.InvariantCulture);
        }

        var injected = new AIContext
        {
            Messages =
            [
                new ChatMessage(ChatRole.User, payload.Text)
                {
                    AdditionalProperties = properties,
                },
            ],
        };

        Report(trace, new ExperienceInjectionResult(
            injectedRecords ? InjectionOutcome.Injected : InjectionOutcome.Retracted,
            payload.ExperienceIds,
            omitted,
            retrieved?.Excluded ?? NoExclusions,
            retrieved?.Truncated ?? false,
            retrieved?.EnvironmentUnrestricted ?? false,
            payload.ByteCount,
            correlationId,
            // A block of withdrawal notices alone still carries why no record came with it.
            Failure: injectedRecords ? null : stoppedFailure,
            retrieved?.VectorFallback)
        {
            RetractedExperienceIds = payload.RetractedExperienceIds,
            Session = session?.Usage(),
        });

        return injected;
    }

    /// <summary>The content-free failure reason for a session state that does not read.</summary>
    internal const string UnreadableSessionState =
        "The session's injection state could not be read, so nothing was injected. It is left as it is; remove the '"
        + SessionStateKey + "' state bag key to reset it.";

    /// <summary>
    /// Why nothing was injected when every step ran: the session's budget could not take the records that
    /// survived, or nothing survived at all.
    /// </summary>
    private static InjectionOutcome Quiet(List<OmittedExperience> omitted) =>
        omitted.Exists(omission => omission.Reason == InjectionOmissionReason.OverSessionBudget)
            ? InjectionOutcome.SessionBudgetExhausted
            : InjectionOutcome.NothingToInject;

    /// <summary>
    /// The session's account for this invocation, or <see langword="null"/> when tracking is off or there
    /// is no session. A stage an earlier invocation left unsettled is committed first.
    /// </summary>
    private SessionTracker? OpenSession(AgentSession? agentSession, out bool unreadable)
    {
        unreadable = false;
        if (_options.SessionLimits is not { } limits || agentSession is null)
        {
            return null;
        }

        if (!InjectionSessionState.TryLoad(agentSession.StateBag, out var state, out var absent))
        {
            unreadable = true;
            return null;
        }

        if (absent)
        {
            // Written on first use, even with nothing to inject, so that later invocations find the key
            // and never have to tell "absent" from "present as another type" again.
            return new SessionTracker(agentSession, state, limits, dirty: true);
        }

        // An earlier invocation's stage that MAF never settled -- a stream abandoned before it reported,
        // say. The block was handed over, so the session is charged for it: when unsure, charge. But its
        // withdrawal notices stay owed: MAF keeps no history for an abandoned stream, so a notice marked
        // delivered here could be lost for good. When unsure, withdraw again.
        return new SessionTracker(agentSession, state.Commit(settled: false), limits, dirty: state.Pending is not null);
    }

    /// <summary>
    /// A read-only copy of a store's owner allowlist, or <see langword="null"/> when there is none or it cannot be
    /// read -- which shows no borrowed value.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>>? FrozenGrantArguments(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? stored)
    {
        if (stored is null)
        {
            return null;
        }

        try
        {
            var copy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var (toolName, keys) in stored)
            {
                if (toolName is null || keys is null || !copy.TryAdd(toolName, Array.AsReadOnly(keys.ToArray())))
                {
                    return null;
                }
            }

            return new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<string>>(copy);
        }
#pragma warning disable CA1031 // A custom store's collection that throws names no key; it must not fail the block.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>What this invocation's block gives the session, as it will be tracked.</summary>
    private static PendingDelivery Delivery(HistoricalReferencePayload payload, List<RankedExperience> injectable)
    {
        var delivered = new List<DeliveredRecord>(payload.ExperienceIds.Count);
        foreach (var experienceId in payload.ExperienceIds)
        {
            var ranked = injectable.Find(candidate => candidate.Record.ExperienceId == experienceId)!;
            delivered.Add(new DeliveredRecord(
                experienceId,
                ranked.Record.Revision,
                ApproachByGrant: ranked.SharedByGrant && HistoricalReferenceWriter.ShowsApproach(ranked.GrantDisclosure),
                Withdrawn: false)
            {
                // The grant whose owner allowlist the block applied, so a later read through any other grant --
                // even one at the same level, whose allowlist may be narrower -- withdraws what was shown.
                // Guid.Empty stands for a store that named no grant, and matches only a later read that names none.
                // Only a delivery whose line actually showed a borrowed value is tracked this way.
                ArgumentsGrantId = payload.BorrowedArgumentsShown.Contains(experienceId)
                    ? ranked.PermittingGrantId ?? Guid.Empty
                    : null,
            });
        }

        return new PendingDelivery(Guid.NewGuid(), payload.ByteCount, delivered, payload.RetractedExperienceIds);
    }

    /// <summary>Whether <paramref name="messages"/> holds the block that staged <paramref name="stage"/>.</summary>
    private static bool Carries(IEnumerable<ChatMessage>? messages, Guid stage)
    {
        if (messages is null)
        {
            return false;
        }

        var text = stage.ToString("N", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var message in messages)
        {
            if (message?.AdditionalProperties is { } properties
                && properties.TryGetValue(StageKey, out var value)
                && string.Equals(value?.ToString(), text, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Settles the delivery this invocation staged: charged when MAF reports success, discarded when it
    /// reports a failure. Never throws into the invocation.
    /// </summary>
    /// <param name="context">How the invocation ended.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the stage is settled.</returns>
    protected override async ValueTask InvokedCoreAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        await base.InvokedCoreAsync(context, cancellationToken).ConfigureAwait(false);

        if (_options.SessionLimits is null || context?.Session is not { } agentSession)
        {
            return;
        }

        try
        {
            if (!InjectionSessionState.TryLoad(agentSession.StateBag, out var state)
                || state.Pending is not { } pending
                || !Carries(context.RequestMessages, pending.Stage))
            {
                // Nothing staged, a stage this invocation's request did not carry -- another invocation's,
                // which only that one may settle -- or a state that does not read, which the next
                // invocation reports.
                return;
            }

            (context.InvokeException is null ? state.Commit() : state.Discard()).Save(agentSession.StateBag);
        }
        catch (Exception)
        {
            // Settling is bookkeeping. An unsettled stage is committed by the next invocation.
        }
    }

    /// <summary>The one state bag key this provider uses: <see cref="SessionStateKey"/>.</summary>
    public override IReadOnlyList<string> StateKeys => SessionStateKeys;

    /// <summary>
    /// Takes the top <see cref="ExperienceInjectionLimits.MaxRecords"/> in rank order and records the
    /// rest, so the final eligibility check only ever re-reads records that could actually be injected.
    /// With session tracking, a revision the session already holds takes no slot, and the session's
    /// remaining record budget caps the selection too.
    /// </summary>
    private List<RankedExperience> Select(IReadOnlyList<RankedExperience> ranked, List<OmittedExperience> omitted, SessionTracker? session)
    {
        var sessionRemaining = session?.RemainingRecords ?? int.MaxValue;
        var limit = Math.Min(_options.Limits.MaxRecords, sessionRemaining);
        var selected = new List<RankedExperience>(Math.Min(ranked.Count, limit));

        for (var index = 0; index < ranked.Count; index++)
        {
            var candidate = ranked[index];

            if (candidate?.Record is null)
            {
                // Unreachable with Core's retrieval service, which never ranks a null. Accounted for
                // anyway rather than silently dropped -- with Guid.Empty, because there is no ID to
                // report -- so "every record that falls out is reported" stays literally true.
                omitted.Add(new OmittedExperience(
                    Guid.Empty,
                    InjectionOmissionReason.Unreadable,
                    $"The candidate ranked {index + 1} of {ranked.Count} carried no record and could not be identified."));
                continue;
            }

            // A revision the session already holds is in its conversation already. Decided on the ranked
            // revision here, so it takes no slot, and again on the re-read one.
            if (session?.State.ActiveFor(candidate.Record.ExperienceId) is { Confirmed: true } delivered
                && delivered.Revision >= candidate.Record.Revision)
            {
                omitted.Add(new OmittedExperience(candidate.Record.ExperienceId, InjectionOmissionReason.AlreadyDelivered, AlreadyDeliveredDetail));
                continue;
            }

            // Counted by what has actually been selected, not by rank index: a skip above must not
            // silently cost a slot that a later record could have filled.
            if (selected.Count < limit)
            {
                selected.Add(candidate);
                continue;
            }

            omitted.Add(sessionRemaining < _options.Limits.MaxRecords
                ? new OmittedExperience(
                    candidate.Record.ExperienceId,
                    InjectionOmissionReason.OverSessionBudget,
                    $"Ranked {index + 1} of {ranked.Count}, beyond the {sessionRemaining} record deliveries the session's budget has left.")
                : new OmittedExperience(
                    candidate.Record.ExperienceId,
                    InjectionOmissionReason.OverRecordLimit,
                    $"Ranked {index + 1} of {ranked.Count}, beyond the limit of {limit} records."));
        }

        return selected;
    }

    /// <summary>
    /// The final gate, run immediately before the payload is built: re-read every candidate in one
    /// batched read in the request's own authorization and scope, re-apply every eligibility rule
    /// retrieval applies to each, drop anything that now fails one, and ask the host about what is left.
    /// The re-read record replaces the retrieved one, so what is rendered is what was just checked. The
    /// read and the per-record checks after it are bounded together by
    /// <see cref="ExperienceInjectionLimits.EligibilityCheckTimeout"/>.
    /// </summary>
    /// <remarks>
    /// With session tracking, it also decides which records the session holds no longer stand: those among
    /// the candidates by their own re-read, and the rest by a second batched read, with
    /// <see cref="ExperienceReadPurpose.ScopeCheck"/>, inside the same bound. That read failing fails the
    /// check: no new record is injected while the provider cannot tell whether an earlier one still stands.
    /// </remarks>
    private async Task<CheckOutcome> CheckAsync(
        RetrieveExperienceRequest request,
        List<RankedExperience> selected,
        List<DeliveredRecord> recheck,
        List<OmittedExperience> omitted,
        SessionTracker? session,
        CancellationToken cancellationToken)
    {
        var injectable = new List<RankedExperience>(selected.Count);
        var withdrawn = new HashSet<Guid>();
        var policy = _retrieval.Policy;
        var now = _options.TimeProvider.GetUtcNow();
        var required = request.RequiredEnvironmentAttributes;
        var unrestricted = required is null or { Count: 0 };

        var timeout = _options.Limits.EligibilityCheckTimeout;
        var started = _options.TimeProvider.GetTimestamp();
        using var expiry = new CancellationTokenSource(timeout, _options.TimeProvider);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);

        // The bound, as a fact about the clock rather than about a timer. The token above is what a store
        // is handed, and it only flips when its timer callback has actually run -- which, on a starved
        // thread pool, can be well after the deadline. The checks between records must not depend on that,
        // so they compare the elapsed time as well.
        bool Expired() => expiry.IsCancellationRequested || _options.TimeProvider.GetElapsedTime(started) >= timeout;

        var ids = new Guid[selected.Count];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = selected[i].Record.ExperienceId;
        }

        // One scoped batch read for every selected candidate (story 5.6, KL-1), rather than one read per
        // candidate. Each position is answered exactly as the single read would answer it -- same
        // authorization, scope, grant, disclosure and tombstone rules, same access rows -- so everything
        // below is the per-record check it always was, applied to the batch's per-record results.
        var readOptions = new ExperienceReadOptions(ExperienceReadPurpose.Delivery, request.CorrelationId);
        ExperienceRecordGetManyResult? batch = null;
        var perRecord = false;
        try
        {
            // A delivery, and named: this re-read is what actually hands the records to the model, so an
            // access log records it, and the request's correlation ID ties each row to the invocation it
            // was injected into. With no candidate -- only records the session holds to re-check -- there
            // is nothing to deliver and no read.
            if (ids.Length > 0)
            {
                batch = await _store
                    .GetManyAsync(request.Authorization, request.Scope, ids, readOptions, bounded.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Rethrown against the caller's own token, not the linked one the store was handed,
            // so a caller inspecting the exception sees the token it actually cancelled.
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested)
        {
            return CheckOutcome.TimedOut(_options.Limits.EligibilityCheckTimeout);
        }
        catch (Exception)
        {
            // A batch fails as a whole, but the reads it stood for need not: a store whose single reads
            // fail for some records and not others would otherwise lose every record for one bad one.
            // So a batch that throws falls back to the pre-5.6 loop -- one read per candidate, inside the
            // same bound -- and each record is then omitted, or not, exactly as it was before batching.
            perRecord = true;
        }

        for (var index = 0; index < selected.Count; index++)
        {
            var candidate = selected[index];
            var experienceId = ids[index];

            // The session's delivery of this record that still stands, if any: a newer revision was
            // selected, and this re-read decides whether the delivered one is withdrawn as well.
            var delivered = session?.State.ActiveFor(experienceId);

            ExperienceRecordGetResult? result;
            if (perRecord)
            {
                try
                {
                    result = await _store
                        .GetAsync(request.Authorization, request.Scope, experienceId, readOptions, bounded.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw;
                }
                catch (OperationCanceledException) when (expiry.IsCancellationRequested)
                {
                    return CheckOutcome.TimedOut(_options.Limits.EligibilityCheckTimeout);
                }
                catch (Exception ex)
                {
                    // Fail-closed, per record: a record that could not be re-checked is not injected. A
                    // failed read says nothing about the record itself, so a delivery of it is not
                    // withdrawn on this: it is re-checked on the next invocation.
                    omitted.Add(new OmittedExperience(
                        experienceId,
                        InjectionOmissionReason.Unreadable,
                        $"Re-reading the record threw {ex.GetType().FullName}."));
                    continue;
                }
            }
            else
            {
                // A request-wide refusal (Denied, Invalid) answers every position the way a per-record
                // refusal did, and a store that returned fewer results than it was asked for has not
                // answered the rest: all of them are unreadable below.
                result = batch is { Outcome: ExperienceStoreOutcome.Found, Results: { } results } && index < results.Count
                    ? results[index]
                    : null;
            }

            // The per-record loop's next read failed on a cancelled token; with one read there is no next
            // read, so the caller's cancellation is honoured here instead of deciding more records for an
            // invocation that is already over.
            cancellationToken.ThrowIfCancellationRequested();

            if (Expired())
            {
                // A store that ignores the token still has to stop the check here, or the bound would only
                // ever apply to one that honours it. Checked for every record, as the per-record loop
                // checked after every read, so time the host's own decision callback spends still counts.
                return CheckOutcome.TimedOut(timeout);
            }

            // Denied, NotFound, Invalid, a null record, a record that came back under another ID, and a
            // record outside the requested scope are all one thing here: not readable in this scope.
            // So is Deleted -- a record erased since retrieval returned it. InjectionOmissionReason has
            // no erased member and Unreadable is already the terminal one (the record is omitted and
            // never injected), so a tombstone deliberately lands here rather than in a case of its own.
            //
            // The scope check stays strict equality unless the store itself declared the record shared
            // through an active grant -- only it applied the predicate, so only it can say -- and even
            // then the record must lie inside the boundary no grant can cross. So a store that hands
            // back a foreign record without declaring it, and one that declares a record from another
            // tenant, application, or project, are both still dropped here.
            if (!ReadableInScope(result, experienceId, request.Scope, out var current))
            {
                omitted.Add(new OmittedExperience(
                    experienceId,
                    InjectionOmissionReason.Unreadable,
                    "The record could not be read in the requested scope at injection time."));
                if (delivered is not null)
                {
                    withdrawn.Add(experienceId);
                }

                continue;
            }

            // Every rule retrieval applies, re-applied to the record as it stands now. Checking only
            // the status would leave a record retrieval would exclude today still injectable. The
            // record's own rules withdraw a delivery of it; the request's environment attributes do not,
            // because they say nothing about the record.
            if (RecordIneligible(current, policy, now) is { } recordReason)
            {
                omitted.Add(new OmittedExperience(experienceId, InjectionOmissionReason.Ineligible, recordReason));
                if (delivered is not null)
                {
                    withdrawn.Add(experienceId);
                }

                continue;
            }

            if (EnvironmentIneligible(current, unrestricted, required) is { } reason)
            {
                omitted.Add(new OmittedExperience(experienceId, InjectionOmissionReason.Ineligible, reason));
                continue;
            }

            if (delivered is not null)
            {
                // A grant that now withholds the approach the session was shown withdraws that delivery,
                // and the record is not shown again in the same block that says so.
                if (Narrowed(delivered, result))
                {
                    withdrawn.Add(experienceId);
                    omitted.Add(new OmittedExperience(experienceId, InjectionOmissionReason.Ineligible, NarrowedDetail));
                    continue;
                }

                // The ranked revision was newer, but the record as it stands now is not.
                if (delivered.Confirmed && delivered.Revision >= current.Revision)
                {
                    omitted.Add(new OmittedExperience(experienceId, InjectionOmissionReason.AlreadyDelivered, AlreadyDeliveredDetail));
                    continue;
                }
            }

            // The re-read decides sharing too: a grant that expired since retrieval leaves the record
            // readable only if the reader owns it, and the block must say what is true now.
            // The re-read decides which grant, too. It is the delivery the store audits, so the ID the
            // host sees here is the one that appears in the access trail for this record.
            // And the grant's disclosure level, from that same read. Fail closed: a shared re-read whose
            // store reported no level -- or one this build does not define -- renders as LessonOnly. A
            // record in the reader's own scope has no level at all.
            var refreshed = candidate with
            {
                Record = current,
                SharedByGrant = result.SharedByGrant,
                PermittingGrantId = result.SharedByGrant ? result.PermittingGrantId : null,
                GrantDisclosure = result.SharedByGrant
                    ? result.GrantDisclosure is ExperienceGrantDisclosure.LessonAndApproach or ExperienceGrantDisclosure.LessonApproachAndArguments
                        ? result.GrantDisclosure
                        : ExperienceGrantDisclosure.LessonOnly
                    : null,

                // The owner's argument allowlist travels only with the level that is consent to it, and only
                // for a borrowed record: an owned record, or any other level, carries none whatever the store
                // said.
                // It needs a named grant too: session tracking withdraws shown values by the grant that showed them,
                // and a store that names none could switch grants unseen. And it is copied into read-only
                // collections here, so neither the host's decision callback nor anything else can widen it between
                // the decision and the rendering.
                GrantApproachArguments = result.SharedByGrant
                    && result.GrantDisclosure == ExperienceGrantDisclosure.LessonApproachAndArguments
                    && result.PermittingGrantId is not null
                        ? FrozenGrantArguments(result.GrantApproachArguments)
                        : null,
            };

            if (_options.DecideInjection is { } decide)
            {
                InjectionDecision? decision;
                try
                {
                    decision = decide(new ExperienceInjectionDecisionContext(
                        refreshed,
                        current,
                        result.SharedByGrant,
                        refreshed.PermittingGrantId,
                        refreshed.GrantDisclosure,
                        refreshed.GrantApproachArguments));
                }
                catch (Exception ex)
                {
                    // A risk decision that could not be taken is a denial, never an admission.
                    omitted.Add(new OmittedExperience(
                        experienceId,
                        InjectionOmissionReason.HostDenied,
                        $"The host injection decision threw {ex.GetType().FullName}, so the record was denied."));
                    continue;
                }

                if (decision is not { Permitted: true })
                {
                    omitted.Add(new OmittedExperience(
                        experienceId,
                        InjectionOmissionReason.HostDenied,
                        decision?.Reason ?? "The host injection decision denied the record."));
                    continue;
                }
            }

            // The record rendered is refreshed as built above, never anything the decision returned: the
            // host can only deny, never widen the disclosure level.
            injectable.Add(refreshed);
        }

        if (recheck.Count > 0)
        {
            if (Expired())
            {
                return CheckOutcome.TimedOut(timeout);
            }

            var heldIds = new Guid[recheck.Count];
            for (var i = 0; i < heldIds.Length; i++)
            {
                heldIds[i] = recheck[i].ExperienceId;
            }

            ExperienceRecordGetManyResult held;
            try
            {
                // A scope check, not a delivery: nothing of these records is handed to the model -- at
                // most their ID, in a withdrawal notice -- so no access row claims otherwise.
                held = await _store
                    .GetManyAsync(
                        request.Authorization,
                        request.Scope,
                        heldIds,
                        new ExperienceReadOptions(ExperienceReadPurpose.ScopeCheck, request.CorrelationId),
                        bounded.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (OperationCanceledException) when (expiry.IsCancellationRequested)
            {
                return CheckOutcome.TimedOut(timeout);
            }
            catch (Exception ex)
            {
                return CheckOutcome.Failed(new InjectionFailure(
                    $"Re-checking the records this session was given threw {ex.GetType().FullName}, so nothing was injected.",
                    ex));
            }

            cancellationToken.ThrowIfCancellationRequested();

            // A request-wide refusal (Denied, Invalid), a missing position and any other answer that is not
            // the record, readable in this scope and still standing, all withdraw: a notice the record did
            // not need costs a line, and one it needed and did not get is the failure this exists to stop.
            var answers = held is { Outcome: ExperienceStoreOutcome.Found, Results: { } heldResults } ? heldResults : null;
            for (var i = 0; i < recheck.Count; i++)
            {
                var answer = answers is not null && i < answers.Count ? answers[i] : null;
                if (!Stands(answer, recheck[i], request.Scope, policy, now))
                {
                    withdrawn.Add(recheck[i].ExperienceId);
                }
            }
        }

        // And once more after the last record: the whole check is bounded, the last decision included.
        // The per-record loop never looked again after its last callback, so a slow decision on the last
        // record used to inject past the bound.
        if (Expired())
        {
            return CheckOutcome.TimedOut(timeout);
        }

        // Notices in the order the records were delivered, so the block is deterministic.
        var withdrawals = session is null || withdrawn.Count == 0
            ? []
            : session.State.Delivered.Where(entry => withdrawn.Contains(entry.ExperienceId)).Select(entry => entry.ExperienceId).ToList();
        return CheckOutcome.Checked(injectable, withdrawals);
    }

    /// <summary>The omission detail for a revision the session already holds.</summary>
    private const string AlreadyDeliveredDetail =
        "This revision of the record was already delivered earlier in this session, and has not been withdrawn.";

    /// <summary>The omission detail for a delivery withdrawn because its grant no longer shows the approach.</summary>
    private const string NarrowedDetail =
        "The grant this record is read through no longer permits the approach the session was shown, so that delivery is withdrawn; the record can be delivered again on a later invocation.";

    /// <summary>
    /// Whether a re-read answered with the record itself, readable in <paramref name="scope"/>: found, under
    /// the ID asked for, and either in exactly that scope or declared shared by the store and inside the
    /// boundary no grant can cross.
    /// </summary>
    private static bool ReadableInScope([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] ExperienceRecordGetResult? result, Guid experienceId, Scope scope, out ExperienceRecord current)
    {
        current = null!;
        if (result is not { Outcome: ExperienceStoreOutcome.Found, Record: { } record }
            || record.ExperienceId != experienceId
            || record.Scope is null
            || !(result.SharedByGrant ? record.Scope.SharesGrantBoundary(scope) : record.Scope == scope))
        {
            return false;
        }

        current = record;
        return true;
    }

    /// <summary>
    /// Whether a delivery the session holds still stands: the record is readable in scope, passes the
    /// record's own eligibility rules, and is not read through a grant that now withholds an approach the
    /// session was shown.
    /// </summary>
    private static bool Stands(ExperienceRecordGetResult? result, DeliveredRecord delivered, Scope scope, RetrievalPolicy policy, DateTimeOffset now) =>
        ReadableInScope(result, delivered.ExperienceId, scope, out var current)
        && RecordIneligible(current, policy, now) is null
        && !Narrowed(delivered, result);

    /// <summary>
    /// Whether the session was shown this record's approach through a grant, and the grant it is read
    /// through now withholds it -- or reports no level, which is the least disclosure.
    /// </summary>
    /// <remarks>
    /// A delivery that showed argument values through a grant is narrowed, too, when the record is now read
    /// through a level that shows none, or through any grant but the one whose owner allowlist was applied: a
    /// grant is immutable, so the same grant means the same allowlist, and a different one may name fewer keys.
    /// </remarks>
    private static bool Narrowed(DeliveredRecord delivered, ExperienceRecordGetResult result) =>
        result.SharedByGrant
        && ((delivered.ApproachByGrant && !HistoricalReferenceWriter.ShowsApproach(result.GrantDisclosure))
            || (delivered.ArgumentsGrantId is { } shownThrough
                && (result.GrantDisclosure != ExperienceGrantDisclosure.LessonApproachAndArguments
                    || (result.PermittingGrantId ?? Guid.Empty) != shownThrough)));

    /// <summary>
    /// Re-applies retrieval's own eligibility rules that are about the record itself: eligible status,
    /// the policy's reuse-confidence floor, and the policy's <see cref="RetrievalPolicy.MaxAge"/>. Returns
    /// the content-free reason the record is no longer eligible, or <see langword="null"/> when it still is.
    /// </summary>
    private static string? RecordIneligible(ExperienceRecord record, RetrievalPolicy policy, DateTimeOffset now)
    {
        if (!ExperienceRetrievalService.EligibleStatuses.Contains(record.Status))
        {
            return $"The record's status is '{record.Status}', which is not reusable.";
        }

        if (record.ReuseConfidence < policy.MinimumConfidence)
        {
            return "The record's reuse confidence has fallen below the retrieval policy's floor.";
        }

        if (policy.MaxAge is { } maxAge && now - record.UpdatedAt > maxAge)
        {
            return "The record's last lifecycle activity is older than the retrieval policy's maximum age.";
        }

        return null;
    }

    /// <summary>
    /// Re-applies the request's required environment attributes to a re-read record. Returns the
    /// content-free reason the record no longer satisfies them, or <see langword="null"/> when it does.
    /// </summary>
    private static string? EnvironmentIneligible(
        ExperienceRecord record,
        bool unrestricted,
        IReadOnlyDictionary<string, string>? required)
    {
        if (!unrestricted)
        {
            foreach (var (key, value) in required!)
            {
                if (record.Environment?.Metadata is not { } metadata
                    || !metadata.TryGetValue(key, out var stored)
                    || !string.Equals(stored, value, StringComparison.Ordinal))
                {
                    // The key itself is the request's own, not record content, so naming it is safe.
                    return $"The record no longer satisfies the required environment attribute '{key}'.";
                }
            }
        }

        return null;
    }

    /// <summary>Reports the attempt and returns an <see cref="AIContext"/> that adds nothing to the invocation.</summary>
    private AIContext Nothing(
        InjectionTrace trace,
        InjectionOutcome outcome,
        IReadOnlyList<OmittedExperience> omitted,
        ExperienceRetrievalResult? retrieved,
        string? correlationId,
        InjectionFailure? failure,
        SessionTracker? session = null)
    {
        if (session is not null)
        {
            try
            {
                // Only an unsettled earlier stage, committed on the way in, can have changed it.
                session.Save();
            }
            catch (Exception)
            {
                // The commit is found and made again on the next invocation.
            }
        }

        Report(trace, new ExperienceInjectionResult(
            outcome,
            NoIds,
            omitted,
            retrieved?.Excluded ?? NoExclusions,
            retrieved?.Truncated ?? false,
            retrieved?.EnvironmentUnrestricted ?? false,
            PayloadBytes: 0,
            correlationId ?? retrieved?.CorrelationId,
            failure,
            retrieved?.VectorFallback)
        {
            Session = session?.Usage(),
        });
        return new AIContext();
    }

    /// <summary>
    /// What the final eligibility check produced: what survived it and which of the session's deliveries
    /// no longer stand, or the failure that ended it.
    /// </summary>
    private readonly record struct CheckOutcome(List<RankedExperience> Injectable, List<Guid> Withdrawn, InjectionFailure? Failure)
    {
        public static CheckOutcome Checked(List<RankedExperience> injectable, List<Guid> withdrawn) => new(injectable, withdrawn, null);

        public static CheckOutcome Failed(InjectionFailure failure) => new([], [], failure);

        public static CheckOutcome TimedOut(TimeSpan timeout) => Failed(
            new InjectionFailure(
                $"The final eligibility check exceeded its {timeout} bound, so nothing was injected.",
                Exception: null));
    }

    /// <summary>
    /// One invocation's view of its session's account: the state as loaded (with any unsettled earlier
    /// stage committed), the limits it runs under, and what this invocation staged.
    /// </summary>
    private sealed class SessionTracker(
        AgentSession session,
        InjectionSessionState state,
        ExperienceInjectionSessionLimits limits,
        bool dirty)
    {
        private bool _dirty = dirty;

        /// <summary>The account, as this invocation sees and changes it.</summary>
        public InjectionSessionState State { get; private set; } = state;

        /// <summary>What the session's byte budget has left for records.</summary>
        public long RemainingBytes => Math.Max(0, limits.MaxBytes - State.BytesUsed);

        /// <summary>
        /// How many more record deliveries the session may be given: its record budget, and never so
        /// many that the account could hold more than <see cref="ExperienceInjectionSessionLimits.MaxTrackedRecords"/> entries.
        /// </summary>
        public int RemainingRecords => (int)Math.Max(
            0,
            Math.Min(
                (long)limits.MaxRecords - State.RecordsUsed,
                ExperienceInjectionSessionLimits.MaxTrackedRecords - State.Delivered.Count));

        /// <summary>Whether the budget cannot take another record at all, so retrieval need not run.</summary>
        public bool Exhausted => RemainingRecords == 0 || RemainingBytes <= HistoricalReferenceWriter.BlockOverheadBytes;

        /// <summary>Stages this invocation's delivery, to be settled when MAF reports how it ended.</summary>
        public void Stage(PendingDelivery pending)
        {
            State = State with { Pending = pending };
            _dirty = true;
        }

        /// <summary>Writes the account back when this invocation changed it.</summary>
        public void Save()
        {
            if (_dirty)
            {
                State.Save(session.StateBag);
                _dirty = false;
            }
        }

        /// <summary>The account as reported: counting what this invocation staged as though it succeeds.</summary>
        public ExperienceInjectionSessionUsage Usage()
        {
            var settled = State.Commit();
            return new ExperienceInjectionSessionUsage(
                settled.RecordsUsed,
                settled.BytesUsed,
                limits.MaxRecords,
                limits.MaxBytes,
                settled.Active.Count());
        }
    }

    /// <summary>
    /// Closes this injection's span and hands the result to the host. Every non-throwing exit runs
    /// through here exactly once, which is what makes it the one place the <c>inject</c> operation is
    /// counted and timed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The span carries the bounded outcome, the host's own correlation identifier (omitted, never
    /// blank, when the host supplied none), and how many ranked records were left out. It never
    /// carries the injected block, a record's lesson, an omission's reason text, or the task text the
    /// retrieval matched on.
    /// </para>
    /// <para>
    /// <b>The duration is recorded after the host callback, not before it.</b> The span stops in
    /// <c>ProvideAIContextAsync</c>'s <c>finally</c>, which is after the callback, so measuring before
    /// it would leave span p99 and histogram p99 disagreeing by whatever the host's callback costs --
    /// and Core, which has no callback, has no such gap. A callback that throws is caught, so it can
    /// still never cost the operation its measurement.
    /// </para>
    /// </remarks>
    /// <param name="trace">This injection's span and start timestamp.</param>
    /// <param name="result">What the attempt ended as.</param>
    private void Report(InjectionTrace trace, ExperienceInjectionResult result)
    {
        InjectionDiagnostics.Tag(trace, InjectionDiagnostics.OmittedCountAttribute, result.Omitted.Count);
        if (result.RetractedExperienceIds.Count > 0)
        {
            InjectionDiagnostics.Tag(trace, InjectionDiagnostics.RetractedCountAttribute, result.RetractedExperienceIds.Count);
        }

        try
        {
            _options.OnContextInjected?.Invoke(result);
        }
        catch (Exception)
        {
            // Reporting is diagnostics. It can never change what the caller of the agent observes.
        }
        finally
        {
            InjectionDiagnostics.Succeeded(trace, result.Outcome.ToString());
        }
    }
}

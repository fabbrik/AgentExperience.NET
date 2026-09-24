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
/// <b>What the check cannot do is reach backwards.</b> Once a block has been handed to a model, a
/// later revocation cannot retract it, and the provider does not pretend otherwise. This is sharper
/// than it sounds when an <see cref="AgentSession"/> is reused: a block injected on one turn can
/// stay in that session's conversation, so a later turn may show the model the fresh block
/// <em>and</em> the earlier one, verbatim -- including a record the fresh check has just omitted as
/// revoked. MAF filters this provider's input to external messages, so the provider cannot reliably
/// see, let alone strip, its own earlier blocks, and it does not claim to. Two consequences to plan
/// for: <see cref="ExperienceInjectionLimits.MaxBytes"/> bounds one injected block, not a
/// conversation; and revocation only takes effect for injections that have not happened yet. Where
/// either matters, use a fresh session per task, or a chat-history provider that drops earlier
/// injected blocks.
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

    private static readonly IReadOnlyList<Guid> NoIds = [];

    private static readonly IReadOnlyList<OmittedExperience> NoOmissions = [];

    private static readonly IReadOnlyList<ExcludedExperience> NoExclusions = [];

    private readonly ExperienceRetrievalService _retrieval;
    private readonly IExperienceRecordStore _store;
    private readonly ExperienceInjectionOptions _options;

    /// <summary>
    /// Creates a provider over Core's retrieval service, the record store its final eligibility check
    /// re-reads through, and the host's configuration.
    /// </summary>
    /// <param name="retrieval">Core's retrieval service. It owns eligibility, ranking, and the retrieval timeout.</param>
    /// <param name="store">The record store each selected candidate is re-read through, in the request's own authorization and scope.</param>
    /// <param name="options">Host configuration: the resolver, the limits, the risk decision, and the result callback.</param>
    /// <exception cref="ArgumentNullException">Any argument, or <see cref="ExperienceInjectionOptions.ResolveRequest"/> or <see cref="ExperienceInjectionOptions.Limits"/>, is <see langword="null"/>.</exception>
    public ExperienceContextProvider(
        ExperienceRetrievalService retrieval,
        IExperienceRecordStore store,
        ExperienceInjectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(retrieval);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(nameof(options));

        _retrieval = retrieval;
        _store = store;
        _options = options;
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

        ExperienceRetrievalResult retrieved;
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
            return Nothing(
                trace,
                InjectionOutcome.RetrievalFailed,
                NoOmissions,
                retrieved: null,
                request.CorrelationId,
                new InjectionFailure($"Retrieval threw {ex.GetType().FullName}.", ex));
        }

        if (retrieved.Outcome is not RetrievalOutcome.Completed)
        {
            return Nothing(
                trace,
                retrieved.Outcome switch
                {
                    RetrievalOutcome.TimedOut => InjectionOutcome.RetrievalTimedOut,
                    RetrievalOutcome.Denied => InjectionOutcome.RetrievalDenied,
                    _ => InjectionOutcome.RetrievalFailed,
                },
                NoOmissions,
                retrieved,
                retrieved.CorrelationId,
                retrieved.Failure is { } failure ? new InjectionFailure(failure.Reason, failure.Exception) : null);
        }

        var omitted = new List<OmittedExperience>();
        var selected = Select(retrieved.Records, omitted);
        if (selected.Count == 0)
        {
            return Nothing(trace, InjectionOutcome.NothingToInject, omitted, retrieved, retrieved.CorrelationId, failure: null);
        }

        CheckOutcome recheck;
        try
        {
            recheck = await CheckAsync(request, selected, omitted, cancellationToken).ConfigureAwait(false);
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
                retrieved.CorrelationId,
                new InjectionFailure($"The final eligibility check threw {ex.GetType().FullName}.", ex));
        }

        if (recheck.Failure is { } checkFailure)
        {
            // The check ran out of time. Nothing is injected rather than injecting the part of it that
            // had been re-checked before the bound was reached.
            return Nothing(trace, InjectionOutcome.Failed, omitted, retrieved, retrieved.CorrelationId, checkFailure);
        }

        if (recheck.Injectable.Count == 0)
        {
            return Nothing(trace, InjectionOutcome.NothingToInject, omitted, retrieved, retrieved.CorrelationId, failure: null);
        }

        HistoricalReferencePayload payload;
        try
        {
            payload = HistoricalReferenceWriter.Write(recheck.Injectable, _options.Limits);
        }
        catch (Exception ex)
        {
            return Nothing(
                trace,
                InjectionOutcome.Failed,
                omitted,
                retrieved,
                retrieved.CorrelationId,
                new InjectionFailure($"Building the Historical Reference threw {ex.GetType().FullName}.", ex));
        }

        omitted.AddRange(payload.Omitted);

        if (payload.IsEmpty)
        {
            return Nothing(trace, InjectionOutcome.NothingToInject, omitted, retrieved, retrieved.CorrelationId, failure: null);
        }

        // A user-role message, not a system one: the block is reference material the model may read,
        // never an instruction from the host. MAF merges it with the invocation's own messages. Built
        // before the report so that nothing which could throw remains after the span has been closed.
        var injected = new AIContext
        {
            Messages =
            [
                new ChatMessage(ChatRole.User, payload.Text)
                {
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        [HistoricalReferenceKey] = true,
                    },
                },
            ],
        };

        Report(trace, new ExperienceInjectionResult(
            InjectionOutcome.Injected,
            payload.ExperienceIds,
            omitted,
            retrieved.Excluded,
            retrieved.Truncated,
            retrieved.EnvironmentUnrestricted,
            payload.ByteCount,
            retrieved.CorrelationId,
            Failure: null,
            retrieved.VectorFallback));

        return injected;
    }

    /// <summary>
    /// Takes the top <see cref="ExperienceInjectionLimits.MaxRecords"/> in rank order and records the
    /// rest, so the final eligibility check only ever re-reads records that could actually be injected.
    /// </summary>
    private List<RankedExperience> Select(IReadOnlyList<RankedExperience> ranked, List<OmittedExperience> omitted)
    {
        var limit = _options.Limits.MaxRecords;
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

            // Counted by what has actually been selected, not by rank index: a skip above must not
            // silently cost a slot that a later record could have filled.
            if (selected.Count < limit)
            {
                selected.Add(candidate);
                continue;
            }

            omitted.Add(new OmittedExperience(
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
    private async Task<CheckOutcome> CheckAsync(
        RetrieveExperienceRequest request,
        List<RankedExperience> selected,
        List<OmittedExperience> omitted,
        CancellationToken cancellationToken)
    {
        var injectable = new List<RankedExperience>(selected.Count);
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
            // was injected into.
            batch = await _store
                .GetManyAsync(request.Authorization, request.Scope, ids, readOptions, bounded.Token)
                .ConfigureAwait(false);
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
                    // Fail-closed, per record: a record that could not be re-checked is not injected.
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
            if (result is not { Outcome: ExperienceStoreOutcome.Found, Record: { } current }
                || current.ExperienceId != experienceId
                || current.Scope is null
                || !(result.SharedByGrant
                    ? current.Scope.SharesGrantBoundary(request.Scope)
                    : current.Scope == request.Scope))
            {
                omitted.Add(new OmittedExperience(
                    experienceId,
                    InjectionOmissionReason.Unreadable,
                    "The record could not be read in the requested scope at injection time."));
                continue;
            }

            // Every rule retrieval applies, re-applied to the record as it stands now. Checking only
            // the status would leave a record retrieval would exclude today still injectable.
            if (Ineligible(current, policy, now, unrestricted, required) is { } reason)
            {
                omitted.Add(new OmittedExperience(experienceId, InjectionOmissionReason.Ineligible, reason));
                continue;
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
                    ? result.GrantDisclosure == ExperienceGrantDisclosure.LessonAndApproach
                        ? ExperienceGrantDisclosure.LessonAndApproach
                        : ExperienceGrantDisclosure.LessonOnly
                    : null,
            };

            if (_options.DecideInjection is { } decide)
            {
                InjectionDecision? decision;
                try
                {
                    decision = decide(new ExperienceInjectionDecisionContext(
                        refreshed, current, result.SharedByGrant, refreshed.PermittingGrantId, refreshed.GrantDisclosure));
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

        // And once more after the last record: the whole check is bounded, the last decision included.
        // The per-record loop never looked again after its last callback, so a slow decision on the last
        // record used to inject past the bound.
        return Expired() ? CheckOutcome.TimedOut(timeout) : CheckOutcome.Checked(injectable);
    }

    /// <summary>
    /// Re-applies retrieval's own eligibility rules to a re-read record: eligible status, the
    /// policy's reuse-confidence floor, the policy's <see cref="RetrievalPolicy.MaxAge"/>, and the
    /// request's required environment attributes. Returns the content-free reason the record is no
    /// longer eligible, or <see langword="null"/> when it still is.
    /// </summary>
    private static string? Ineligible(
        ExperienceRecord record,
        RetrievalPolicy policy,
        DateTimeOffset now,
        bool unrestricted,
        IReadOnlyDictionary<string, string>? required)
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
        InjectionFailure? failure)
    {
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
            retrieved?.VectorFallback));
        return new AIContext();
    }

    /// <summary>What the final eligibility check produced: what survived it, or the bound that ended it.</summary>
    private readonly record struct CheckOutcome(List<RankedExperience> Injectable, InjectionFailure? Failure)
    {
        public static CheckOutcome Checked(List<RankedExperience> injectable) => new(injectable, null);

        public static CheckOutcome TimedOut(TimeSpan timeout) => new(
            [],
            new InjectionFailure(
                $"The final eligibility check exceeded its {timeout} bound, so nothing was injected.",
                Exception: null));
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

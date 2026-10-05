using AgentExperience.Abstractions;
using AgentExperience.Core.Retrieval;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>
/// The bounds one injected Historical Reference runs under. Both values are validated at
/// construction <em>and</em> on a <c>with</c> expression (each property's <c>init</c> accessor
/// re-validates via the C# <c>field</c> keyword), exactly as
/// <see cref="AgentExperience.Core.Retrieval.RetrievalPolicy"/> and
/// <see cref="AgentExperience.Core.Capture.CaptureLimits"/> do, because a record's property
/// initializers alone do not re-run when a property is changed with <c>with</c>. An invalid value
/// throws <see cref="ArgumentOutOfRangeException"/>, so a misconfigured limit fails at startup rather
/// than silently widening what a model is shown.
/// </summary>
/// <remarks>
/// <para>
/// Both size limits are enforced by dropping <em>whole</em> records, never by cutting one: a record
/// is either rendered in full or omitted with its reason. That is what keeps every evidence label
/// intact, and it is why a single record larger than the whole byte budget is omitted rather than
/// truncated.
/// </para>
/// <para>
/// <b><see cref="MaxBytes"/> bounds one injected block, not a conversation.</b> When the same
/// <see cref="AgentSession"/> is reused across turns, earlier blocks remain in the session's
/// conversation. What bounds the conversation is <see cref="ExperienceInjectionOptions.SessionLimits"/>.
/// See <see cref="ExperienceContextProvider"/>.
/// </para>
/// </remarks>
/// <param name="MaxRecords">
/// The most records one injected block may carry. Records are taken in rank order, and the rest are
/// recorded as <see cref="InjectionOmissionReason.OverRecordLimit"/>. It also bounds the final
/// eligibility re-check: only these records are re-read. Must be strictly positive.
/// </param>
/// <param name="MaxBytes">
/// The most UTF-8 bytes one injected block may occupy, delimiters and label included. Records are
/// written in rank order until the next one would not fit; that record and every record after it are
/// recorded as <see cref="InjectionOmissionReason.OverByteBudget"/>. Must leave room for at least one
/// byte of record after the block's own fixed header and footer -- that is, it must be strictly
/// greater than <see cref="HistoricalReferenceWriter.BlockOverheadBytes"/> -- because a smaller
/// budget could never fit any record at all and would report a per-record
/// <see cref="InjectionOmissionReason.OverByteBudget"/> on every invocation forever.
/// </param>
public sealed record ExperienceInjectionLimits(int MaxRecords, int MaxBytes)
{
    /// <summary>The documented default record limit: 8 records.</summary>
    public const int DefaultMaxRecords = 8;

    /// <summary>The documented default byte budget: 16 KB of UTF-8.</summary>
    public const int DefaultMaxBytes = 16 * 1024;

    /// <summary>
    /// The default bound on the whole final eligibility re-check: 500 milliseconds, matching
    /// <see cref="RetrievalPolicy.DefaultTimeout"/>. The check is one batched store read of up to
    /// <see cref="MaxRecords"/> records (one round trip against the PostgreSQL adapter; one read per
    /// record against a store that keeps the port's default) on the invocation's critical path, so it
    /// needs a bound of its own -- retrieval's timeout has already been spent by the time it starts.
    /// Every read inside it is hard-bounded by what is left of this one budget and abandoned on expiry,
    /// so the worst case before the model call is about the retrieval timeout plus this one (plus the
    /// host's own resolver callback; time in the host's decision callback counts against this budget,
    /// though one synchronous call is not cut short). The bound is released by a
    /// <see cref="TimeProvider"/> timer, so a starved thread pool can still release it late. An abandoned
    /// read keeps running against the store in the background until it observes its cancellation, so the
    /// store must tolerate concurrent use, and a grant's access rows may be written after the timeout is
    /// reported.
    /// </summary>
    public static readonly TimeSpan DefaultEligibilityCheckTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The default bound on abandoned eligibility re-reads still running against the store: 16 (see
    /// <see cref="MaxAbandonedReads"/>).
    /// </summary>
    public const int DefaultMaxAbandonedReads = 16;

    /// <summary>The largest permitted <see cref="EligibilityCheckTimeout"/>: one day, matching <see cref="RetrievalPolicy.MaxTimeout"/>.</summary>
    public static readonly TimeSpan MaxEligibilityCheckTimeout = RetrievalPolicy.MaxTimeout;

    /// <summary>The documented defaults: at most 8 records and 16 KB of UTF-8, re-checked within 500 milliseconds.</summary>
    public static ExperienceInjectionLimits Default { get; } = new(DefaultMaxRecords, DefaultMaxBytes);

    /// <summary>The most records one injected block may carry (see the primary constructor's parameter doc).</summary>
    public int MaxRecords
    {
        get;
        init => field = EnsurePositive(value, nameof(MaxRecords));
    } = EnsurePositive(MaxRecords, nameof(MaxRecords));

    /// <summary>The most UTF-8 bytes one injected block may occupy (see the primary constructor's parameter doc).</summary>
    public int MaxBytes
    {
        get;
        init => field = EnsureBudget(value);
    } = EnsureBudget(MaxBytes);

    /// <summary>
    /// How long the whole final eligibility re-check may take, measured with
    /// <see cref="ExperienceInjectionOptions.TimeProvider"/>. Exceeding it is never an exception:
    /// nothing is injected and the overrun is reported like any other failure. Must be strictly
    /// positive and at most <see cref="MaxEligibilityCheckTimeout"/>.
    /// </summary>
    public TimeSpan EligibilityCheckTimeout
    {
        get;
        init => field = EnsureTimeout(value);
    } = DefaultEligibilityCheckTimeout;

    /// <summary>
    /// How many abandoned eligibility re-reads one provider may have still running against the store before
    /// it stops starting new ones. A read is abandoned when it runs past <see cref="EligibilityCheckTimeout"/>
    /// or when the caller cancels; it is not stopped but keeps running in the background until it ends, and
    /// counts against this cap until then. So against a store that hangs on every call each invocation
    /// would otherwise add another running read and they would pile up until the connection or thread pool
    /// ran out. At this many, the check is not started: it fails at once, without calling the store, as
    /// <see cref="InjectionOutcome.Failed"/> with a reason that names this cap; nothing is injected.
    /// Once abandoned reads finish, the count drops and checks reach the store again. Counted per provider
    /// instance, so the cap only engages when one provider is shared and long-lived (for example a DI
    /// singleton); a provider built per request starts every count at zero and never reaches it. Counted across every read of the check (the batch, its per-record fallback and the session's
    /// scope check). Defaults to <see cref="DefaultMaxAbandonedReads"/>; must be strictly positive.
    /// </summary>
    public int MaxAbandonedReads
    {
        get;
        init => field = EnsurePositive(value, nameof(MaxAbandonedReads));
    } = DefaultMaxAbandonedReads;

    private static int EnsurePositive(int value, string paramName) =>
        value > 0
            ? value
            : throw new ArgumentOutOfRangeException(paramName, value, "Injection limits must be strictly positive.");

    private static int EnsureBudget(int value) =>
        value > HistoricalReferenceWriter.BlockOverheadBytes
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(MaxBytes),
                value,
                $"The byte budget must exceed the block's fixed header and footer ({HistoricalReferenceWriter.BlockOverheadBytes} bytes), or no record could ever fit.");

    private static TimeSpan EnsureTimeout(TimeSpan value) =>
        value > TimeSpan.Zero && value <= MaxEligibilityCheckTimeout
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(EligibilityCheckTimeout),
                value,
                $"The eligibility-check timeout must be strictly positive and at most {MaxEligibilityCheckTimeout}.");
}

/// <summary>
/// What the host sees when it is asked which experience, if any, applies to a MAF invocation that is
/// about to run.
/// </summary>
/// <param name="Messages">
/// The messages for this invocation that MAF passed to the provider. MAF filters its input with the
/// provider's <c>ProvideInputMessageFilter</c>, which by default keeps only <em>external</em>
/// messages, so this is the caller-facing conversation -- not necessarily everything the model will
/// receive, and not this provider's own earlier blocks (session tracking keeps its own account of those,
/// in the session's state bag). It may be empty, so read it defensively
/// (<c>LastOrDefault(...)</c>, never <c>Last()</c>: a resolver that throws injects nothing for the
/// rest of that agent's life and says so only through the result callback).
/// </param>
/// <param name="Session">The session associated with the invocation, or <see langword="null"/> when the caller passed none.</param>
/// <param name="Agent">The agent being invoked.</param>
public sealed record ExperienceInjectionContext(
    IReadOnlyList<ChatMessage> Messages,
    AgentSession? Session,
    AIAgent Agent)
{
    private DerivedTaskTextCache? _derivedTaskText;

    /// <summary>
    /// The full request the provider saw before MAF's input filter, paired with the <see cref="Messages"/> list it was
    /// given alongside, so a <c>with</c> that replaces <see cref="Messages"/> derives from the new list instead.
    /// </summary>
    private (IReadOnlyList<ChatMessage> ForMessages, IReadOnlyList<ChatMessage> Request)? _derivationSource;

    /// <summary>A copy for <c>with</c>: the same members and derivation source, and a fresh, unread derived text.</summary>
    /// <param name="original">The context being copied.</param>
    private ExperienceInjectionContext(ExperienceInjectionContext original)
    {
        Messages = original.Messages;
        Session = original.Session;
        Agent = original.Agent;
        _derivationSource = original._derivationSource;
    }

    /// <summary>
    /// The task text <see cref="ExperienceTaskText.Derive(IEnumerable{ChatMessage}, int)"/> derives for this invocation
    /// (at most <see cref="ExperienceTaskText.DefaultMaxLength"/> UTF-16 code units), or <see langword="null"/> when no
    /// user message qualifies. Nothing uses it unless the host does. The recommended wiring in
    /// <see cref="ExperienceInjectionOptions.ResolveRequestAsync"/> (or the synchronous
    /// <see cref="ExperienceInjectionOptions.ResolveRequest"/>) is
    /// <c>context.DerivedTaskText is { } text ? new RetrieveExperienceRequest(..., TaskText: text) : null</c>: return
    /// <see langword="null"/> to skip injection when it is <see langword="null"/>, and fall back to other text only when
    /// the host has a meaningful task label of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it reads.</b> When <see cref="ExperienceContextProvider"/> builds the context, the whole request MAF
    /// handed the provider before its input filter: the new input <em>and</em> the replayed chat history, so a short
    /// follow-up such as "and retry" is joined to the previous turn's request (see <see cref="ExperienceTaskText"/>;
    /// history is used for that join only, never as the latest request, and injected context never). A context built
    /// any other way, or one whose <see cref="Messages"/> a <c>with</c> replaced, reads <see cref="Messages"/>. The
    /// cross-turn join needs the history replayed into the request; with history the service keeps (a conversation ID)
    /// there is none to join.
    /// </para>
    /// <para>
    /// <b>Computed on first read.</b> The messages are snapshotted then, so a host that changes the same list
    /// afterwards still sees the earlier text. It is computed at most once per instance (a <c>with</c> copy computes its
    /// own). If reading the messages throws, the first read throws that exception and every later read returns
    /// <see langword="null"/>.
    /// </para>
    /// <para>
    /// <b>Not part of equality.</b> Equality and <see cref="object.ToString"/> look at <see cref="Messages"/>,
    /// <see cref="Session"/> and <see cref="Agent"/> only, so two equal contexts, one built by the provider (which reads
    /// the history too) and one built by hand, can derive different text.
    /// </para>
    /// <para>It is the user's own words, unredacted; see <see cref="ExperienceTaskText"/>.</para>
    /// </remarks>
    public string? DerivedTaskText => DerivedTaskTextCache.For(ref _derivedTaskText).Get(() =>
        _derivationSource is { } source && ReferenceEquals(source.ForMessages, Messages) ? source.Request : Messages);

    /// <summary>
    /// Sets the request <see cref="DerivedTaskText"/> derives from, for the provider: the messages before MAF's input
    /// filter, history included.
    /// </summary>
    /// <param name="request">The unfiltered request messages, snapshotted by the caller.</param>
    /// <returns>This context.</returns>
    internal ExperienceInjectionContext WithDerivationSource(IReadOnlyList<ChatMessage>? request)
    {
        if (request is not null)
        {
            _derivationSource = (Messages, request);
        }

        return this;
    }

    /// <summary>Equal when <see cref="Messages"/>, <see cref="Session"/> and <see cref="Agent"/> are.</summary>
    /// <param name="other">The context to compare with.</param>
    /// <returns><see langword="true"/> when the two contexts are equal.</returns>
    public bool Equals(ExperienceInjectionContext? other) =>
        other is not null
        && (ReferenceEquals(this, other)
            || (EqualityComparer<IReadOnlyList<ChatMessage>>.Default.Equals(Messages, other.Messages)
                && EqualityComparer<AgentSession?>.Default.Equals(Session, other.Session)
                && EqualityComparer<AIAgent>.Default.Equals(Agent, other.Agent)));

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Messages, Session, Agent);

    // The derived text is the user's own words, so it stays out of ToString, which hosts log.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Messages = ").Append(Messages)
            .Append(", Session = ").Append(Session)
            .Append(", Agent = ").Append(Agent);
        return true;
    }
}

/// <summary>
/// What the host sees when it is asked whether one specific record may be injected into this
/// invocation.
/// </summary>
/// <param name="Candidate">The record as retrieval ranked it, carrying the score and every ranking component.</param>
/// <param name="Current">
/// The same record as the final pre-injection re-read found it, and the version that would actually
/// be rendered. It is already known to be readable in scope and in an eligible status; the host's
/// decision is a further, independent gate on top of that.
/// </param>
/// <param name="SharedByGrant">
/// <see langword="true"/> when <paramref name="Current"/> belongs to another scope and the store
/// reported it readable only through an active <see cref="ExperienceGrant"/>. A host that trusts
/// borrowed experience less than its own can deny on this alone; it is the re-read's answer, so a
/// grant that has since expired or been revoked never shows up as <see langword="true"/> here.
/// </param>
/// <param name="PermittingGrantId">
/// <em>Which</em> grant permitted it, when <paramref name="SharedByGrant"/> is
/// <see langword="true"/>: the one the store's own predicate used for this re-read, which is also the
/// grant the access row for this delivery names. A host can deny one specific grant's records, or
/// correlate what it injected with the sharing trail, instead of only knowing that some grant applied.
/// <see langword="null"/> when the record is the requester's own, or when the store did not say.
/// </param>
/// <param name="GrantDisclosure">
/// The permitting grant's <see cref="ExperienceGrant.Disclosure"/>, from the same re-read, when
/// <paramref name="SharedByGrant"/> is <see langword="true"/>: what the block will show of this record.
/// Under <see cref="ExperienceGrantDisclosure.LessonOnly"/> the <c>Tried:</c> and <c>Worked:</c> lines are omitted. A
/// store that did not report a level is shown here, and rendered, as
/// <see cref="ExperienceGrantDisclosure.LessonOnly"/>. <see langword="null"/> when the record is the
/// requester's own. It is informational: a host may deny on it, but nothing it returns can widen it.
/// Under <see cref="ExperienceGrantDisclosure.LessonApproachAndArguments"/> the line may also show argument
/// values; see <paramref name="GrantApproachArguments"/>.
/// </param>
/// <param name="GrantApproachArguments">
/// Under <see cref="ExperienceGrantDisclosure.LessonApproachAndArguments"/>, the argument keys the owner named on
/// the permitting grant, from the same re-read; <see langword="null"/> under every other level, for a record the
/// requester owns, and when the store did not say. The block shows a borrowed value only for a key this names
/// <em>and</em> <see cref="ExperienceInjectionOptions.ApproachArguments"/> names for the same tool. Informational,
/// like <paramref name="GrantDisclosure"/>.
/// </param>
public sealed record ExperienceInjectionDecisionContext(
    RankedExperience Candidate,
    ExperienceRecord Current,
    bool SharedByGrant = false,
    Guid? PermittingGrantId = null,
    ExperienceGrantDisclosure? GrantDisclosure = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? GrantApproachArguments = null)
{
    /// <summary>
    /// The provider's verdict on <see cref="Current"/>: <see langword="true"/> when it will render the record as
    /// model-authored, fenced and labelled. That is when its reflection's authorship is anything but
    /// <see cref="ReflectionAuthorship.Deterministic"/>, its producer names the library's own model-backed reflector,
    /// or, with provenance signing configured, its content is unconfirmed
    /// (<see cref="ExperienceRetrievalService.IsModelAuthored"/>, story 17.2). Decide on this, never on
    /// <c>Current.Reflection.Authorship</c>: the declared value is what a party that can write the store controls.
    /// The provider always sets it; a context built without it defaults to <see langword="true"/> (fail closed).
    /// </summary>
    public bool ModelAuthored { get; init; } = true;
}

/// <summary>
/// Host configuration for <see cref="ExperienceContextProvider"/>.
/// </summary>
/// <remarks>
/// The provider is added to an agent by the host, through
/// <c>ChatClientAgentOptions.AIContextProviders</c>; unlike capture there is no builder extension,
/// because the capture middleware never constructs <c>ChatClientAgentOptions</c> and injection has
/// nothing to hook into a pipeline.
/// </remarks>
public sealed class ExperienceInjectionOptions
{
    /// <summary>
    /// The synchronous form of <see cref="ResolveRequestAsync"/>: turns one invocation into a retrieval request --
    /// the host-established authorization, the exact scope, the task text to match, and any required environment
    /// attributes. Called once per invocation, before the model is called. Set exactly one of this and
    /// <see cref="ResolveRequestAsync"/>; prefer the async form whenever building the request needs I/O.
    /// </summary>
    /// <remarks>
    /// Returning <see langword="null"/> skips injection for that invocation and is not a failure
    /// (<see cref="InjectionOutcome.Skipped"/>). If it throws, nothing is injected, the invocation
    /// runs normally, and the failure is reported through <see cref="OnContextInjected"/> as
    /// <see cref="InjectionOutcome.Failed"/>. The authorization it returns is the host's own: nothing
    /// in the invocation -- and nothing in a retrieved record -- may be used to widen it. The task
    /// text it returns must be non-blank and at most
    /// <see cref="ExperienceCandidateQuery.MaxTaskTextLength"/> characters, or retrieval refuses the
    /// request and the invocation gets no context.
    /// </remarks>
    public Func<ExperienceInjectionContext, RetrieveExperienceRequest?>? ResolveRequest { get; init; }

    /// <summary>
    /// Turns one invocation into a retrieval request, asynchronously: the host-established authorization, the exact
    /// scope, the task text to match, and any required environment attributes. Awaited once per invocation, before the
    /// model is called, with the invocation's <see cref="CancellationToken"/>. Set exactly one of this and
    /// <see cref="ResolveRequest"/>; this is the preferred form, because looking up the caller's authorization and scope
    /// usually needs I/O.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its result is treated exactly as <see cref="ResolveRequest"/>'s: <see langword="null"/> skips injection for that
    /// invocation (<see cref="InjectionOutcome.Skipped"/>); a throw or a faulted task injects nothing, lets the
    /// invocation run normally, and is reported through <see cref="OnContextInjected"/> as
    /// <see cref="InjectionOutcome.Failed"/>; the authorization it returns is the host's own and may not be widened by
    /// anything in the invocation or in a retrieved record; and the task text must be non-blank and at most
    /// <see cref="ExperienceCandidateQuery.MaxTaskTextLength"/> characters.
    /// </para>
    /// <para>
    /// <b>Cancellation.</b> An <see cref="OperationCanceledException"/> raised while the invocation's own token is
    /// cancelled propagates to the caller, as the provider's cancellation does everywhere else; any other cancellation
    /// is a failure like any other throw. The time it takes is host time, spent before the model is called and on top
    /// of the provider's own pre-model budget; the provider puts no timeout around it.
    /// </para>
    /// </remarks>
    public Func<ExperienceInjectionContext, CancellationToken, ValueTask<RetrieveExperienceRequest?>>? ResolveRequestAsync { get; init; }

    /// <summary>
    /// The record and byte bounds one injected block runs under. Defaults to
    /// <see cref="ExperienceInjectionLimits.Default"/> (8 records, 16 KB).
    /// </summary>
    public ExperienceInjectionLimits Limits { get; init; } = ExperienceInjectionLimits.Default;

    /// <summary>
    /// Session tracking, on by default with <see cref="ExperienceInjectionSessionLimits.Default"/> (32 record
    /// deliveries, 64 KB). Set to <see langword="null"/> to turn it off, which restores the behaviour of
    /// earlier previews exactly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With a session supplied, the provider keeps a small state in the session's
    /// <see cref="AgentSession.StateBag"/>, under <see cref="SessionStateKey"/>,
    /// and uses it to do three things across the session's invocations:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>Bound the conversation.</b> The session is given at most these many record
    /// deliveries and bytes; when the budget cannot take another record the outcome is
    /// <see cref="InjectionOutcome.SessionBudgetExhausted"/>.</description></item>
    /// <item><description><b>Never repeat a revision.</b> A record revision the session already holds is
    /// omitted as <see cref="InjectionOmissionReason.AlreadyDelivered"/>; a strictly newer revision is
    /// injected again.</description></item>
    /// <item><description><b>Withdraw what no longer stands.</b> Every record the session holds is re-read on
    /// each invocation, and one that has since been revoked, superseded, erased, or has lost the grant it was
    /// read through is named in a fixed withdrawal notice in that invocation's block, ahead of any new
    /// record.</description></item>
    /// </list>
    /// <para>
    /// <b>It assumes the session keeps what was injected.</b> MAF's <see cref="ChatClientAgent"/> does: an
    /// invocation's request messages, the block included, go into the session's chat history. A host whose
    /// history provider drops injected blocks should turn tracking off, or the deduplication hides a record
    /// the model no longer sees. With no session (<see cref="ExperienceInjectionContext.Session"/> is
    /// <see langword="null"/>) nothing is tracked.
    /// </para>
    /// <para>
    /// When tracking is on, <see cref="Limits"/>' <see cref="ExperienceInjectionLimits.MaxBytes"/> must be at
    /// least <see cref="HistoricalReferenceWriter.RetractionBlockBytes"/>, so a withdrawal notice always fits
    /// in a block; the provider's constructor refuses anything smaller.
    /// </para>
    /// </remarks>
    public ExperienceInjectionSessionLimits? SessionLimits { get; init; } = ExperienceInjectionSessionLimits.Default;

    /// <summary>
    /// The <see cref="AgentSession.StateBag"/> key session tracking keeps its account under. Defaults to
    /// <see cref="ExperienceContextProvider.SessionStateKey"/> (<c>AgentExperience.InjectionSession</c>), so a
    /// host that does not set it reads and writes exactly the key earlier previews did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it is configurable.</b> Each provider's account is its own: its budget, the record revisions it
    /// has delivered, and the withdrawal notices it owes. Two providers on one agent -- over two stores, or two
    /// scopes -- need two keys, or they would read and overwrite one account. Give each a different key and
    /// they keep independent budgets, deduplication and withdrawals within one session. The provider declares
    /// the key as its one <see cref="AIContextProvider.StateKeys"/> entry, and <see cref="ChatClientAgent"/>
    /// refuses two of its own providers that declare the same one, so a collision among them fails when the
    /// agent is built rather than silently sharing an account. That check covers only that agent's own
    /// providers: a component elsewhere that writes the same key to the session's state bag is not checked,
    /// so choose a key no one else writes.
    /// </para>
    /// <para>
    /// <b>Changing it on a live deployment starts every session afresh.</b> A session's existing account stays
    /// under the old key and is no longer read: its budget is new, and a withdrawal notice owed under the old
    /// key is never delivered. Change it only for new sessions, or remove the old key yourself.
    /// </para>
    /// <para>
    /// <b>Validated at construction.</b> <see cref="ExperienceContextProvider"/> refuses a
    /// <see langword="null"/> key with an <see cref="ArgumentNullException"/>, and with an
    /// <see cref="ArgumentException"/> a key that is blank, longer than <see cref="MaxSessionStateKeyLength"/>
    /// characters, contains whitespace or a control, format, private-use, unassigned or surrogate code point,
    /// or equals <see cref="ExperienceCaptureAgentBuilderExtensions.RunIdStateKey"/>, the key capture writes to.
    /// It is validated, and <see cref="ExperienceContextProvider.StateKeys"/> declares it, even with
    /// <see cref="SessionLimits"/> set to <see langword="null"/>.
    /// </para>
    /// </remarks>
    public string SessionStateKey { get; init; } = ExperienceContextProvider.SessionStateKey;

    /// <summary>The most characters <see cref="SessionStateKey"/> may have.</summary>
    public const int MaxSessionStateKeyLength = 128;

    /// <summary>
    /// Optional. The host's risk decision for each candidate, asked once per record immediately after
    /// the final eligibility re-read and immediately before the payload is built. A denial omits that
    /// record whatever its stored confidence or status, is recorded on the result, and never alters
    /// the stored record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fail-closed: a callback that throws, or that returns <see langword="null"/>, denies the record
    /// rather than admitting it. Leave it unset to permit every candidate that survived retrieval and
    /// the final eligibility check.
    /// </para>
    /// <para>
    /// <b>Why it is synchronous.</b> With session tracking on, it runs while the session's lock is held, so a
    /// concurrent invocation on the same session waits for it. Keep it to an in-memory decision. Per-candidate I/O has
    /// no async hook: prefetch what it needs, keyed by scope, in <see cref="ResolveRequestAsync"/>, or decide offline
    /// and hand it the result.
    /// </para>
    /// </remarks>
    public Func<ExperienceInjectionDecisionContext, InjectionDecision>? DecideInjection { get; init; }

    /// <summary>
    /// Optional. What the agent receiving the Historical Reference can and may do, so that a record whose
    /// verified approach calls a tool the agent lacks, or a tool riskier than it may use, is kept out of
    /// the block: omitted as <see cref="InjectionOmissionReason.ToolUnavailable"/> or
    /// <see cref="InjectionOmissionReason.RiskClassExceeded"/>. Leave it <see langword="null"/> (the default)
    /// and nothing is gated: the block and the omissions are byte for byte what they are without it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Checked on the record the final eligibility re-read returned, after
    /// <see cref="InjectionOmissionReason.AlreadyDelivered"/> and before <see cref="DecideInjection"/>. A gated
    /// record is never passed to <see cref="DecideInjection"/>, rendered, charged to the session budget, or
    /// recorded as a run exposure, and its omission carries no detail, so no tool name reaches the result or
    /// telemetry. See <see cref="ReceivingAgentCapabilities"/> for exactly what is checked.
    /// </para>
    /// <para>
    /// The gate sits between the re-read and <see cref="DecideInjection"/>, so it runs after the
    /// <see cref="ExperienceInjectionLimits.MaxRecords"/> cut: a gated record still takes a record slot, and the
    /// agent may get fewer records than the limit. It checks only the tools of the attempt the <c>Worked:</c> line names (not an earlier attempt), not
    /// tool names the lesson text may mention. <see cref="ReceivingAgentCapabilities.ToolRiskClasses"/> has no
    /// effect without <see cref="ReceivingAgentCapabilities.MaxRiskClass"/>; <see cref="ToolRiskClass.Critical"/>
    /// as the maximum disables the risk check; and an available tool missing from
    /// <see cref="ReceivingAgentCapabilities.ToolRiskClasses"/> counts as <see cref="ToolRiskClass.Critical"/>. A
    /// gated borrowed record still writes a grant access row, because the store disclosed it on the re-read, but
    /// it is never injected or recorded as an exposure.
    /// </para>
    /// <para>
    /// Passing the gate grants nothing: the host's approval boundary still decides every tool call.
    /// </para>
    /// <para>
    /// <b>Validated and snapshotted at construction.</b> <see cref="ExperienceContextProvider"/> copies the
    /// declaration into ordinal collections when it is built, so a later edit changes nothing, and refuses a
    /// <see langword="null"/>, empty or whitespace tool name (in either collection) or a risk class that is not a defined <see cref="ToolRiskClass"/>.
    /// </para>
    /// </remarks>
    public ReceivingAgentCapabilities? ReceivingAgent { get; init; }

    /// <summary>
    /// Whether records whose free text a model wrote (<see cref="Reflection.Authorship"/> is anything but
    /// <see cref="ReflectionAuthorship.Deterministic"/>, or <see cref="Reflection.Producer"/> names the library's own
    /// <see cref="Reflections.ChatClientExperienceReflector"/>) may be injected. <see cref="ModelAuthoredLessonPolicy.Include"/>
    /// (the default) injects them with their model-written fields between
    /// <see cref="HistoricalReferenceWriter.ModelAuthoredLine"/> and <see cref="HistoricalReferenceWriter.ModelAuthoredEndLine"/>;
    /// <see cref="ModelAuthoredLessonPolicy.Exclude"/> omits them as <see cref="InjectionOmissionReason.ModelAuthored"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This, and the label, are the controls to rely on for model-authored lessons; finalization's content guard is a
    /// best-effort filter. Authorship is what the reflector declared, except that a record the library's own
    /// <see cref="Reflections.ChatClientExperienceReflector"/> wrote counts as model-authored by its producer, including
    /// one written before that reflector declared authorship (story 17.1). A third-party model-backed reflector that does
    /// not declare it reads as deterministic.
    /// </para>
    /// <para>
    /// With <see cref="ModelAuthoredLessonPolicy.Exclude"/>, the provider sets
    /// <see cref="RetrieveExperienceRequest.ExcludeModelAuthored"/> on the resolved request (story 14.4), so every
    /// candidate source leaves model-authored records out before its own limit and the candidate window
    /// (<see cref="RetrieveExperienceRequest.Limit"/>, or the policy's candidate limit) is filled with deterministic
    /// records. The retrieval service then excludes any model-authored record a source still returned, as
    /// <see cref="RetrievalExclusionReason.ModelAuthored"/> in the result's <see cref="ExperienceInjectionResult.Excluded"/>:
    /// a source that does not honour the request, or a stored authorship flag that disagrees with the opened record,
    /// which takes a place in that source's candidate window but never a result slot. (A PostgreSQL row sealed without
    /// its authorship flag is left out by the source itself, story 17.1.) The provider still checks every candidate before the
    /// <see cref="ExperienceInjectionLimits.MaxRecords"/> cut, and the re-read record again before the capability gate
    /// and <see cref="DecideInjection"/>, and omits a model-authored one as <see cref="InjectionOmissionReason.ModelAuthored"/>:
    /// an omitted record is never re-read (unless it changed between the two checks), shown to the host's decision,
    /// rendered, charged to the session budget, or recorded as a run exposure, and it withdraws nothing. Authorship is read from the stored reflection by the same rule the stores apply; its <see cref="Reflection.Producer"/> counts only when it names the library's own reflector. A value
    /// that is not a defined <see cref="ModelAuthoredLessonPolicy"/> is refused when the provider is constructed.
    /// </para>
    /// </remarks>
    public ModelAuthoredLessonPolicy ModelAuthoredLessons { get; init; } = ModelAuthoredLessonPolicy.Include;

    /// <summary>
    /// Optional. Receives the content-free account of every injection attempt -- injected, empty,
    /// skipped, timed out, denied, or failed -- including each omission and its reason. Exceptions
    /// thrown by the callback are swallowed.
    /// </summary>
    public Action<ExperienceInjectionResult>? OnContextInjected { get; init; }

    /// <summary>
    /// The clock the final eligibility re-check measures its timeout and record expiry with.
    /// Defaults to <see cref="TimeProvider.System"/>.
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Optional, empty by default. Per tool name, the argument keys whose values the injected
    /// <c>Tried:</c> lines may show next to that tool's name, for example
    /// <c>ApproachArguments = { ["run_incident_check"] = ["strategy"] }</c>. Leave it empty and the
    /// block is byte for byte what it is without this option: tool names only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it exists.</b> Two approaches that call the same tool with different arguments --
    /// <c>retry_refund(delay: 0)</c> failing and <c>retry_refund(delay: 30)</c> working -- otherwise
    /// render as the same line. A host that knows which of its arguments carry the <em>choice</em>
    /// can name them here instead of writing a reflector to restate them in prose.
    /// </para>
    /// <para>
    /// <b>What an allowlisted value is, and every bound on it.</b>
    /// </para>
    /// <list type="bullet">
    /// <item><description>It is the value the record stores, which is what the capture-time
    /// <c>ISanitizer</c> returned -- never the raw value. A value the sanitizer redacted is shown in its
    /// redacted form (the default redactor leaves an empty string, shown as <c>""</c>); a key it omitted
    /// is absent and shows nothing.</description></item>
    /// <item><description>Only a string, a number or a boolean is shown (and a null, as <c>null</c>,
    /// and an enum, as its quoted name).
    /// An object, an array or any other shape is written as
    /// <see cref="HistoricalReferenceWriter.ArgumentNotShown"/>, and its content is never
    /// read.</description></item>
    /// <item><description>A key may be a dotted path into an object- or array-valued argument --
    /// <c>options.mode</c>, or <c>targets.0</c> for an array's first element -- and then only the scalar the
    /// path ends on is shown, as <c>options.mode="fast"</c>, under every bound here. A path that ends on an
    /// object or an array gets <see cref="HistoricalReferenceWriter.ArgumentNotShown"/>; a container is never
    /// shown whole, and nothing beside the path's own steps is read. A key that exists literally at the top
    /// level (a key named <c>options.mode</c>) is matched first, exactly as before paths existed. A path that
    /// cannot be walked -- a missing step, a step through a scalar, an index that is not a plain non-negative
    /// number (<c>01</c> is not) -- shows nothing.</description></item>
    /// <item><description>A string's whitespace, control and format characters are collapsed to single
    /// spaces and trimmed from the ends (so an all-whitespace value reads as <c>""</c>),
    /// the block's markers are neutralized as in every other field, it is cut to
    /// <see cref="HistoricalReferenceWriter.MaxArgumentValueLength"/> characters with the cut marked,
    /// and it is written in double quotes, with every double quote or look-alike inside it turned into
    /// a single quote and every <c>-&gt;</c> broken up, so it can neither add a line, nor end its own
    /// quotes, nor spell the step separator.</description></item>
    /// <item><description>All the arguments on one line together are capped at
    /// <see cref="HistoricalReferenceWriter.MaxApproachArgumentsLength"/> characters; an argument that
    /// would pass the cap is left out whole, with every later one, and the line says so. The record as
    /// a whole still counts against <see cref="ExperienceInjectionLimits.MaxBytes"/>, which drops it
    /// whole rather than cutting it.</description></item>
    /// <item><description>A value of any key not listed for that exact tool name is never shown: the
    /// writer looks keys up from this list and never enumerates a call's arguments. Tool names and
    /// keys are matched ordinally.</description></item>
    /// <item><description>A record borrowed through a sharing grant shows an argument value only when its grant is
    /// <see cref="ExperienceGrantDisclosure.LessonApproachAndArguments"/>, and then only for a key the owner named
    /// on that grant (<see cref="ExperienceGrantRequest.ApproachArguments"/>) <em>and</em> this allowlist names
    /// for the same tool. This allowlist is the reader's configuration and can only narrow the owner's consent,
    /// never widen it. Under <see cref="ExperienceGrantDisclosure.LessonAndApproach"/> a borrowed line is tool
    /// names only, and under <see cref="ExperienceGrantDisclosure.LessonOnly"/> it is withheld.</description></item>
    /// </list>
    /// <para>
    /// <b>Allowlisting a key lets a later model read that argument's values.</b> A value is still text
    /// the captured run's model chose, from whatever was in its context, and the sanitizer classifies
    /// by field name rather than content. List only keys whose values are choices from a small, known
    /// set -- a strategy, a mode, a delay -- never free text, identifiers of people, or anything a
    /// secret could be written into. The authorization boundary outside the block still decides what
    /// a later agent may call, whatever a shown value says.
    /// </para>
    /// <para>
    /// <b>Snapshotted at construction.</b> <see cref="ExperienceContextProvider"/> validates and
    /// copies this dictionary when it is constructed, so changing it afterwards changes nothing about
    /// that provider. A blank tool name, a <see langword="null"/> key list, or a key that is blank,
    /// longer than 64 characters, listed twice, or contains whitespace, a control, format or surrogate
    /// character, or one of <c>= ( ) , " \</c> is refused with an <see cref="ArgumentException"/> there.
    /// </para>
    /// </remarks>
    public IDictionary<string, IReadOnlyList<string>> ApproachArguments { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>
    /// How much a <c>Tried:</c> line says about an attempt that ended with an error.
    /// <see cref="AttemptFailureDetail.ErrorClass"/> (the default) shows the error's class -- an exception type name,
    /// an exit code, an HTTP status, a POSIX errno name or a timeout, built only from tokens the library recognises
    /// and never from other text in the error. <see cref="AttemptFailureDetail.Excerpt"/> adds the error's first
    /// line, cut to <see cref="HistoricalReferenceWriter.MaxErrorExcerptLength"/> characters, neutralized and quoted;
    /// <see cref="AttemptFailureDetail.None"/> shows <c>failed</c> alone.
    /// </summary>
    /// <remarks>
    /// <see cref="AttemptFailureDetail.Excerpt"/> lets captured error text reach a later model: error messages often
    /// echo paths, hosts, identifiers or content a tool read, which the capture-time sanitizer classified only by
    /// field. A record borrowed through a sharing grant never shows an excerpt, only the class. A value that is not
    /// a defined <see cref="AttemptFailureDetail"/> is refused when the provider is constructed.
    /// </remarks>
    public AttemptFailureDetail FailureDetail { get; init; } = AttemptFailureDetail.ErrorClass;

    /// <summary>
    /// How the block lays out each record. <see cref="HistoricalReferenceRendering.Compact"/> (the default) keeps the
    /// framing, fences, <c>Shared:</c> line, confidence, verification and lifecycle status and the decision content, and
    /// adds a <c>Matched:</c> line; it leaves out identifiers, the ranking arithmetic, timestamps, the captured
    /// environment fingerprint and the evidence count (see <see cref="HistoricalReferenceRendering.Compact"/>).
    /// <see cref="HistoricalReferenceRendering.Verbose"/> is the earlier layout, byte for byte except that record text
    /// starting a line with <c>Matched:</c> is now neutralized.
    /// </summary>
    /// <remarks>
    /// <see cref="Limits"/> and <see cref="SessionLimits"/> apply to the block as rendered, so more compact records fit in
    /// the same budget. A value that is not a defined <see cref="HistoricalReferenceRendering"/> is refused when the
    /// provider is constructed.
    /// </remarks>
    public HistoricalReferenceRendering Rendering { get; init; } = HistoricalReferenceRendering.Compact;

    /// <summary>
    /// The chat role the block's message is sent in: <see cref="HistoricalReferenceMessageRole.User"/> (the default)
    /// or <see cref="HistoricalReferenceMessageRole.System"/>. Either way the message carries
    /// <c>AdditionalProperties["AgentExperience.HistoricalReference"] = true</c> and the same text. A value that is not a
    /// defined <see cref="HistoricalReferenceMessageRole"/> is refused when the provider is constructed.
    /// </summary>
    /// <remarks>
    /// Some chat APIs reject a system message that is not the first, hoist or merge system messages, or allow only one,
    /// and the block arrives alongside the invocation's own messages: test <see cref="HistoricalReferenceMessageRole.System"/>
    /// with your provider before relying on it.
    /// </remarks>
    public HistoricalReferenceMessageRole MessageRole { get; init; } = HistoricalReferenceMessageRole.User;

    /// <summary>
    /// Validates this instance, in the same style as
    /// <see cref="ExperienceCaptureAgentBuilderExtensions.UseExperienceCapture(Microsoft.Agents.AI.AIAgentBuilder, AgentExperience.Core.Capture.IExperienceCaptureService, ExperienceCaptureOptions)"/>: a misconfigured
    /// provider fails when it is constructed, not on the first invocation it silently does nothing on.
    /// </summary>
    /// <param name="paramName">The parameter name to report on a validation failure.</param>
    /// <returns>The validated snapshot of <see cref="ApproachArguments"/>.</returns>
    /// <exception cref="ArgumentNullException"><see cref="Limits"/>, <see cref="TimeProvider"/>, <see cref="ApproachArguments"/>, or <see cref="SessionStateKey"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Not exactly one of <see cref="ResolveRequest"/> and <see cref="ResolveRequestAsync"/> is set, or <see cref="ApproachArguments"/> or <see cref="SessionStateKey"/> is malformed, or <see cref="SessionLimits"/> is set and <see cref="Limits"/> cannot fit one withdrawal notice.</exception>
    internal ApproachArgumentAllowlist Validate(string paramName)
    {
        if (ResolveRequest is null == (ResolveRequestAsync is null))
        {
            throw new ArgumentException(
                $"Exactly one of {nameof(ResolveRequest)} and {nameof(ResolveRequestAsync)} must be set.",
                $"{paramName}.{nameof(ResolveRequest)}/{nameof(ResolveRequestAsync)}");
        }

        ArgumentNullException.ThrowIfNull(Limits, $"{paramName}.{nameof(Limits)}");
        ArgumentNullException.ThrowIfNull(TimeProvider, $"{paramName}.{nameof(TimeProvider)}");
        ArgumentNullException.ThrowIfNull(ApproachArguments, $"{paramName}.{nameof(ApproachArguments)}");
        ValidateSessionStateKey(SessionStateKey, $"{paramName}.{nameof(SessionStateKey)}");
        if (!Enum.IsDefined(ModelAuthoredLessons))
        {
            throw new ArgumentException(
                "ModelAuthoredLessons must be a defined ModelAuthoredLessonPolicy value.",
                $"{paramName}.{nameof(ModelAuthoredLessons)}");
        }

        if (!Enum.IsDefined(FailureDetail))
        {
            throw new ArgumentException(
                "FailureDetail must be a defined AttemptFailureDetail value.",
                $"{paramName}.{nameof(FailureDetail)}");
        }

        if (!Enum.IsDefined(Rendering))
        {
            throw new ArgumentException(
                "Rendering must be a defined HistoricalReferenceRendering value.",
                $"{paramName}.{nameof(Rendering)}");
        }

        if (!Enum.IsDefined(MessageRole))
        {
            throw new ArgumentException(
                "MessageRole must be a defined HistoricalReferenceMessageRole value.",
                $"{paramName}.{nameof(MessageRole)}");
        }

        if (SessionLimits is not null && Limits.MaxBytes < HistoricalReferenceWriter.RetractionBlockBytes)
        {
            // A notice that can never fit would stay owed forever and, since no record is written while
            // one is owed, silence injection for the rest of the session.
            throw new ArgumentException(
                $"With session tracking on, the block byte budget must be at least {HistoricalReferenceWriter.RetractionBlockBytes} bytes, so a withdrawal notice always fits. Raise Limits.MaxBytes or set SessionLimits to null.",
                $"{paramName}.{nameof(Limits)}");
        }

        return ApproachArgumentAllowlist.From(ApproachArguments, $"{paramName}.{nameof(ApproachArguments)}");
    }

    /// <summary>Refuses a <see cref="SessionStateKey"/> that could not name one account, unambiguously, in a state bag.</summary>
    private static void ValidateSessionStateKey(string? key, string paramName)
    {
        ArgumentNullException.ThrowIfNull(key, paramName);
        if (key.Length == 0 || key.Length > MaxSessionStateKeyLength)
        {
            throw new ArgumentException(
                $"The session state key must be 1 to {MaxSessionStateKeyLength} characters long.",
                paramName);
        }

        var index = 0;
        while (index < key.Length)
        {
            if (System.Text.Rune.DecodeFromUtf16(key.AsSpan(index), out var rune, out var consumed) != System.Buffers.OperationStatus.Done
                || System.Text.Rune.IsWhiteSpace(rune)
                || System.Text.Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Control
                    or System.Globalization.UnicodeCategory.Format
                    or System.Globalization.UnicodeCategory.PrivateUse
                    or System.Globalization.UnicodeCategory.OtherNotAssigned
                    or System.Globalization.UnicodeCategory.Surrogate)
            {
                throw new ArgumentException(
                    "The session state key may not contain whitespace, or a control, format, private-use, unassigned or surrogate code point.",
                    paramName);
            }

            index += consumed;
        }

        if (string.Equals(key, ExperienceCaptureAgentBuilderExtensions.RunIdStateKey, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The session state key may not be '{ExperienceCaptureAgentBuilderExtensions.RunIdStateKey}', the key capture writes the run ID to.",
                paramName);
        }
    }
}

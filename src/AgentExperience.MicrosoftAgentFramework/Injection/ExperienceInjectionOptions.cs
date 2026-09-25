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
/// <see cref="AgentSession"/> is reused across turns, an earlier block can remain in the session's
/// conversation, so what the model sees can exceed this budget several times over. See
/// <see cref="ExperienceContextProvider"/>.
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
    /// The default bound on the whole final eligibility re-check: 2 seconds. The check is one batched
    /// store read of up to <see cref="MaxRecords"/> records (one round trip against the PostgreSQL
    /// adapter; one read per record against a store that keeps the port's default) on the invocation's
    /// critical path, so it needs a bound of its own -- retrieval's timeout has already been spent by
    /// the time it starts.
    /// </summary>
    public static readonly TimeSpan DefaultEligibilityCheckTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The largest permitted <see cref="EligibilityCheckTimeout"/>: one day, matching <see cref="RetrievalPolicy.MaxTimeout"/>.</summary>
    public static readonly TimeSpan MaxEligibilityCheckTimeout = RetrievalPolicy.MaxTimeout;

    /// <summary>The documented defaults: at most 8 records and 16 KB of UTF-8, re-checked within 2 seconds.</summary>
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
/// receive, and not this provider's own earlier blocks. It may be empty, so read it defensively
/// (<c>LastOrDefault(...)</c>, never <c>Last()</c>: a resolver that throws injects nothing for the
/// rest of that agent's life and says so only through the result callback).
/// </param>
/// <param name="Session">The session associated with the invocation, or <see langword="null"/> when the caller passed none.</param>
/// <param name="Agent">The agent being invoked.</param>
public sealed record ExperienceInjectionContext(
    IReadOnlyList<ChatMessage> Messages,
    AgentSession? Session,
    AIAgent Agent);

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
/// Under <see cref="ExperienceGrantDisclosure.LessonOnly"/> the <c>Approach:</c> line is omitted. A
/// store that did not report a level is shown here, and rendered, as
/// <see cref="ExperienceGrantDisclosure.LessonOnly"/>. <see langword="null"/> when the record is the
/// requester's own. It is informational: a host may deny on it, but nothing it returns can widen it.
/// </param>
public sealed record ExperienceInjectionDecisionContext(
    RankedExperience Candidate,
    ExperienceRecord Current,
    bool SharedByGrant = false,
    Guid? PermittingGrantId = null,
    ExperienceGrantDisclosure? GrantDisclosure = null);

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
    /// Turns one invocation into a retrieval request: the host-established authorization, the exact
    /// scope, the task text to match, and any required environment attributes. Called once per
    /// invocation, before the model is called.
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
    public required Func<ExperienceInjectionContext, RetrieveExperienceRequest?> ResolveRequest { get; init; }

    /// <summary>
    /// The record and byte bounds one injected block runs under. Defaults to
    /// <see cref="ExperienceInjectionLimits.Default"/> (8 records, 16 KB).
    /// </summary>
    public ExperienceInjectionLimits Limits { get; init; } = ExperienceInjectionLimits.Default;

    /// <summary>
    /// Optional. The host's risk decision for each candidate, asked once per record immediately after
    /// the final eligibility re-read and immediately before the payload is built. A denial omits that
    /// record whatever its stored confidence or status, is recorded on the result, and never alters
    /// the stored record.
    /// </summary>
    /// <remarks>
    /// Fail-closed: a callback that throws, or that returns <see langword="null"/>, denies the record
    /// rather than admitting it. Leave it unset to permit every candidate that survived retrieval and
    /// the final eligibility check.
    /// </remarks>
    public Func<ExperienceInjectionDecisionContext, InjectionDecision>? DecideInjection { get; init; }

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
    /// <c>Approach:</c> line may show next to that tool's name, for example
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
    /// <item><description>A record borrowed through a sharing grant never shows an argument value,
    /// whatever its grant's disclosure level. The allowlist is the reader's configuration, not the
    /// owner's, and a <see cref="ExperienceGrantDisclosure.LessonAndApproach"/> grant was issued as
    /// consent to show tool names only.</description></item>
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
    /// Validates this instance, in the same style as
    /// <see cref="ExperienceCaptureAgentBuilderExtensions.UseExperienceCapture(Microsoft.Agents.AI.AIAgentBuilder, AgentExperience.Core.Capture.IExperienceCaptureService, ExperienceCaptureOptions)"/>: a misconfigured
    /// provider fails when it is constructed, not on the first invocation it silently does nothing on.
    /// </summary>
    /// <param name="paramName">The parameter name to report on a validation failure.</param>
    /// <returns>The validated snapshot of <see cref="ApproachArguments"/>.</returns>
    /// <exception cref="ArgumentNullException"><see cref="ResolveRequest"/>, <see cref="Limits"/>, <see cref="TimeProvider"/>, or <see cref="ApproachArguments"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><see cref="ApproachArguments"/> is malformed.</exception>
    internal ApproachArgumentAllowlist Validate(string paramName)
    {
        ArgumentNullException.ThrowIfNull(ResolveRequest, $"{paramName}.{nameof(ResolveRequest)}");
        ArgumentNullException.ThrowIfNull(Limits, $"{paramName}.{nameof(Limits)}");
        ArgumentNullException.ThrowIfNull(TimeProvider, $"{paramName}.{nameof(TimeProvider)}");
        ArgumentNullException.ThrowIfNull(ApproachArguments, $"{paramName}.{nameof(ApproachArguments)}");
        return ApproachArgumentAllowlist.From(ApproachArguments, $"{paramName}.{nameof(ApproachArguments)}");
    }
}

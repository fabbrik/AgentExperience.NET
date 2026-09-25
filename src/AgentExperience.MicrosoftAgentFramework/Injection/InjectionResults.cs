using AgentExperience.Core.Retrieval;

namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>
/// The host's decision on whether one retrieved record may be injected into this invocation at all.
/// </summary>
/// <remarks>
/// <para>
/// This mirrors <see cref="AgentExperience.Core.Finalization.StorageDecision"/> and exists for the
/// same reason: risk policy belongs to the host, not to this library. The decision is asked for each
/// candidate individually, <em>after</em> the final eligibility re-read, and a denial wins whatever
/// the record's stored confidence or lifecycle status says. It is a read-side decision only: a denial
/// is recorded on the injection result and never writes to, re-scores, or re-statuses the record.
/// </para>
/// <para>
/// <see cref="Reason"/> is content-free: it is surfaced back to the host on the injection result and
/// must never carry record payload, captured content, or private reasoning.
/// </para>
/// </remarks>
/// <param name="Permitted"><see langword="true"/> when the host permits this record to be injected into this invocation.</param>
/// <param name="Reason">Optional, auditable, content-free explanation of the decision (most usefully, why injection was denied).</param>
public sealed record InjectionDecision(bool Permitted, string? Reason = null)
{
    /// <summary>A decision that permits injection, with no reason attached.</summary>
    public static InjectionDecision Permit { get; } = new(Permitted: true);

    /// <summary>Creates a decision that denies injection.</summary>
    /// <param name="reason">A content-free explanation of why injection was denied.</param>
    public static InjectionDecision Deny(string? reason = null) => new(Permitted: false, reason);
}

/// <summary>
/// Why a record that retrieval ranked did not end up in the injected Historical Reference. Every
/// omission is recorded with one of these, so "nothing applied" is always distinguishable from
/// "something applied but was not injected here".
/// </summary>
public enum InjectionOmissionReason
{
    /// <summary>
    /// The final pre-injection re-read found the record no longer eligible for reuse. That is the
    /// same set of checks retrieval applies, re-applied to the record as it stands now: its status is
    /// outside <see cref="ExperienceRetrievalService.EligibleStatuses"/> (revoked, quarantined,
    /// contested, superseded, …), its reuse confidence has fallen below the policy's floor, its last
    /// lifecycle activity is older than the policy's <see cref="RetrievalPolicy.MaxAge"/>, or it no
    /// longer satisfies the request's required environment attributes. The omission's detail says
    /// which.
    /// </summary>
    Ineligible,

    /// <summary>
    /// The final pre-injection re-read could not read the record in the request's scope: it is gone,
    /// it moved out of scope, the read was denied or refused, or the store failed. These are
    /// deliberately one reason and not several, because a record outside the caller's scope must be
    /// indistinguishable from a missing one.
    /// </summary>
    Unreadable,

    /// <summary>The host's <see cref="InjectionDecision"/> denied this record at injection time.</summary>
    HostDenied,

    /// <summary>
    /// The record ranked below the top <see cref="ExperienceInjectionLimits.MaxRecords"/> and was never
    /// re-read or rendered.
    /// </summary>
    OverRecordLimit,

    /// <summary>
    /// The record did not fit inside <see cref="ExperienceInjectionLimits.MaxBytes"/> once the
    /// higher-ranked records had been written, so the whole record was dropped. A record is never cut
    /// to fit -- including a single record larger than the entire budget, which is omitted rather than
    /// truncated.
    /// </summary>
    OverByteBudget,

    /// <summary>
    /// Session tracking: this revision of the record was already delivered earlier in the same
    /// <see cref="Microsoft.Agents.AI.AgentSession"/> and has not been withdrawn, so the session's
    /// conversation already carries it. A strictly newer revision is injected again. Checked on the ranked
    /// revision before the record limit is applied, so a duplicate never takes a slot, and again on the
    /// re-read revision.
    /// </summary>
    AlreadyDelivered,

    /// <summary>
    /// Session tracking: the session's <see cref="ExperienceInjectionSessionLimits"/> could not take this
    /// record -- its record budget is spent, or the block with this record would not fit in what its byte
    /// budget has left. Dropped whole, like <see cref="OverByteBudget"/>.
    /// </summary>
    OverSessionBudget,
}

/// <summary>
/// One record that was ranked but not injected, named so a host can audit what the agent did
/// <em>not</em> see and why.
/// </summary>
/// <param name="ExperienceId">The omitted record.</param>
/// <param name="Reason">Which step omitted it.</param>
/// <param name="Detail">Optional, content-free elaboration -- for example the host's own denial reason. Never carries record payload.</param>
public sealed record OmittedExperience(Guid ExperienceId, InjectionOmissionReason Reason, string? Detail = null);

/// <summary>What one attempt to inject a Historical Reference ended as.</summary>
public enum InjectionOutcome
{
    /// <summary>At least one record survived every check and a Historical Reference block was injected.</summary>
    Injected,

    /// <summary>
    /// Nothing was injected because nothing survived: retrieval matched no eligible record, or every
    /// candidate it ranked was omitted. The agent runs normally, with no context and nothing fabricated.
    /// </summary>
    NothingToInject,

    /// <summary>The resolver returned <see langword="null"/>: this invocation opted out of injection. Not a failure.</summary>
    Skipped,

    /// <summary>Retrieval exceeded its own timeout. Nothing was injected; the timeout is reported, never thrown.</summary>
    RetrievalTimedOut,

    /// <summary>The request scope lay outside the host-established authorization, so retrieval refused it. Nothing was injected.</summary>
    RetrievalDenied,

    /// <summary>Retrieval failed (a store or channel failure). Nothing was injected; the failure is reported, never thrown.</summary>
    RetrievalFailed,

    /// <summary>The provider itself failed -- a throwing resolver, or an unexpected exception anywhere inside it. Nothing was injected; the failure is reported, never thrown.</summary>
    Failed,

    /// <summary>
    /// Session tracking: a block was injected that carries withdrawal notices for records delivered
    /// earlier in the session, and no new record. Takes precedence over <see cref="NothingToInject"/>,
    /// <see cref="SessionBudgetExhausted"/> and the retrieval outcomes, because a block was injected; a
    /// retrieval failure is still carried on <see cref="ExperienceInjectionResult.Failure"/>.
    /// </summary>
    Retracted,

    /// <summary>
    /// Session tracking: nothing was injected because the session's <see cref="ExperienceInjectionSessionLimits"/>
    /// cannot take another record. When the budget was already spent, retrieval was not run at all;
    /// otherwise the records that survived are omitted as <see cref="InjectionOmissionReason.OverSessionBudget"/>.
    /// </summary>
    SessionBudgetExhausted,
}

/// <summary>
/// Why an injection attempt could not produce what it was asked for.
/// </summary>
/// <param name="Reason">A human-readable, content-free explanation. Safe to log or surface.</param>
/// <param name="Exception">
/// The original failure, when one was caught. Not held to the content-free standard -- a driver or
/// resolver message can quote SQL text, parameters, or caller data. Treat it as local diagnostics only.
/// </param>
public sealed record InjectionFailure(string Reason, Exception? Exception);

/// <summary>
/// The account of one <see cref="ExperienceContextProvider"/> call, handed to
/// <see cref="ExperienceInjectionOptions.OnContextInjected"/>. It is deliberately content-free: it
/// names which records were injected and which were not, never what they said.
/// </summary>
/// <remarks>
/// <para>
/// <b>Injection cannot be taken back, only withdrawn.</b> <see cref="InjectedExperienceIds"/> is a
/// record of what this invocation handed to the model. A block injected into an
/// <see cref="Microsoft.Agents.AI.AgentSession"/> persists in that session's conversation, so on a later
/// turn the model still sees it. With session tracking on (the default), a record delivered earlier in
/// the session and since revoked, superseded, erased or un-granted is named on a later turn in
/// <see cref="RetractedExperienceIds"/>, and that turn's block tells the model it is withdrawn. That notice
/// is advisory: the earlier text is not removed, and a model that read it cannot be made to forget it.
/// See <see cref="ExperienceContextProvider"/>.
/// </para>
/// </remarks>
/// <param name="Outcome">What the attempt ended as.</param>
/// <param name="InjectedExperienceIds">The records actually written into the payload, in rank order. Empty unless <see cref="Outcome"/> is <see cref="InjectionOutcome.Injected"/>.</param>
/// <param name="Omitted">Every ranked record that was not injected, with the reason it was not.</param>
/// <param name="Excluded">
/// Candidates an eligibility check in Core removed <em>before</em> ranking, copied from the
/// retrieval result. They never became candidates for injection at all, so they are not in
/// <see cref="Omitted"/>, and the list is not a complete account of everything filtered -- scope,
/// status, and the confidence floor are applied in the database.
/// </param>
/// <param name="Truncated">
/// <see langword="true"/> when the search hit its candidate ceiling, copied from the retrieval
/// result: more records matched than were ever ranked, so a record that would have outranked what
/// was injected may simply not have been considered. Treat an injected block built on a truncated
/// search as a partial answer.
/// </param>
/// <param name="EnvironmentUnrestricted"><see langword="true"/> when the request named no required environment attributes, copied from the retrieval result, so every candidate passed that check unconditionally.</param>
/// <param name="PayloadBytes">The UTF-8 size of the injected block, at most <see cref="ExperienceInjectionLimits.MaxBytes"/>; 0 when nothing was injected. A block that carries only withdrawal notices counts too.</param>
/// <param name="CorrelationId">The retrieval request's correlation identifier, echoed back on every outcome including a timeout.</param>
/// <param name="Failure">Why the attempt failed, on <see cref="InjectionOutcome.RetrievalFailed"/> or <see cref="InjectionOutcome.Failed"/>; otherwise <see langword="null"/>.</param>
/// <param name="VectorFallback">
/// Why the vector channel contributed nothing to the retrieval this block was built from, when it
/// did not; copied from the retrieval result and <see langword="null"/> when both channels ran. A
/// block built on a degraded, text-only channel is a complete answer but a narrower one, and a host
/// auditing injection should be able to tell that apart from a clean match.
/// </param>
public sealed record ExperienceInjectionResult(
    InjectionOutcome Outcome,
    IReadOnlyList<Guid> InjectedExperienceIds,
    IReadOnlyList<OmittedExperience> Omitted,
    IReadOnlyList<ExcludedExperience> Excluded,
    bool Truncated,
    bool EnvironmentUnrestricted,
    int PayloadBytes,
    string? CorrelationId,
    InjectionFailure? Failure,
    VectorChannelFallback? VectorFallback = null)
{
    /// <summary>Whether a Historical Reference block was actually injected into the invocation.</summary>
    public bool Injected => Outcome is InjectionOutcome.Injected;

    /// <summary>How many records the injected block carried.</summary>
    public int InjectedCount => InjectedExperienceIds.Count;

    /// <summary>Whether this block was built from the text channel alone, that is, whether <see cref="VectorFallback"/> is present.</summary>
    public bool TextOnly => VectorFallback is not null;

    /// <summary>
    /// Session tracking: the records delivered earlier in this session whose withdrawal notice this
    /// invocation's block carries, in the order written. Empty with tracking off, with no session, or when
    /// nothing delivered earlier has been withdrawn. IDs only, as everywhere on this result.
    /// </summary>
    public IReadOnlyList<Guid> RetractedExperienceIds { get; init; } = [];

    /// <summary>
    /// Session tracking: what the session has been given, counting this invocation's block as though it
    /// succeeds; <see langword="null"/> with tracking off or no session. It is charged for real only once MAF
    /// reports the invocation succeeded.
    /// </summary>
    public ExperienceInjectionSessionUsage? Session { get; init; }
}

/// <summary>
/// What one <see cref="Microsoft.Agents.AI.AgentSession"/> has been given, against its
/// <see cref="ExperienceInjectionSessionLimits"/>. Counts only: no record content.
/// </summary>
/// <param name="RecordsUsed">Record deliveries charged to the session, this invocation's included.</param>
/// <param name="BytesUsed">UTF-8 bytes of Historical Reference charged to the session, this invocation's block included. Can exceed <paramref name="MaxBytes"/> only by withdrawal notices, which are never refused.</param>
/// <param name="MaxRecords">The session's record budget.</param>
/// <param name="MaxBytes">The session's byte budget.</param>
/// <param name="TrackedRecords">How many distinct records the session holds as delivered and not withdrawn, each re-checked on every invocation.</param>
public sealed record ExperienceInjectionSessionUsage(
    int RecordsUsed,
    long BytesUsed,
    int MaxRecords,
    int MaxBytes,
    int TrackedRecords)
{
    /// <summary>Whether the session's record budget is spent. The byte budget can be spent without it, and then shows as <see cref="InjectionOmissionReason.OverSessionBudget"/> omissions.</summary>
    public bool RecordsExhausted => RecordsUsed >= MaxRecords;
}

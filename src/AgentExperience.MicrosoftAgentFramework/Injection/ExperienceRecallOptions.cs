using AgentExperience.Abstractions;

namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>
/// Recall at the moment of failure (<see cref="ExperienceInjectionOptions.Recall"/>, or
/// <c>AgentExperienceOptions.Recall</c> under the one-call setup): when a tool call in a captured run fails -- it threw,
/// or the host's <see cref="ExperienceCaptureOptions.ClassifyToolResult"/> named an error class for it -- and a reusable
/// lesson holds the same failure signature as resolved (a later call in its verified run worked after that failure), the
/// model is shown one short, labelled hint naming what worked, before its next model call in the same run. Off unless
/// set.
/// </summary>
/// <remarks>
/// <para>
/// <b>How the hint reaches the model.</b> It is appended to the per-run <see cref="Microsoft.Extensions.AI.ChatOptions.Instructions"/>
/// the function-invoking chat client hands its next model call, so it sits in the instructions of every later model call
/// in that run and nowhere else: never in the tool's result, never in the session's chat history. What capture records
/// for the failed call is the tool's own result or error, and a thrown exception is rethrown unchanged.
/// </para>
/// <para>
/// <b>What it needs.</b> An <see cref="IExperienceSignatureSource"/>: <see cref="SignatureSource"/>, else the record
/// store when it implements the port (the one-call setup passes the one its storage registers). Without one, recall is
/// reported once through <see cref="ExperienceCaptureOptions.OnCaptureFailure"/>
/// (<see cref="ExperienceCaptureFailureStage.Recall"/>) and does nothing. It also needs the provider to have run for the
/// invocation, inside capture, with a request: a run the host's resolver skipped gets no hint.
/// </para>
/// <para>
/// <b>The same rules as the block.</b> Only records the signature source returns as eligible, then the same final
/// eligibility re-read, scope rule, grant disclosure (a borrowed record gives a hint only when its grant shows the
/// approach), <see cref="ExperienceInjectionOptions.ModelAuthoredLessons"/> policy, capability gate and host decision as
/// the block; the same holdout arm; session tracking. A lookup that times out, is refused or fails gives no hint and
/// never fails the call or the run.
/// </para>
/// </remarks>
public sealed record ExperienceRecallOptions
{
    /// <summary>The default <see cref="LookupTimeout"/>: 300 milliseconds.</summary>
    public static readonly TimeSpan DefaultLookupTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>The default <see cref="MaxHintBytes"/>.</summary>
    public const int DefaultMaxHintBytes = 600;

    /// <summary>The largest <see cref="MaxHintBytes"/> allowed.</summary>
    public const int MaxHintBytesLimit = 16 * 1024;

    /// <summary>The default <see cref="MaxRecordsPerHint"/>.</summary>
    public const int DefaultMaxRecordsPerHint = 1;

    /// <summary>The largest <see cref="MaxRecordsPerHint"/> allowed.</summary>
    public const int MaxRecordsPerHintLimit = 5;

    /// <summary>The default <see cref="MaxHintsPerRun"/>.</summary>
    public const int DefaultMaxHintsPerRun = 3;

    /// <summary>The largest <see cref="MaxHintsPerRun"/> allowed.</summary>
    public const int MaxHintsPerRunLimit = 20;

    /// <summary>
    /// Whether a failure with no exact match may be matched on its tool name and error class alone, whatever the error's
    /// fingerprint (<see cref="ExperienceSignatureMatch.Coarse"/>). Off by default: the exact signature is the precise
    /// key, and a coarse match is a weaker claim, which the hint says.
    /// </summary>
    public bool AllowCoarseMatch { get; init; }

    /// <summary>
    /// How long one failure's lookup may take, the signature search and the final eligibility re-read together, on
    /// <see cref="ExperienceInjectionOptions.TimeProvider"/>. Past it the failure gets no hint
    /// (<see cref="ExperienceRecallOutcome.TimedOut"/>). Defaults to <see cref="DefaultLookupTimeout"/>; must be positive.
    /// </summary>
    public TimeSpan LookupTimeout { get; init; } = DefaultLookupTimeout;

    /// <summary>
    /// The most UTF-8 bytes one hint may occupy, delimiters included. A record that does not fit is dropped whole, never
    /// cut. From 1 to <see cref="MaxHintBytesLimit"/>; defaults to <see cref="DefaultMaxHintBytes"/>.
    /// </summary>
    public int MaxHintBytes { get; init; } = DefaultMaxHintBytes;

    /// <summary>The most records one hint names, from 1 to <see cref="MaxRecordsPerHintLimit"/>; defaults to 1.</summary>
    public int MaxRecordsPerHint { get; init; } = DefaultMaxRecordsPerHint;

    /// <summary>
    /// The most failures one run answers (hinted, or withheld from a held-out run), from 1 to
    /// <see cref="MaxHintsPerRunLimit"/>; defaults to 3. A signature is looked up once per run unless its lookup timed out
    /// or failed, and a run starts at most four times this many lookups; a failure past any of these is
    /// <see cref="ExperienceRecallOutcome.Bounded"/>.
    /// </summary>
    public int MaxHintsPerRun { get; init; } = DefaultMaxHintsPerRun;

    /// <summary>
    /// The signature port to look failures up through, or <see langword="null"/> to use the record store when it
    /// implements <see cref="IExperienceSignatureSource"/>. The one-call setup sets it to the source its storage
    /// registered unless the host set one.
    /// </summary>
    public IExperienceSignatureSource? SignatureSource { get; init; }

    /// <summary>
    /// Called with every failure recall looked at, in the run, after the hint (if any) is in place. Content-free, like
    /// <see cref="ExperienceInjectionOptions.OnContextInjected"/>. An exception it throws is swallowed.
    /// </summary>
    public Action<ExperienceRecallResult>? OnRecall { get; init; }

    /// <summary>Refuses a bound out of range.</summary>
    internal void Validate(string paramName)
    {
        if (LookupTimeout <= TimeSpan.Zero || LookupTimeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException($"{paramName}.{nameof(LookupTimeout)}", LookupTimeout, $"The recall lookup timeout must be positive and at most {uint.MaxValue - 1} milliseconds.");
        }

        if (MaxHintBytes is < 1 or > MaxHintBytesLimit)
        {
            throw new ArgumentOutOfRangeException($"{paramName}.{nameof(MaxHintBytes)}", MaxHintBytes, $"The hint byte budget must be from 1 to {MaxHintBytesLimit}.");
        }

        if (MaxRecordsPerHint is < 1 or > MaxRecordsPerHintLimit)
        {
            throw new ArgumentOutOfRangeException($"{paramName}.{nameof(MaxRecordsPerHint)}", MaxRecordsPerHint, $"The records per hint must be from 1 to {MaxRecordsPerHintLimit}.");
        }

        if (MaxHintsPerRun is < 1 or > MaxHintsPerRunLimit)
        {
            throw new ArgumentOutOfRangeException($"{paramName}.{nameof(MaxHintsPerRun)}", MaxHintsPerRun, $"The hints per run must be from 1 to {MaxHintsPerRunLimit}.");
        }
    }
}

/// <summary>What recall did with one failed tool call.</summary>
public enum ExperienceRecallOutcome
{
    /// <summary>A hint naming <see cref="ExperienceRecallResult.ExperienceIds"/> was put in front of the model's next call.</summary>
    Hinted,

    /// <summary>
    /// The run is in the injection holdout's holdout arm: the hint was built and withheld, and its records were recorded as
    /// the run's assignment (<see cref="ExperienceRecallResult.WithheldExperienceIds"/>).
    /// </summary>
    Withheld,

    /// <summary>
    /// No record gives a hint: none holds the signature as resolved, or every one that does was left out (see
    /// <see cref="ExperienceRecallResult.Omitted"/>), or the failure has no signature (a tool name or error that cannot be
    /// keyed).
    /// </summary>
    NoMatch,

    /// <summary>The lookup ran past <see cref="ExperienceRecallOptions.LookupTimeout"/>: no hint.</summary>
    TimedOut,

    /// <summary>The lookup was refused or failed, or the hint could not be delivered: no hint. See <see cref="ExperienceRecallResult.Failure"/>.</summary>
    Failed,

    /// <summary>
    /// No hint was given for a bound: the signature was already looked up in this run, the run answered
    /// <see cref="ExperienceRecallOptions.MaxHintsPerRun"/> failures or started its most lookups, or the session's budget
    /// cannot take the hint (in either holdout arm).
    /// </summary>
    Bounded,
}

/// <summary>
/// The account of one failed tool call recall looked at, handed to <see cref="ExperienceRecallOptions.OnRecall"/>.
/// Content-free: identifiers, the signature (a tool name, an error class and a fingerprint) and counts, never a hint's text.
/// </summary>
/// <param name="RunId">The captured run the call failed in.</param>
/// <param name="Signature">The failure's signature, or <see langword="null"/> when it has none.</param>
/// <param name="Outcome">What recall did.</param>
/// <param name="ExperienceIds">The records the hint named, when <see cref="Outcome"/> is <see cref="ExperienceRecallOutcome.Hinted"/>; otherwise empty.</param>
public sealed record ExperienceRecallResult(
    Guid RunId,
    ExperienceFailureSignature? Signature,
    ExperienceRecallOutcome Outcome,
    IReadOnlyList<Guid> ExperienceIds)
{
    /// <summary>How the hinted (or withheld) records matched: exact, or coarse; <see langword="null"/> when none did.</summary>
    public ExperienceSignatureMatch? Match { get; init; }

    /// <summary>The records a held-out run would have been hinted, when <see cref="Outcome"/> is <see cref="ExperienceRecallOutcome.Withheld"/>; otherwise empty.</summary>
    public IReadOnlyList<Guid> WithheldExperienceIds { get; init; } = [];

    /// <summary>Every matching record left out, and why, as <see cref="ExperienceInjectionResult.Omitted"/> reports them.</summary>
    public IReadOnlyList<OmittedExperience> Omitted { get; init; } = [];

    /// <summary>The hint's UTF-8 size, when one was delivered; otherwise 0.</summary>
    public int HintBytes { get; init; }

    /// <summary>The host's correlation identifier from the invocation's request, or <see langword="null"/>.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Why the lookup failed or timed out, when it did; otherwise <see langword="null"/>.</summary>
    public InjectionFailure? Failure { get; init; }
}

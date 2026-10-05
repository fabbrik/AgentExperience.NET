namespace AgentExperience.Core.Capture;

/// <summary>
/// Positive, configured limits an <see cref="InMemoryExperienceCaptureService"/> enforces while
/// capturing a run, so its in-memory footprint stays bounded. A limit violation is never a
/// rejection (that is <see cref="AgentExperience.Abstractions.ISanitizer"/>'s job, fail-closed on
/// <see cref="AgentExperience.Abstractions.SanitizationDecision.Rejected"/>): a string field that
/// exceeds its limit is truncated to a safe placeholder; a run or attempt that has already reached
/// its count limit rejects a further, genuinely new entry outright -- reported on the call's
/// result, never as a new field on <c>AgentExperience.Abstractions</c>'s record shapes. Every limit
/// must be strictly positive; a limit of zero or less could never let anything be captured at all,
/// so it is rejected both at construction <em>and</em> on a <c>with</c> expression (each property's
/// <c>init</c> accessor re-validates via the C# <c>field</c> keyword, since a record's default
/// property-initializer validation alone does not re-run when a property is changed via <c>with</c>).
/// </summary>
/// <param name="MaxAttemptsPerRun">
/// The maximum number of distinct attempts a single run may accumulate (and, correspondingly, the
/// most <c>AttemptId</c>s the run tracks for idempotency at all). Once a run already holds this
/// many attempts, a further <c>AppendAttempt</c> call for a genuinely new <c>AttemptId</c> is
/// rejected outright (<c>CapacityExceeded</c>) and is not tracked -- nothing about it is retained.
/// </param>
/// <param name="MaxToolCallsPerAttempt">
/// The maximum number of tool calls a single attempt may accumulate. Excess tool calls beyond this
/// limit, submitted within one <c>AppendAttempt</c> call, are dropped from the stored attempt
/// (earliest-first order preserved) -- reported as a truncated <c>"ToolCalls"</c> field on that
/// call's result.
/// </param>
/// <param name="MaxResultLength">
/// The maximum character length allowed for a stored (already-sanitized) attempt or tool-call
/// <c>Result</c> string. A longer value is replaced with a safe placeholder, itself clamped to this
/// same limit, that never reflects the original content.
/// </param>
/// <param name="MaxErrorLength">
/// The maximum character length allowed for a stored (already-sanitized) attempt or tool-call
/// <c>Error</c> string. A longer value is replaced with a safe placeholder, itself clamped to this
/// same limit, that never reflects the original content.
/// </param>
public sealed record CaptureLimits(
    int MaxAttemptsPerRun,
    int MaxToolCallsPerAttempt,
    int MaxResultLength,
    int MaxErrorLength)
{
    /// <summary>The maximum number of attempts a single run may accumulate (see the primary constructor's parameter doc).</summary>
    public int MaxAttemptsPerRun
    {
        get;
        init => field = EnsurePositive(value, nameof(MaxAttemptsPerRun));
    } = EnsurePositive(MaxAttemptsPerRun, nameof(MaxAttemptsPerRun));

    /// <summary>The maximum number of tool calls a single attempt may accumulate (see the primary constructor's parameter doc).</summary>
    public int MaxToolCallsPerAttempt
    {
        get;
        init => field = EnsurePositive(value, nameof(MaxToolCallsPerAttempt));
    } = EnsurePositive(MaxToolCallsPerAttempt, nameof(MaxToolCallsPerAttempt));

    /// <summary>The maximum character length allowed for a stored <c>Result</c> string (see the primary constructor's parameter doc).</summary>
    public int MaxResultLength
    {
        get;
        init => field = EnsurePositive(value, nameof(MaxResultLength));
    } = EnsurePositive(MaxResultLength, nameof(MaxResultLength));

    /// <summary>The maximum character length allowed for a stored <c>Error</c> string (see the primary constructor's parameter doc).</summary>
    public int MaxErrorLength
    {
        get;
        init => field = EnsurePositive(value, nameof(MaxErrorLength));
    } = EnsurePositive(MaxErrorLength, nameof(MaxErrorLength));

    /// <summary>
    /// The most <em>completed</em> runs an <see cref="InMemoryExperienceCaptureService"/> keeps, finalized or
    /// not. Past it, the run that completed earliest is dropped first, and
    /// from then on it answers exactly as a run that never existed. Open runs are never counted or dropped
    /// by this bound. Defaults to 10,000; must be strictly positive.
    /// </summary>
    public int MaxRetainedCompletedRuns
    {
        get;
        init => field = EnsurePositive(value, nameof(MaxRetainedCompletedRuns));
    } = 10_000;

    /// <summary>
    /// How long an <see cref="InMemoryExperienceCaptureService"/> keeps a <em>completed</em> run, finalized
    /// or not, measured on the service's <see cref="TimeProvider"/>'s monotonic timestamp
    /// (<see cref="TimeProvider.GetTimestamp"/>) from when it recorded the completion, so wall-clock steps
    /// do not change it. An older run is dropped on the service's next call, and from then on it
    /// answers exactly as a run that never existed. Open runs are never dropped by this bound. Defaults to
    /// 24 hours; must be strictly positive.
    /// </summary>
    public TimeSpan CompletedRunRetention
    {
        get;
        init => field = EnsurePositive(value, nameof(CompletedRunRetention));
    } = TimeSpan.FromHours(24);

    /// <summary>
    /// The most <em>open</em> (started, not yet completed) runs an <see cref="InMemoryExperienceCaptureService"/>
    /// holds at once. While this many are open, a <c>StartRun</c> call that would open a new run is refused
    /// with <see cref="StartRunOutcome.CapacityExceeded"/> and nothing is stored; a call that continues an
    /// open run is never refused by it. A slot frees as soon as a run completes. Defaults to 10,000; must be
    /// strictly positive.
    /// </summary>
    public int MaxOpenRuns
    {
        get;
        init => field = EnsurePositive(value, nameof(MaxOpenRuns));
    } = 10_000;

    /// <summary>
    /// How long an <see cref="InMemoryExperienceCaptureService"/> lets a run stay <em>open</em>, measured on the
    /// service's <see cref="TimeProvider"/>'s monotonic timestamp (<see cref="TimeProvider.GetTimestamp"/>) from
    /// when the service opened it. An older open run is completed by the service itself, as
    /// <see cref="AgentExperience.Abstractions.RunExecutionStatus.Cancelled"/>, on the service's next call; from
    /// then on it is a completed run, kept and dropped under <see cref="MaxRetainedCompletedRuns"/> and
    /// <see cref="CompletedRunRetention"/>. Defaults to <see langword="null"/>: open runs are not aged out.
    /// When set, must be strictly positive.
    /// </summary>
    public TimeSpan? MaxOpenRunAge
    {
        get;
        init => field = value is { } age ? EnsurePositive(age, nameof(MaxOpenRunAge)) : null;
    }

    private static int EnsurePositive(int value, string paramName) =>
        value > 0 ? value : throw new ArgumentOutOfRangeException(paramName, value, "Capture limits must be strictly positive.");

    private static TimeSpan EnsurePositive(TimeSpan value, string paramName) =>
        value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(paramName, value, "Capture limits must be strictly positive.");
}

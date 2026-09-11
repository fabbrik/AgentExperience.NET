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

    private static int EnsurePositive(int value, string paramName) =>
        value > 0 ? value : throw new ArgumentOutOfRangeException(paramName, value, "Capture limits must be strictly positive.");
}

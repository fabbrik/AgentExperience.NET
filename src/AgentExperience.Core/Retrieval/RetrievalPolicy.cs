using AgentExperience.Abstractions;

namespace AgentExperience.Core.Retrieval;

/// <summary>
/// The bounds and thresholds a retrieval call runs under. Every value is validated both at
/// construction <em>and</em> on a <c>with</c> expression (each property's <c>init</c> accessor
/// re-validates via the C# <c>field</c> keyword), exactly as
/// <see cref="AgentExperience.Core.Capture.CaptureLimits"/> does, because a record's property
/// initializers alone do not re-run when a property is changed with <c>with</c>. An invalid value
/// throws <see cref="ArgumentOutOfRangeException"/>, so a misconfigured policy fails at startup
/// rather than silently widening what may be reused.
/// </summary>
/// <param name="Timeout">
/// How long the whole retrieval call may take, measured with the service's injected
/// <see cref="TimeProvider"/>. Exceeding it is never an exception: the caller gets an empty result
/// carrying a timeout signal. Must be strictly positive and at most <see cref="MaxTimeout"/>.
/// </param>
/// <param name="MinimumConfidence">
/// The smallest <see cref="AgentExperience.Abstractions.ExperienceRecord.ReuseConfidence"/> a record
/// may have and still be a candidate, in [0, 1]. Applied in the database, before ranking.
/// </param>
/// <param name="MaxAge">
/// How long since a record's <em>last lifecycle activity</em> it may still be reusable.
/// <see langword="null"/> means records never expire. Must be strictly positive when set. Expiry is
/// decided in Core, not in the database, because it is relative to the clock the service was given.
/// <para>
/// <b>What "age" means here.</b> It is measured from
/// <see cref="AgentExperience.Abstractions.ExperienceRecord.UpdatedAt"/>, which every lifecycle commit
/// bumps -- so it is the age of the record's last status change, <em>not</em> of the lesson itself. A
/// years-old lesson reinforced yesterday is treated as one day old and does not expire; a lesson
/// learned yesterday and never touched since ages normally. That is deliberate (recent revalidation is
/// evidence the lesson still holds), but it is not "when this was learned".
/// </para>
/// </param>
/// <param name="RecencyHalfLife">
/// The last-activity age at which the recency component has decayed to half. Recency is
/// <c>2^(-age / RecencyHalfLife)</c>, so it is always in (0, 1], is 1 for a record just committed, and
/// stays defined whether or not <see cref="MaxAge"/> is set. It measures the same
/// <see cref="AgentExperience.Abstractions.ExperienceRecord.UpdatedAt"/> as <see cref="MaxAge"/>, with
/// the same caveat. Must be strictly positive.
/// </param>
/// <param name="CandidateLimit">
/// The most candidates the search may return for Core to filter and rank. It bounds the work a single
/// retrieval does; the caller's own limit then bounds how many ranked records come back, and may not
/// exceed this. Because the search orders by <em>text</em> relevance before cutting, this is a real
/// recall ceiling: a record with a weaker text match but strong confidence, recency, or status is not
/// ranked at all once this many stronger text matches exist. The service asks the source for one more
/// candidate than this, so it can tell "exactly at the ceiling" from "more existed" and report
/// <c>Truncated</c>; that is why the largest permitted value is one below
/// <see cref="ExperienceCandidateQuery.MaxLimit"/>.
/// </param>
public sealed record RetrievalPolicy(
    TimeSpan Timeout,
    double MinimumConfidence,
    TimeSpan? MaxAge,
    TimeSpan RecencyHalfLife,
    int CandidateLimit)
{
    /// <summary>The documented default timeout: 500 ms.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The largest permitted <see cref="Timeout"/>: one day. Retrieval is a request-scoped bound
    /// measured in milliseconds, so a day is already far past any sensible value, and capping it keeps
    /// the wait comfortably inside what <see cref="Task"/>'s timeout support accepts -- a longer span
    /// would throw out of the retrieval call instead of bounding it.
    /// </summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromDays(1);

    /// <summary>The documented default eligibility confidence threshold: 0.5.</summary>
    public const double DefaultMinimumConfidence = 0.5;

    /// <summary>The default half-life of the recency component: 30 days.</summary>
    public static readonly TimeSpan DefaultRecencyHalfLife = TimeSpan.FromDays(30);

    /// <summary>The default bound on how many candidates one search may return.</summary>
    public const int DefaultCandidateLimit = 50;

    /// <summary>
    /// The largest permitted <see cref="CandidateLimit"/>: one below
    /// <see cref="ExperienceCandidateQuery.MaxLimit"/>, because the service asks the source for
    /// <see cref="CandidateLimit"/> + 1 candidates to detect truncation.
    /// </summary>
    public const int MaxCandidateLimit = ExperienceCandidateQuery.MaxLimit - 1;

    /// <summary>
    /// The documented defaults: a 500 ms timeout, a 0.5 confidence threshold, no expiry, a 30-day
    /// recency half-life, and at most 50 candidates per search.
    /// </summary>
    public static RetrievalPolicy Default { get; } = new(
        DefaultTimeout,
        DefaultMinimumConfidence,
        MaxAge: null,
        DefaultRecencyHalfLife,
        DefaultCandidateLimit);

    /// <summary>How long the whole retrieval call may take (see the primary constructor's parameter doc).</summary>
    public TimeSpan Timeout
    {
        get;
        init => field = EnsureTimeout(value);
    } = EnsureTimeout(Timeout);

    /// <summary>The confidence floor a candidate must clear (see the primary constructor's parameter doc).</summary>
    public double MinimumConfidence
    {
        get;
        init => field = EnsureUnitInterval(value, nameof(MinimumConfidence));
    } = EnsureUnitInterval(MinimumConfidence, nameof(MinimumConfidence));

    /// <summary>How old a reusable record may be, or <see langword="null"/> for no expiry (see the primary constructor's parameter doc).</summary>
    public TimeSpan? MaxAge
    {
        get;
        init => field = value is { } age ? EnsurePositive(age, nameof(MaxAge)) : null;
    } = MaxAge is { } maxAge ? EnsurePositive(maxAge, nameof(MaxAge)) : null;

    /// <summary>The age at which the recency component has decayed to half (see the primary constructor's parameter doc).</summary>
    public TimeSpan RecencyHalfLife
    {
        get;
        init => field = EnsurePositive(value, nameof(RecencyHalfLife));
    } = EnsurePositive(RecencyHalfLife, nameof(RecencyHalfLife));

    /// <summary>The most candidates one search may return (see the primary constructor's parameter doc).</summary>
    public int CandidateLimit
    {
        get;
        init => field = EnsureCandidateLimit(value);
    } = EnsureCandidateLimit(CandidateLimit);

    private static TimeSpan EnsurePositive(TimeSpan value, string paramName) =>
        value > TimeSpan.Zero
            ? value
            // TimeSpan.Zero excludes Timeout.InfiniteTimeSpan (-1 tick) too: an unbounded retrieval is
            // exactly what the timeout exists to prevent.
            : throw new ArgumentOutOfRangeException(paramName, value, "Retrieval durations must be strictly positive.");

    private static TimeSpan EnsureTimeout(TimeSpan value)
    {
        EnsurePositive(value, nameof(Timeout));
        return value <= MaxTimeout
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Timeout), value, $"The retrieval timeout must be at most {MaxTimeout}.");
    }

    private static double EnsureUnitInterval(double value, string paramName) =>
        value >= 0d && value <= 1d
            ? value
            : throw new ArgumentOutOfRangeException(paramName, value, "The confidence threshold must be between 0 and 1 inclusive.");

    private static int EnsureCandidateLimit(int value) =>
        value is >= ExperienceCandidateQuery.MinLimit and <= MaxCandidateLimit
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(CandidateLimit),
                value,
                $"The candidate limit must be between {ExperienceCandidateQuery.MinLimit} and {MaxCandidateLimit}.");
}

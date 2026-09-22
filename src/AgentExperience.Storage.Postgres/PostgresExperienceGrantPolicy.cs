namespace AgentExperience.Storage.Postgres;

/// <summary>
/// The policy this adapter administers sharing grants under. Today it is one rule: how long a grant
/// may live. Every value is validated both at construction <em>and</em> on a <c>with</c> expression
/// (each property's <c>init</c> accessor re-validates via the C# <c>field</c> keyword), exactly as
/// <c>AgentExperience.Core.Retrieval.RetrievalPolicy</c> does, because a record's property
/// initializers alone do not re-run when a property is changed with <c>with</c>. An invalid value
/// throws <see cref="ArgumentOutOfRangeException"/>, so a misconfigured policy fails at startup rather
/// than silently permitting a longer grant than the deployment meant to allow.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is a policy on the store, not a hidden constant.</b> The maximum is what this deployment
/// considers a reasonable sharing window, and a host that needs a different one says so. What a host
/// cannot do is switch it off: there is no "unbounded" value, because an unbounded grant is exactly
/// the thing this exists to remove.
/// </para>
/// <para>
/// <b>It binds a grant when it is created, and never afterwards.</b> Raising the maximum does not
/// extend a grant already issued -- nothing reaches back into stored rows, and
/// <c>0006</c>'s trigger refuses any <c>UPDATE</c> that moves <c>expires_at</c> anyway. Lowering it
/// does not shorten one either; end an over-long grant by revoking it, which is what revocation is
/// for. The existing rule that an expiry may only shrink is untouched by any of this.
/// </para>
/// <para>
/// <b>The database keeps its own ceiling underneath.</b> <c>0009</c> adds
/// <c>experience_grants_lifetime_bounded</c>, a fixed and deliberately generous bound that no writer
/// can exceed, including one that bypasses this library. A CHECK cannot express "whatever interval
/// this deployment configured", so the two are different jobs: this record is the policy, the
/// constraint is the floor the policy can never be configured below.
/// </para>
/// </remarks>
/// <param name="MaxLifetime">
/// The longest a grant issued from now may last. A request whose <c>ExpiresAt</c> is further ahead
/// than this is <c>Invalid</c> on that field with nothing written; an expiry exactly at the maximum is
/// accepted. Must be strictly positive and at most <see cref="MaxSupportedLifetime"/>.
/// </param>
public sealed record PostgresExperienceGrantPolicy(TimeSpan MaxLifetime)
{
    /// <summary>
    /// The default maximum grant lifetime: 90 days. Long enough for a genuine cross-team
    /// collaboration to run its course, short enough that a grant nobody remembers issuing expires on
    /// its own rather than outliving the reason for it.
    /// </summary>
    public static readonly TimeSpan DefaultMaxLifetime = TimeSpan.FromDays(90);

    /// <summary>
    /// The largest <see cref="MaxLifetime"/> a host may configure: 3650 days. It stays below the
    /// database's own <c>expires_at &lt;= issued_at + interval '10 years'</c> ceiling for every
    /// possible issue date -- the shortest ten calendar years can be is 3651 days, when the span
    /// crosses a century year that is not a leap year (1900, 2100) and so contains only one leap day --
    /// so a grant this policy accepts is never one the database will then refuse to store.
    /// </summary>
    public static readonly TimeSpan MaxSupportedLifetime = TimeSpan.FromDays(3650);

    /// <summary>The documented default: a 90-day maximum grant lifetime.</summary>
    public static PostgresExperienceGrantPolicy Default { get; } = new(DefaultMaxLifetime);

    /// <summary>The longest a grant issued from now may last (see the primary constructor's parameter doc).</summary>
    public TimeSpan MaxLifetime
    {
        get;
        init => field = EnsureLifetime(value);
    } = EnsureLifetime(MaxLifetime);

    /// <summary>
    /// Whether an expiry requested at <paramref name="now"/> is past the maximum lifetime.
    /// </summary>
    /// <remarks>
    /// An upper bound that would run off the end of <see cref="DateTimeOffset"/> is reported as
    /// exceeded rather than saturated at <see cref="DateTimeOffset.MaxValue"/>. Saturating would make
    /// the comparison vacuously false and <em>accept</em> <see cref="DateTimeOffset.MaxValue"/> -- the
    /// one value this bound exists to refuse -- so the safe direction is to refuse. A clock sitting
    /// within <see cref="MaxLifetime"/> of the year 9999 is broken, and refusing every grant it issues
    /// is the right answer to that.
    /// </remarks>
    /// <param name="expiresAt">The requested expiry.</param>
    /// <param name="now">The moment the request is being validated against.</param>
    /// <returns><see langword="true"/> when the request must be refused.</returns>
    public bool ExceedsMaxLifetime(DateTimeOffset expiresAt, DateTimeOffset now) =>
        DateTimeOffset.MaxValue - now < MaxLifetime || expiresAt > now + MaxLifetime;

    private static TimeSpan EnsureLifetime(TimeSpan value) =>
        value > TimeSpan.Zero && value <= MaxSupportedLifetime
            ? value
            // TimeSpan.Zero excludes Timeout.InfiniteTimeSpan (-1 tick) too: a grant that never expires
            // is exactly what this bound exists to prevent, so it must not be expressible as one.
            : throw new ArgumentOutOfRangeException(
                nameof(MaxLifetime),
                value,
                "The maximum grant lifetime must be strictly positive and at most 3650 days.");
}

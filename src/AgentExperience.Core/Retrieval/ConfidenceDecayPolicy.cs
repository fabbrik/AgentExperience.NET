using System.Collections.Frozen;

namespace AgentExperience.Core.Retrieval;

/// <summary>
/// How a record's reuse confidence fades with age <em>at ranking time</em>, by the record's domain.
/// When a retrieval service has one, each eligible record's
/// <see cref="RankingComponentKind.Confidence"/> component is its stored confidence multiplied by
/// <c>2^(-age / halfLife)</c>, where the half-life is chosen by the record's domain.
/// </summary>
/// <remarks>
/// <para>
/// <b>Domain.</b> The value of the record's
/// <see cref="AgentExperience.Abstractions.EnvironmentFingerprint.Metadata"/> entry named
/// <see cref="DomainKey"/> (default <c>"domain"</c>), matched ordinally against the keys of
/// <see cref="HalfLives"/>. A record whose domain is missing or not listed uses
/// <see cref="DefaultHalfLife"/>.
/// </para>
/// <para>
/// <b>Age.</b> Measured from <see cref="AgentExperience.Abstractions.ExperienceRecord.CreatedAt"/>,
/// the age of the lesson itself. This is deliberately <em>not</em> the
/// <see cref="AgentExperience.Abstractions.ExperienceRecord.UpdatedAt"/> that drives the separate
/// recency component: revalidation already earns recency, and a lesson about a fast-moving API does
/// not become current again because it was reinforced. An age of zero or less (clock skew) decays
/// nothing.
/// </para>
/// <para>
/// <b>Ranking only.</b> Decay never excludes a record -- eligibility, including
/// <see cref="RetrievalPolicy.MinimumConfidence"/>, uses the stored confidence -- and a read never
/// rewrites stored confidence.
/// </para>
/// <para>
/// Every value is validated at construction and on a <c>with</c> expression, as
/// <see cref="RetrievalPolicy"/> is: a zero or negative half-life throws
/// <see cref="ArgumentOutOfRangeException"/>, and a blank <see cref="DomainKey"/> or a null or blank
/// key in <see cref="HalfLives"/> throws <see cref="ArgumentException"/>.
/// </para>
/// </remarks>
public sealed record ConfidenceDecayPolicy
{
    /// <summary>The default <see cref="DomainKey"/>: <c>"domain"</c>.</summary>
    public const string DefaultDomainKey = "domain";

    private static readonly IReadOnlyDictionary<string, TimeSpan?> NoHalfLives = FrozenDictionary<string, TimeSpan?>.Empty.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The <see cref="AgentExperience.Abstractions.EnvironmentFingerprint.Metadata"/> key whose value
    /// names a record's domain. Defaults to <see cref="DefaultDomainKey"/>. Must be non-blank.
    /// </summary>
    public string DomainKey
    {
        get;
        init => field = EnsureDomainKey(value);
    } = DefaultDomainKey;

    /// <summary>
    /// The half-life for each domain, matched ordinally. A positive <see cref="TimeSpan"/> decays that
    /// domain's records; <see langword="null"/> means that domain never decays, whatever
    /// <see cref="DefaultHalfLife"/> says. Empty by default. Keys must be non-blank, and every value
    /// that is set must be strictly positive. The dictionary is copied into a read-only map, so later
    /// changes to the one passed in have no effect, and keys always match ordinally, whatever comparer
    /// the caller's dictionary used.
    /// </summary>
    public IReadOnlyDictionary<string, TimeSpan?> HalfLives
    {
        get;
        init => field = EnsureHalfLives(value);
    } = NoHalfLives;

    /// <summary>
    /// The half-life for a record whose domain is missing or not listed in <see cref="HalfLives"/>.
    /// <see langword="null"/> (the default) means such a record does not decay. Must be strictly
    /// positive when set.
    /// </summary>
    public TimeSpan? DefaultHalfLife
    {
        get;
        init => field = value is { } halfLife ? EnsurePositive(halfLife, nameof(DefaultHalfLife)) : null;
    }

    /// <summary>
    /// The half-life that applies to a record whose domain is <paramref name="domain"/>, or
    /// <see langword="null"/> when that record does not decay.
    /// </summary>
    /// <param name="domain">The record's domain, or <see langword="null"/> when it names none.</param>
    /// <returns>The half-life, or <see langword="null"/> for no decay.</returns>
    public TimeSpan? HalfLifeFor(string? domain) =>
        domain is not null && HalfLives.TryGetValue(domain, out var halfLife) ? halfLife : DefaultHalfLife;

    private static string EnsureDomainKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(DomainKey));
        return value;
    }

    private static IReadOnlyDictionary<string, TimeSpan?> EnsureHalfLives(IReadOnlyDictionary<string, TimeSpan?> value)
    {
        ArgumentNullException.ThrowIfNull(value, nameof(HalfLives));

        var copy = new Dictionary<string, TimeSpan?>(value.Count, StringComparer.Ordinal);
        foreach (var (domain, halfLife) in value)
        {
            if (string.IsNullOrWhiteSpace(domain))
            {
                throw new ArgumentException("A domain in the half-life map must be non-blank.", nameof(HalfLives));
            }

            copy[domain] = halfLife is { } positive ? EnsurePositive(positive, nameof(HalfLives)) : null;
        }

        return copy.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static TimeSpan EnsurePositive(TimeSpan value, string paramName) =>
        value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(paramName, value, "A confidence half-life must be strictly positive.");
}

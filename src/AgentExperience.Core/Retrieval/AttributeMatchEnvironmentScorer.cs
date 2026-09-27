using AgentExperience.Abstractions;

namespace AgentExperience.Core.Retrieval;

/// <summary>
/// The default <see cref="IEnvironmentCompatibilityScorer"/>: the fraction of preferred attributes
/// the record's <see cref="EnvironmentFingerprint.Metadata"/> carries with an ordinally equal value.
/// </summary>
/// <remarks>
/// With nothing preferred it returns <see cref="ExperienceRetrievalService.CompatibleEnvironmentScore"/>
/// (1.0), which keeps retrieval's results identical to a request that never heard of preferences. A
/// missing key or a different value simply does not count as a match: there is no version-range or
/// semantic matching here, which a host scorer may add.
/// </remarks>
public sealed class AttributeMatchEnvironmentScorer : IEnvironmentCompatibilityScorer
{
    private AttributeMatchEnvironmentScorer()
    {
    }

    /// <summary>The shared instance. The scorer is stateless.</summary>
    public static AttributeMatchEnvironmentScorer Instance { get; } = new();

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="recordEnvironment"/> or <paramref name="preferredAttributes"/> is <see langword="null"/>.</exception>
    public double Score(EnvironmentFingerprint recordEnvironment, IReadOnlyDictionary<string, string> preferredAttributes)
    {
        ArgumentNullException.ThrowIfNull(recordEnvironment);
        ArgumentNullException.ThrowIfNull(preferredAttributes);

        if (preferredAttributes.Count == 0)
        {
            return ExperienceRetrievalService.CompatibleEnvironmentScore;
        }

        var metadata = recordEnvironment.Metadata;
        var matched = 0;
        foreach (var (key, value) in preferredAttributes)
        {
            if (metadata is not null
                && metadata.TryGetValue(key, out var stored)
                && string.Equals(stored, value, StringComparison.Ordinal))
            {
                matched++;
            }
        }

        return (double)matched / preferredAttributes.Count;
    }
}

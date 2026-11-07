using AgentExperience.Abstractions;

namespace AgentExperience.Core.Holdout;

/// <summary>
/// The model capture records under <see cref="HoldoutOutcomeRow.ModelMetadataKey"/> is the library's own bookkeeping, read
/// only by the holdout's per-model estimate. It is never a precondition, never rendered into a block, and never filters or
/// grades retrieval, so a lesson learned on one model reads and ranks the same for an agent on any other.
/// </summary>
internal static class ModelMetadata
{
    /// <summary>Whether <paramref name="key"/> is the model bookkeeping key.</summary>
    internal static bool IsModelKey(string? key) => string.Equals(key, HoldoutOutcomeRow.ModelMetadataKey, StringComparison.Ordinal);

    /// <summary><paramref name="attributes"/> without the model key; the same instance when it has none.</summary>
    internal static IReadOnlyDictionary<string, string>? WithoutModel(IReadOnlyDictionary<string, string>? attributes)
    {
        if (attributes is null || !attributes.ContainsKey(HoldoutOutcomeRow.ModelMetadataKey))
        {
            return attributes;
        }

        var without = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in attributes)
        {
            if (!IsModelKey(key))
            {
                without[key] = value;
            }
        }

        return without;
    }

    /// <summary><paramref name="environment"/> without the model key in its metadata; the same instance when it has none.</summary>
    internal static EnvironmentFingerprint WithoutModel(EnvironmentFingerprint environment) =>
        environment?.Metadata is { } metadata && metadata.ContainsKey(HoldoutOutcomeRow.ModelMetadataKey)
            ? environment with { Metadata = WithoutModel(metadata)! }
            : environment!;
}

using System.Text;
using System.Text.Json;
using AgentExperience.Abstractions;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// The stored form of a grant's <see cref="ExperienceGrant.ApproachArguments"/>: one JSON object whose members are
/// tool names and whose values are arrays of argument keys, in <c>experience_grants.approach_arguments</c>
/// (<c>0017</c>).
/// </summary>
/// <remarks>
/// Parsing is strict and bounded, and a stored value it cannot read is <see langword="null"/>: the grant store
/// turns that into a loud decode failure, and the read path into "the owner named no key", which shows no borrowed
/// argument value. A stored allowlist is never repaired into a wider one.
/// </remarks>
internal static class GrantApproachArgumentsCodec
{
    /// <summary>
    /// A private copy of a caller's allowlist, taken in one pass: a dictionary of arrays, in the caller's order. A
    /// null key list stays null, so the validator reports it.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>>? Snapshot(IReadOnlyDictionary<string, IReadOnlyList<string>>? allowlist)
    {
        if (allowlist is null)
        {
            return null;
        }

        var copy = new List<KeyValuePair<string, IReadOnlyList<string>>>();
        foreach (var (toolName, keys) in allowlist)
        {
            copy.Add(new(toolName, keys is null ? null! : keys.ToArray()));
        }

        // Not a dictionary keyed by the caller's comparer: a duplicate tool name under ordinal comparison must reach
        // the validator as a duplicate, not be merged away here.
        return new SnapshotAllowlist(copy);
    }

    /// <summary>An ordered, read-only list of pairs presented as a dictionary; lookups are ordinal.</summary>
    private sealed class SnapshotAllowlist(List<KeyValuePair<string, IReadOnlyList<string>>> pairs)
        : IReadOnlyDictionary<string, IReadOnlyList<string>>
    {
        public IReadOnlyList<string> this[string key] =>
            TryGetValue(key, out var value) ? value : throw new KeyNotFoundException();

        public IEnumerable<string> Keys => pairs.Select(pair => pair.Key);

        public IEnumerable<IReadOnlyList<string>> Values => pairs.Select(pair => pair.Value);

        public int Count => pairs.Count;

        public bool ContainsKey(string key) => pairs.Any(pair => string.Equals(pair.Key, key, StringComparison.Ordinal));

        public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out IReadOnlyList<string> value)
        {
            foreach (var pair in pairs)
            {
                if (string.Equals(pair.Key, key, StringComparison.Ordinal))
                {
                    value = pair.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        public IEnumerator<KeyValuePair<string, IReadOnlyList<string>>> GetEnumerator() => pairs.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Writes <paramref name="allowlist"/> as the stored JSON object. The caller has validated it.</summary>
    internal static string Serialize(IReadOnlyDictionary<string, IReadOnlyList<string>> allowlist)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (toolName, keys) in allowlist)
            {
                writer.WriteStartArray(toolName);
                foreach (var key in keys)
                {
                    writer.WriteStringValue(key);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Reads a stored allowlist, or <see langword="null"/> when it is not a non-empty object of non-empty string
    /// arrays within <see cref="ExperienceGrant.MaxApproachArgumentTools"/> tools and
    /// <see cref="ExperienceGrant.MaxApproachArgumentKeysPerTool"/> keys per tool.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>>? Parse(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(stored);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var allowlist = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var tool in root.EnumerateObject())
            {
                if (allowlist.Count >= ExperienceGrant.MaxApproachArgumentTools
                    || tool.Value.ValueKind != JsonValueKind.Array
                    || tool.Value.GetArrayLength() is 0 or > ExperienceGrant.MaxApproachArgumentKeysPerTool)
                {
                    return null;
                }

                var keys = new List<string>(tool.Value.GetArrayLength());
                foreach (var key in tool.Value.EnumerateArray())
                {
                    if (key.ValueKind != JsonValueKind.String)
                    {
                        return null;
                    }

                    keys.Add(key.GetString()!);
                }

                // A tool named twice in the stored object is not two consents to merge: refuse the whole value.
                if (!allowlist.TryAdd(tool.Name, keys))
                {
                    return null;
                }
            }

            return allowlist.Count == 0 ? null : allowlist;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

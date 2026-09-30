using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentExperience.Upgrade.Tests;

/// <summary>
/// Collects every manifest item that did not survive the upgrade, each line naming the preview, the PostgreSQL major
/// and the item, so one run reports all of them rather than the first.
/// </summary>
internal sealed class UpgradeReport(string subject, int major)
{
    /// <summary>The seeders write the manifest with these settings (tests/AgentExperience.Upgrade.Seeders/Shared/Seeder.cs).</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The same settings, refusing a manifest member today's type has no place for: a member removed or renamed since
    /// the preview fails the comparison instead of being dropped on the floor.
    /// </summary>
    private static readonly JsonSerializerOptions Strict = new(Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>
    /// The members today's API returns that a published preview's did not, each new by design. An item the preview
    /// returned without one of these compares against the value today's type gives it when it is absent (the README's
    /// "Where the data changes by design" table says what that value means after an upgrade). Any other member today's
    /// API returns that the preview's did not fails the comparison.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> NewByDesign = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["closedRoundId"] = "ExperienceRecord, 0.1.0-preview.2 (story 6.6): the round finalization closed; absent before",
        ["origin"] = "ExperienceRecord, 0.1.0-preview.2 (story 7.3): HostWritten when absent",
        ["exposedTo"] = "Provenance, 0.1.0-preview.2 (story 7.3): empty when absent",
        ["assessmentId"] = "ConfidenceUpdate, 0.1.0-preview.2 (story 6.6): human evidence only",
        ["admission"] = "ConfidenceUpdate, 0.1.0-preview.2 (0018): not recorded before",
        ["disclosure"] = "ExperienceGrant, ExperienceGrantEvent and ExperienceGrantAccess, 0.1.0-preview.2 (0011)",
        ["approachArguments"] = "ExperienceGrant, 0.1.0-preview.2 (0017)",
        ["grantDisclosure"] = "ExperienceRecordGetResult, 0.1.0-preview.2 (0011)",
        ["grantApproachArguments"] = "ExperienceRecordGetResult, 0.1.0-preview.2 (0017)",
    };

    private const int MaxDifferencesPerItem = 5;
    private const int MaxShownLength = 160;

    private readonly List<string> _failures = [];

    public int Checked { get; private set; }

    /// <summary>Records a failure for <paramref name="item"/> unless <paramref name="condition"/> holds.</summary>
    public void Check(bool condition, string item, string detail)
    {
        Checked++;
        if (!condition)
        {
            _failures.Add($"{subject} on PostgreSQL {major}: {item} did not survive: {detail}");
        }
    }

    /// <summary>
    /// Compares what today's API returned for <paramref name="item"/> with what the preview's API returned before the
    /// upgrade, semantically. The manifest's JSON is first mapped to today's type, strictly (a member today's type
    /// cannot hold fails), so a member the preview did not have takes the value today's type gives it when absent;
    /// then both are compared as JSON, object members in any order. Separately, every member today's API returned that
    /// the preview's did not must be one of <see cref="NewByDesign"/>.
    /// </summary>
    public void Compare<T>(string item, JsonNode? before, T after)
    {
        JsonNode? expected;
        try
        {
            expected = JsonSerializer.SerializeToNode(before.Deserialize<T>(Strict), Json);
        }
        catch (JsonException exception)
        {
            Check(false, item, $"the preview's {typeof(T).Name} no longer maps to today's: {exception.Message}");
            return;
        }

        var actual = JsonSerializer.SerializeToNode(after, Json);

        var differences = new List<string>();
        Diff("$", expected, actual, differences);
        NewMembers("$", before, actual, differences);
        Check(differences.Count == 0, item, string.Join("; ", differences.Take(MaxDifferencesPerItem)) + (differences.Count > MaxDifferencesPerItem ? $"; and {differences.Count - MaxDifferencesPerItem} more" : string.Empty));
    }

    public void AssertNothingFailed()
    {
        Assert.True(Checked > 0, $"{subject} on PostgreSQL {major}: nothing was checked.");
        Assert.True(_failures.Count == 0, $"{_failures.Count} of {Checked} checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, _failures)}");
    }

    private static void Diff(string path, JsonNode? expected, JsonNode? actual, List<string> differences)
    {
        switch (expected, actual)
        {
            case (JsonObject e, JsonObject a):
                foreach (var name in e.Select(p => p.Key).Union(a.Select(p => p.Key), StringComparer.Ordinal))
                {
                    var inExpected = e.TryGetPropertyValue(name, out var ev);
                    var inActual = a.TryGetPropertyValue(name, out var av);
                    if (inExpected != inActual)
                    {
                        differences.Add($"{path}.{name}: {(inExpected ? "missing after the upgrade" : "not in the preview's type")}");
                        continue;
                    }

                    Diff($"{path}.{name}", ev, av, differences);
                }

                break;

            case (JsonArray e, JsonArray a):
                if (e.Count != a.Count)
                {
                    differences.Add($"{path}: {e.Count} items before, {a.Count} after");
                    break;
                }

                for (var i = 0; i < e.Count; i++)
                {
                    Diff($"{path}[{i}]", e[i], a[i], differences);
                }

                break;

            default:
                if (!JsonNode.DeepEquals(expected, actual))
                {
                    differences.Add($"{path}: expected {Show(expected)}, got {Show(actual)}");
                }

                break;
        }
    }

    /// <summary>Every member today's API returned under <paramref name="path"/> that the raw manifest lacks, unless new by design.</summary>
    private static void NewMembers(string path, JsonNode? raw, JsonNode? actual, List<string> differences)
    {
        switch (raw, actual)
        {
            case (JsonObject r, JsonObject a):
                foreach (var (name, value) in a)
                {
                    if (!r.TryGetPropertyValue(name, out var rawValue))
                    {
                        if (!NewByDesign.ContainsKey(name))
                        {
                            differences.Add($"{path}.{name}: returned today but not by the preview, and not a member that is new by design");
                        }

                        continue;
                    }

                    NewMembers($"{path}.{name}", rawValue, value, differences);
                }

                break;

            case (JsonArray r, JsonArray a):
                for (var i = 0; i < Math.Min(r.Count, a.Count); i++)
                {
                    NewMembers($"{path}[{i}]", r[i], a[i], differences);
                }

                break;
        }
    }

    public static string Show(JsonNode? node) => Truncate(node is null ? "null" : node.ToJsonString());

    public static string Truncate(string text) =>
        text.Length <= MaxShownLength ? text : string.Concat(text.AsSpan(0, MaxShownLength), "...");
}

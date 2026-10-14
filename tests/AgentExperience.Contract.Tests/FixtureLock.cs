using System.Text.Json.Nodes;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// The per-family lock, <c>fixtures/&lt;family&gt;/lock.json</c>: the contract version and the SHA-256 of every fixture file
/// of the family. It is what makes "the contract version is bumped whenever a fixture changes meaning" a check rather than
/// a promise.
/// </summary>
/// <remarks>
/// A file that was locked under the current <see cref="ContractFiles.ContractVersion"/> may not change or disappear: that is
/// a fixture changing meaning, and it fails here -- even when regenerating with the update switch -- until the version is
/// bumped. A new file (a new case) may be added under the same version; regenerating locks it. Bumping the version
/// relocks every file.
/// </remarks>
internal static class FixtureLock
{
    private const string FileName = "lock.json";

    public static void Verify(string family, List<string> drift)
    {
        var directory = Path.Combine(ContractFiles.Fixtures, family);
        var current = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
                if (relative != FileName)
                {
                    current[relative] = ContractFiles.Sha256(file);
                }
            }
        }

        var lockPath = $"fixtures/{family}/{FileName}";
        var existing = ContractFiles.Read(lockPath) is { } text ? JsonNode.Parse(text)!.AsObject() : null;
        var lockedVersion = existing?["contractVersion"]?.GetValue<string>();
        var locked = existing?["files"]?.AsObject().ToDictionary(entry => entry.Key, entry => entry.Value!.GetValue<string>(), StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var changedMeaning = lockedVersion == ContractFiles.ContractVersion
            ? locked.Where(entry => !current.TryGetValue(entry.Key, out var hash) || hash != entry.Value).Select(entry => entry.Key).ToList()
            : [];
        if (changedMeaning.Count > 0)
        {
            drift.Add(
                $"{lockPath}: {changedMeaning.Count} fixture file(s) locked under {ContractFiles.ContractVersion} changed or were removed " +
                $"({string.Join(", ", changedMeaning.Take(5))}{(changedMeaning.Count > 5 ? ", ..." : string.Empty)}). A fixture that changes " +
                "meaning needs a new contract version: bump ContractFiles.ContractVersion and contract/README.md, then regenerate.");
            return;
        }

        var files = new JsonObject();
        foreach (var (name, hash) in current)
        {
            files[name] = hash;
        }

        var content = ContractJson.Write(new JsonObject { ["contractVersion"] = ContractFiles.ContractVersion, ["files"] = files });
        ContractFiles.Verify(lockPath, content, drift);
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// <c>contract/lock.json</c>: the contract version and the SHA-256 of every contract file (the fixtures, the schema, the
/// SQL and the README). It makes "any change to the contract is a new version" a check rather than a promise.
/// </summary>
/// <remarks>
/// <para>
/// <b>The authority is <see cref="ContractFiles.LockDigests"/>, not <c>lock.json</c>.</b> The update switch rewrites
/// <c>lock.json</c>, but it never rewrites that source file, so neither regenerating nor editing <c>lock.json</c> by hand can
/// accept a changed file under a version already published: the lock's own hash would no longer be the published one.
/// </para>
/// <para>
/// <b>Publishing a change.</b> Bump <see cref="ContractFiles.ContractVersion"/> (and the README): the minor version when
/// the change only adds files (new cases), the major version when any file already locked changed or went away. Regenerate
/// with the update switch, which checks the kind of bump against the previous lock and writes the new one, then add the
/// digest it reports to <see cref="ContractFiles.LockDigests"/>.
/// </para>
/// </remarks>
internal static class ContractLock
{
    private const string FileName = "lock.json";

    private static readonly Lock Gate = new();

    /// <summary>The lock as it was before this run wrote anything: what a version bump is checked against.</summary>
    private static readonly string? Original = ContractFiles.Read(FileName);

    /// <summary>Checks the checked-in lock against the published digest and against every contract file on disk.</summary>
    public static void Verify(List<string> drift)
    {
        var text = ContractFiles.Read(FileName);
        if (text is null)
        {
            drift.Add($"{FileName}: missing.");
            return;
        }

        if (!ContractFiles.LockDigests.TryGetValue(ContractFiles.ContractVersion, out var digest))
        {
            drift.Add($"{FileName}: version {ContractFiles.ContractVersion} has no published digest in ContractFiles.LockDigests.");
        }
        else if (Hash(text) != digest)
        {
            drift.Add($"{FileName}: is not the lock published for version {ContractFiles.ContractVersion} (its SHA-256 is {Hash(text)}). " +
                "It was edited by hand, or regenerated without a version bump.");
        }

        var (version, files) = Parse(text, drift);
        if (version != ContractFiles.ContractVersion)
        {
            drift.Add($"{FileName}: locks version {version ?? "(none)"}, not {ContractFiles.ContractVersion}.");
        }

        var current = Current();
        foreach (var name in files.Keys.Union(current.Keys).Order(StringComparer.Ordinal))
        {
            if (!current.TryGetValue(name, out var now))
            {
                drift.Add($"{name}: locked, but gone.");
            }
            else if (!files.TryGetValue(name, out var locked))
            {
                drift.Add($"{name}: not in {FileName}.");
            }
            else if (locked != now)
            {
                drift.Add($"{name}: changed since version {version} was locked.");
            }
        }
    }

    /// <summary>
    /// Update mode only: writes the lock for the files as they now are, when that is allowed, and says what is still owed.
    /// Safe to call from every test that rewrote files; the last call sees every write.
    /// </summary>
    public static void Relock(List<string> drift)
    {
        lock (Gate)
        {
            var content = Render(Current());
            (string? Version, Dictionary<string, string> Files)? previous = Original is null ? null : Parse(Original, []);
            var previousVersion = previous?.Version;
            var previousTrusted = Original is not null && previousVersion is not null
                && ContractFiles.LockDigests.TryGetValue(previousVersion, out var published) && published == Hash(Original);

            if (Original is not null && content == Original)
            {
                return;
            }

            if (previous is { } old && previousVersion == ContractFiles.ContractVersion && ContractFiles.LockDigests.ContainsKey(previousVersion))
            {
                drift.Add($"Contract files changed under published version {previousVersion}. Bump ContractFiles.ContractVersion and the " +
                    "README: the minor version if cases were only added, the major version if anything locked changed.");
                return;
            }

            if (previous is { } prior && previousTrusted)
            {
                var changed = prior.Files.Where(entry => !Current().TryGetValue(entry.Key, out var hash) || hash != entry.Value).Select(entry => entry.Key).ToList();
                var (oldMajor, oldMinor) = Split(previousVersion!);
                var (newMajor, newMinor) = Split(ContractFiles.ContractVersion);
                if (changed.Count > 0 && newMajor <= oldMajor)
                {
                    drift.Add($"{changed.Count} locked file(s) changed or went away ({string.Join(", ", changed.Take(5))}): that changes the " +
                        $"contract's meaning, so version {ContractFiles.ContractVersion} must be a major bump from {previousVersion}.");
                    return;
                }

                if (newMajor < oldMajor || (newMajor == oldMajor && newMinor <= oldMinor))
                {
                    drift.Add($"Version {ContractFiles.ContractVersion} is not later than {previousVersion}.");
                    return;
                }
            }

            ContractFiles.Verify(FileName, content, drift);
            if (!ContractFiles.LockDigests.TryGetValue(ContractFiles.ContractVersion, out var digest) || digest != Hash(content))
            {
                drift.Add($"Publish version {ContractFiles.ContractVersion}: set [\"{ContractFiles.ContractVersion}\"] = \"{Hash(content)}\" in ContractFiles.LockDigests.");
            }
        }
    }

    private static string Render(SortedDictionary<string, string> files)
    {
        var entries = new JsonObject();
        foreach (var (name, hash) in files)
        {
            entries[name] = hash;
        }

        return ContractJson.Write(new JsonObject { ["contractVersion"] = ContractFiles.ContractVersion, ["files"] = entries });
    }

    /// <summary>Every contract file but the lock itself, by its path under <c>contract/</c>, with its SHA-256.</summary>
    private static SortedDictionary<string, string> Current()
    {
        var current = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(ContractFiles.Root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(ContractFiles.Root, file).Replace('\\', '/');
            if (relative != FileName)
            {
                current[relative] = ContractFiles.Sha256(file);
            }
        }

        return current;
    }

    /// <summary>The lock's version and entries; a malformed lock or entry is reported, never thrown.</summary>
    private static (string? Version, Dictionary<string, string> Files) Parse(string text, List<string> drift)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException ex)
        {
            drift.Add($"{FileName}: is not JSON ({ex.Message}).");
            return (null, files);
        }

        string? version = root?["contractVersion"] is JsonValue value && value.TryGetValue<string>(out var v) ? v : null;
        if (root?["files"] is not JsonObject entries)
        {
            drift.Add($"{FileName}: has no \"files\" object.");
            return (version, files);
        }

        foreach (var (name, hash) in entries)
        {
            if (hash is JsonValue node && node.TryGetValue<string>(out var h) && h.Length == 64)
            {
                files[name] = h;
            }
            else
            {
                drift.Add($"{FileName}: the entry for {name} is not a SHA-256 hex string.");
            }
        }

        return (version, files);
    }

    private static (int Major, int Minor) Split(string version)
    {
        var parts = version.Split('.');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            ? (major, minor)
            : (-1, -1);
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

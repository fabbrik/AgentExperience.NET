using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// The checked-in contract under <c>contract/</c>, read from the working tree and rewritten only on request.
/// </summary>
/// <remarks>
/// <b>It never updates itself.</b> Set <c>AGENTEXPERIENCE_CONTRACT_FIXTURES_UPDATE=1</c> to rewrite the fixtures from the
/// current library, then read the diff and commit it on purpose. A fixture whose content changes is a change to the
/// contract: bump <see cref="ContractVersion"/> (and the version named in <c>contract/README.md</c>) in the same change,
/// or the lock check fails (see <see cref="FixtureLock"/>).
/// </remarks>
internal static class ContractFiles
{
    /// <summary>The contract version the checked-in fixtures describe. Bumped whenever a fixture changes meaning.</summary>
    public const string ContractVersion = "contract-v1";

    /// <summary>The environment variable that rewrites the fixtures instead of only comparing against them.</summary>
    public const string UpdateVariable = "AGENTEXPERIENCE_CONTRACT_FIXTURES_UPDATE";

    /// <summary>Whether this run rewrites the fixtures.</summary>
    public static bool Updating => Environment.GetEnvironmentVariable(UpdateVariable) == "1";

    /// <summary>The repository's <c>contract/</c> directory.</summary>
    public static string Root { get; } = Locate();

    /// <summary>The <c>contract/fixtures/</c> directory.</summary>
    public static string Fixtures => Path.Combine(Root, "fixtures");

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The checked-in text of <paramref name="relativePath"/> under <c>contract/</c>, or <see langword="null"/> when it does not exist.</summary>
    public static string? Read(string relativePath)
    {
        var path = Path.Combine(Root, relativePath);
        return File.Exists(path) ? File.ReadAllText(path, Utf8) : null;
    }

    /// <summary>
    /// Compares <paramref name="content"/> with the checked-in <paramref name="relativePath"/> byte for byte, adding a line to
    /// <paramref name="drift"/> when they differ; or, when <see cref="Updating"/>, writes it.
    /// </summary>
    public static void Verify(string relativePath, string content, List<string> drift)
    {
        if (content.Contains('\r', StringComparison.Ordinal))
        {
            drift.Add($"{relativePath}: generated content holds a carriage return; the contract is LF only.");
            return;
        }

        var path = Path.Combine(Root, relativePath);
        if (Updating)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, Utf8);
            return;
        }

        if (!File.Exists(path))
        {
            drift.Add($"{relativePath}: missing.");
            return;
        }

        var existing = File.ReadAllBytes(path);
        if (!existing.AsSpan().SequenceEqual(Utf8.GetBytes(content)))
        {
            drift.Add($"{relativePath}: differs from what the library produces now.");
        }
    }

    /// <summary>
    /// The case directories under <c>fixtures/<paramref name="family"/></c> that no case defines: reported as drift, or deleted
    /// when <see cref="Updating"/>.
    /// </summary>
    public static void VerifyNoStrayCases(string family, IEnumerable<string> caseNames, List<string> drift)
    {
        var directory = Path.Combine(Fixtures, family);
        if (!Directory.Exists(directory))
        {
            return;
        }

        var expected = caseNames.ToHashSet(StringComparer.Ordinal);
        foreach (var stray in Directory.EnumerateDirectories(directory).Select(Path.GetFileName).Where(name => !expected.Contains(name!)))
        {
            if (Updating)
            {
                Directory.Delete(Path.Combine(directory, stray!), recursive: true);
            }
            else
            {
                drift.Add($"fixtures/{family}/{stray}: no case defines it.");
            }
        }
    }

    /// <summary>The assertion every fixture check ends with: no drift, or the way to see it and accept it.</summary>
    public static void AssertNoDrift(List<string> drift) =>
        Assert.True(
            drift.Count == 0,
            $"The contract fixtures differ from the library ({drift.Count}):\n  " + string.Join("\n  ", drift) +
            $"\nIf the change is intended, run the tests with {UpdateVariable}=1, read the diff under contract/, bump the " +
            $"contract version if a fixture changed meaning, and commit it.");

    /// <summary>SHA-256 of a file's bytes, lower-case hex.</summary>
    public static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string Locate([CallerFilePath] string thisFile = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "contract"));
        if (!File.Exists(Path.Combine(root, "README.md")))
        {
            throw new InvalidOperationException($"'{root}' is not the contract directory: it has no README.md.");
        }

        return root;
    }
}

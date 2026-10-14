using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// The checked-in contract under <c>contract/</c>, read from the working tree and rewritten only on request.
/// </summary>
/// <remarks>
/// <b>It never updates itself.</b> Set <c>AGENTEXPERIENCE_CONTRACT_FIXTURES_UPDATE=1</c> to rewrite the fixtures from the
/// current library, then read the diff and commit it on purpose. Any change to a contract file is a new contract version:
/// see <see cref="ContractLock"/>.
/// </remarks>
internal static class ContractFiles
{
    /// <summary>
    /// The contract version, <c>major.minor</c>. A change that only adds files (new cases, and a <c>README-1.N.md</c> for
    /// what minor version N adds) bumps the minor version; a change to anything already published (a fixture, the
    /// schema, the SQL, a README) bumps the major version.
    /// </summary>
    public const string ContractVersion = "1.2";

    /// <summary>
    /// The SHA-256 of <c>contract/lock.json</c> for every published version. This, not <c>lock.json</c>, is the authority:
    /// the update switch rewrites <c>lock.json</c> but never this source file, so a changed contract file can only be
    /// accepted by bumping <see cref="ContractVersion"/> and adding its digest here, by hand, in review.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LockDigests { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["1.0"] = "cd0864cee6fc7fbfe2693adf53f95727e0f9ccf02dfca99c287a9eb8fc31f3a1",
        ["1.1"] = "e52de445919c02ca1a6d29c58fd42b12cbbc360ed071947a9b76ac087ebd0db7",
        ["1.2"] = "cd366adbe6cad044ebb924dc5251e0772a847e62bafe336fb6a8c2d1441ba885",
    };

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
    /// Every directory and file under <c>fixtures/<paramref name="family"/></c> that no case defines: a case directory no
    /// case names, a file in a case directory other than <paramref name="caseFiles"/>, or a file in the family directory
    /// other than <paramref name="familyFiles"/>. Reported as drift, or deleted when <see cref="Updating"/>.
    /// </summary>
    public static void VerifyNoStray(string family, IEnumerable<string> caseNames, IReadOnlyCollection<string> caseFiles, List<string> drift, params string[] familyFiles)
    {
        var directory = Path.Combine(Fixtures, family);
        if (!Directory.Exists(directory))
        {
            return;
        }

        void Stray(string path, bool isDirectory)
        {
            if (Updating)
            {
                if (isDirectory)
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }
            }
            else
            {
                drift.Add($"{Path.GetRelativePath(Root, path).Replace('\\', '/')}: no case defines it.");
            }
        }

        var expected = caseNames.ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory).Where(file => !familyFiles.Contains(Path.GetFileName(file))))
        {
            Stray(file, isDirectory: false);
        }

        foreach (var caseDirectory in Directory.EnumerateDirectories(directory))
        {
            if (!expected.Contains(Path.GetFileName(caseDirectory)))
            {
                Stray(caseDirectory, isDirectory: true);
                continue;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(caseDirectory).Where(entry => !caseFiles.Contains(Path.GetFileName(entry))))
            {
                Stray(entry, Directory.Exists(entry));
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

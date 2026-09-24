using System.Runtime.CompilerServices;

namespace AgentExperience.Release.Tests;

/// <summary>
/// The repository root, located from this file's own compile-time path. Release gates read checked-in
/// files (csproj files, lock files, documents), so they need the real tree, not the test output folder.
/// </summary>
internal static class RepositoryRoot
{
    internal static string Path { get; } = Locate();

    private static string Locate([CallerFilePath] string thisFile = "")
    {
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(thisFile)!, "..", ".."));
        if (!File.Exists(System.IO.Path.Combine(root, "AgentExperience.NET.sln")))
        {
            throw new InvalidOperationException($"'{root}' is not the repository root: no AgentExperience.NET.sln there.");
        }

        return root;
    }
}

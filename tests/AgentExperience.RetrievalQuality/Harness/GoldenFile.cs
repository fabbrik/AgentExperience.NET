using System.Runtime.CompilerServices;
using System.Text;

namespace AgentExperience.RetrievalQuality.Harness;

/// <summary>
/// The checked-in reports, read from this assembly's embedded resources and rewritten only on request.
/// </summary>
/// <remarks>
/// <b>It never updates itself.</b> Set <c>AGENTEXPERIENCE_RETRIEVALQUALITY_GOLDEN_UPDATE=1</c> to rewrite the golden
/// files from the current adapters, then read the diff and commit it on purpose.
/// </remarks>
internal static class GoldenFile
{
    /// <summary>The environment variable that rewrites the golden files instead of only comparing against them.</summary>
    public const string UpdateVariable = "AGENTEXPERIENCE_RETRIEVALQUALITY_GOLDEN_UPDATE";

    /// <summary>The checked-in file <paramref name="fileName"/>, as embedded at build time.</summary>
    public static string Read(string fileName)
    {
        var name = "AgentExperience.RetrievalQuality." + fileName;
        using var stream = typeof(GoldenFile).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"'{name}' is not embedded in the harness assembly.");
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return reader.ReadToEnd();
    }

    /// <summary>Writes <paramref name="rendered"/> over the working tree's <paramref name="fileName"/>, only when <see cref="UpdateVariable"/> is <c>1</c>.</summary>
    public static void RegenerateIfRequested(string fileName, string rendered)
    {
        if (Environment.GetEnvironmentVariable(UpdateVariable) != "1")
        {
            return;
        }

        File.WriteAllText(Path.Combine(ProjectDirectory(), fileName), rendered, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>The message a failed comparison carries: how to see the difference, and how to accept it.</summary>
    public static string DriftHint(string fileName) =>
        $"The report differs from the checked-in {fileName}. If the change in retrieval is intended, run the tests with " +
        $"{UpdateVariable}=1, read the diff of {fileName}, and commit it.";

    /// <summary>Where the golden files live in the working tree, for the deliberate regeneration path only.</summary>
    /// <param name="thisFile">Supplied by the compiler; never passed.</param>
    private static string ProjectDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!;
}

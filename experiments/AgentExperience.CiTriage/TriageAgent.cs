using System.ComponentModel;
using System.Text;
using AgentExperience.CiTriage.GitHub;
using Microsoft.Extensions.AI;

namespace AgentExperience.CiTriage;

/// <summary>Everything the agent may see about one failure: only what existed when the run failed.</summary>
/// <param name="Branch">The failing run's branch.</param>
/// <param name="FailSha">The failing commit.</param>
/// <param name="FailedJobs">The names of the failed jobs.</param>
/// <param name="Headlines">The first few specific error lines, for the opening sentence (and so the task text).</param>
/// <param name="TrimmedLog">The failed jobs' logs, trimmed to their error regions (at most 12,000 characters).</param>
public sealed record TriageInput(string Branch, string FailSha, IReadOnlyList<string> FailedJobs, IReadOnlyList<string> Headlines, string TrimmedLog);

/// <summary>
/// The failure the tools read from. Set before each triage and cleared after it: <c>changed_files</c> and
/// <c>read_file</c> only ever see the failing commit, never a later one.
/// </summary>
internal sealed class TriageWorkspace(IRepository repository)
{
    public string? FailSha { get; set; }

    public IRepository Repository => repository;
}

/// <summary>The triage agent's instructions, prompt and tools.</summary>
public static class TriageAgent
{
    public const string Name = "ci-triage";
    public const string ChangedFilesTool = "changed_files";
    public const string ReadFileTool = "read_file";
    public const string LogHeading = "Failed job logs (trimmed):";
    public const int MaxFileCharacters = 8_000;

    /// <summary>The follow-up sent once, in the same run, when a reply holds no parseable answer.</summary>
    public const string AnswerNudge = "Your last message had no answer. Reply now with only the JSON object: {\"kind\": \"code\", \"files\": [...]} or {\"kind\": \"flaky\", \"files\": []}.";

    public const string Instructions =
        "You triage failed CI runs of the AgentExperience.NET repository (a .NET library, with Python and Go ports under python/ and go/). "
        + "Decide whether the failure is flaky (it would pass on a re-run with no change) or needs a code change, and if so name the repository files most likely to need the fix. "
        + "Call changed_files to see what the failing commit changed, and read_file to read a file as it was at the failing commit. "
        + "You only see what existed when the run failed. "
        + "Answer with only a JSON object and no other text: {\"kind\": \"code\", \"files\": [\"path/one.cs\", \"path/two.cs\"]} with at most 5 repository-relative paths, most likely first, "
        + "or {\"kind\": \"flaky\", \"files\": []}.";

    /// <summary>
    /// The user message. It opens with one sentence naming the branch, the failed jobs and the first error lines, because
    /// its first 512 characters become the run's task text, which is what retrieval matches lessons on.
    /// </summary>
    public static string BuildPrompt(TriageInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var builder = new StringBuilder();
        builder.Append("CI failed on branch ").Append(input.Branch)
            .Append(". Failed jobs: ").Append(string.Join(", ", input.FailedJobs)).Append('.');
        if (input.Headlines.Count > 0)
        {
            builder.Append(" Errors: ").Append(string.Join(" | ", input.Headlines));
        }

        builder.Append("\n\nFailing commit: ").Append(input.FailSha).Append('\n');
        builder.Append('\n').Append(LogHeading).Append('\n').Append(input.TrimmedLog).Append('\n');
        builder.Append("\nWhich files most likely need the fix, or is it flaky? Reply with the JSON object only.");
        return builder.ToString();
    }

    internal static IList<AITool> Tools(TriageWorkspace workspace) =>
    [
        AIFunctionFactory.Create(
            [Description("Lists the files the failing commit changed, one per line.")]
            async (CancellationToken cancellationToken) => await ChangedFilesAsync(workspace, cancellationToken).ConfigureAwait(false),
            ChangedFilesTool),
        AIFunctionFactory.Create(
            [Description("Reads one repository file as it was at the failing commit (at most 8,000 characters).")]
            async ([Description("A repository-relative path, such as src/AgentExperience.Core/Foo.cs.")] string path, CancellationToken cancellationToken) =>
                await ReadFileAsync(workspace, path, cancellationToken).ConfigureAwait(false),
            ReadFileTool),
    ];

    internal static async Task<string> ChangedFilesAsync(TriageWorkspace workspace, CancellationToken cancellationToken)
    {
        var sha = workspace.FailSha ?? throw new InvalidOperationException("No failure is being triaged.");
        var files = await workspace.Repository.ChangedFilesAsync(sha, cancellationToken).ConfigureAwait(false);
        return files is null ? "(the failing commit is not available)" : files.Count == 0 ? "(no files)" : string.Join('\n', files);
    }

    internal static async Task<string> ReadFileAsync(TriageWorkspace workspace, string path, CancellationToken cancellationToken)
    {
        var sha = workspace.FailSha ?? throw new InvalidOperationException("No failure is being triaged.");
        var normalised = Scoring.Normalise(path);
        if (normalised is null || normalised.Contains(':', StringComparison.Ordinal) || normalised.Split('/').Contains(".."))
        {
            return "(refused: give a repository-relative path)";
        }

        var text = await workspace.Repository.ShowFileAsync(sha, normalised, cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return "(no such file at the failing commit)";
        }

        return text.Length <= MaxFileCharacters ? text : text[..MaxFileCharacters] + "\n... (cut at 8,000 characters)";
    }
}

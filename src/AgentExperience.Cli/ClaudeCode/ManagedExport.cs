using System.Text;
using AgentExperience.Abstractions;
using AgentExperience.Cli.Engine;

namespace AgentExperience.Cli.ClaudeCode;

/// <summary>
/// <c>--claude-md [file]</c> and <c>--agents-md [file]</c>: the measured lessons synced into the managed section of the
/// project's <c>CLAUDE.md</c> or <c>AGENTS.md</c> (by default the one at the project root), for <c>aexp export</c> and
/// the <c>Stop</c> hook.
/// </summary>
internal static class ManagedExport
{
    /// <summary>The files the options name, resolved: inside the project root, never through a link.</summary>
    public static IReadOnlyList<string> Targets(AexpCli.Arguments args, AexpEngine engine, AexpConsole console)
    {
        var targets = new List<string>();
        foreach (var (option, file) in new[] { ("claude-md", "CLAUDE.md"), ("agents-md", "AGENTS.md") })
        {
            if (!args.Flag(option))
            {
                continue;
            }

            var path = args.Value(option) is { } named
                ? Path.GetFullPath(named, console.WorkingDirectory)
                : Path.Combine(engine.Settings.ProjectRoot, file);
            targets.Add(AexpCli.InsideProject(engine, path, console));
        }

        return targets;
    }

    /// <summary>Reads the measured lessons once and writes each file's section. Returns one sentence saying what changed.</summary>
    public static async Task<string> WriteAsync(AexpEngine engine, IReadOnlyList<string> targets, CancellationToken cancellationToken)
    {
        var (result, lessons) = await engine.MeasuredLessonsAsync(cancellationToken).ConfigureAwait(false);
        if (result.Outcome != ExperienceStoreOutcome.Found)
        {
            throw new AexpUsageException($"The store answered {result.Outcome}; no file was changed.");
        }

        var text = new StringBuilder();
        text.Append("Synced ").Append(lessons.Count).Append(" lesson(s) measured as helping");
        if (!result.HoldoutTracked)
        {
            text.Append(" (this store does not track the holdout)");
        }

        text.Append(": ");
        text.AppendJoin(", ", targets.Select(path =>
            Path.GetRelativePath(engine.Settings.ProjectRoot, path) + " " + ManagedSection.Write(path, lessons).ToString().ToLowerInvariant()));
        return text.Append('.').ToString();
    }
}

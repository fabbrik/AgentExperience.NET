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
        var written = new List<string>();
        foreach (var path in targets)
        {
            var change = await ManagedSection.WriteAsync(path, lessons, LockOf(engine, path), cancellationToken).ConfigureAwait(false);
            written.Add(Path.GetRelativePath(engine.Settings.ProjectRoot, path) + " " + change.ToString().ToLowerInvariant());
        }

        text.AppendJoin(", ", written);
        return text.Append('.').ToString();
    }

    /// <summary>The lock for one file's section: beside the config, named by a hash of the file's full path.</summary>
    private static string LockOf(AexpEngine engine, string path)
    {
        var directory = Path.GetDirectoryName(engine.Settings.ConfigPath)!;
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..16];
        return Path.Combine(directory, "managed-" + hash + ".lock");
    }
}

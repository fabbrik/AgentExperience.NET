using System.Text;
using System.Text.RegularExpressions;

namespace AgentExperience.CiTriage;

/// <summary>One failed job's raw log.</summary>
public sealed record JobLog(string JobName, string Text);

/// <summary>
/// Trims failed jobs' logs to their error regions: GitHub's timestamps and ANSI colours are removed, each line that
/// looks like an error is kept with a few lines before and more after it, overlapping regions are merged, and the whole
/// is cut to <see cref="MaxCharacters"/>, shared evenly between the jobs. A job with no recognisable error line keeps
/// its last lines, where a runner prints why it stopped.
/// </summary>
public static partial class LogTrimmer
{
    public const int MaxCharacters = 12_000;
    public const int LinesBefore = 3;
    public const int LinesAfter = 12;
    public const int TailLines = 40;
    public const string Gap = "...";

    public static string Trim(IReadOnlyList<JobLog> logs, int maxCharacters = MaxCharacters)
    {
        ArgumentNullException.ThrowIfNull(logs);
        if (logs.Count == 0)
        {
            return string.Empty;
        }

        var share = maxCharacters / logs.Count;
        var builder = new StringBuilder();
        foreach (var log in logs)
        {
            var section = Section(log);
            builder.Append(section.Length <= share ? section : section[..Math.Max(0, share - Gap.Length - 1)] + Gap + "\n");
        }

        var text = builder.ToString();
        return text.Length <= maxCharacters ? text : text[..maxCharacters];
    }

    /// <summary>The first few distinct, specific error lines, for the task text (generic runner lines are left out).</summary>
    public static IReadOnlyList<string> Headlines(IReadOnlyList<JobLog> logs, int count = 3, int maxLength = 160)
    {
        var lines = new List<string>();
        foreach (var line in logs.SelectMany(log => Clean(log.Text)))
        {
            var trimmed = line.Trim();
            if (!IsError(trimmed) || Generic().IsMatch(trimmed))
            {
                continue;
            }

            var cut = trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
            if (!lines.Contains(cut, StringComparer.Ordinal))
            {
                lines.Add(cut);
            }

            if (lines.Count == count)
            {
                break;
            }
        }

        return lines;
    }

    internal static string Section(JobLog log)
    {
        var lines = Clean(log.Text);
        var keep = new bool[lines.Length];
        var any = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!IsError(lines[i]))
            {
                continue;
            }

            any = true;
            for (var j = Math.Max(0, i - LinesBefore); j <= Math.Min(lines.Length - 1, i + LinesAfter); j++)
            {
                keep[j] = true;
            }
        }

        if (!any)
        {
            for (var j = Math.Max(0, lines.Length - TailLines); j < lines.Length; j++)
            {
                keep[j] = true;
            }
        }

        var builder = new StringBuilder();
        builder.Append("=== job: ").Append(log.JobName).Append(" ===\n");
        var previous = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!keep[i])
            {
                continue;
            }

            if (i != previous + 1)
            {
                builder.Append(Gap).Append('\n');
            }

            builder.Append(lines[i]).Append('\n');
            previous = i;
        }

        if (previous < lines.Length - 1)
        {
            builder.Append(Gap).Append('\n');
        }

        return builder.ToString();
    }

    internal static string[] Clean(string text) =>
        [.. text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\n')
            .Split('\n')
            .Select(line => Ansi().Replace(Timestamp().Replace(line, string.Empty), string.Empty).TrimEnd())];

    internal static bool IsError(string line) => !Command().IsMatch(line) && ErrorLine().IsMatch(line);

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z ?")]
    private static partial Regex Timestamp();

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex Ansi();

    // A runner echoing the step's script, not output.
    [GeneratedRegex(@"^\s*(\[command\]|##\[group\]|##\[endgroup\]|shell:)")]
    private static partial Regex Command();

    [GeneratedRegex(@"##\[error\]|\berror\b|\[FAIL\]|^\s*Failed\b|\bFAILED\b|Error Message:|Exception\b|Assert\.\w+\(\) Failure|AssertionError|Traceback \(most recent call last\)|^\s*--- FAIL|^panic:|exit code [1-9]", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLine();

    [GeneratedRegex(@"Process completed with exit code|^##\[error\]$", RegexOptions.IgnoreCase)]
    private static partial Regex Generic();
}

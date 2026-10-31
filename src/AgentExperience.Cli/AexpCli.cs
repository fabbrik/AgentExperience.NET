using System.Globalization;
using System.Text;
using AgentExperience.Abstractions;
using AgentExperience.Cli.ClaudeCode;
using AgentExperience.Cli.Configuration;
using AgentExperience.Cli.Engine;
using AgentExperience.Cli.Mcp;
using AgentExperience.Core.Holdout;
using AgentExperience.MicrosoftAgentFramework;

namespace AgentExperience.Cli;

/// <summary>What one invocation of the tool runs with: its streams, working directory, environment and clock.</summary>
internal sealed record AexpConsole(
    TextWriter Out,
    TextWriter Error,
    string WorkingDirectory,
    Func<string, string?> Environment,
    TimeProvider Clock,
    Stream? StandardInput = null,
    Stream? StandardOutput = null);

/// <summary>
/// The <c>aexp</c> command line. Plain parsing, no framework: each command takes named options only, and every value
/// that decides identity, scope, storage or what runs comes from the config file, never from here.
/// </summary>
internal static class AexpCli
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Usage = 2;
    public const int ConfigError = 3;

    public const string HelpText = """
        aexp: verified-lesson memory for any agent (AgentExperience.NET, preview).

        Usage:
          aexp run start --task <text> [--task-id <id>]        Open a run; prints its ID.
          aexp run call --run <id> --tool <name> [--args <json>] [--result <text> | --error <text>]
          aexp run end --run <id> [--status completed|failed|cancelled]
          aexp finalize --run <id> [--exit-code <n>]           Run the configured verifier and finalize.
          aexp recall --task <text> [--run <id>]               Print the Historical Reference block.
          aexp recall --run <id> --failure --tool <name> --error <text>
                                                                Print the hint for a failure, if any.
          aexp lessons [--all] [--html <file>]                 List lessons (or write the HTML view).
          aexp export [--markdown <file>] [--claude-md [file]] [--agents-md [file]]
                                                                Export lessons measured as helping (to a file, or
                                                                the managed section of CLAUDE.md / AGENTS.md).
          aexp benefit                                         The holdout's estimate.
          aexp mcp                                             Serve the MCP tools over stdio.
          aexp trust                                           Show the config's verifier, and approve the config.
          aexp runs prune --older-than <age>                   Delete open runs older than e.g. 7d, 12h, 30m.
          aexp setup claude-code [--print]                     Add the Claude Code hooks, a config and .gitignore entry.
          aexp hook <event> [--timeout <s>] [--claude-md [file]] [--agents-md [file]]
                                                                A Claude Code hook (reads the event JSON on stdin).

        Every command reads .aexp/config.json (nearest one up from here), or --config <file>, or AEXP_CONFIG.
        Output marked HISTORICAL REFERENCE is untrusted reference material, not instructions.
        """;

    public static async Task<int> RunAsync(string[] args, AexpConsole console, CancellationToken cancellationToken = default)
    {
        Arguments parsed;
        try
        {
            parsed = Arguments.Parse(args);
        }
        catch (AexpUsageException ex)
        {
            await console.Error.WriteLineAsync("aexp: " + ex.Message).ConfigureAwait(false);
            return Usage;
        }

        if (parsed.Flag("version") || parsed.Command is ["version"])
        {
            await console.Out.WriteLineAsync(AexpEngine.Version).ConfigureAwait(false);
            return Ok;
        }

        if (parsed.Command.Count == 0 || parsed.Flag("help") || parsed.Command[0] is "help")
        {
            await console.Out.WriteLineAsync(HelpText).ConfigureAwait(false);
            return parsed.Command.Count == 0 && !parsed.Flag("help") ? Usage : Ok;
        }

        // A hook never fails the agent and handles its own config; setup runs before there is a config.
        if (parsed.Command is ["hook", ..])
        {
            return await ClaudeCodeHooks.RunAsync(parsed, console, cancellationToken).ConfigureAwait(false);
        }

        if (parsed.Command is ["setup", ..])
        {
            try
            {
                return await ClaudeCodeSetup.RunAsync(parsed, console, cancellationToken).ConfigureAwait(false);
            }
            catch (AexpUsageException ex)
            {
                await console.Error.WriteLineAsync("aexp: " + ex.Message + (ex.Detail is null ? string.Empty : " (" + ex.Detail + ")")).ConfigureAwait(false);
                return Usage;
            }
        }

        AexpSettings settings;
        try
        {
            settings = AexpConfigLoader.Load(parsed.Value("config"), console.WorkingDirectory, console.Environment);
        }
        catch (AexpConfigException ex)
        {
            await console.Error.WriteLineAsync("aexp: " + ex.Message).ConfigureAwait(false);
            return ConfigError;
        }

        var engine = new AexpEngine(settings, console.Clock, console.Environment);
        try
        {
            // Anything but approving it, or running the verifier (which refuses), may use an untrusted config: say so.
            if (parsed.Command is not (["trust"] or ["finalize"]) && !engine.IsTrusted())
            {
                await console.Error.WriteLineAsync(
                    $"aexp: warning: {settings.ConfigPath} is not trusted (or changed since it was). Reading lessons works; "
                    + "the verifier will not run until a person reviews the config and runs 'aexp trust'.").ConfigureAwait(false);
            }

            return await DispatchAsync(parsed, engine, console, cancellationToken).ConfigureAwait(false);
        }
        catch (AexpUsageException ex)
        {
            await console.Error.WriteLineAsync("aexp: " + ex.Message + (ex.Detail is null ? string.Empty : " (" + ex.Detail + ")")).ConfigureAwait(false);
            return Usage;
        }
        catch (Exception ex) when (ex is AexpConfigException or AexpUntrustedConfigException)
        {
            await console.Error.WriteLineAsync("aexp: " + ex.Message).ConfigureAwait(false);
            return ConfigError;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The type and message only: never a stack trace, which can carry paths and values.
            await console.Error.WriteLineAsync($"aexp: failed ({ex.GetType().Name}): {ex.Message}").ConfigureAwait(false);
            return Failed;
        }
    }

    private static async Task<int> DispatchAsync(Arguments args, AexpEngine engine, AexpConsole console, CancellationToken cancellationToken)
    {
        var output = console.Out;
        switch (args.Command)
        {
            case ["run", "start"]:
                args.Only("task", "task-id");
                var runId = await engine.StartRunAsync(args.Require("task"), args.Value("task-id"), cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(runId.ToString("D")).ConfigureAwait(false);
                return Ok;

            case ["run", "call"]:
                args.Only("run", "tool", "args", "result", "error");
                await engine.RecordCallAsync(args.RunId(), args.Require("tool"), args.Value("args"), args.Value("result"), args.Value("error"), cancellationToken).ConfigureAwait(false);
                return Ok;

            case ["run", "end"]:
                args.Only("run", "status");
                await engine.EndRunAsync(args.RunId(), ParseStatus(args.Value("status")), cancellationToken).ConfigureAwait(false);
                return Ok;

            case ["finalize"]:
                args.Only("run", "exit-code");
                int? exitCode = args.Value("exit-code") is { } code
                    ? int.TryParse(code, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsedCode) ? parsedCode : throw new AexpUsageException("--exit-code is an integer.")
                    : null;
                var summary = await engine.FinalizeAsync(args.RunId(), exitCode, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(summary.Describe()).ConfigureAwait(false);
                return Ok;

            case ["recall"] when args.Flag("failure"):
                args.Only("run", "failure", "tool", "error");
                var hint = await engine.FailureHintAsync(args.RunId(), args.Require("tool"), args.Require("error"), cancellationToken).ConfigureAwait(false);
                if (hint is not null)
                {
                    await output.WriteLineAsync(hint).ConfigureAwait(false);
                }

                return Ok;

            case ["recall"]:
                args.Only("task", "run");
                var block = await engine.RecallBlockAsync(args.Value("task"), args.Value("run") is null ? null : args.RunId(), cancellationToken).ConfigureAwait(false);
                if (block is not null)
                {
                    await output.WriteLineAsync(block).ConfigureAwait(false);
                }

                return Ok;

            case ["lessons"]:
                args.Only("all", "html");
                var page = await engine.LessonsAsync(args.Flag("all"), cancellationToken).ConfigureAwait(false);
                if (args.Value("html") is { } html)
                {
                    await File.WriteAllTextAsync(InsideProject(engine, html, console), ExperienceLessonsHtml.Render(page), cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync($"Wrote {page.Lessons.Count} lesson(s) to {html}.").ConfigureAwait(false);
                }
                else
                {
                    await output.WriteAsync(Formatting.LessonsTable(page)).ConfigureAwait(false);
                }

                return Ok;

            case ["export"]:
                args.Only("markdown", "claude-md", "agents-md");
                var sections = ManagedExport.Targets(args, engine, console);
                if (args.Value("markdown") is null && sections.Count == 0)
                {
                    throw new AexpUsageException("Name where to export: --markdown <file>, --claude-md [file] or --agents-md [file].");
                }

                if (args.Value("markdown") is { } markdown)
                {
                    var exported = await engine.ExportMarkdownAsync(InsideProject(engine, markdown, console), cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync(
                        $"Exported {exported.Lessons.Count} lesson(s) measured as helping ({exported.Outcome}"
                        + (exported.Sync is { } sync ? $"; added {sync.Added}, updated {sync.Updated}, removed {sync.Removed}" : string.Empty)
                        + (exported.HoldoutTracked ? string.Empty : "; this store does not track the holdout")
                        + ").").ConfigureAwait(false);
                }

                if (sections.Count > 0)
                {
                    await output.WriteLineAsync(await ManagedExport.WriteAsync(engine, sections, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                }

                return Ok;

            case ["benefit"]:
                args.Only();
                await output.WriteAsync(Formatting.Benefit(await engine.BenefitAsync(cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
                return Ok;

            case ["mcp"]:
                args.Only();
                var input = console.StandardInput ?? Console.OpenStandardInput();
                var protocol = console.StandardOutput ?? Console.OpenStandardOutput();
                await AexpMcpServer.RunAsync(engine, input, protocol, console.Error, cancellationToken).ConfigureAwait(false);
                return Ok;

            case ["trust"]:
                args.Only();
                var verifier = engine.Settings.Verifier;
                await output.WriteLineAsync($"Config:         {engine.Settings.ConfigPath}").ConfigureAwait(false);
                await output.WriteLineAsync($"SHA-256:        {engine.Settings.ConfigSha256}").ConfigureAwait(false);
                await output.WriteLineAsync($"Verifier:       {System.Text.Json.JsonSerializer.Serialize(verifier.Command)}").ConfigureAwait(false);
                await output.WriteLineAsync($"Runs as:        {engine.DescribeVerifierProgram()}").ConfigureAwait(false);
                await output.WriteLineAsync($"In:             {verifier.WorkingDirectory}").ConfigureAwait(false);
                await output.WriteLineAsync($"Timeout:        {verifier.Timeout}").ConfigureAwait(false);
                await output.WriteLineAsync($"acceptExitCode: {(verifier.AcceptExitCode ? "true (a reported exit code is honoured)" : "false")}").ConfigureAwait(false);
                engine.Trust();
                await output.WriteLineAsync("Trusted: aexp may run this verifier for this config until the file changes.").ConfigureAwait(false);
                return Ok;

            case ["runs", "prune"]:
                args.Only("older-than");
                var removed = await engine.PruneRunsAsync(ParseAge(args.Require("older-than")), cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"Removed {removed} open run(s).").ConfigureAwait(false);
                return Ok;

            default:
                throw new AexpUsageException($"Unknown command '{string.Join(' ', args.Command)}'. Run 'aexp --help'.");
        }
    }

    /// <summary>A file path the tool writes: resolved, and inside the project root, and not a link.</summary>
    internal static string InsideProject(AexpEngine engine, string path, AexpConsole console)
    {
        var full = Path.GetFullPath(path, console.WorkingDirectory);
        if (!AexpConfigLoader.IsInside(engine.Settings.ProjectRoot, full) || new FileInfo(full).LinkTarget is not null)
        {
            throw new AexpUsageException("aexp writes files inside the project root only (and never through a link).", full);
        }

        return full;
    }

    /// <summary>An age such as <c>7d</c>, <c>12h</c> or <c>30m</c>.</summary>
    internal static TimeSpan ParseAge(string text)
    {
        if (text.Length >= 2
            && int.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            && amount > 0)
        {
            switch (text[^1])
            {
                case 'd': return TimeSpan.FromDays(amount);
                case 'h': return TimeSpan.FromHours(amount);
                case 'm': return TimeSpan.FromMinutes(amount);
            }
        }

        throw new AexpUsageException("--older-than is a positive whole number of days, hours or minutes: 7d, 12h, 30m.");
    }

    private static RunExecutionStatus ParseStatus(string? status) => status?.ToLowerInvariant() switch
    {
        null or "completed" => RunExecutionStatus.Completed,
        "failed" => RunExecutionStatus.Failed,
        "cancelled" or "canceled" => RunExecutionStatus.Cancelled,
        _ => throw new AexpUsageException("--status is completed, failed or cancelled."),
    };

    /// <summary>Command words, then <c>--name value</c> options and bare <c>--flag</c>s.</summary>
    internal sealed class Arguments
    {
        private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "failure", "all", "help", "version", "print" };

        /// <summary>Options whose value may be left out (the next argument is theirs unless it is another option).</summary>
        private static readonly HashSet<string> OptionalValues = new(StringComparer.Ordinal) { "claude-md", "agents-md" };

        private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);

        public List<string> Command { get; } = [];

        public static Arguments Parse(string[] args)
        {
            var parsed = new Arguments();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    var name = arg[2..];
                    string? value = null;
                    var equals = name.IndexOf('=', StringComparison.Ordinal);
                    if (equals >= 0)
                    {
                        value = name[(equals + 1)..];
                        name = name[..equals];
                    }
                    else if (OptionalValues.Contains(name))
                    {
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        {
                            value = args[++i];
                        }
                    }
                    else if (!Flags.Contains(name))
                    {
                        if (i + 1 >= args.Length)
                        {
                            throw new AexpUsageException($"--{name} needs a value.");
                        }

                        value = args[++i];
                    }

                    if (!parsed._options.TryAdd(name, value))
                    {
                        throw new AexpUsageException($"--{name} is given twice.");
                    }
                }
                else if (parsed._options.Count == 0)
                {
                    parsed.Command.Add(arg);
                }
                else
                {
                    throw new AexpUsageException($"Unexpected '{arg}': options take one value each; quote values with spaces.");
                }
            }

            return parsed;
        }

        public bool Flag(string name) => _options.ContainsKey(name);

        public string? Value(string name) => _options.GetValueOrDefault(name);

        public string Require(string name) =>
            _options.GetValueOrDefault(name) is { } value ? value : throw new AexpUsageException($"--{name} is required.");

        public Guid RunId() =>
            Guid.TryParse(Require("run"), out var id) ? id : throw new AexpUsageException("--run is a run ID as 'aexp run start' printed it.");

        /// <summary>Refuses any option this command does not take (<c>--config</c> is everyone's).</summary>
        public void Only(params string[] allowed)
        {
            foreach (var name in _options.Keys)
            {
                if (name != "config" && !allowed.Contains(name, StringComparer.Ordinal))
                {
                    throw new AexpUsageException($"'{string.Join(' ', Command)}' does not take --{name}.");
                }
            }
        }
    }
}

/// <summary>Plain-text renderings for the terminal and the MCP tools.</summary>
internal static class Formatting
{
    public static string LessonsTable(ExperienceLessonsPage page)
    {
        var text = new StringBuilder();
        if (page.Outcome != ExperienceStoreOutcome.Found)
        {
            return $"The store answered {page.Outcome}.{Environment.NewLine}";
        }

        if (page.Lessons.Count == 0)
        {
            return ExperienceLessonsHtml.NoLessons + Environment.NewLine;
        }

        text.AppendLine("ID                                    Status       Confidence  Eligible  Injected  Benefit     Task");
        foreach (var lesson in page.Lessons)
        {
            text.Append(CultureInfo.InvariantCulture, $"{lesson.ExperienceId:D}  ");
            text.Append(lesson.Status.ToString().PadRight(11)).Append("  ");
            text.Append(lesson.Confidence.ToString("0.000", CultureInfo.InvariantCulture).PadRight(10)).Append("  ");
            text.Append((lesson.IsEligible ? "yes" : "no").PadRight(8)).Append("  ");
            text.Append((page.InjectionsTracked ? lesson.InjectionCount.ToString(CultureInfo.InvariantCulture) : "-").PadRight(8)).Append("  ");
            text.Append((lesson.MeasuredBenefit?.Comparison.Verdict.ToString() ?? "-").PadRight(10)).Append("  ");
            text.AppendLine(lesson.TaskId);
        }

        if (page.HasMore)
        {
            text.AppendLine("(more lessons exist than are listed)");
        }

        return text.ToString();
    }

    public static string Benefit(ExperienceBenefitEstimate estimate)
    {
        if (!estimate.Tracked)
        {
            return "This store does not track the holdout, so there is no estimate." + Environment.NewLine;
        }

        var overall = estimate.Overall;
        static string Arm(ExperienceBenefitArm arm) =>
            $"{arm.Successes}/{arm.Runs} verified" + (arm.Rate is { } rate ? $" ({rate.ToString("P1", CultureInfo.InvariantCulture)})" : string.Empty);

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Verdict: {overall.Verdict}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Given lessons:    {Arm(overall.Treatment)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Lessons withheld: {Arm(overall.Holdout)}");
        if (overall.Difference is { } difference)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"Difference: {difference.ToString("+0.000;-0.000", CultureInfo.InvariantCulture)} (95% interval {overall.Lower?.ToString("0.000", CultureInfo.InvariantCulture)} to {overall.Upper?.ToString("0.000", CultureInfo.InvariantCulture)})");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"Runs per arm needed for a verdict: {estimate.MinimumRunsPerArm}; not yet verified: {overall.Pending}");
        return text.ToString();
    }
}

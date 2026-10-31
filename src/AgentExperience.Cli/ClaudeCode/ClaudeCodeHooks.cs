using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Cli.Configuration;
using AgentExperience.Cli.Engine;

namespace AgentExperience.Cli.ClaudeCode;

/// <summary>
/// <c>aexp hook &lt;event&gt;</c>: Claude Code's hooks, turned into the CLI's run, recall and finalize. Each hook reads
/// the event's JSON from standard input and writes the hooks output contract to standard output:
/// <list type="bullet">
/// <item><c>SessionStart</c>: a resumed, compacted or cleared session that still has its open run gets the run's block
/// again; a new one waits for its first prompt.</item>
/// <item><c>UserPromptSubmit</c>: the session's first prompt (or the first after a finalized turn) opens a run with the
/// prompt as its task and gets the Historical Reference block, once per run.</item>
/// <item><c>PostToolUse</c> and <c>PostToolUseFailure</c>: the call is recorded (sanitized by the config's rules); a
/// failed one gets the failure hint, when recall has one.</item>
/// <item><c>Stop</c>: a turn that changed something ends its run and finalizes it with the configured verifier, inside
/// the hook's time; a run the verifier could not finish in time stays stored for <c>aexp finalize</c>. An untrusted
/// config records and injects, but the verifier does not run.</item>
/// <item><c>SessionEnd</c>: a run still open is ended and stored for a later <c>aexp finalize</c>.</item>
/// </list>
/// A hook never blocks the agent: whatever goes wrong, it exits 0, writes nothing a model reads but aexp's own fixed
/// texts, and logs to <c>.aexp/hook.log</c>.
/// </summary>
internal static class ClaudeCodeHooks
{
    public const string SessionStart = "SessionStart";
    public const string UserPromptSubmit = "UserPromptSubmit";
    public const string PostToolUse = "PostToolUse";
    public const string PostToolUseFailure = "PostToolUseFailure";
    public const string Stop = "Stop";
    public const string SessionEnd = "SessionEnd";

    /// <summary>The events aexp handles, with the timeout <c>aexp setup claude-code</c> gives each (seconds).</summary>
    public static IReadOnlyList<(string Event, int TimeoutSeconds)> Events { get; } =
    [
        (SessionStart, 60),
        (UserPromptSubmit, 30),
        (PostToolUse, 60),
        (PostToolUseFailure, 60),
        (Stop, 600),
        (SessionEnd, 30),
    ];

    /// <summary>
    /// Built-in tools that only read: a turn that used nothing else changed nothing, so its run stays open (no verifier
    /// runs for a question), and the next turn continues it.
    /// </summary>
    public static IReadOnlySet<string> DefaultReadOnlyTools { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "Read", "Grep", "Glob", "LS", "NotebookRead", "WebFetch", "WebSearch", "TodoWrite", "TodoRead", "ToolSearch",
        "AskUserQuestion", "ExitPlanMode", "BashOutput", "TaskList", "TaskGet",
    };

    /// <summary>What Stop keeps back from its time for ending and finalizing around the verifier.</summary>
    internal static TimeSpan FinalizeReserve { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>A verifier given less than this is not started: the run stays stored for a later finalize.</summary>
    internal static TimeSpan MinimumVerifierTime { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Claude Code's default command-hook timeouts, used when the hook names none with <c>--timeout</c>.</summary>
    private static TimeSpan DefaultTimeout(string hookEvent) => hookEvent switch
    {
        UserPromptSubmit => TimeSpan.FromSeconds(30),
        SessionEnd => TimeSpan.FromSeconds(1.5),
        _ => TimeSpan.FromSeconds(600),
    };

    public static async Task<int> RunAsync(string[] rawArgs, AexpConsole console, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var hookEvent = rawArgs.Length >= 2 && rawArgs[1].All(char.IsAsciiLetter) && rawArgs[1].Length <= 40 ? rawArgs[1] : "(none)";
        HookLog? log = null;
        string? sessionId = null;
        try
        {
            // Parsed here, inside the hook's own error handling: a bad hook command line must not fail the agent either.
            var args = AexpCli.Arguments.Parse(rawArgs);
            if (args.Command.Count != 2 || !Events.Any(known => known.Event == hookEvent))
            {
                throw new AexpUsageException(
                    "aexp hook takes one event: " + string.Join(", ", Events.Select(known => known.Event)) + ".");
            }

            args.Only("timeout", "claude-md", "agents-md");
            var timeout = args.Value("timeout") is { } seconds
                ? double.TryParse(seconds, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and <= 86_400
                    ? TimeSpan.FromSeconds(parsed)
                    : throw new AexpUsageException("--timeout is the hook's timeout in seconds, as its settings give it.")
                : DefaultTimeout(hookEvent);

            // Leave a margin before Claude Code's own timeout, so the hook answers rather than being killed.
            var budget = timeout - TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1).Ticks, timeout.Ticks / 10));
            if (budget <= TimeSpan.Zero)
            {
                budget = timeout / 2;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(budget);

            var settings = AexpConfigLoader.Load(args.Value("config"), StartDirectory(console, args.Value("config")), console.Environment);
            log = new HookLog(Path.Combine(Path.GetDirectoryName(settings.ConfigPath)!, "hook.log"), console.Clock);

            var bytes = await ReadInputAsync(console, deadline.Token).ConfigureAwait(false);
            var input = HookInput.Parse(bytes);
            if (!string.Equals(input.HookEventName, hookEvent, StringComparison.Ordinal))
            {
                throw new FormatException($"the input is a {OneWord(input.HookEventName)} event, not {hookEvent}");
            }

            if (!HookSessionStore.IsValidId(input.SessionId))
            {
                throw new FormatException("the session_id is not a session ID (letters, digits, '-', '_', '.', at most 128)");
            }

            sessionId = input.SessionId;
            var handler = new Handler(new AexpEngine(settings, console.Clock, console.Environment), settings, console, log, args, input, started, budget);
            var output = await handler.RunAsync(deadline.Token).ConfigureAwait(false);
            if (output is not null)
            {
                await console.Out.WriteLineAsync(output.ToJsonString()).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            var message = ex switch
            {
                FormatException => "malformed input: " + ex.Message + "; nothing recorded",
                AexpUsageException usage => "refused: " + usage.Message,
                AexpConfigException config => "no usable config: " + config.Message,
                OperationCanceledException => "out of time: stopped before the hook's timeout; what was done is kept",
                _ => $"failed ({ex.GetType().Name}); nothing more done",
            };
            if (log is not null)
            {
                log.Write(hookEvent, sessionId, message);
            }
            else
            {
                // No project config, so no log: standard error, which Claude Code shows only in its debug output.
                await console.Error.WriteLineAsync("aexp hook " + hookEvent + ": " + message).ConfigureAwait(false);
            }
        }

        // Never block the agent: every outcome is exit 0.
        return AexpCli.Ok;
    }

    /// <summary>
    /// Where the config is looked for: the project directory Claude Code names in the environment. Without it (and
    /// without an explicit <c>--config</c> or <c>AEXP_CONFIG</c>) the hook refuses rather than guess from its working
    /// directory, which a session can move.
    /// </summary>
    private static string StartDirectory(AexpConsole console, string? explicitConfig)
    {
        var project = console.Environment("CLAUDE_PROJECT_DIR");
        if (!string.IsNullOrWhiteSpace(project) && Path.IsPathFullyQualified(project) && Directory.Exists(project))
        {
            return project;
        }

        if (!string.IsNullOrWhiteSpace(explicitConfig) || !string.IsNullOrWhiteSpace(console.Environment(AexpConfigLoader.EnvironmentVariable)))
        {
            return console.WorkingDirectory;
        }

        throw new AexpConfigException(
            "CLAUDE_PROJECT_DIR is not set (or is not an existing absolute directory), so the project is unknown; nothing done. "
            + "Claude Code sets it for hooks; elsewhere name the config with --config or AEXP_CONFIG.");
    }

    private static async Task<byte[]> ReadInputAsync(AexpConsole console, CancellationToken cancellationToken)
    {
        var stream = console.StandardInput ?? Console.OpenStandardInput();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > HookInput.MaxBytes)
            {
                throw new FormatException($"the hook input is larger than {HookInput.MaxBytes} bytes");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string OneWord(string text) =>
        text.Length <= 40 && text.All(char.IsAsciiLetterOrDigit) ? text : "different";

    internal static JsonObject Context(string hookEvent, string text) => new()
    {
        ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = hookEvent, ["additionalContext"] = text },
    };

    internal static JsonObject Message(string text) => new() { ["systemMessage"] = text };

    /// <summary>
    /// Whether a call only read: a tool on the read-only list (the config's, else the default), or a Bash command that
    /// starts with one of the config's read-only prefixes and chains, pipes or redirects nothing.
    /// </summary>
    internal static bool OnlyReads(AexpSettings settings, string tool, JsonObject? toolInput)
    {
        var tools = settings.ReadOnlyTools is { } configured ? configured : (IEnumerable<string>)DefaultReadOnlyTools;
        if (tools.Contains(tool, StringComparer.Ordinal))
        {
            return true;
        }

        if (tool != "Bash" || settings.ReadOnlyBashPrefixes is not { Count: > 0 } prefixes
            || toolInput?["command"] is not JsonValue value || !value.TryGetValue<string>(out var command))
        {
            return false;
        }

        command = command.Trim();
        if (command.IndexOfAny([';', '&', '|', '>', '<', '`', '$', '\n', '\r', '(', ')']) >= 0)
        {
            return false;
        }

        return prefixes.Any(prefix =>
            command.StartsWith(prefix, StringComparison.Ordinal)
            && (command.Length == prefix.Length || command[prefix.Length] == ' '));
    }

    private sealed class Handler(
        AexpEngine engine,
        AexpSettings settings,
        AexpConsole console,
        HookLog log,
        AexpCli.Arguments args,
        HookInput input,
        long started,
        TimeSpan budget)
    {
        private readonly HookSessionStore _sessions = new(Path.Combine(Path.GetDirectoryName(settings.ConfigPath)!, "sessions"));

        private void Log(string message) => log.Write(input.HookEventName, input.SessionId, message);

        public Task<JsonObject?> RunAsync(CancellationToken cancellationToken) => input.HookEventName switch
        {
            SessionStart => SessionStartAsync(cancellationToken),
            UserPromptSubmit => PromptAsync(cancellationToken),
            PostToolUse or PostToolUseFailure => ToolAsync(cancellationToken),
            Stop => StopAsync(cancellationToken),
            SessionEnd => SessionEndAsync(cancellationToken),
            _ => Task.FromResult<JsonObject?>(null),
        };

        private Task<T> WithSessionAsync<T>(Func<HookSession, Task<T>> update, CancellationToken cancellationToken) =>
            _sessions.UpdateAsync(input.SessionId, console.Clock.GetUtcNow(), update, cancellationToken);

        /// <summary>The session's run, when it is still open; a run that ended elsewhere is forgotten.</summary>
        private RunJournal? OpenRun(HookSession session)
        {
            if (session.RunId is not { } runId)
            {
                return null;
            }

            var journal = engine.PeekOpenRun(runId);
            if (journal is null)
            {
                Forget(session);
            }

            return journal;
        }

        private static void Forget(HookSession session)
        {
            session.RunId = null;
            session.RunChanged = false;
            session.RunUnpinned = false;
        }

        private async Task<JsonObject?> SessionStartAsync(CancellationToken cancellationToken)
        {
            var source = OneWord(input.Source ?? "unknown");
            var block = await WithSessionAsync<string?>(async session =>
            {
                if (OpenRun(session) is not { } journal)
                {
                    Log($"session started ({source}); the run opens with the first prompt");
                    return null;
                }

                if (!journal.BlockRequested)
                {
                    Log($"session {source}: continues run {journal.RunId:D}");
                    return null;
                }

                // The context was resumed or compacted: give the run's block again. Asked again, the CLI repeats what was
                // delivered and records nothing new.
                Log($"session {source}: continues run {journal.RunId:D}; its block given again");
                return await engine.RecallBlockAsync(null, journal.RunId, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            return block is null ? null : Context(SessionStart, block);
        }

        /// <summary>
        /// The next run's ID: with the holdout on, one the library assigns to the session's arm (drawn once per session);
        /// <see langword="null"/> when none was found within the bound.
        /// </summary>
        private Guid? NextRunId(HookSession session)
        {
            session.Turns++;
            if (settings.HoldoutRate <= 0)
            {
                return Guid.NewGuid();
            }

            var salt = settings.HoldoutSalt ?? MicrosoftAgentFramework.Injection.ExperienceHoldoutOptions.DefaultSalt;
            session.Arm ??= SessionHoldout.ArmOf(input.SessionId, salt, settings.HoldoutRate);
            return SessionHoldout.RunIdFor(input.SessionId, session.Turns, session.Arm.Value, salt, settings.HoldoutRate);
        }

        private async Task<JsonObject?> PromptAsync(CancellationToken cancellationToken)
        {
            var block = await WithSessionAsync<string?>(async session =>
            {
                if (OpenRun(session) is { } open)
                {
                    Log($"prompt: run {open.RunId:D} continues");
                    return null;
                }

                if (string.IsNullOrWhiteSpace(input.Prompt))
                {
                    Log("prompt: empty; no run opened");
                    return null;
                }

                var pinned = NextRunId(session);
                var runId = await engine.StartRunAsync(input.Prompt, null, cancellationToken, pinned ?? Guid.NewGuid()).ConfigureAwait(false);

                // Linked before anything else can fail: the session file is written even if what follows throws.
                session.RunId = runId;
                session.RunChanged = false;
                session.RunUnpinned = pinned is null;
                if (pinned is null)
                {
                    Log($"prompt: run {runId:D} opened; no run ID in the session's holdout arm was found, so it gets no block "
                        + "or hint and nothing is recorded to the holdout");
                    return null;
                }

                try
                {
                    var given = await engine.RecallBlockAsync(null, runId, cancellationToken).ConfigureAwait(false);
                    Log($"prompt: run {runId:D} opened (arm {session.Arm?.ToString() ?? "none"}); "
                        + (given is null ? "no block (none applies, or the holdout withheld it)" : "block given"));
                    return given;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log($"prompt: run {runId:D} opened; the block could not be asked for ({Describe(ex)})");
                    return null;
                }
            }, cancellationToken).ConfigureAwait(false);

            return block is null ? null : Context(UserPromptSubmit, block);
        }

        private async Task<JsonObject?> ToolAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(input.ToolName))
            {
                throw new FormatException("the input has no tool_name");
            }

            var (result, error, interrupted) = input.HookEventName == PostToolUseFailure
                ? (null, string.IsNullOrWhiteSpace(input.Error) ? "the tool failed" : input.Error, input.IsInterrupt)
                : input.ToolOutcome();

            // A coding agent's tool results are file contents and command output: kept only when a recipe consent asks
            // for that tool's result excerpt. The call's name, allowed arguments and outcome are what a lesson needs.
            if (!settings.Recipes.Any(recipe => recipe.ResultExcerpt && string.Equals(recipe.Tool, input.ToolName, StringComparison.Ordinal)))
            {
                result = null;
            }

            var changes = !OnlyReads(settings, input.ToolName, input.ToolInput);
            var hint = await WithSessionAsync<string?>(async session =>
            {
                if (OpenRun(session) is not { } journal)
                {
                    Log($"{input.ToolName}: no open run (no prompt seen in this session yet); not recorded");
                    return null;
                }

                await engine.RecordCallAsync(journal.RunId, input.ToolName, input.ToolInput?.ToJsonString(), result, error, cancellationToken).ConfigureAwait(false);
                session.RunChanged |= changes;
                var kind = error is null ? "returned" : interrupted ? "interrupted" : "failed";
                if (error is null || interrupted || session.RunUnpinned)
                {
                    Log($"{input.ToolName}: recorded ({kind}{(changes ? string.Empty : ", read-only")}) in run {journal.RunId:D}");
                    return null;
                }

                var given = await engine.FailureHintAsync(journal.RunId, input.ToolName, error, cancellationToken).ConfigureAwait(false);
                Log($"{input.ToolName}: recorded (failed) in run {journal.RunId:D}; " + (given is null ? "no hint" : "hint given"));
                return given;
            }, cancellationToken).ConfigureAwait(false);

            return hint is null ? null : Context(input.HookEventName, hint);
        }

        private async Task<JsonObject?> StopAsync(CancellationToken cancellationToken)
        {
            if (input.StopHookActive)
            {
                Log("stop: already continuing because of a stop hook; nothing done");
                return null;
            }

            // End the run under the session's lock, and forget it, so the next prompt opens a new one; the verifier runs
            // after, holding no lock.
            var ended = await WithSessionAsync<Guid?>(async session =>
            {
                if (OpenRun(session) is not { } journal)
                {
                    Log("stop: no open run");
                    return null;
                }

                if (!session.RunChanged)
                {
                    Log($"stop: run {journal.RunId:D} changed nothing yet ({journal.Calls.Count} read-only call(s)); it stays open");
                    return null;
                }

                await engine.EndRunAsync(journal.RunId, RunExecutionStatus.Completed, cancellationToken).ConfigureAwait(false);
                Forget(session);
                return journal.RunId;
            }, cancellationToken).ConfigureAwait(false);

            if (ended is not { } runId)
            {
                return null;
            }

            var message = await FinalizeAsync(runId, cancellationToken).ConfigureAwait(false);
            try
            {
                if (ManagedExport.Targets(args, engine, console) is { Count: > 0 } targets)
                {
                    message += " " + await ManagedExport.WriteAsync(engine, targets, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message += " Lessons not exported: " + Describe(ex);
            }

            Log(message);
            return Message(message);
        }

        private async Task<string> FinalizeAsync(Guid runId, CancellationToken cancellationToken)
        {
            var later = $"Run 'aexp finalize --run {runId:D}' to finalize it.";
            if (!engine.IsTrusted())
            {
                return $"aexp: run {runId:D} recorded; the verifier did not run, because .aexp/config.json is not trusted "
                    + "(or changed since it was). A person reviews it and runs 'aexp trust'; then " + later;
            }

            var remaining = budget - Stopwatch.GetElapsedTime(started) - FinalizeReserve;
            if (remaining < MinimumVerifierTime)
            {
                return $"aexp: run {runId:D} recorded; no time was left in this hook for the verifier. " + later;
            }

            try
            {
                var summary = await engine.FinalizeAsync(runId, null, cancellationToken, remaining).ConfigureAwait(false);
                return "aexp: " + summary.Describe();
            }
            catch (AexpVerifierTimedOutException)
            {
                return $"aexp: run {runId:D} recorded; the verifier did not finish in the time it had "
                    + $"({Seconds(remaining < settings.Verifier.Timeout ? remaining : settings.Verifier.Timeout)}), so nothing was finalized. " + later;
            }
            catch (AexpUsageException ex)
            {
                return $"aexp: run {runId:D} recorded, not finalized: {ex.Message}";
            }
        }

        private async Task<JsonObject?> SessionEndAsync(CancellationToken cancellationToken)
        {
            var reason = OneWord(input.Reason ?? "unknown");
            await WithSessionAsync(async session =>
            {
                if (OpenRun(session) is not { } journal)
                {
                    Log($"session ended ({reason})");
                    return 0;
                }

                if (session.RunChanged)
                {
                    // No time to verify here (Claude Code gives SessionEnd hooks seconds): store it for a later finalize.
                    await engine.EndRunAsync(journal.RunId, RunExecutionStatus.Completed, cancellationToken).ConfigureAwait(false);
                    Log($"session ended ({reason}); run {journal.RunId:D} stored, not finalized: run 'aexp finalize --run {journal.RunId:D}'");
                }
                else
                {
                    engine.DiscardOpenRun(journal.RunId);
                    Log($"session ended ({reason}); run {journal.RunId:D} changed nothing and was discarded");
                }

                Forget(session);
                return 0;
            }, cancellationToken).ConfigureAwait(false);
            _sessions.Delete(input.SessionId);
            return null;
        }

        private static string Describe(Exception ex) => ex is AexpUsageException ? ex.Message : $"{ex.GetType().Name}";

        private static string Seconds(TimeSpan time) =>
            Math.Max(0, (int)Math.Round(time.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + "s";
    }
}

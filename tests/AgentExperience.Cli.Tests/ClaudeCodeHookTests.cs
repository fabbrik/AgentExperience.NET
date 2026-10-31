using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentExperience.Cli.ClaudeCode;
using AgentExperience.MicrosoftAgentFramework.Injection;

namespace AgentExperience.Cli.Tests;

/// <summary>
/// Story 28.9: the Claude Code hooks, driven with hook inputs shaped as the hooks reference documents them (no Claude Code
/// needed). Each hook runs through the tool's entry point with a fresh container, as Claude Code's separate hook
/// processes would.
/// </summary>
public sealed partial class ClaudeCodeHookTests
{
    private const string Task = "Fix the failing npm test in the payments service";
    private const string MissingModule = "Error: Cannot find module 'ledger'";

    private static Dictionary<string, string> Session(string id, string? prompt = null, string? error = null)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["session"] = id };
        if (prompt is not null)
        {
            values["prompt"] = prompt;
        }

        if (error is not null)
        {
            values["error"] = error;
        }

        return values;
    }

    /// <summary>A hook that must succeed (they all exit 0) and print nothing to standard error.</summary>
    private static async Task<JsonObject?> HookAsync(TempProject project, string fixture, Dictionary<string, string> values, params string[] extra)
    {
        var result = await project.HookAsync(fixture, values, extra);
        Assert.Equal(0, result.Exit);
        Assert.Equal(string.Empty, result.Error);
        return string.IsNullOrWhiteSpace(result.Output) ? null : JsonNode.Parse(result.Output)!.AsObject();
    }

    private static string? AdditionalContext(JsonObject? output, string hookEvent)
    {
        if (output?["hookSpecificOutput"] is not JsonObject specific)
        {
            return null;
        }

        Assert.Equal(hookEvent, (string?)specific["hookEventName"]);
        return (string?)specific["additionalContext"];
    }

    /// <summary>A session that fails a Bash test run, writes the fix and passes, finalized on the verifier at Stop.</summary>
    private static async Task<JsonObject?> LearnAsync(TempProject project, string session, params string[] stopArgs)
    {
        Assert.Null(await HookAsync(project, "SessionStart.startup", Session(session)));
        Assert.Null(await HookAsync(project, "UserPromptSubmit", Session(session, Task)));
        Assert.Null(await HookAsync(project, "PostToolUseFailure.bash", Session(session, error: MissingModule)));
        Assert.Null(await HookAsync(project, "PostToolUse.write", Session(session)));
        Assert.Null(await HookAsync(project, "PostToolUse.bash", Session(session)));
        project.Fix();
        return await HookAsync(project, "Stop", Session(session), stopArgs);
    }

    [Fact]
    public async Task A_session_that_fails_then_fixes_is_verified_and_the_next_sessions_get_the_block_and_the_hint()
    {
        using var project = new TempProject();

        // Session 1: fail, fix, pass; Stop runs the verifier (never anything the model said) and finalizes it.
        var stop = await LearnAsync(project, "11111111-1111-4111-8111-111111111111");
        var message = (string?)stop?["systemMessage"];
        Assert.NotNull(message);
        Assert.Contains("verifier exit 0 (pass); finalization Validated", message);
        Assert.Null(stop!["hookSpecificOutput"]); // A Stop hook's additionalContext would keep Claude going: never given.
        Assert.Empty(Directory.EnumerateFiles(project.RunsDirectory, "*.json"));

        // Session 2, same task: the prompt gets the block, once per run.
        const string second = "22222222-2222-4222-8222-222222222222";
        Assert.Null(await HookAsync(project, "SessionStart.startup", Session(second)));
        var block = AdditionalContext(await HookAsync(project, "UserPromptSubmit", Session(second, Task + " again")), "UserPromptSubmit");
        Assert.NotNull(block);
        Assert.StartsWith(HistoricalReferenceWriter.BlockBegin, block);
        Assert.Contains("Bash [failed: exit 1]", block);
        Assert.Contains("Write [returned]", block);
        Assert.Null(await HookAsync(project, "UserPromptSubmit", Session(second, "and keep the changelog tidy")));

        // A compacted context gets the run's block again, recording nothing new.
        Assert.Equal(block, AdditionalContext(await HookAsync(project, "SessionStart.compact", Session(second)), "SessionStart"));

        // Session 3, another task: the same failure gets the hint for what worked after it.
        const string third = "33333333-3333-4333-8333-333333333333";
        Assert.Null(await HookAsync(project, "UserPromptSubmit", Session(third, "Rotate the staging certificates")));
        var hint = AdditionalContext(
            await HookAsync(project, "PostToolUseFailure.bash", Session(third, error: "Error: Cannot find module 'audit'")),
            "PostToolUseFailure");
        Assert.NotNull(hint);
        Assert.StartsWith(HistoricalReferenceWriter.RecallBegin, hint);
        Assert.Contains("Bash [failed: exit 1]", hint);
        Assert.Contains(HistoricalReferenceWriter.RecallWorkedPrefix, hint);

        // Once per signature per run.
        Assert.Null(await HookAsync(project, "PostToolUseFailure.bash", Session(third, error: MissingModule)));

        // The log says what happened, never what the session carried.
        var log = project.HookLog;
        Assert.Contains("UserPromptSubmit", log);
        Assert.Contains("hint given", log);
        Assert.DoesNotContain("Cannot find module", log);
        Assert.DoesNotContain("staging certificates", log);
    }

    [Fact]
    public async Task Stop_can_sync_the_managed_section_after_finalizing()
    {
        using var project = new TempProject();
        var claudeMd = Path.Combine(project.Root, "CLAUDE.md");
        File.WriteAllText(claudeMd, "# Project\n\nUse npm.\n");

        var stop = await LearnAsync(project, "44444444-4444-4444-8444-444444444444", "--claude-md");
        Assert.Contains("Synced 0 lesson(s)", (string?)stop?["systemMessage"]);
        var text = File.ReadAllText(claudeMd);
        Assert.StartsWith("# Project\n\nUse npm.\n\n" + ManagedSection.Begin, text);
        Assert.Contains(ManagedSection.NoLessons, text);
    }

    [Fact]
    public async Task An_untrusted_config_records_and_injects_but_the_stop_hook_does_not_run_the_verifier()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var project = new TempProject(
            config => config["verify"]!["command"] = new JsonArray("sh", "-c", "touch verifier-ran; test -f fixed"),
            trusted: false);
        var stop = await LearnAsync(project, "55555555-5555-4555-8555-555555555555");
        var message = (string?)stop?["systemMessage"];
        Assert.NotNull(message);
        Assert.Contains("not trusted", message);
        Assert.Contains("aexp trust", message);
        Assert.False(File.Exists(Path.Combine(project.Root, "verifier-ran")));
        Assert.Contains("not trusted", project.HookLog);

        // The run is stored: once a person approves the config, it is finalized as the message says.
        var runId = RunIdIn(message);
        project.Trust();
        Assert.Contains("finalization Validated", await project.OkAsync("finalize", "--run", runId));
        Assert.True(File.Exists(Path.Combine(project.Root, "verifier-ran")));
    }

    [Fact]
    public async Task A_verifier_longer_than_the_hook_leaves_the_run_stored_for_a_later_finalize()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Fails fast once fixed is gone... and hangs until killed while it is missing: the hook cannot wait for it.
        using var project = new TempProject(config => config["verify"]!["command"] = new JsonArray("sh", "-c", "test -f fixed && exit 0; sleep 300"));
        const string session = "66666666-6666-4666-8666-666666666666";
        Assert.Null(await HookAsync(project, "UserPromptSubmit", Session(session, Task)));
        Assert.Null(await HookAsync(project, "PostToolUse.write", Session(session)));
        var stop = await HookAsync(project, "Stop", Session(session), "--timeout", "12");
        var message = (string?)stop?["systemMessage"];
        Assert.NotNull(message);
        Assert.Contains("did not finish within this hook's time", message);

        project.Fix();
        Assert.Contains("finalization Validated", await project.OkAsync("finalize", "--run", RunIdIn(message)));
    }

    [Fact]
    public async Task A_turn_that_only_read_keeps_its_run_open_and_the_next_turn_continues_it()
    {
        using var project = new TempProject();
        const string session = "77777777-7777-4777-8777-777777777777";
        Assert.Null(await HookAsync(project, "UserPromptSubmit", Session(session, "What does the ledger module do?")));
        Assert.Null(await HookAsync(project, "PostToolUse.read", Session(session)));
        Assert.Null(await HookAsync(project, "Stop", Session(session)));
        var open = Assert.Single(Directory.EnumerateFiles(project.RunsDirectory, "*.json"));

        Assert.Null(await HookAsync(project, "UserPromptSubmit", Session(session, "Now fix it")));
        Assert.Null(await HookAsync(project, "PostToolUse.write", Session(session)));
        project.Fix();
        var stop = await HookAsync(project, "Stop", Session(session));
        Assert.Contains($"Run {Path.GetFileNameWithoutExtension(open)}: verifier exit 0 (pass)", (string?)stop?["systemMessage"]);
    }

    [Fact]
    public async Task Session_end_stores_an_open_run_that_changed_something()
    {
        using var project = new TempProject();
        const string session = "88888888-8888-4888-8888-888888888888";
        Assert.Null(await HookAsync(project, "UserPromptSubmit", Session(session, Task)));
        Assert.Null(await HookAsync(project, "PostToolUse.write", Session(session)));
        var runId = Path.GetFileNameWithoutExtension(Assert.Single(Directory.EnumerateFiles(project.RunsDirectory, "*.json")));

        Assert.Null(await HookAsync(project, "SessionEnd", Session(session)));
        Assert.Empty(Directory.EnumerateFiles(project.RunsDirectory, "*.json"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(project.Root, ".aexp", "sessions"), "*.json"));
        Assert.Contains($"aexp finalize --run {runId}", project.HookLog);

        project.Fix();
        Assert.Contains("finalization Validated", await project.OkAsync("finalize", "--run", runId));
    }

    [Theory]
    [InlineData("not json at all", "malformed input: the hook input is not JSON")]
    [InlineData("[1, 2]", "malformed input: the hook input is not a JSON object")]
    [InlineData("""{"hook_event_name":"Stop"}""", "malformed input: the hook input has no session_id")]
    [InlineData("""{"hook_event_name":"PreToolUse","session_id":"abc"}""", "is a PreToolUse event, not PostToolUse")]
    [InlineData("""{"hook_event_name":"PostToolUse","session_id":"../../etc"}""", "the session_id is not a session ID")]
    [InlineData("""{"hook_event_name":"PostToolUse","session_id":"abc"}""", "the input has no tool_name")]
    public async Task Malformed_input_exits_0_says_nothing_to_the_agent_and_is_logged(string input, string logged)
    {
        using var project = new TempProject();
        var result = await project.HookRawAsync("PostToolUse", System.Text.Encoding.UTF8.GetBytes(input));
        Assert.Equal(0, result.Exit);
        Assert.Equal(string.Empty, result.Output);
        Assert.Contains(logged, project.HookLog);
        Assert.Empty(Directory.Exists(project.RunsDirectory) ? Directory.EnumerateFiles(project.RunsDirectory) : []);
    }

    [Fact]
    public async Task A_hook_exits_0_without_a_config_and_for_an_unknown_event()
    {
        var empty = Path.Combine(Path.GetTempPath(), $"aexp-noconfig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(empty, ".git"));
        try
        {
            var output = new StringWriter();
            var error = new StringWriter();
            using var stdin = new MemoryStream("{}"u8.ToArray());
            var exit = await AexpCli.RunAsync(
                ["hook", "Stop"],
                new AexpConsole(output, error, empty, name => name == "CLAUDE_PROJECT_DIR" ? null : Environment.GetEnvironmentVariable(name), TimeProvider.System, stdin));
            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Contains("no usable config", error.ToString());
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }

        using var project = new TempProject();
        var unknown = await project.HookRawAsync("PreToolUse", "{}"u8.ToArray());
        Assert.Equal(0, unknown.Exit);
        Assert.Equal(string.Empty, unknown.Output);
    }

    [Fact]
    public async Task A_tool_call_before_any_prompt_is_not_recorded()
    {
        using var project = new TempProject();
        Assert.Null(await HookAsync(project, "PostToolUse.bash", Session("99999999-9999-4999-8999-999999999999")));
        Assert.False(Directory.Exists(project.RunsDirectory) && Directory.EnumerateFiles(project.RunsDirectory, "*.json").Any());
        Assert.Contains("no open run", project.HookLog);
    }

    [Fact]
    public void A_post_tool_use_response_is_read_as_the_reference_documents_it()
    {
        static HookInput Input(string response) => HookInput.Parse(System.Text.Encoding.UTF8.GetBytes(
            $$"""{"hook_event_name":"PostToolUse","session_id":"s","tool_name":"Bash","tool_input":{"command":"ls"},"tool_response":{{response}}}"""));

        Assert.Equal<(string?, string?, bool)>(("42 passing", null, false), Input("""{"stdout":"42 passing","stderr":"","interrupted":false,"isImage":false}""").ToolOutcome());
        Assert.Equal<(string?, string?, bool)>((null, "interrupted", true), Input("""{"stdout":"","stderr":"","interrupted":true,"isImage":false}""").ToolOutcome());
        Assert.Equal<(string?, string?, bool)>((null, "Exit code 2\nboom", false), Input("""{"stdout":"","stderr":"boom","exit_code":2}""").ToolOutcome());
        Assert.Equal<(string?, string?, bool)>((null, "bad input", false), Input("""{"is_error":true,"error":"bad input"}""").ToolOutcome());
        Assert.Equal<(string?, string?, bool)>(("""{"filePath":"/a","type":"create"}""", null, false), Input("""{"filePath":"/a","type":"create"}""").ToolOutcome());
        Assert.Equal<(string?, string?, bool)>(("plain", null, false), Input("\"plain\"").ToolOutcome());
    }

    [Fact]
    public async Task The_built_tool_reads_the_hook_input_from_its_standard_input()
    {
        using var project = new TempProject();
        await LearnAsync(project, "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

        var input = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "HookFixtures", "UserPromptSubmit.json"))
            .Replace("{{session}}", "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", StringComparison.Ordinal)
            .Replace("{{prompt}}", Task, StringComparison.Ordinal);
        var (exit, output) = await SpawnHookAsync(project, "UserPromptSubmit", input);
        Assert.Equal(0, exit);
        var block = AdditionalContext(JsonNode.Parse(output)!.AsObject(), "UserPromptSubmit");
        Assert.StartsWith(HistoricalReferenceWriter.BlockBegin, block);

        // Garbage on standard input: exit 0, nothing on standard output.
        var (garbageExit, garbageOutput) = await SpawnHookAsync(project, "PostToolUse", "\u0001\u0002 not json");
        Assert.Equal(0, garbageExit);
        Assert.Equal(string.Empty, garbageOutput);
    }

    private static async Task<(int Exit, string Output)> SpawnHookAsync(TempProject project, string hookEvent, string input)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = project.Root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["XDG_CONFIG_HOME"] = project.UserConfig;
        start.Environment.Remove("CLAUDE_PROJECT_DIR");
        foreach (var arg in new[] { Path.Combine(AppContext.BaseDirectory, "AgentExperience.Cli.dll"), "hook", hookEvent, "--timeout", "120" })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        // A hang guard only: the assertions are on what the process said, not on how long it took.
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(5)), "the hook did not exit");
        _ = await error;
        return (process.ExitCode, await output);
    }

    private static string RunIdIn(string message) => RunId().Match(message) is { Success: true } match
        ? match.Value
        : throw new Xunit.Sdk.XunitException("No run ID in: " + message);

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex RunId();
}

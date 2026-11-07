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
    public async Task A_verifier_that_does_not_finish_leaves_the_run_stored_for_a_later_finalize()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Hangs until killed while 'fixed' is missing; its own bound (2 s) is far inside the hook's (600 s).
        using var project = new TempProject(config =>
        {
            config["verify"]!["command"] = new JsonArray("sh", "-c", "test -f fixed && exit 0; sleep 300");
            config["verify"]!["timeoutSeconds"] = 2;
        });
        const string session = "66666666-6666-4666-8666-666666666666";
        Assert.Null(await HookAsync(project, "UserPromptSubmit", Session(session, Task)));
        Assert.Null(await HookAsync(project, "PostToolUse.write", Session(session)));
        var stop = await HookAsync(project, "Stop", Session(session), "--timeout", "600");
        var message = (string?)stop?["systemMessage"];
        Assert.NotNull(message);
        Assert.Contains("the verifier did not finish in the time it had (2s)", message);

        project.Fix();
        Assert.Contains("finalization Validated", await project.OkAsync("finalize", "--run", RunIdIn(message)));
    }

    [Fact]
    public async Task With_no_time_left_in_the_hook_the_verifier_is_not_started()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var project = new TempProject(config => config["verify"]!["command"] = new JsonArray("sh", "-c", "touch verifier-ran; test -f fixed"));
        const string session = "67676767-6767-4676-8676-676767676767";
        Assert.Null(await HookAsync(project, "UserPromptSubmit", Session(session, Task)));
        Assert.Null(await HookAsync(project, "PostToolUse.write", Session(session)));
        var reserve = ClaudeCodeHooks.FinalizeReserve;
        ClaudeCodeHooks.FinalizeReserve = TimeSpan.FromDays(1);
        try
        {
            var stop = await HookAsync(project, "Stop", Session(session), "--timeout", "600");
            var message = (string?)stop?["systemMessage"];
            Assert.Contains("no time was left in this hook for the verifier", message);
            Assert.False(File.Exists(Path.Combine(project.Root, "verifier-ran")));
            project.Fix();
            Assert.Contains("finalization Validated", await project.OkAsync("finalize", "--run", RunIdIn(message!)));
        }
        finally
        {
            ClaudeCodeHooks.FinalizeReserve = reserve;
        }
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
    [InlineData("""{"hook_event_name":"PostToolUse","session_id":".."}""", "the session_id is not a session ID")]
    [InlineData("""{"hook_event_name":"PostToolUse","session_id":"a/b"}""", "the session_id is not a session ID")]
    [InlineData("""{"hook_event_name":"PostToolUse","session_id":"a\\b"}""", "the session_id is not a session ID")]
    [InlineData("""{"hook_event_name":"PostToolUse","session_id":""}""", "the session_id is not a session ID")]
    [InlineData("""{"hook_event_name":"PostToolUse","session_id":42}""", "the hook input has no session_id")]
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
                new AexpConsole(output, error, empty, name => name == "CLAUDE_PROJECT_DIR" ? empty : Environment.GetEnvironmentVariable(name), TimeProvider.System, stdin));
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
    public async Task A_call_keeps_its_allowed_arguments_and_its_result_only_under_a_recipe_consent()
    {
        using var project = new TempProject(config =>
        {
            config["sanitization"] = new JsonObject { ["allowArguments"] = new JsonArray("file_path") };
            config["recipes"] = new JsonArray(new JsonObject { ["tool"] = "Bash", ["arguments"] = new JsonArray(), ["resultExcerpt"] = true });
        });
        const string session = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
        Assert.Null(await HookAsync(project, "UserPromptSubmit", Session(session, Task)));
        Assert.Null(await HookAsync(project, "PostToolUse.write", Session(session)));
        Assert.Null(await HookAsync(project, "PostToolUse.bash", Session(session)));

        var journal = File.ReadAllText(Assert.Single(Directory.EnumerateFiles(project.RunsDirectory, "*.json")));
        Assert.Contains("/home/user/my-project/src/ledger.js", journal); // file_path: allowed
        Assert.DoesNotContain("module.exports", journal);              // Write's content argument: not allowed
        Assert.DoesNotContain("filePath", journal);                      // Write's response: no consent
        Assert.Contains("42 passing", journal);                          // Bash's output: consented
        Assert.DoesNotContain("Run test suite", journal);                // Bash description argument: not allowed
        Assert.DoesNotContain("\"command\"", journal);                     // Bash's command argument: not allowed
    }

    [Fact]
    public async Task Without_claude_project_dir_a_hook_refuses_rather_than_guess_from_its_working_directory()
    {
        using var project = new TempProject();
        project.Overrides["CLAUDE_PROJECT_DIR"] = null;
        project.Overrides["AEXP_CONFIG"] = null;
        var result = await project.HookAsync("UserPromptSubmit", Session("d0d0d0d0-d0d0-4d0d-8d0d-d0d0d0d0d0d0", Task));
        Assert.Equal(0, result.Exit);
        Assert.Equal(string.Empty, result.Output);
        Assert.Contains("CLAUDE_PROJECT_DIR is not set", result.Error);
        Assert.False(Directory.Exists(project.RunsDirectory) && Directory.EnumerateFiles(project.RunsDirectory, "*.json").Any());
    }

    [Theory]
    [InlineData("hook")]
    [InlineData("hook", "Stop", "--timeout")]
    [InlineData("hook", "Stop", "--timeout", "5", "stray")]
    [InlineData("hook", "Stop", "--timeout", "5", "--timeout", "6")]
    [InlineData("hook", "Stop", "--timeout", "never")]
    [InlineData("hook", "Stop", "--tenant", "evil")]
    [InlineData("hook", "Stop", "Extra")]
    public async Task A_bad_hook_command_line_still_exits_0(params string[] args)
    {
        using var project = new TempProject();
        var output = new StringWriter();
        var error = new StringWriter();
        using var stdin = new MemoryStream("{}"u8.ToArray());
        var exit = await AexpCli.RunAsync(args, new AexpConsole(output, error, project.Root, project.Environment, TimeProvider.System, stdin));
        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task A_stop_while_a_stop_hook_is_already_continuing_does_nothing()
    {
        using var project = new TempProject();
        const string session = "e1e1e1e1-e1e1-4e1e-8e1e-e1e1e1e1e1e1";
        Assert.Null(await HookAsync(project, "UserPromptSubmit", Session(session, Task)));
        Assert.Null(await HookAsync(project, "PostToolUse.write", Session(session)));
        Assert.Null(await HookAsync(project, "Stop.active", Session(session)));
        Assert.Single(Directory.EnumerateFiles(project.RunsDirectory, "*.json"));
        Assert.Contains("already continuing because of a stop hook", project.HookLog);
    }

    [Fact]
    public async Task A_resumed_session_gets_its_runs_block_again_and_an_interrupt_is_never_hinted()
    {
        using var project = new TempProject();
        await LearnAsync(project, "f1f1f1f1-f1f1-4f1f-8f1f-f1f1f1f1f1f1");

        const string session = "f2f2f2f2-f2f2-4f2f-8f2f-f2f2f2f2f2f2";
        var block = AdditionalContext(await HookAsync(project, "UserPromptSubmit", Session(session, Task)), "UserPromptSubmit");
        Assert.StartsWith(HistoricalReferenceWriter.BlockBegin, block);
        Assert.Equal(block, AdditionalContext(await HookAsync(project, "SessionStart.resume", Session(session)), "SessionStart"));

        // Another session, other task: a failure that would be hinted is not, when it was an interrupt.
        const string other = "f3f3f3f3-f3f3-4f3f-8f3f-f3f3f3f3f3f3";
        Assert.Null(await HookAsync(project, "UserPromptSubmit", Session(other, "Rotate the staging certificates")));
        Assert.Null(await HookAsync(project, "PostToolUseFailure.interrupt", Session(other, error: MissingModule)));
        Assert.Contains("recorded (interrupted)", project.HookLog);
        Assert.NotNull(AdditionalContext(await HookAsync(project, "PostToolUseFailure.bash", Session(other, error: MissingModule)), "PostToolUseFailure"));
    }

    [Fact]
    public async Task Parallel_tool_calls_of_one_session_are_all_recorded()
    {
        using var project = new TempProject();
        const string session = "a1a1a1a1-a1a1-4a1a-8a1a-a1a1a1a1a1a1";
        Assert.Null(await HookAsync(project, "UserPromptSubmit", Session(session, Task)));
        var fixtures = Enumerable.Range(0, 12).Select(i => (i % 3) switch { 0 => "PostToolUse.write", 1 => "PostToolUse.bash", _ => "PostToolUse.read" });
        var results = await System.Threading.Tasks.Task.WhenAll(fixtures.Select(fixture => project.HookAsync(fixture, Session(session))));
        Assert.All(results, result => Assert.Equal(0, result.Exit));
        var journal = JsonNode.Parse(File.ReadAllText(Assert.Single(Directory.EnumerateFiles(project.RunsDirectory, "*.json"))))!;
        Assert.Equal(12, journal["calls"]!.AsArray().Count);
    }

    [Fact]
    public async Task Read_only_bash_prefixes_from_the_config_keep_a_turn_unverified()
    {
        using var project = new TempProject(config => config["claudeCode"] = new JsonObject { ["readOnlyBashPrefixes"] = new JsonArray("git status", "ls") });
        const string session = "b1b1b1b1-b1b1-4b1b-8b1b-b1b1b1b1b1b1";
        Assert.Null(await HookAsync(project, "UserPromptSubmit", Session(session, Task)));
        foreach (var command in new[] { "git status", "git status --short", "ls -la" })
        {
            Assert.Null(await HookAsync(project, "PostToolUse.bash-git-status", new(Session(session)) { ["command"] = command }));
        }

        Assert.Null(await HookAsync(project, "Stop", Session(session)));
        Assert.Contains("changed nothing yet", project.HookLog);

        // Not read-only: a chained command, a prefix that is only a prefix of a word, a redirect.
        foreach (var command in new[] { "git status; rm -rf build", "lsof -i", "ls > out.txt" })
        {
            Assert.False(ClaudeCodeHooks.OnlyReads(
                Configuration.AexpConfigLoader.Load(null, project.Root, project.Environment),
                "Bash",
                new JsonObject { ["command"] = command }));
        }

        Assert.Null(await HookAsync(project, "PostToolUse.bash-git-status", new(Session(session)) { ["command"] = "git status && git push" }));
        Assert.NotNull((await HookAsync(project, "Stop", Session(session)))?["systemMessage"]);
    }

    [Fact]
    public async Task All_runs_of_a_session_share_its_holdout_arm_and_two_sessions_can_differ()
    {
        using var project = new TempProject(config => config["injection"] = new JsonObject { ["holdoutRate"] = 0.5 });
        await LearnAsync(project, "c0c0c0c0-c0c0-4c0c-8c0c-c0c0c0c0c0c0");

        // One session the library puts in each arm.
        var salt = MicrosoftAgentFramework.Injection.ExperienceHoldoutOptions.DefaultSalt;
        string Find(Abstractions.HoldoutArm arm) => Enumerable.Range(0, 1000)
            .Select(i => $"c1c1c1c1-c1c1-4c1c-8c1c-{i:D12}")
            .First(id => SessionHoldout.ArmOf(id, salt, 0.5) == arm);
        foreach (var (session, arm) in new[] { (Find(Abstractions.HoldoutArm.Treatment), Abstractions.HoldoutArm.Treatment), (Find(Abstractions.HoldoutArm.Holdout), Abstractions.HoldoutArm.Holdout) })
        {
            for (var turn = 0; turn < 4; turn++)
            {
                var block = AdditionalContext(await HookAsync(project, "UserPromptSubmit", Session(session, Task + " turn " + turn)), "UserPromptSubmit");
                Assert.True((block is not null) == (arm == Abstractions.HoldoutArm.Treatment), $"turn {turn} of a {arm} session: block {(block is null ? "withheld" : "given")}");
                Assert.Null(await HookAsync(project, "PostToolUse.write", Session(session)));
                var stop = (string?)(await HookAsync(project, "Stop", Session(session)))?["systemMessage"];
                Assert.Equal(arm, SessionHoldout.ArmOfRun(Guid.Parse(RunIdIn(stop!)), salt, 0.5));
            }
        }

        var benefit = await project.OkAsync("benefit");
        Assert.Contains("Given lessons:    4/4 verified", benefit);
        Assert.Contains("Lessons withheld: 4/4 verified", benefit);
    }

    [Fact]
    public void Run_ids_are_pinned_to_the_session_arm_deterministically()
    {
        var salt = MicrosoftAgentFramework.Injection.ExperienceHoldoutOptions.DefaultSalt;
        foreach (var rate in new[] { 0.05, 0.2, 0.5, 0.95 })
        {
            for (var i = 0; i < 20; i++)
            {
                var session = $"session-{i}";
                var arm = SessionHoldout.ArmOf(session, salt, rate);
                var first = SessionHoldout.RunIdFor(session, 1, arm, salt, rate);
                Assert.NotNull(first);
                Assert.Equal(first, SessionHoldout.RunIdFor(session, 1, arm, salt, rate));
                Assert.NotEqual(first, SessionHoldout.RunIdFor(session, 2, arm, salt, rate));
                Assert.Equal(arm, SessionHoldout.ArmOfRun(first!.Value, salt, rate));
                Assert.Equal(8, first.Value.Version);
            }
        }

        // An arm the rate makes all but unreachable: the bound is hit and the caller fails closed.
        Assert.Null(SessionHoldout.RunIdFor("s", 1, Abstractions.HoldoutArm.Holdout, salt, 1e-9));
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
        start.Environment["CLAUDE_PROJECT_DIR"] = project.Root;
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

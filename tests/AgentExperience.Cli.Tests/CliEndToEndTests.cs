using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentExperience.MicrosoftAgentFramework.Injection;

// Every test owns its project directory and SQLite file, but SqliteConnection.ClearAllPools in cleanup reaches every
// file's pool, so tests here run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AgentExperience.Cli.Tests;

/// <summary>
/// The CLI end to end, in process: every command builds the library afresh from the config and the files on disk, as a
/// separate process would, so a run started by one command is ended, finalized and recalled by others.
/// </summary>
public sealed class CliEndToEndTests
{
    private const string Task = "Fix the failing build in the payments service";

    private static readonly (string, string?, string?, string?)[] FailThenFix =
    [
        ("run_tests", """{"filter":"payments"}""", null, "Process exited with code 2: error CS0246: The type 'Ledger' could not be found"),
        ("add_reference", """{"package":"Ledger"}""", "added Ledger", null),
        ("run_tests", """{"filter":"payments"}""", "all 42 tests passed", null),
    ];

    [Fact]
    public async Task A_verified_run_becomes_the_lesson_the_next_run_is_given()
    {
        using var project = new TempProject();

        // A run the verifier fails: no lesson to give yet.
        var failed = await project.RunTaskAsync(Task, ("run_tests", null, null, "exit 2: build failed"));
        var first = await project.OkAsync("finalize", "--run", failed);
        Assert.Contains("verifier exit 1 (fail)", first);
        Assert.Equal(string.Empty, (await project.OkAsync("recall", "--task", Task)).Trim());

        // The fixed run: the verifier passes, and the run is stored as a validated lesson.
        var fixedRun = await project.RunTaskAsync(Task, FailThenFix);
        project.Fix();
        var finalized = await project.OkAsync("finalize", "--run", fixedRun);
        Assert.Contains("verifier exit 0 (pass); finalization Validated", finalized);

        // A later run is handed it, through the library's own writer.
        var next = (await project.OkAsync("run", "start", "--task", Task + " again")).Trim();
        var block = await project.OkAsync("recall", "--run", next);
        Assert.StartsWith(HistoricalReferenceWriter.BlockBegin, block);
        Assert.Contains("add_reference [returned]", block);
        Assert.Contains("run_tests [failed: exit 2]", block);
        Assert.Contains(HistoricalReferenceWriter.BlockEnd, block);

        // Asked again, the run gets the same block, and the lessons view counts what was delivered.
        Assert.Equal(block, await project.OkAsync("recall", "--run", next));
        var lessons = await project.OkAsync("lessons");
        Assert.Contains("Validated", lessons);
    }

    [Fact]
    public async Task A_failure_after_a_resolved_one_gets_the_hint_once()
    {
        using var project = new TempProject();
        var learned = await project.RunTaskAsync(Task, FailThenFix);
        project.Fix();
        await project.OkAsync("finalize", "--run", learned);

        // A run whose words match nothing stored fails the same way: recall names what worked after it.
        var run = (await project.OkAsync("run", "start", "--task", "Rotate the staging certificates")).Trim();
        var hint = await project.OkAsync("recall", "--run", run, "--failure", "--tool", "run_tests", "--error", "exit 2: another file this time");
        Assert.StartsWith(HistoricalReferenceWriter.RecallBegin, hint);
        Assert.Contains("run_tests [failed: exit 2]", hint);
        Assert.Contains(HistoricalReferenceWriter.RecallWorkedPrefix, hint);
        Assert.Contains("add_reference [returned]", hint);

        // One answer per signature per run.
        Assert.Equal(string.Empty, await project.OkAsync("recall", "--run", run, "--failure", "--tool", "run_tests", "--error", "exit 2: again"));

        // A different failure has no fix on record.
        Assert.Equal(string.Empty, await project.OkAsync("recall", "--run", run, "--failure", "--tool", "deploy", "--error", "HTTP 503"));
    }

    [Fact]
    public async Task A_run_given_its_lesson_in_the_block_is_not_given_it_again_as_a_hint()
    {
        using var project = new TempProject();
        var learned = await project.RunTaskAsync(Task, FailThenFix);
        project.Fix();
        await project.OkAsync("finalize", "--run", learned);

        var run = (await project.OkAsync("run", "start", "--task", Task)).Trim();
        Assert.Contains(HistoricalReferenceWriter.BlockBegin, await project.OkAsync("recall", "--run", run));
        Assert.Equal(string.Empty, await project.OkAsync("recall", "--run", run, "--failure", "--tool", "run_tests", "--error", "exit 2"));
    }

    [Fact]
    public async Task A_run_is_finalized_by_a_separate_process_of_the_built_tool()
    {
        using var project = new TempProject();
        var run = await project.RunTaskAsync(Task, FailThenFix);
        project.Fix();

        var tool = Path.Combine(AppContext.BaseDirectory, "AgentExperience.Cli.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = project.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { tool, "finalize", "--run", run })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        // A hang guard only: the assertion is on what the process said, not on how long it took.
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(5)), "the tool did not exit");
        Assert.True(process.ExitCode == 0, await error);
        Assert.Contains("finalization Validated", await output);

        // The record the other process wrote is in the file this one reads.
        var next = (await project.OkAsync("run", "start", "--task", Task)).Trim();
        Assert.Contains("add_reference", await project.OkAsync("recall", "--run", next));
    }

    [Fact]
    public async Task What_a_call_carried_is_sanitized_before_it_is_written_down()
    {
        using var project = new TempProject(config => config["sanitization"] = new JsonObject { ["allowArguments"] = new JsonArray("filter") });
        var run = (await project.OkAsync("run", "start", "--task", Task)).Trim();
        await project.OkAsync(
            "run", "call", "--run", run, "--tool", "deploy",
            "--args", """{"filter":"payments","password":"hunter2-secret","token":"tok-abcdef"}""",
            "--error", "exit 3: login failed for user alice with password hunter2-secret");

        var journal = await File.ReadAllTextAsync(Path.Combine(project.RunsDirectory, run + ".json"));
        Assert.Contains("payments", journal);
        Assert.DoesNotContain("hunter2-secret", journal);
        Assert.DoesNotContain("tok-abcdef", journal);
        // The error is kept as its class, as the MAF adapter's classifier would record it, never its words.
        Assert.Contains("\"error\": \"exit 3\"", journal);
        Assert.DoesNotContain("alice", journal);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(project.RunsDirectory, run + ".json")));
        }
    }

    [Fact]
    public async Task Ending_a_run_hands_it_to_the_run_store_and_removes_its_open_file()
    {
        using var project = new TempProject();
        var run = await project.RunTaskAsync(Task, ("run_tests", null, "ok", null));
        Assert.False(File.Exists(Path.Combine(project.RunsDirectory, run + ".json")));

        var again = await project.RunAsync("run", "call", "--run", run, "--tool", "x", "--result", "y");
        Assert.Equal(AexpCli.Usage, again.Exit);
        Assert.Contains("No open run", again.Error);
    }

    [Fact]
    public async Task Finalize_refuses_an_open_run_and_a_reported_exit_code_unless_the_config_allows_it()
    {
        using var project = new TempProject();
        var open = (await project.OkAsync("run", "start", "--task", Task)).Trim();
        var refused = await project.RunAsync("finalize", "--run", open);
        Assert.Equal(AexpCli.Usage, refused.Exit);
        Assert.Contains("still open", refused.Error);

        await project.OkAsync("run", "end", "--run", open);
        var reported = await project.RunAsync("finalize", "--run", open, "--exit-code", "0");
        Assert.Equal(AexpCli.Usage, reported.Exit);
        Assert.Contains("acceptExitCode", reported.Error);
    }

    [Fact]
    public async Task A_host_that_ran_the_check_itself_reports_its_exit_code_when_the_config_allows_it()
    {
        using var project = new TempProject(config => config["verify"]!["acceptExitCode"] = true);
        var run = await project.RunTaskAsync(Task, FailThenFix);

        // The project is not fixed, so the configured verifier would fail; the host's own verdict is what counts here.
        var finalized = await project.OkAsync("finalize", "--run", run, "--exit-code", "0");
        Assert.Contains("finalization Validated", finalized);
    }

    [Fact]
    public async Task Lessons_export_and_benefit_read_the_project_scope()
    {
        using var project = new TempProject(config => config["injection"] = new JsonObject { ["holdoutRate"] = 0.5 });
        var run = await project.RunTaskAsync(Task, FailThenFix);
        project.Fix();
        await project.OkAsync("finalize", "--run", run);

        var html = Path.Combine(project.Root, "lessons.html");
        Assert.Contains("Wrote 1 lesson(s)", await project.OkAsync("lessons", "--html", html));
        Assert.Contains("<html", await File.ReadAllTextAsync(html), StringComparison.OrdinalIgnoreCase);

        var markdown = Path.Combine(project.Root, "LESSONS.md");
        // Nothing is measured as helping yet, so nothing is exported.
        Assert.Contains("Exported 0 lesson(s)", await project.OkAsync("export", "--markdown", markdown));

        var benefit = await project.OkAsync("benefit");
        Assert.Contains("Verdict: NoEvidence", benefit);
    }

    [Fact]
    public async Task A_run_that_asked_for_its_block_is_counted_in_its_holdout_arm()
    {
        using var project = new TempProject(config => config["injection"] = new JsonObject { ["holdoutRate"] = 0.5 });
        var learned = await project.RunTaskAsync(Task, FailThenFix);
        project.Fix();
        await project.OkAsync("finalize", "--run", learned);

        // Shown or withheld by the run's arm; either way the assignment is recorded against the run before the process
        // would exit, so the verified run is counted in exactly one arm.
        var run = (await project.OkAsync("run", "start", "--task", Task)).Trim();
        await project.RunAsync("recall", "--run", run);
        await project.OkAsync("run", "call", "--run", run, "--tool", "run_tests", "--result", "all passed");
        await project.OkAsync("run", "end", "--run", run);
        await project.OkAsync("finalize", "--run", run);

        var benefit = await project.OkAsync("benefit");
        var given = benefit.Contains("Given lessons:    1/1 verified", StringComparison.Ordinal);
        var withheld = benefit.Contains("Lessons withheld: 1/1 verified", StringComparison.Ordinal);
        Assert.True(given ^ withheld, benefit);
    }

    [Fact]
    public async Task Identity_and_scope_are_never_taken_from_arguments()
    {
        using var project = new TempProject();
        foreach (var option in new[] { "--tenant", "--scope", "--project", "--user", "--store", "--verify" })
        {
            var result = await project.RunAsync("run", "start", "--task", Task, option, "other");
            Assert.Equal(AexpCli.Usage, result.Exit);
        }
    }

    [Theory]
    [InlineData("""{"identity":{"tenant":"t","application":"a","project":"p"}}""", "verify.command is required")]
    [InlineData("""{"identity":{"tenant":"t","application":"a","project":"p"},"verify":{"command":[]}}""", "verify.command is required")]
    [InlineData("""{"verify":{"command":["true"]}}""", "identity")]
    [InlineData("""{"identity":{"tenant":"t","application":"a","project":"p"},"verify":{"command":["true"]},"store":"postgres:Host=db;Password=x"}""", "environment variable")]
    [InlineData("""{"identity":{"tenant":"t","application":"a","project":"p"},"verify":{"command":["true"]},"store":"mysql:x"}""", "store")]
    [InlineData("""{"identity":{"tenant":"t","application":"a","project":"p"},"verify":{"command":["true"],"comand":["x"]}}""", "not valid")]
    public async Task A_config_the_tool_cannot_trust_stops_it_before_anything_runs(string config, string reason)
    {
        using var project = new TempProject();
        await File.WriteAllTextAsync(project.ConfigPath, config);
        var result = await project.RunAsync("run", "start", "--task", Task);
        Assert.Equal(AexpCli.ConfigError, result.Exit);
        Assert.Contains(reason, result.Error);
        Assert.False(Directory.Exists(project.RunsDirectory));
    }

    [Fact]
    public async Task Without_a_config_the_tool_refuses_to_start()
    {
        var empty = Path.Combine(Path.GetTempPath(), $"aexp-none-{Guid.NewGuid():N}");
        Directory.CreateDirectory(empty);
        try
        {
            var error = new StringWriter();
            var exit = await AexpCli.RunAsync(["lessons"], new AexpConsole(new StringWriter(), error, empty, static _ => null, TimeProvider.System));
            Assert.Equal(AexpCli.ConfigError, exit);
            Assert.Contains(".aexp/config.json", error.ToString());
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public async Task The_config_can_be_named_by_option_or_environment_variable()
    {
        using var project = new TempProject();
        var elsewhere = Path.GetTempPath();
        var byOption = new StringWriter();
        Assert.Equal(0, await AexpCli.RunAsync(["lessons", "--config", project.ConfigPath], new AexpConsole(byOption, new StringWriter(), elsewhere, static _ => null, TimeProvider.System)));
        var byVariable = new StringWriter();
        Assert.Equal(0, await AexpCli.RunAsync(["lessons"], new AexpConsole(byVariable, new StringWriter(), elsewhere, name => name == "AEXP_CONFIG" ? project.ConfigPath : null, TimeProvider.System)));
        Assert.Contains("No lessons yet.", byOption.ToString());
        Assert.Contains("No lessons yet.", byVariable.ToString());
    }

    [Fact]
    public async Task Version_and_help_need_no_config()
    {
        var output = new StringWriter();
        Assert.Equal(AexpCli.Ok, await AexpCli.RunAsync(["--version"], new AexpConsole(output, new StringWriter(), Path.GetTempPath(), static _ => null, TimeProvider.System)));
        Assert.StartsWith("0.1.0-preview.", output.ToString());

        var help = new StringWriter();
        Assert.Equal(AexpCli.Ok, await AexpCli.RunAsync(["--help"], new AexpConsole(help, new StringWriter(), Path.GetTempPath(), static _ => null, TimeProvider.System)));
        Assert.Contains("aexp mcp", help.ToString());
    }

    [Theory]
    [InlineData("run")]
    [InlineData("run", "start")]
    [InlineData("run", "start", "--task")]
    [InlineData("run", "call", "--run", "not-a-guid", "--tool", "x")]
    [InlineData("frobnicate")]
    [InlineData("run", "start", "--task", "a", "--task", "b")]
    public async Task Malformed_commands_are_usage_errors(params string[] args)
    {
        using var project = new TempProject();
        var result = await project.RunAsync(args);
        Assert.Equal(AexpCli.Usage, result.Exit);
    }
}

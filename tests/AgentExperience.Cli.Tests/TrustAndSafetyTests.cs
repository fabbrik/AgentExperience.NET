using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Cli.Configuration;
using AgentExperience.Cli.Engine;
using AgentExperience.MicrosoftAgentFramework.Injection;

namespace AgentExperience.Cli.Tests;

/// <summary>
/// What keeps the one command aexp runs in a person's hands: config trust, how the verifier's program is found, what is
/// checked before it is spawned, its timeout, and the files aexp writes. The verifier scripts here are Unix shell, so
/// the tests that need one return early on Windows.
/// </summary>
public sealed class TrustAndSafetyTests
{
    private const string TaskText = "Fix the failing build in the payments service";

    private static readonly (string, string?, string?, string?)[] FailThenFix =
    [
        ("run_tests", null, null, "exit 2: build failed"),
        ("add_reference", null, "added Ledger", null),
        ("run_tests", null, "all passed", null),
    ];

    private static void Verifier(JsonObject config, params string[] command) =>
        config["verify"]!["command"] = new JsonArray([.. command.Select(part => (JsonNode?)JsonValue.Create(part))]);

    [Fact]
    public async Task A_config_nobody_approved_runs_no_verifier_until_a_person_trusts_it()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var project = new TempProject(config => Verifier(config, "sh", "-c", "touch spawned; test -f fixed"), trusted: false);
        var run = await project.RunTaskAsync(TaskText, FailThenFix);
        project.Fix();

        // Reading works, with a warning; running the verifier is refused, and nothing is spawned.
        var lessons = await project.RunAsync("lessons");
        Assert.Equal(AexpCli.Ok, lessons.Exit);
        Assert.Contains("is not trusted", lessons.Error);

        var refused = await project.RunAsync("finalize", "--run", run);
        Assert.Equal(AexpCli.ConfigError, refused.Exit);
        Assert.Contains("aexp trust", refused.Error);
        Assert.False(File.Exists(Path.Combine(project.Root, "spawned")));

        // The same config named by --config from elsewhere is just as untrusted.
        var named = new StringWriter();
        Assert.Equal(AexpCli.ConfigError, await AexpCli.RunAsync(
            ["finalize", "--run", run, "--config", project.ConfigPath],
            new AexpConsole(new StringWriter(), named, Path.GetTempPath(), project.Environment, TimeProvider.System)));

        // A person reviews what it runs, and approves it.
        var trust = await project.OkAsync("trust");
        Assert.Contains("""["sh","-c","touch spawned; test -f fixed"]""", trust);
        Assert.Contains("acceptExitCode: false", trust);
        Assert.Contains("finalization Validated", await project.OkAsync("finalize", "--run", run));
        Assert.True(File.Exists(Path.Combine(project.Root, "spawned")));

        var trustFile = Path.Combine(project.UserConfig, "aexp", "trusted.json");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(trustFile));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(trustFile)!));
    }

    [Fact]
    public async Task Editing_a_trusted_config_revokes_its_trust()
    {
        using var project = new TempProject(config => config["verify"]!["acceptExitCode"] = true);
        var run = await project.RunTaskAsync(TaskText, FailThenFix);

        // An edit, by anyone (an agent, say), changes the bytes: neither the verifier nor a reported exit code counts now.
        await File.AppendAllTextAsync(project.ConfigPath, "\n");
        var refused = await project.RunAsync("finalize", "--run", run, "--exit-code", "0");
        Assert.Equal(AexpCli.ConfigError, refused.Exit);
        Assert.Contains("changed since it was trusted", refused.Error);

        project.Trust();
        Assert.Contains("finalization Validated", await project.OkAsync("finalize", "--run", run, "--exit-code", "0"));
    }

    [Fact]
    public async Task A_bare_verifier_name_is_found_on_PATH_never_in_the_project_directory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var project = new TempProject(config => Verifier(config, "aexp-test-verifier"));

        // A program of the same name in the working directory, which a shell's or the OS's search might pick up.
        Script(Path.Combine(project.Root, "aexp-test-verifier"), "touch shadow-ran; exit 0");
        var bin = Path.Combine(project.Root, "trusted-bin");
        Directory.CreateDirectory(bin);
        Script(Path.Combine(bin, "aexp-test-verifier"), "exit 1");

        var run = await project.RunTaskAsync(TaskText, FailThenFix);

        // Not on PATH (and an empty PATH entry, the current directory, is skipped): refused, the shadow never runs.
        project.Overrides["PATH"] = ":" + "/usr/bin:/bin";
        var missing = await project.RunAsync("finalize", "--run", run);
        Assert.Equal(AexpCli.Usage, missing.Exit);
        Assert.Contains("not found on PATH", missing.Error);
        Assert.False(File.Exists(Path.Combine(project.Root, "shadow-ran")));

        // On PATH: that one runs.
        project.Overrides["PATH"] = bin + ":/usr/bin:/bin";
        Assert.Contains("verifier exit 1 (fail)", await project.OkAsync("finalize", "--run", run));
        Assert.False(File.Exists(Path.Combine(project.Root, "shadow-ran")));
    }

    [Fact]
    public async Task A_relative_verifier_is_resolved_against_its_working_directory_and_must_stay_in_the_project()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var project = new TempProject(config =>
        {
            Verifier(config, "./verify.sh");
            config["verify"]!["workingDirectory"] = "scripts";
        });
        Directory.CreateDirectory(Path.Combine(project.Root, "scripts"));
        Script(Path.Combine(project.Root, "scripts", "verify.sh"), "test -f ../fixed");
        var run = await project.RunTaskAsync(TaskText, FailThenFix);
        project.Fix();
        Assert.Contains("finalization Validated", await project.OkAsync("finalize", "--run", run));

        using var outside = new TempProject(config => Verifier(config, "../outside.sh"));
        var other = await outside.RunTaskAsync(TaskText, FailThenFix);
        var refused = await outside.RunAsync("finalize", "--run", other);
        Assert.Equal(AexpCli.Usage, refused.Exit);
        Assert.Contains("outside the project root", refused.Error);
    }

    [Fact]
    public async Task Finalize_checks_the_run_before_it_spawns_anything()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var project = new TempProject(config => Verifier(config, "sh", "-c", "touch spawned"));
        var unknown = await project.RunAsync("finalize", "--run", Guid.NewGuid().ToString());
        Assert.Equal(AexpCli.Usage, unknown.Exit);
        Assert.Contains("No ended run", unknown.Error);
        Assert.False(File.Exists(Path.Combine(project.Root, "spawned")));

        // An already finalized run spawns nothing either.
        var run = await project.RunTaskAsync(TaskText, FailThenFix);
        await project.OkAsync("finalize", "--run", run);
        File.Delete(Path.Combine(project.Root, "spawned"));
        var again = await project.RunAsync("finalize", "--run", run);
        Assert.Contains("already finalized", again.Error);
        Assert.False(File.Exists(Path.Combine(project.Root, "spawned")));
    }

    [Fact]
    public async Task A_verifier_that_times_out_finalizes_nothing_and_a_retry_finalizes()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var project = new TempProject(config =>
        {
            Verifier(config, "sh", "-c", "if [ -f slow ]; then sleep 600; fi; test -f fixed");
            config["verify"]!["timeoutSeconds"] = 1;
        });
        var run = await project.RunTaskAsync(TaskText, FailThenFix);
        await File.WriteAllTextAsync(Path.Combine(project.Root, "slow"), "slow");

        var timedOut = await project.RunAsync("finalize", "--run", run);
        Assert.Equal(AexpCli.Usage, timedOut.Exit);
        Assert.Contains("did not finish", timedOut.Error);
        Assert.Contains($"aexp finalize --run {run}", timedOut.Error);

        File.Delete(Path.Combine(project.Root, "slow"));
        project.Fix();
        Assert.Contains("finalization Validated", await project.OkAsync("finalize", "--run", run));
    }

    [Fact]
    public async Task A_verifier_that_times_out_is_killed_with_everything_it_started()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var project = new TempProject(config =>
        {
            Verifier(config, "sh", "-c", "sleep 600 & echo $! > child.pid; wait");
            config["verify"]!["timeoutSeconds"] = 1;
        });
        var run = await project.RunTaskAsync(TaskText, FailThenFix);
        Assert.Contains("did not finish", (await project.RunAsync("finalize", "--run", run)).Error);

        var pid = int.Parse((await File.ReadAllTextAsync(Path.Combine(project.Root, "child.pid"))).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(Gone(pid), $"the verifier's child {pid} is still running");
    }

    [Fact]
    public async Task An_ended_run_whose_finalize_failed_is_finalized_by_a_later_experience_finish()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var project = new TempProject(config =>
        {
            Verifier(config, "sh", "-c", "if [ -f slow ]; then sleep 600; fi; test -f fixed");
            config["verify"]!["timeoutSeconds"] = 1;
        });
        await using var session = await McpTestSession.StartAsync(project);
        await session.CallAsync("experience_recall", new() { ["task"] = TaskText });
        await session.CallAsync("experience_record_call", new() { ["tool"] = "run_tests", ["result"] = "all passed" });
        await File.WriteAllTextAsync(Path.Combine(project.Root, "slow"), "slow");

        var failed = await session.Client.CallToolAsync("experience_finish", new Dictionary<string, object?>());
        Assert.True(failed.IsError == true);
        var message = McpTestSession.Text(failed);
        Assert.Contains("ended but is not finalized", message);
        Assert.Matches("aexp finalize --run [0-9a-f-]{36}", message);

        // The run stays ended: a new call is told to finish it, and finishing again only finalizes.
        var blocked = await session.Client.CallToolAsync("experience_record_call", new Dictionary<string, object?> { ["tool"] = "x", ["result"] = "y" });
        Assert.Contains("waiting to be finalized", McpTestSession.Text(blocked));
        File.Delete(Path.Combine(project.Root, "slow"));
        project.Fix();
        Assert.Contains("finalization Validated", await session.CallAsync("experience_finish", new()));
    }

    [Fact]
    public async Task A_hint_only_run_is_counted_in_its_holdout_arm()
    {
        using var project = new TempProject(config => config["injection"] = new JsonObject { ["holdoutRate"] = 0.5 });
        var learned = await project.RunTaskAsync(TaskText, FailThenFix);
        project.Fix();
        await project.OkAsync("finalize", "--run", learned);

        // No block asked for: the run is assigned when its failure matches, shown the hint or not by its arm.
        var run = (await project.OkAsync("run", "start", "--task", "Rotate the staging certificates")).Trim();
        var hint = await project.OkAsync("recall", "--run", run, "--failure", "--tool", "run_tests", "--error", "exit 2");
        Assert.True(hint.Length == 0 || hint.StartsWith(HistoricalReferenceWriter.RecallBegin, StringComparison.Ordinal), hint);
        await project.OkAsync("run", "call", "--run", run, "--tool", "run_tests", "--result", "all passed");
        await project.OkAsync("run", "end", "--run", run);
        await project.OkAsync("finalize", "--run", run);

        var benefit = await project.OkAsync("benefit");
        var given = benefit.Contains("Given lessons:    1/1 verified", StringComparison.Ordinal);
        var withheld = benefit.Contains("Lessons withheld: 1/1 verified", StringComparison.Ordinal);
        Assert.True(given ^ withheld, benefit);
        Assert.Equal(given, hint.Length > 0);
    }

    [Fact]
    public async Task A_journal_update_is_written_whole_even_when_the_command_is_cancelled()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"aexp-journal-{Guid.NewGuid():N}");
        try
        {
            var store = new RunJournalStore(directory);
            var runId = Guid.NewGuid();
            await store.CreateAsync(NewJournal(runId), CancellationToken.None);

            using var cancellation = new CancellationTokenSource();
            await store.UpdateAsync(runId, journal =>
            {
                journal.AnsweredSignatures.Add("first");
                cancellation.Cancel();
                return Task.FromResult(0);
            }, cancellation.Token);

            Assert.Equal(["first"], store.Peek(runId)!.AnsweredSignatures);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));

            // Many writers at once take turns on the lock; none is lost.
            await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => store.UpdateAsync(runId, journal =>
            {
                journal.AnsweredSignatures.Add($"w{i}");
                return Task.FromResult(0);
            }, CancellationToken.None))));
            Assert.Equal(13, store.Peek(runId)!.AnsweredSignatures.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Stale_open_runs_are_pruned_and_fresh_ones_kept()
    {
        using var project = new TempProject();
        var run = (await project.OkAsync("run", "start", "--task", TaskText)).Trim();
        Assert.Contains("Removed 0", await project.OkAsync("runs", "prune", "--older-than", "1d"));

        project.Clock = new ShiftedClock(TimeSpan.FromDays(2));
        Assert.Contains("Removed 1", await project.OkAsync("runs", "prune", "--older-than", "1d"));
        Assert.False(File.Exists(Path.Combine(project.RunsDirectory, run + ".json")));
        Assert.Equal(AexpCli.Usage, (await project.RunAsync("runs", "prune", "--older-than", "soon")).Exit);
    }

    [Fact]
    public async Task Files_are_written_inside_the_project_only()
    {
        using var project = new TempProject();
        var html = await project.RunAsync("lessons", "--html", Path.Combine(Path.GetTempPath(), $"aexp-{Guid.NewGuid():N}.html"));
        Assert.Equal(AexpCli.Usage, html.Exit);
        var markdown = await project.RunAsync("export", "--markdown", "../LESSONS.md");
        Assert.Equal(AexpCli.Usage, markdown.Exit);
        Assert.Contains("inside the project root", markdown.Error);
        await project.OkAsync("export", "--markdown", "docs-LESSONS.md");
    }

    [Fact]
    public async Task Discovery_stops_at_the_repository_root()
    {
        using var project = new TempProject();
        var repository = Path.Combine(project.Root, "checkout");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        Directory.CreateDirectory(Path.Combine(repository, "src"));

        // The project's config sits above this repository: it is not this repository's, so it is not found.
        var error = new StringWriter();
        var exit = await AexpCli.RunAsync(["lessons"], new AexpConsole(new StringWriter(), error, Path.Combine(repository, "src"), project.Environment, TimeProvider.System));
        Assert.Equal(AexpCli.ConfigError, exit);
        Assert.Contains("repository root", error.ToString());
    }

    [Fact]
    public void Ownership_is_read_for_this_users_own_files()
    {
        var owned = FileOwnership.OwnedByCurrentUser(Path.GetTempPath());
        if (OperatingSystem.IsWindows())
        {
            Assert.Null(owned);
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"aexp-owned-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Assert.True(FileOwnership.OwnedByCurrentUser(directory));
            if (File.Exists("/etc/hosts") && Environment.UserName != "root")
            {
                Assert.False(FileOwnership.OwnedByCurrentUser("/etc/hosts"));
            }
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    private static RunJournal NewJournal(Guid runId) => new()
    {
        RunId = runId,
        TaskId = "default",
        Task = TaskText,
        Scope = new Scope("t", "a", "p"),
        StartedAt = DateTimeOffset.UnixEpoch,
    };

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void Script(string path, string body)
    {
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Whether the process has gone, waiting a bounded while: a hang guard, not a timing assertion.</summary>
    private static bool Gone(int pid)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentExperience.Cli.Configuration;
using AgentExperience.Cli.Engine;

namespace AgentExperience.Cli.Tests;

/// <summary>Story 29.1: <c>aexp init</c>, one command that writes the config, and the one generator it shares with setup.</summary>
public sealed class InitTests
{
    /// <summary>
    /// A project directory (a repository root unless <c>git</c> is false) with its own user config directory. Lines in
    /// <see cref="Answers"/> are what a person at the terminal types; <see langword="null"/> means standard input is a pipe.
    /// </summary>
    private sealed class Scratch : IDisposable
    {
        public Scratch(bool git = true)
        {
            Directory.CreateDirectory(Root);
            if (git)
            {
                Directory.CreateDirectory(Path.Combine(Root, ".git"));
            }

            Directory.CreateDirectory(UserConfig);
        }

        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"aexp-init-{Guid.NewGuid():N}");

        public string UserConfig { get; } = Path.Combine(Path.GetTempPath(), $"aexp-user-{Guid.NewGuid():N}");

        public string Config => Path.Combine(Root, ".aexp", "config.json");

        public string Gitignore => Path.Combine(Root, ".gitignore");

        public Queue<string?>? Answers { get; set; }

        public int Asked { get; private set; }

        public void Touch(string name, string content = "") => File.WriteAllText(Path.Combine(Root, name), content);

        public JsonObject ReadConfig() =>
            JsonNode.Parse(File.ReadAllText(Config), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!.AsObject();

        public string Verifier() => ReadConfig()["verify"]!["command"]!.ToJsonString();

        public Task<CliResult> RunAsync(params string[] args) => RunInAsync(Root, args);

        /// <summary>Runs the tool in <paramref name="workingDirectory"/> (a subdirectory of the root, say).</summary>
        public async Task<CliResult> RunInAsync(string workingDirectory, params string[] args)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await AexpCli.RunAsync(
                args,
                new AexpConsole(
                    output,
                    error,
                    workingDirectory,
                    name => name switch
                    {
                        "XDG_CONFIG_HOME" => UserConfig,
                        "CLAUDE_PROJECT_DIR" => null,
                        _ => Environment.GetEnvironmentVariable(name),
                    },
                    TimeProvider.System,
                    ReadLine: Answers is null ? null : () =>
                    {
                        Asked++;
                        return Answers.Count > 0 ? Answers.Dequeue() : null;
                    }));
            return new CliResult(exit, output.ToString(), error.ToString());
        }

        public async Task<string> OkAsync(params string[] args)
        {
            var result = await RunAsync(args);
            Assert.True(result.Exit == 0, $"aexp {string.Join(' ', args)} exited {result.Exit}: {result.Error}");
            return result.Output;
        }

        public void Dispose()
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Directory.Delete(Root, recursive: true);
                Directory.Delete(UserConfig, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task One_kind_detected_writes_that_verifier_the_gitignore_entry_and_the_next_steps()
    {
        using var project = new Scratch();
        project.Touch("go.mod", "module example.com/x\n");

        var output = await project.OkAsync("init");

        Assert.Equal("""["go","test","./..."]""", project.Verifier());
        Assert.Contains("""["go","test","./..."]""", output);
        Assert.Contains("go.mod", output);
        Assert.Contains("\n.aexp/\n", File.ReadAllText(project.Gitignore));
        Assert.Contains("aexp trust", output);
        Assert.Contains("aexp setup claude-code", output);
        Assert.Contains("aexp mcp", output);
        Assert.Contains("approved nothing", output);
        Assert.Equal(0, project.Asked);
    }

    [Fact]
    public async Task Acceptance_a_csproj_under_git_gives_dotnet_test_which_trust_then_prints()
    {
        using var project = new Scratch();
        project.Touch("Payments.csproj", "<Project />");

        var init = await project.OkAsync("init");
        Assert.Contains("aexp trust", init);
        Assert.Contains("aexp setup claude-code", init);
        Assert.Contains("approved nothing", init);
        Assert.Contains(".aexp/", File.ReadAllText(project.Gitignore));

        // The written config loads, and 'aexp trust' (a person's step, run here as the test's person) shows the chosen verifier.
        var trust = await project.OkAsync("trust");
        Assert.Contains("""Verifier:       ["dotnet","test"]""", trust);
        Assert.Contains("Trusted:", trust);
        Assert.DoesNotContain("agent", project.ReadConfig()["identity"]!.AsObject().Select(p => p.Key)); // init ties the config to no agent

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(project.Config)!));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(project.Config));

            // An .aexp/ made by hand with a looser mode is tightened when the config is written into it.
            using var loose = new Scratch();
            var directory = Path.Combine(loose.Root, ".aexp");
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            await loose.OkAsync("init");
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        }
    }

    [Fact]
    public void A_makefile_with_a_test_target_is_make_test_only_when_nothing_else_fits()
    {
        using var project = new Scratch();
        project.Touch("Makefile", "build:\n\tgo build\n\ntest := nope\ntest ::= nope\n");
        Assert.Empty(ProjectInit.Detect(project.Root));

        project.Touch("Makefile", "build:\n\tgo build\n\ntest: build\n\tgo test ./...\n");
        var only = Assert.Single(ProjectInit.Detect(project.Root));
        Assert.Equal(["make", "test"], only.Command);
        Assert.Contains("Makefile", only.Evidence);

        // A Makefile beside go.mod wraps go: go test is the one candidate, as it was before make was detected.
        project.Touch("go.mod", "module example.com/x\n");
        Assert.Equal(["go", "test", "./..."], Assert.Single(ProjectInit.Detect(project.Root)).Command);
    }

    [Fact]
    public async Task Init_takes_no_config_option()
    {
        using var project = new Scratch();
        var result = await project.RunAsync("init", "--config", "elsewhere.json");
        Assert.Equal(AexpCli.Usage, result.Exit);
        Assert.Contains("--config", result.Error);
        Assert.False(File.Exists(project.Config));
    }

    [Fact]
    public async Task Several_detected_and_no_terminal_writes_the_placeholder_and_lists_the_candidates()
    {
        using var project = new Scratch();
        project.Touch("package.json", """{ "scripts": { "test": "vitest run" } }""");
        project.Touch("pyproject.toml", "[tool.pytest.ini_options]\n");

        var output = await project.OkAsync("init");

        Assert.Equal(JsonSerializer.Serialize(new[] { VerifierRunner.Placeholder }), project.Verifier());
        Assert.Contains("""1. ["npm","test"]""", output);
        Assert.Contains("""2. ["pytest"]""", output);
        Assert.Contains("verify.command", output);
        Assert.Equal(0, project.Asked);
    }

    [Fact]
    public async Task None_detected_and_no_terminal_writes_the_placeholder_and_says_what_it_looked_for()
    {
        using var project = new Scratch();
        var output = await project.OkAsync("init");
        Assert.Equal(JsonSerializer.Serialize(new[] { VerifierRunner.Placeholder }), project.Verifier());
        Assert.Contains("No test command was detected", output);
        Assert.Contains("verify.command", output);
    }

    [Theory]
    [InlineData("2", """["pytest"]""", 1)]
    [InlineData(" 1 ", """["npm","test"]""", 1)]
    [InlineData("""["make", "check"]""", """["make","check"]""", 1)]
    [InlineData("", "placeholder", 1)]
    public async Task At_a_terminal_the_answer_is_a_number_a_json_array_or_empty_for_the_placeholder(string answer, string expected, int asked)
    {
        using var project = new Scratch();
        project.Touch("package.json", """{ "scripts": { "test": "vitest run" } }""");
        project.Touch("pyproject.toml", "[tool.pytest.ini_options]\n");
        project.Answers = new Queue<string?>([answer]);

        var output = await project.OkAsync("init");

        Assert.Equal(expected == "placeholder" ? JsonSerializer.Serialize(new[] { VerifierRunner.Placeholder }) : expected, project.Verifier());
        Assert.Equal(asked, project.Asked);
        Assert.Contains("Verifier:", output);
    }

    [Fact]
    public async Task A_bad_answer_is_asked_again_once_then_the_placeholder()
    {
        using var project = new Scratch();
        project.Touch("package.json", """{ "scripts": { "test": "vitest run" } }""");
        project.Touch("pyproject.toml", "[tool.pytest.ini_options]\n");

        project.Answers = new Queue<string?>(["7", "npm test", "2"]);
        await project.OkAsync("init");
        Assert.Equal(JsonSerializer.Serialize(new[] { VerifierRunner.Placeholder }), project.Verifier());
        Assert.Equal(2, project.Asked); // the third answer is never read

        project.Answers = new Queue<string?>(["""["x", ""]""", "1"]);
        await project.OkAsync("init", "--force");
        Assert.Equal("""["npm","test"]""", project.Verifier());

        // End of input at the prompt is an empty answer.
        project.Answers = new Queue<string?>();
        await project.OkAsync("init", "--force");
        Assert.Equal(JsonSerializer.Serialize(new[] { VerifierRunner.Placeholder }), project.Verifier());
    }

    [Fact]
    public async Task Verify_given_is_used_as_is_with_no_detection_and_no_prompt()
    {
        using var project = new Scratch();
        project.Touch("go.mod", "module example.com/x\n");
        project.Touch("package.json", """{ "scripts": { "test": "vitest run" } }""");
        project.Answers = new Queue<string?>(["1"]);

        var output = await project.OkAsync("init", "--verify", """["npm", "test", "--", "--ci"]""");

        Assert.Equal("""["npm","test","--","--ci"]""", project.Verifier());
        Assert.Equal(0, project.Asked);
        Assert.DoesNotContain("Detected", output);
        Assert.DoesNotContain("go.mod", output);
    }

    [Theory]
    [InlineData("npm test")]
    [InlineData("\"npm test\"")]
    [InlineData("[]")]
    [InlineData("""["npm", ""]""")]
    [InlineData("""["npm", 1]""")]
    [InlineData("""{"command": ["npm"]}""")]
    public async Task Verify_that_is_not_a_json_array_of_non_empty_strings_is_a_usage_error(string verify)
    {
        using var project = new Scratch();
        var result = await project.RunAsync("init", "--verify", verify);
        Assert.Equal(AexpCli.Usage, result.Exit);
        Assert.Contains("JSON array", result.Error);
        Assert.False(File.Exists(project.Config));
    }

    [Fact]
    public async Task An_existing_config_is_kept_and_force_replaces_it_atomically_noting_that_trust_is_revoked()
    {
        using var project = new Scratch();
        project.Touch("go.mod", "module example.com/x\n");
        Directory.CreateDirectory(Path.GetDirectoryName(project.Config)!);
        File.WriteAllText(project.Config, "{ \"mine\": true }");

        var kept = await project.RunAsync("init");
        Assert.Equal(AexpCli.Usage, kept.Exit);
        Assert.Contains("--force", kept.Error);
        Assert.Equal("{ \"mine\": true }", File.ReadAllText(project.Config));

        var replaced = await project.OkAsync("init", "--force");
        Assert.Contains("replaced", replaced);
        Assert.Contains("aexp trust", replaced);
        Assert.Contains("no longer applies", replaced);
        Assert.Equal("""["go","test","./..."]""", project.Verifier());
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(project.Config)!, "*.tmp"));
    }

    [Fact]
    public async Task Print_shows_the_config_and_the_gitignore_line_and_writes_nothing()
    {
        using var project = new Scratch();
        project.Touch("go.mod", "module example.com/x\n");

        var output = await project.OkAsync("init", "--print");

        Assert.Contains("""["go","test","./..."]""", output);
        Assert.Contains("\"holdoutRate\": 0.2", output);
        Assert.Contains("// ", output);
        Assert.Contains(".gitignore would get the line: .aexp/", output);
        Assert.False(Directory.Exists(Path.GetDirectoryName(project.Config)!));
        Assert.False(File.Exists(project.Gitignore));

        // With a config in place, --print still prints and writes nothing.
        await project.OkAsync("init");
        var bytes = File.ReadAllBytes(project.Config);
        var again = await project.OkAsync("init", "--print");
        Assert.Contains("exists", again);
        Assert.Equal(bytes, File.ReadAllBytes(project.Config));
    }

    [Fact]
    public async Task Not_under_git_writes_the_config_and_no_gitignore_and_says_so()
    {
        using var project = new Scratch(git: false);
        project.Touch("go.mod", "module example.com/x\n");

        var output = await project.OkAsync("init");

        Assert.True(File.Exists(project.Config));
        Assert.False(File.Exists(project.Gitignore));
        Assert.Contains("not under git", output);

        // A subdirectory of a repository is under git: the .gitignore is created in the init directory itself.
        using var repo = new Scratch();
        var sub = Path.Combine(repo.Root, "services", "payments");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "go.mod"), "module example.com/x\n");
        var inSub = await repo.RunInAsync(sub, "init");
        Assert.True(inSub.Exit == 0, inSub.Error);
        Assert.Contains(".gitignore: added .aexp/.", inSub.Output);
        Assert.Equal("# aexp: the local lesson store, open runs and the config\n.aexp/\n", File.ReadAllText(Path.Combine(sub, ".gitignore")));
        Assert.True(File.Exists(Path.Combine(sub, ".aexp", "config.json")));
        Assert.False(File.Exists(repo.Gitignore));

        // An existing .gitignore without .git is enough to add the line to, as is a .git file (a worktree).
        using var withIgnore = new Scratch(git: false);
        withIgnore.Touch(".gitignore", "bin/");
        withIgnore.Touch("go.mod", "module example.com/x\n");
        await withIgnore.OkAsync("init");
        Assert.Equal("bin/\n# aexp: the local lesson store, open runs and the config\n.aexp/\n", File.ReadAllText(withIgnore.Gitignore));

        using var worktree = new Scratch(git: false);
        worktree.Touch(".git", "gitdir: /elsewhere\n");
        await worktree.OkAsync("init");
        Assert.Contains(".aexp/", File.ReadAllText(worktree.Gitignore));
        Assert.Contains("already ignores", await worktree.OkAsync("init", "--force"));
    }

    [Fact]
    public async Task Setup_after_init_keeps_the_config_byte_for_byte()
    {
        using var project = new Scratch();
        project.Touch("Payments.csproj", "<Project />");
        await project.OkAsync("init");
        var bytes = File.ReadAllBytes(project.Config);

        var setup = await project.OkAsync("setup", "claude-code");

        Assert.Contains(".aexp/config.json: exists; left as it is.", setup);
        Assert.Equal(bytes, File.ReadAllBytes(project.Config));
        Assert.Contains("already ignores", setup);
        Assert.Equal(1, File.ReadAllText(project.Gitignore).Split('\n').Count(line => line == ".aexp/"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "claude-code")]
    [InlineData("""["dotnet","test"]""", "claude-code")]
    [InlineData("""["npm","test","--","--ci"]""", null)]
    [InlineData("""["<odd>","&","olá"]""", null)]
    public void Every_rendered_config_loads_with_todays_defaults_and_no_unknown_key(string? verifier, string? agent)
    {
        using var project = new Scratch();
        var command = verifier is null ? null : JsonSerializer.Deserialize<string[]>(verifier);
        var json = ProjectInit.Render(project.Root, command, agent);
        ProjectInit.WriteConfig(project.Config, json, overwrite: false);

        var settings = AexpConfigLoader.Load(null, project.Root, Environment.GetEnvironmentVariable);

        Assert.Equal(verifier is null ? [VerifierRunner.Placeholder] : command!, settings.Verifier.Command);
        Assert.Equal(TimeSpan.FromSeconds(540), settings.Verifier.Timeout);
        Assert.Equal("tests", settings.Verifier.CheckId);
        Assert.Equal(0.2, settings.HoldoutRate);
        Assert.Equal(["file_path"], settings.AllowArguments);
        Assert.True(settings.RecallEnabled);
        Assert.False(settings.Verifier.AcceptExitCode);
        Assert.Equal(Path.Combine(Path.GetFullPath(project.Root), ".aexp", "experience.db"), Assert.IsType<StoreSetting.Sqlite>(settings.Store).Path);
        Assert.Equal("local", settings.Scope.TenantId);
        Assert.Equal(Path.GetFileName(project.Root), settings.Scope.ApplicationId);
        Assert.Equal(Path.GetFileName(project.Root), settings.Scope.ProjectId);
        Assert.Equal(agent, settings.Scope.AgentId);
        Assert.Equal(agent is null ? "aexp:local" : "aexp:" + agent, settings.Authorization(DateTimeOffset.UnixEpoch).PrincipalId);
        Assert.DoesNotContain("\\u", json); // non-ASCII and '<', '>', '&' as written, for a person reading it
    }
}

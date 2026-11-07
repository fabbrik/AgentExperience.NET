using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentExperience.Cli.Configuration;
using AgentExperience.Cli.Engine;
using AgentExperience.Core.Holdout;

namespace AgentExperience.Cli.Tests;

/// <summary>
/// The repository's own dogfood setup (<c>dogfood/</c>): its config loads with the tool's loader once copied to
/// <c>.aexp/config.json</c>, its verifier resolves to the committed script, and the pre-registration names the exact files.
/// Nothing here installs a hook, approves a config or runs the script.
/// </summary>
public sealed class DogfoodConfigTests
{
    private static string Repository { get; } = FindRepository();

    private static string Dogfood(string name) => Path.Combine(Repository, "dogfood", name);

    [Fact]
    public void The_config_copied_to_aexp_loads_and_its_verifier_is_the_committed_script()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aexp-dogfood-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".aexp"));
            Directory.CreateDirectory(Path.Combine(root, "dogfood"));
            File.Copy(Dogfood("aexp.config.json"), Path.Combine(root, ".aexp", "config.json"));
            var script = Path.Combine(root, "dogfood", "verify.sh");
            File.Copy(Dogfood("verify.sh"), script);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(script, File.GetUnixFileMode(Dogfood("verify.sh")));
            }

            var settings = AexpConfigLoader.Load(null, root, Environment.GetEnvironmentVariable);

            Assert.Equal(Path.GetFullPath(root), Path.GetFullPath(settings.ProjectRoot));
            Assert.Equal(("fabbrik", "agentexperience-net", "dev"), (settings.Scope.TenantId, settings.Scope.ApplicationId, settings.Scope.ProjectId));
            var store = Assert.IsType<StoreSetting.Sqlite>(settings.Store);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), ".aexp", "experience.db"), store.Path);
            Assert.Equal(0.5, settings.HoldoutRate);
            Assert.Null(settings.HoldoutSalt);
            Assert.True(settings.RecallEnabled);
            Assert.Equal(["file_path"], settings.AllowArguments);
            Assert.Empty(settings.Recipes);

            // Below the Stop hook's 600 s (aexp setup claude-code), so the hook can finalize around it.
            Assert.Equal(["dogfood/verify.sh"], settings.Verifier.Command);
            Assert.True(settings.Verifier.Timeout < TimeSpan.FromSeconds(600));
            Assert.False(settings.Verifier.AcceptExitCode);
            Assert.Equal(Path.GetFullPath(root), Path.GetFullPath(settings.Verifier.WorkingDirectory));
            Assert.Equal(script, VerifierRunner.ResolveProgram(settings.Verifier, settings.ProjectRoot, Environment.GetEnvironmentVariable));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_committed_script_is_executable_and_runs_the_tests_without_a_shell_in_the_config()
    {
        var script = File.ReadAllText(Dogfood("verify.sh"));
        Assert.StartsWith("#!/usr/bin/env bash\n", script, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', script);
        Assert.Contains("dotnet build", script, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(Dogfood("verify.sh")).HasFlag(UnixFileMode.UserExecute), "dogfood/verify.sh is not executable.");
        }
    }

    [Fact]
    public void The_preregistration_names_the_files_as_they_are_and_the_library_defaults_it_relies_on()
    {
        var registration = JsonNode.Parse(File.ReadAllText(Dogfood("preregistration.json")))!;
        var design = registration["design"]!;
        Assert.Equal("dogfood/aexp.config.json", (string?)design["config"]!["file"]);
        Assert.Equal("dogfood/verify.sh", (string?)design["verifier"]!["file"]);

        // A change to either file is a change to the design: it must come with a new digest (and an amendment).
        Assert.Equal(Sha256(Dogfood("aexp.config.json")), (string?)design["config"]!["sha256"]);
        Assert.Equal(Sha256(Dogfood("verify.sh")), (string?)design["verifier"]!["sha256"]);

        Assert.Equal(ExperienceBenefitOptions.DefaultMinimumRunsPerArm, (int)registration["comparison"]!["minimumVerifiedRunsPerArm"]!);
        Assert.Equal("One Claude Code session.", (string?)registration["randomization"]!["unit"]);
        Assert.NotNull(registration["amendments"]!.AsArray());
    }

    /// <summary>SHA-256 of the file with line endings normalized to LF, in lowercase hex.</summary>
    private static string Sha256(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(path).ReplaceLineEndings("\n"))));

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AgentExperience.NET.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (AgentExperience.NET.sln) was not found above the test output.");
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentExperience.Cli.Tests;

/// <summary>
/// A project directory of its own in the temporary directory, with a <c>.aexp/config.json</c> whose verifier passes once
/// the file <c>fixed</c> exists in the project, and runs the tool's entry point in it.
/// </summary>
public sealed class TempProject : IDisposable
{
    public TempProject(Action<JsonObject>? configure = null, bool trusted = true)
    {
        Root = Path.Combine(Path.GetTempPath(), $"aexp-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(Root, ".aexp"));
        UserConfig = Path.Combine(Path.GetTempPath(), $"aexp-user-{Guid.NewGuid():N}");
        Directory.CreateDirectory(UserConfig);
        var config = new JsonObject
        {
            ["identity"] = new JsonObject { ["tenant"] = "local", ["application"] = "cli-tests", ["project"] = "repo" },
            ["verify"] = new JsonObject
            {
                ["command"] = new JsonArray([.. VerifierPassesWhenFixed.Select(part => (JsonNode?)JsonValue.Create(part))]),
                ["timeoutSeconds"] = 600,
                ["checkId"] = "tests",
            },
            // Generous: a loaded machine must never turn a lookup into a timeout.
            ["recall"] = new JsonObject { ["lookupTimeoutMs"] = 120_000 },
        };
        configure?.Invoke(config);
        WriteConfig(config);
        if (trusted)
        {
            Trust();
        }
    }

    /// <summary>This test's own user config directory (XDG_CONFIG_HOME): where its trust file lives.</summary>
    public string UserConfig { get; }

    /// <summary>The environment the tool sees: the real one, but this test's own user config directory.</summary>
    public string? Environment(string name) =>
        name == "XDG_CONFIG_HOME" ? UserConfig : System.Environment.GetEnvironmentVariable(name);

    /// <summary>Approves the current config, as a person would with 'aexp trust'.</summary>
    public void Trust()
    {
        var result = RunAsync("trust").GetAwaiter().GetResult();
        Assert.True(result.Exit == 0, result.Error);
    }

    /// <summary>A verifier that exits 0 when the file <c>fixed</c> exists in the working directory, 1 otherwise.</summary>
    public static IReadOnlyList<string> VerifierPassesWhenFixed { get; } = OperatingSystem.IsWindows()
        ? ["cmd", "/c", "if exist fixed (exit 0) else (exit 1)"]
        : ["sh", "-c", "test -f fixed"];

    public string Root { get; }

    public string ConfigPath => Path.Combine(Root, ".aexp", "config.json");

    public string RunsDirectory => Path.Combine(Root, ".aexp", "runs");

    public void WriteConfig(JsonObject config) =>
        File.WriteAllText(ConfigPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>Makes the verifier pass from now on.</summary>
    public void Fix() => File.WriteAllText(Path.Combine(Root, "fixed"), "fixed");

    /// <summary>Runs one command through the tool's entry point, in this project, with a fresh container like a new process.</summary>
    public async Task<CliResult> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await AexpCli.RunAsync(
            args,
            new AexpConsole(output, error, Root, Environment, TimeProvider.System));
        return new CliResult(exit, output.ToString(), error.ToString());
    }

    /// <summary>Runs a command that must succeed, and returns its output.</summary>
    public async Task<string> OkAsync(params string[] args)
    {
        var result = await RunAsync(args);
        Assert.True(result.Exit == 0, $"aexp {string.Join(' ', args)} exited {result.Exit}: {result.Error}");
        return result.Output;
    }

    /// <summary>One run: started, its calls recorded, ended. Returns its ID.</summary>
    public async Task<string> RunTaskAsync(string task, params (string Tool, string? Args, string? Result, string? Error)[] calls)
    {
        var runId = (await OkAsync("run", "start", "--task", task)).Trim();
        foreach (var call in calls)
        {
            var args = new List<string> { "run", "call", "--run", runId, "--tool", call.Tool };
            if (call.Args is not null)
            {
                args.AddRange(["--args", call.Args]);
            }

            if (call.Result is not null)
            {
                args.AddRange(["--result", call.Result]);
            }

            if (call.Error is not null)
            {
                args.AddRange(["--error", call.Error]);
            }

            await OkAsync([.. args]);
        }

        await OkAsync("run", "end", "--run", runId);
        return runId;
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
            // Best effort: a temporary directory left behind is harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed record CliResult(int Exit, string Output, string Error);

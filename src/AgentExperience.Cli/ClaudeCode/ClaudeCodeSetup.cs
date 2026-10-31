using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentExperience.Cli.Configuration;
using AgentExperience.Cli.Engine;

namespace AgentExperience.Cli.ClaudeCode;

/// <summary>
/// <c>aexp setup claude-code [--print]</c>, run in the project root: adds aexp's hooks to the project's
/// <c>.claude/settings.json</c> (merged: every existing hook and setting stays, and a hook already there is not added
/// twice), writes a default <c>.aexp/config.json</c> when there is none (never over one), and adds <c>.aexp/</c> to
/// <c>.gitignore</c>. Idempotent. <c>--print</c> writes nothing and prints what it would add. It never trusts the
/// config: approving the verifier stays a person's step.
/// </summary>
internal static class ClaudeCodeSetup
{
    /// <summary>The permission rules setup recommends, so the agent cannot edit its own verifier or approve it.</summary>
    public static IReadOnlyList<string> RecommendedDenyRules { get; } =
        ["Edit(/.aexp/**)", "Bash(aexp trust)", "Bash(aexp trust *)"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, IndentSize = 2 };

    public static async Task<int> RunAsync(AexpCli.Arguments args, AexpConsole console, CancellationToken cancellationToken)
    {
        if (args.Command is not ["setup", "claude-code"])
        {
            throw new AexpUsageException("Usage: aexp setup claude-code [--print]  (Claude Code is the one agent set up so far.)");
        }

        args.Only("print");
        var print = args.Flag("print");
        var root = Path.GetFullPath(console.WorkingDirectory);
        var output = console.Out;

        // 1. The hooks, merged into the project's settings.
        var settingsPath = Path.Combine(root, ".claude", "settings.json");
        var existing = File.Exists(settingsPath) ? await File.ReadAllTextAsync(settingsPath, cancellationToken).ConfigureAwait(false) : null;
        if (print)
        {
            await output.WriteLineAsync("Hooks for .claude/settings.json (merged with what is there; nothing written):").ConfigureAwait(false);
            await output.WriteLineAsync(new JsonObject { ["hooks"] = HooksBlock() }.ToJsonString(Indented)).ConfigureAwait(false);
        }
        else
        {
            var (merged, added) = MergeSettings(existing);
            if (added.Count == 0)
            {
                await output.WriteLineAsync(".claude/settings.json: the aexp hooks are already there; unchanged.").ConfigureAwait(false);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                var mode = existing is not null && !OperatingSystem.IsWindows() ? File.GetUnixFileMode(settingsPath) : DefaultFileMode;
                AtomicFile.Write(settingsPath, Encoding.UTF8.GetBytes(merged), overwrite: true, mode);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(settingsPath, mode);
                }

                await output.WriteLineAsync($".claude/settings.json: added the {string.Join(", ", added)} hook(s); every existing hook kept.").ConfigureAwait(false);
            }
        }

        // 2. The config, only when there is none.
        var configPath = Path.Combine(root, AexpConfigLoader.DefaultRelativePath);
        var verifier = DetectVerifier(root);
        if (File.Exists(configPath))
        {
            await output.WriteLineAsync(".aexp/config.json: exists; left as it is.").ConfigureAwait(false);
        }
        else if (print)
        {
            await output.WriteLineAsync(".aexp/config.json would be written:").ConfigureAwait(false);
            await output.WriteLineAsync(DefaultConfig(root, verifier)).ConfigureAwait(false);
        }
        else
        {
            var directory = Path.GetDirectoryName(configPath)!;
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            AtomicFile.Write(configPath, Encoding.UTF8.GetBytes(DefaultConfig(root, verifier)), overwrite: false);
            await output.WriteLineAsync(verifier is null
                ? ".aexp/config.json: written. No test command was obvious, so verify.command is a placeholder that refuses to run: set it."
                : $".aexp/config.json: written, verifier {JsonSerializer.Serialize(verifier)} (detected; check it).").ConfigureAwait(false);
        }

        // 3. Keep the store, runs, sessions and config out of version control.
        var gitignore = Path.Combine(root, ".gitignore");
        var ignoreText = File.Exists(gitignore) ? await File.ReadAllTextAsync(gitignore, cancellationToken).ConfigureAwait(false) : string.Empty;
        if (Ignores(ignoreText))
        {
            await output.WriteLineAsync(".gitignore: already ignores .aexp/.").ConfigureAwait(false);
        }
        else if (print)
        {
            await output.WriteLineAsync(".gitignore would get the line: .aexp/").ConfigureAwait(false);
        }
        else
        {
            var newline = ignoreText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var prefix = ignoreText.Length == 0 || ignoreText.EndsWith('\n') ? string.Empty : newline;
            await File.AppendAllTextAsync(gitignore, prefix + "# aexp: the local lesson store, open runs and the config" + newline + ".aexp/" + newline, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(".gitignore: added .aexp/.").ConfigureAwait(false);
        }

        await output.WriteLineAsync(NextSteps()).ConfigureAwait(false);
        return AexpCli.Ok;
    }

    private static UnixFileMode DefaultFileMode =>
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>aexp's hooks, one matcher group per event, each with its timeout passed to the hook so it can keep inside it.</summary>
    internal static JsonObject HooksBlock()
    {
        var hooks = new JsonObject();
        foreach (var (hookEvent, timeout) in ClaudeCodeHooks.Events)
        {
            hooks[hookEvent] = new JsonArray(Group(hookEvent, timeout));
        }

        return hooks;
    }

    private static JsonObject Group(string hookEvent, int timeout) => new()
    {
        ["hooks"] = new JsonArray(new JsonObject
        {
            ["type"] = "command",
            ["command"] = Command(hookEvent, timeout),
            ["timeout"] = timeout,
        }),
    };

    internal static string Command(string hookEvent, int timeout) => $"aexp hook {hookEvent} --timeout {timeout}";

    /// <summary>
    /// The settings with aexp's hooks added: every existing key, hook and matcher group is kept as it was; an event that
    /// already has an <c>aexp hook &lt;event&gt;</c> command gets nothing. Refuses settings that are not a JSON object
    /// (comments included, which a rewrite would lose) rather than guess.
    /// </summary>
    internal static (string Json, IReadOnlyList<string> Added) MergeSettings(string? existing)
    {
        JsonObject settings;
        if (string.IsNullOrWhiteSpace(existing))
        {
            settings = [];
        }
        else
        {
            try
            {
                settings = JsonNode.Parse(existing) as JsonObject
                    ?? throw new AexpUsageException(".claude/settings.json is not a JSON object; add the hooks by hand (see 'aexp setup claude-code --print').");
            }
            catch (JsonException ex)
            {
                throw new AexpUsageException(
                    ".claude/settings.json is not plain JSON (aexp will not rewrite it and lose what it cannot read); add the hooks by hand "
                    + "(see 'aexp setup claude-code --print').", ex.Message);
            }
        }

        if (settings["hooks"] is null)
        {
            settings["hooks"] = new JsonObject();
        }

        if (settings["hooks"] is not JsonObject hooks)
        {
            throw new AexpUsageException(".claude/settings.json has a 'hooks' that is not an object; fix it, then run setup again.");
        }

        var added = new List<string>();
        foreach (var (hookEvent, timeout) in ClaudeCodeHooks.Events)
        {
            if (hooks[hookEvent] is null)
            {
                hooks[hookEvent] = new JsonArray();
            }

            if (hooks[hookEvent] is not JsonArray groups)
            {
                throw new AexpUsageException($".claude/settings.json has hooks.{hookEvent} that is not an array; fix it, then run setup again.");
            }

            if (groups.OfType<JsonObject>().Any(group => HasAexpHook(group, hookEvent)))
            {
                continue;
            }

            groups.Add(Group(hookEvent, timeout));
            added.Add(hookEvent);
        }

        var json = settings.ToJsonString(Indented) + "\n";
        return (added.Count == 0 && existing is not null ? existing : json, added);
    }

    private static bool HasAexpHook(JsonObject group, string hookEvent) =>
        group["hooks"] is JsonArray handlers
        && handlers.OfType<JsonObject>().Any(handler =>
            handler["command"] is JsonValue value
            && value.TryGetValue<string>(out var command)
            && (command.Contains($"aexp hook {hookEvent} ", StringComparison.Ordinal) || command.TrimEnd().EndsWith($"aexp hook {hookEvent}", StringComparison.Ordinal)));

    /// <summary>The project's test command when it is obvious (exactly one kind of project at the root); otherwise <see langword="null"/>.</summary>
    internal static IReadOnlyList<string>? DetectVerifier(string root)
    {
        var found = new List<string[]>();
        if (Directory.EnumerateFiles(root, "*.sln").Any() || Directory.EnumerateFiles(root, "*.slnx").Any()
            || Directory.EnumerateFiles(root, "*.csproj").Any() || Directory.EnumerateFiles(root, "*.fsproj").Any())
        {
            found.Add(["dotnet", "test"]);
        }

        if (File.Exists(Path.Combine(root, "package.json")) && HasTestScript(Path.Combine(root, "package.json")))
        {
            found.Add(["npm", "test"]);
        }

        if (new[] { "pytest.ini", "pyproject.toml", "conftest.py", "tox.ini" }.Any(name => File.Exists(Path.Combine(root, name))))
        {
            found.Add(["pytest"]);
        }

        if (File.Exists(Path.Combine(root, "go.mod")))
        {
            found.Add(["go", "test", "./..."]);
        }

        return found.Count == 1 ? found[0] : null;
    }

    private static bool HasTestScript(string packageJson)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(packageJson)) is JsonObject package
                && package["scripts"] is JsonObject scripts
                && scripts["test"] is JsonValue test
                && test.TryGetValue<string>(out var script)
                && !script.Contains("no test specified", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return false;
        }
    }

    internal static string DefaultConfig(string root, IReadOnlyList<string>? verifier)
    {
        var name = new string([.. Path.GetFileName(Path.TrimEndingDirectorySeparator(root)).Where(c => !char.IsControl(c))]).Trim();
        if (name.Length == 0)
        {
            name = "project";
        }
        else if (name.Length > 64)
        {
            name = name[..64];
        }

        var config = new JsonObject
        {
            ["store"] = AexpConfigLoader.DefaultStore,
            ["identity"] = new JsonObject { ["tenant"] = "local", ["application"] = name, ["project"] = name, ["agent"] = "claude-code" },
            ["verify"] = new JsonObject
            {
                ["command"] = new JsonArray([.. (verifier ?? [VerifierRunner.Placeholder]).Select(part => (JsonNode?)JsonValue.Create(part))]),
                // Below the Stop hook's timeout, so the hook can still end and finalize around it.
                ["timeoutSeconds"] = 540,
                ["checkId"] = "tests",
            },
            ["sanitization"] = new JsonObject { ["allowArguments"] = new JsonArray("file_path") },
            ["injection"] = new JsonObject { ["holdoutRate"] = 0.2 },
            ["recall"] = new JsonObject { ["enabled"] = true },
        };
        return config.ToJsonString(Indented) + "\n";
    }

    private static bool Ignores(string gitignore) =>
        gitignore.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => line.Trim())
            .Any(line => line is ".aexp" or ".aexp/" or "/.aexp" or "/.aexp/" or ".aexp/*" or "/.aexp/*" or ".aexp/**" or "/.aexp/**");

    private static string NextSteps()
    {
        var deny = new JsonObject { ["permissions"] = new JsonObject { ["deny"] = new JsonArray([.. RecommendedDenyRules.Select(rule => (JsonNode?)JsonValue.Create(rule))]) } };
        return $"""

            Next (these are a person's steps, not the agent's):
              1. Review .aexp/config.json: verify.command is the one command aexp runs to check the agent's work.
              2. Run 'aexp trust' yourself. Until then the hooks record and inject, but the Stop hook does not run the
                 verifier. Any edit to the config revokes the approval.
              3. Protect the config from the agent: add these permission rules to .claude/settings.json (or your user
                 settings), so Claude Code's file tools cannot edit .aexp/ and the agent cannot approve the config:
            {deny.ToJsonString(Indented)}
                 A session that can still rewrite the verifier some other way (a script, say) can fake success; the
                 lesson's checks name the verifier, so a reviewer can see what verified it.
              4. Install aexp where the hooks can find it: dotnet tool install --global AgentExperience.Cli --prerelease
            Turn it off: remove the 'aexp hook' entries from .claude/settings.json.
            """;
    }
}

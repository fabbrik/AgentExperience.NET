using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentExperience.Cli.Engine;

namespace AgentExperience.Cli.Configuration;

/// <summary>
/// <c>aexp init [--print] [--force] [--verify &lt;json array&gt;]</c>, run in the project root: detects the project's kind
/// and proposes its test command as the verifier, writes a commented <c>.aexp/config.json</c>, adds <c>.aexp/</c> to
/// <c>.gitignore</c> when the project is under git, and prints the steps left to a person. It is the one config generator:
/// <c>aexp setup claude-code</c> writes its config and <c>.gitignore</c> entry through the same methods. It never trusts
/// the config: approving the verifier stays a person's step.
/// </summary>
internal static class ProjectInit
{
    /// <summary>A test command the project's files suggest, and the file that suggested it.</summary>
    internal sealed record Candidate(string Evidence, IReadOnlyList<string> Command)
    {
        public override string ToString() => $"{JsonSerializer.Serialize(Command)}  ({Evidence})";
    }

    /// <summary>The line <c>.gitignore</c> gets, with the comment above it.</summary>
    internal const string GitignoreEntry = ".aexp/";

    private const string GitignoreComment = "# aexp: the local lesson store, open runs and the config";

    // A target line ('test:' or 'test::'), not a variable ('test :=', 'test ::=').
    private static readonly Regex MakeTestTarget = new(@"^test\s*:(?!:*=)", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public static async Task<int> RunAsync(AexpCli.Arguments args, AexpConsole console, CancellationToken cancellationToken)
    {
        if (args.Command is not ["init"])
        {
            throw new AexpUsageException("Usage: aexp init [--print] [--force] [--verify <json array>]  (run in the project root).");
        }

        args.Only("print", "force", "verify");
        if (args.Value("config") is not null)
        {
            throw new AexpUsageException("'init' writes ./.aexp/config.json in the working directory and takes no --config.");
        }

        var print = args.Flag("print");
        var force = args.Flag("force");
        var root = Path.GetFullPath(console.WorkingDirectory);
        var configPath = Path.Combine(root, AexpConfigLoader.DefaultRelativePath);
        var output = console.Out;

        if (File.Exists(configPath) && !print && !force)
        {
            await console.Error.WriteLineAsync(
                $"aexp: {AexpConfigLoader.DefaultRelativePath} exists; nothing written. Run 'aexp init --force' to replace it "
                + "(the replacement revokes any 'aexp trust' approval of the old one), or edit it in place.").ConfigureAwait(false);
            return AexpCli.Usage;
        }

        // 1. The verifier: given, detected, asked for, or the placeholder.
        IReadOnlyList<string>? verifier;
        if (args.Value("verify") is { } given)
        {
            verifier = ParseVerifier(given)
                ?? throw new AexpUsageException("""--verify is a JSON array of non-empty strings, the program then its arguments: --verify '["npm","test"]'. It is never split on spaces.""");
        }
        else
        {
            var candidates = Detect(root);
            if (candidates.Count == 1)
            {
                verifier = candidates[0].Command;
                await output.WriteLineAsync($"Detected verifier: {candidates[0]}.").ConfigureAwait(false);
            }
            else
            {
                await output.WriteLineAsync(candidates.Count == 0
                    ? "No test command was detected at the root (looked for a .NET solution or project, package.json with a test script, pytest files, go.mod, a Makefile with a test target)."
                    : "Several test commands fit this root:").ConfigureAwait(false);
                for (var i = 0; i < candidates.Count; i++)
                {
                    await output.WriteLineAsync($"  {(i + 1).ToString(CultureInfo.InvariantCulture)}. {candidates[i]}").ConfigureAwait(false);
                }

                verifier = console.ReadLine is null
                    ? null
                    : await AskAsync(candidates, console).ConfigureAwait(false);
                if (verifier is null)
                {
                    await output.WriteLineAsync(
                        "verify.command is a placeholder that refuses to run: set it in .aexp/config.json to this project's test command "
                        + """(a JSON array, e.g. ["npm","test"]), or run 'aexp init --force --verify '["npm","test"]''.""").ConfigureAwait(false);
                }
            }
        }

        var json = Render(root, verifier, agent: null);

        // 2. The config, and the .gitignore entry.
        if (print)
        {
            await output.WriteLineAsync(File.Exists(configPath)
                ? $"{AexpConfigLoader.DefaultRelativePath} exists; this is what 'aexp init --force' would replace it with (nothing written):"
                : $"{AexpConfigLoader.DefaultRelativePath} would be written (nothing written):").ConfigureAwait(false);
            await output.WriteAsync(json).ConfigureAwait(false);
            await output.WriteLineAsync(await EnsureGitignoreAsync(root, print: true, onlyUnderGit: true, console.Environment, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            return AexpCli.Ok;
        }

        var existed = File.Exists(configPath);
        WriteConfig(configPath, json, overwrite: force);
        await output.WriteLineAsync(existed
            ? $"{AexpConfigLoader.DefaultRelativePath}: replaced. Any 'aexp trust' approval of the old file no longer applies: the bytes changed."
            : $"{AexpConfigLoader.DefaultRelativePath}: written.").ConfigureAwait(false);
        await output.WriteLineAsync(await EnsureGitignoreAsync(root, print: false, onlyUnderGit: true, console.Environment, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        await output.WriteLineAsync(NextSteps).ConfigureAwait(false);
        return AexpCli.Ok;
    }

    private const string NextSteps = """

        Next (a person's steps, not the agent's; init wrote a config and approved nothing):
          1. Review .aexp/config.json: verify.command is the one command aexp runs to check the agent's work.
          2. Run 'aexp trust' yourself. Until then nothing is verified and no lesson is made; any edit to the config revokes
             the approval.
          3. Connect your agent: 'aexp setup claude-code' adds the Claude Code hooks, or register the MCP server with
             'claude mcp add aexp -- aexp mcp' (any MCP client: the server is 'aexp mcp', run in this directory).
        """;

    /// <summary>
    /// Asks for the verifier on the terminal: a candidate's number, a JSON array, or nothing for the placeholder. An
    /// answer that is neither is asked again once; then the placeholder.
    /// </summary>
    private static async Task<IReadOnlyList<string>?> AskAsync(IReadOnlyList<Candidate> candidates, AexpConsole console)
    {
        var prompt = (candidates.Count > 0 ? "Verifier: a number from the list, " : "Verifier: ")
            + """a JSON array such as ["npm","test"], or Enter for a placeholder to set later: """;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await console.Out.WriteAsync(prompt).ConfigureAwait(false);
            await console.Out.FlushAsync().ConfigureAwait(false);
            var answer = console.ReadLine!()?.Trim();
            if (string.IsNullOrEmpty(answer))
            {
                return null;
            }

            if (int.TryParse(answer, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 && number <= candidates.Count)
            {
                return candidates[number - 1].Command;
            }

            if (ParseVerifier(answer) is { } command)
            {
                return command;
            }

            await console.Out.WriteLineAsync(
                "Not a listed number or a JSON array of non-empty strings" + (attempt == 0 ? "; once more." : "; the placeholder it is.")).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>A JSON array of non-empty strings, or <see langword="null"/> for anything else (text is never split on spaces).</summary>
    internal static IReadOnlyList<string>? ParseVerifier(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonArray array || array.Count == 0)
            {
                return null;
            }

            var parts = new List<string>(array.Count);
            foreach (var item in array)
            {
                if (item is not JsonValue value || !value.TryGetValue<string>(out var part) || string.IsNullOrWhiteSpace(part))
                {
                    return null;
                }

                parts.Add(part);
            }

            return parts;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Every test command the files at the root suggest, in a fixed order; one candidate is an obvious verifier.</summary>
    internal static IReadOnlyList<Candidate> Detect(string root)
    {
        var found = new List<Candidate>();
        var dotnet = new[] { "*.sln", "*.slnx", "*.csproj", "*.fsproj" }.SelectMany(pattern => Directory.EnumerateFiles(root, pattern)).Select(Path.GetFileName).FirstOrDefault();
        if (dotnet is not null)
        {
            found.Add(new Candidate(dotnet, ["dotnet", "test"]));
        }

        if (File.Exists(Path.Combine(root, "package.json")) && HasTestScript(Path.Combine(root, "package.json")))
        {
            found.Add(new Candidate("package.json, scripts.test", ["npm", "test"]));
        }

        if (new[] { "pytest.ini", "pyproject.toml", "conftest.py", "tox.ini" }.FirstOrDefault(name => File.Exists(Path.Combine(root, name))) is { } python)
        {
            found.Add(new Candidate(python, ["pytest"]));
        }

        if (File.Exists(Path.Combine(root, "go.mod")))
        {
            found.Add(new Candidate("go.mod", ["go", "test", "./..."]));
        }

        // A Makefile usually wraps one of the above, so 'make test' is proposed only when nothing else fits.
        if (found.Count == 0
            && new[] { "Makefile", "makefile", "GNUmakefile" }.FirstOrDefault(name => File.Exists(Path.Combine(root, name))) is { } makefile
            && HasMakeTestTarget(Path.Combine(root, makefile)))
        {
            found.Add(new Candidate($"{makefile}, target test", ["make", "test"]));
        }

        return found;
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
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasMakeTestTarget(string makefile)
    {
        try
        {
            return MakeTestTarget.IsMatch(File.ReadAllText(makefile).Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The config as written: today's defaults, one <c>//</c> comment per choice (the loader skips comments), the
    /// placeholder when <paramref name="verifier"/> is <see langword="null"/>, and <c>identity.agent</c> only when
    /// <paramref name="agent"/> names one.
    /// </summary>
    internal static string Render(string root, IReadOnlyList<string>? verifier, string? agent)
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

        static string Json(object value) => JsonSerializer.Serialize(value, Encoded);

        var b = new StringBuilder();
        b.Append("// aexp: the scope the runs are kept under and the one command aexp may run. Written by 'aexp init'; edit it freely.\n");
        b.Append("// Every edit revokes 'aexp trust' until a person approves the file again. Reference: docs/guide/cli-and-mcp.md.\n");
        b.Append("{\n");
        b.Append("  // The store, open runs, sessions and the hook log stay in .aexp/, which .gitignore excludes. Or \"postgres:<ENV_VAR>\".\n");
        b.Append("  \"store\": ").Append(Json(AexpConfigLoader.DefaultStore)).Append(",\n");
        b.Append("  // The scope every run and lesson is kept under. \"agent\" (optional) names the agent the runs belong to.\n");
        b.Append("  \"identity\": { \"tenant\": \"local\", \"application\": ").Append(Json(name)).Append(", \"project\": ").Append(Json(name));
        if (agent is not null)
        {
            b.Append(", \"agent\": ").Append(Json(agent));
        }

        b.Append(" },\n");
        b.Append("  \"verify\": {\n");
        b.Append(verifier is null
            ? "    // No test command was obvious, so this placeholder refuses to run: set it to the project's test command.\n"
            : "    // Detected from the project's files: check it.\n");
        b.Append("    // The only command aexp runs: an argument vector, never a shell line. Exit code 0 is a pass.\n");
        b.Append("    \"command\": ").Append(Json(verifier ?? [VerifierRunner.Placeholder])).Append(",\n");
        b.Append("    // Below the Claude Code Stop hook's 600 s, so the hook can still end and finalize around it.\n");
        b.Append("    \"timeoutSeconds\": 540,\n");
        b.Append("    // The check each lesson names as what verified it.\n");
        b.Append("    \"checkId\": \"tests\"\n");
        b.Append("  },\n");
        b.Append("  // Tool argument names capture keeps. Everything else is dropped; secret-looking names and values are redacted anyway.\n");
        b.Append("  \"sanitization\": { \"allowArguments\": [\"file_path\"] },\n");
        b.Append("  // One run in five is given no lessons, so 'aexp benefit' can measure whether they help.\n");
        b.Append("  \"injection\": { \"holdoutRate\": 0.2 },\n");
        b.Append("  // After a tool call fails, the agent may ask what worked after the same failure before.\n");
        b.Append("  \"recall\": { \"enabled\": true }\n");
        b.Append("}\n");
        return b.ToString();
    }

    private static readonly JsonSerializerOptions Encoded = new()
    {
        // Read by people, not embedded in HTML: '<', '>', '&' and non-ASCII stay as written.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Writes the config whole or not at all: <c>.aexp/</c> owner-only (0700), the file owner read and write.</summary>
    internal static void WriteConfig(string configPath, string json, bool overwrite)
    {
        var directory = Path.GetDirectoryName(configPath)!;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(directory, OwnerOnly);
            // An .aexp/ that was there already (made by hand, say) is tightened to the same mode.
            if (File.GetUnixFileMode(directory) != OwnerOnly)
            {
                File.SetUnixFileMode(directory, OwnerOnly);
            }
        }

        AtomicFile.Write(configPath, Encoding.UTF8.GetBytes(json), overwrite);
    }

    /// <summary>
    /// Adds <c>.aexp/</c> to the root's <c>.gitignore</c> unless it is ignored already, and returns the one line to print.
    /// With <paramref name="onlyUnderGit"/>, a root with no <c>.gitignore</c> of its own and no <c>.git</c> (directory or
    /// file) in it or above it, up to the home directory, gets no file.
    /// </summary>
    internal static async Task<string> EnsureGitignoreAsync(string root, bool print, bool onlyUnderGit, Func<string, string?> environment, CancellationToken cancellationToken)
    {
        var gitignore = Path.Combine(root, ".gitignore");
        if (onlyUnderGit && !File.Exists(gitignore) && !UnderGit(root, environment))
        {
            return ".gitignore: not under git here (no .git or .gitignore), so none was created; keep .aexp/ out of version control yourself.";
        }

        var ignoreText = File.Exists(gitignore) ? await File.ReadAllTextAsync(gitignore, cancellationToken).ConfigureAwait(false) : string.Empty;
        if (Ignores(ignoreText))
        {
            return ".gitignore: already ignores .aexp/.";
        }

        if (print)
        {
            return ".gitignore would get the line: " + GitignoreEntry;
        }

        var newline = ignoreText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var prefix = ignoreText.Length == 0 || ignoreText.EndsWith('\n') ? string.Empty : newline;
        await File.AppendAllTextAsync(gitignore, prefix + GitignoreComment + newline + GitignoreEntry + newline, cancellationToken).ConfigureAwait(false);
        return ".gitignore: added .aexp/.";
    }

    /// <summary>Whether <paramref name="root"/> or a directory above it holds <c>.git</c>, looking no further than the home directory (as the config lookup does).</summary>
    private static bool UnderGit(string root, Func<string, string?> environment)
    {
        var home = environment(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
        home = string.IsNullOrWhiteSpace(home) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
        for (var directory = new DirectoryInfo(Path.GetFullPath(root)); directory is not null; directory = directory.Parent)
        {
            var here = Path.TrimEndingDirectorySeparator(directory.FullName);
            var git = Path.Combine(here, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return true;
            }

            if (home is not null && string.Equals(here, home, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return false;
    }

    internal static bool Ignores(string gitignore) =>
        gitignore.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => line.Trim())
            .Any(line => line is ".aexp" or ".aexp/" or "/.aexp" or "/.aexp/" or ".aexp/*" or "/.aexp/*" or ".aexp/**" or "/.aexp/**");
}

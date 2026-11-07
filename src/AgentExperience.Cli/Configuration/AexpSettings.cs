using System.Text.Json;
using AgentExperience.Abstractions;

namespace AgentExperience.Cli.Configuration;

/// <summary>Where the store lives: a SQLite file, or a PostgreSQL database whose connection string an environment variable holds.</summary>
internal abstract record StoreSetting
{
    internal sealed record Sqlite(string Path) : StoreSetting;

    /// <summary>The connection string is read from <paramref name="Variable"/> when the store is opened, and never written anywhere.</summary>
    internal sealed record Postgres(string Variable) : StoreSetting;
}

/// <summary>The verifier, resolved: an argument vector, a bound, and the check its exit code is evidence for.</summary>
internal sealed record VerifierSetting(
    IReadOnlyList<string> Command,
    TimeSpan Timeout,
    string CheckId,
    string WorkingDirectory,
    string Revision,
    bool AcceptExitCode);

/// <summary>One tool's recipe consent.</summary>
internal sealed record RecipeSetting(string Tool, IReadOnlyList<string> Arguments, bool ResultExcerpt);

/// <summary>
/// The validated configuration every command runs under. Built only by <see cref="AexpConfigLoader"/>, from the config
/// file: nothing here can come from a command argument or a tool call.
/// </summary>
internal sealed record AexpSettings(
    string ConfigPath,
    string ConfigSha256,
    string ProjectRoot,
    StoreSetting Store,
    Scope Scope,
    string TaskId,
    VerifierSetting Verifier,
    IReadOnlyList<string> AllowArguments,
    IReadOnlyList<RecipeSetting> Recipes,
    int? MaxRecords,
    int? MaxBytes,
    double HoldoutRate,
    string? HoldoutSalt,
    bool RecallEnabled,
    int? MaxHintsPerRun,
    int? MaxHintBytes,
    bool AllowCoarseMatch,
    TimeSpan? LookupTimeout,
    IReadOnlyList<string>? ReadOnlyTools = null,
    IReadOnlyList<string>? ReadOnlyBashPrefixes = null)
{
    /// <summary>Where open runs are kept between commands: beside the config file, owner-only.</summary>
    public string RunsDirectory => Path.Combine(Path.GetDirectoryName(ConfigPath)!, "runs");

    /// <summary>The principal every request runs as: the configured user, else the configured agent, else <c>aexp</c>.</summary>
    public AuthorizationContext Authorization(DateTimeOffset now) => new(
        TenantId: Scope.TenantId,
        PrincipalId: "aexp:" + (Scope.UserId ?? Scope.AgentId ?? "local"),
        Roles: [],
        IssuedAt: now,
        ApplicationId: Scope.ApplicationId,
        ProjectId: Scope.ProjectId,
        TeamId: Scope.TeamId,
        AgentId: Scope.AgentId,
        UserId: Scope.UserId);
}

/// <summary>A configuration that cannot be used: the tool refuses to start, saying why.</summary>
internal sealed class AexpConfigException(string message) : Exception(message);

/// <summary>Finds, reads and validates the configuration.</summary>
internal static class AexpConfigLoader
{
    /// <summary>The config file's place in a project.</summary>
    public const string DefaultRelativePath = ".aexp/config.json";

    /// <summary>The environment variable that names a config file.</summary>
    public const string EnvironmentVariable = "AEXP_CONFIG";

    /// <summary>The default SQLite file, relative to the project root.</summary>
    public const string DefaultStore = "sqlite:.aexp/experience.db";

    /// <summary>The default verifier bound.</summary>
    public static readonly TimeSpan DefaultVerifierTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The config path: <paramref name="explicitPath"/> (<c>--config</c>), else <see cref="EnvironmentVariable"/>, else the
    /// nearest <c>.aexp/config.json</c> in <paramref name="workingDirectory"/> or a directory above it, looking no further
    /// than the repository root (the directory holding <c>.git</c>) or the home directory, whichever comes first.
    /// </summary>
    public static string Locate(string? explicitPath, string workingDirectory, Func<string, string?> environment)
    {
        var named = explicitPath ?? environment(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(named))
        {
            var full = Path.GetFullPath(named, workingDirectory);
            return File.Exists(full) ? full : throw new AexpConfigException($"The config file '{full}' does not exist.");
        }

        var home = environment(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
        home = string.IsNullOrWhiteSpace(home) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
        for (var directory = new DirectoryInfo(Path.GetFullPath(workingDirectory)); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, DefaultRelativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var here = Path.TrimEndingDirectorySeparator(directory.FullName);
            if (Directory.Exists(Path.Combine(here, ".git")) || File.Exists(Path.Combine(here, ".git"))
                || (home is not null && string.Equals(here, home, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)))
            {
                break;
            }
        }

        throw new AexpConfigException(
            $"No {DefaultRelativePath} was found in '{workingDirectory}' or above it, up to the repository root or the home "
            + $"directory. Create one (see docs/guide/cli-and-mcp.md), or name one with --config or {EnvironmentVariable}.");
    }

    public static AexpSettings Load(string? explicitPath, string workingDirectory, Func<string, string?> environment)
    {
        var path = Locate(explicitPath, workingDirectory, environment);

        // A config directory another user owns could hold anything this user's tool would then run.
        foreach (var owned in new[] { Path.GetDirectoryName(path)!, path })
        {
            if (FileOwnership.OwnedByCurrentUser(owned) == false)
            {
                throw new AexpConfigException($"'{owned}' is not owned by the current user; aexp refuses a config another user controls.");
            }
        }

        // The bytes parsed are the bytes hashed for trust: nothing can change between the two.
        var bytes = File.ReadAllBytes(path);
        AexpConfigFile file;
        try
        {
            file = JsonSerializer.Deserialize<AexpConfigFile>(bytes, AexpConfigFile.Json)
                ?? throw new AexpConfigException($"The config file '{path}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new AexpConfigException($"The config file '{path}' is not valid: {ex.Message}");
        }

        return Validate(file, path) with { ConfigSha256 = ConfigTrust.Hash(bytes) };
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or lies inside it, after resolving both.</summary>
    internal static bool IsInside(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return string.Equals(Path.TrimEndingDirectorySeparator(full), fullRoot, comparison)
            || full.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>The project root: the directory holding <c>.aexp/</c> when the config sits in one, else the config's own directory.</summary>
    internal static string RootOf(string configPath)
    {
        var directory = Path.GetDirectoryName(configPath)!;
        return string.Equals(Path.GetFileName(directory), ".aexp", StringComparison.Ordinal)
            ? Path.GetDirectoryName(directory)!
            : directory;
    }

    internal static AexpSettings Validate(AexpConfigFile file, string configPath)
    {
        var root = RootOf(configPath);

        var storeText = string.IsNullOrWhiteSpace(file.Store) ? DefaultStore : file.Store.Trim();
        StoreSetting store;
        if (storeText.StartsWith("sqlite:", StringComparison.Ordinal))
        {
            var relative = storeText["sqlite:".Length..].Trim();
            if (relative.Length == 0)
            {
                throw new AexpConfigException("store: 'sqlite:' names no file.");
            }

            store = new StoreSetting.Sqlite(Path.GetFullPath(relative, root));
        }
        else if (storeText.StartsWith("postgres:", StringComparison.Ordinal))
        {
            var variable = storeText["postgres:".Length..].Trim();
            if (variable.Length == 0 || variable.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            {
                throw new AexpConfigException(
                    "store: 'postgres:' must name the environment variable that holds the connection string (letters, digits and _), "
                    + "never the connection string itself.");
            }

            store = new StoreSetting.Postgres(variable);
        }
        else
        {
            throw new AexpConfigException("store: expected 'sqlite:<path>' or 'postgres:<ENV_VAR>'.");
        }

        var identity = file.Identity ?? throw new AexpConfigException("identity: set tenant, application and project.");
        static string Required(string? value, string name) =>
            string.IsNullOrWhiteSpace(value) ? throw new AexpConfigException($"identity.{name} is required.") : value.Trim();
        static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        var scope = new Scope(
            Required(identity.Tenant, "tenant"),
            Required(identity.Application, "application"),
            Required(identity.Project, "project"),
            Optional(identity.Team),
            Optional(identity.Agent),
            Optional(identity.User));

        var verify = file.Verify;
        if (verify?.Command is not { Count: > 0 } command || command.Any(string.IsNullOrEmpty) || string.IsNullOrWhiteSpace(command[0]))
        {
            throw new AexpConfigException(
                "verify.command is required: the program and arguments that check the work (exit code 0 = pass), for example "
                + "[\"dotnet\", \"test\"]. It is the only command aexp runs.");
        }

        var timeout = verify.TimeoutSeconds is { } seconds
            ? seconds is > 0 and <= 86_400 ? TimeSpan.FromSeconds(seconds) : throw new AexpConfigException("verify.timeoutSeconds must be between 1 and 86400.")
            : DefaultVerifierTimeout;
        var checkId = string.IsNullOrWhiteSpace(verify.CheckId) ? "verify" : verify.CheckId.Trim();
        var workingDirectory = Path.GetFullPath(verify.WorkingDirectory ?? ".", root);
        if (!IsInside(root, workingDirectory))
        {
            throw new AexpConfigException("verify.workingDirectory must lie inside the project root.");
        }
        var revision = string.IsNullOrWhiteSpace(verify.Revision) ? "workspace" : verify.Revision.Trim();

        var recipes = new List<RecipeSetting>();
        foreach (var recipe in file.Recipes ?? [])
        {
            if (string.IsNullOrWhiteSpace(recipe.Tool))
            {
                throw new AexpConfigException("recipes: every entry names a tool.");
            }

            recipes.Add(new RecipeSetting(recipe.Tool.Trim(), [.. recipe.Arguments ?? []], recipe.ResultExcerpt ?? false));
        }

        var injection = file.Injection;
        var holdoutRate = injection?.HoldoutRate ?? 0;
        if (holdoutRate is < 0 or >= 1 || double.IsNaN(holdoutRate))
        {
            throw new AexpConfigException("injection.holdoutRate must be at least 0 and below 1.");
        }

        var recall = file.Recall;
        TimeSpan? lookupTimeout = recall?.LookupTimeoutMs is { } ms
            ? ms > 0 ? TimeSpan.FromMilliseconds(ms) : throw new AexpConfigException("recall.lookupTimeoutMs must be positive.")
            : null;

        return new AexpSettings(
            ConfigPath: configPath,
            ConfigSha256: string.Empty,
            ProjectRoot: root,
            Store: store,
            Scope: scope,
            TaskId: string.IsNullOrWhiteSpace(file.TaskId) ? "default" : file.TaskId.Trim(),
            Verifier: new VerifierSetting([.. command], timeout, checkId, workingDirectory, revision, verify.AcceptExitCode ?? false),
            AllowArguments: [.. file.Sanitization?.AllowArguments ?? []],
            Recipes: recipes,
            MaxRecords: injection?.MaxRecords,
            MaxBytes: injection?.MaxBytes,
            HoldoutRate: holdoutRate,
            HoldoutSalt: injection?.HoldoutSalt,
            RecallEnabled: recall?.Enabled ?? true,
            MaxHintsPerRun: recall?.MaxHintsPerRun,
            MaxHintBytes: recall?.MaxHintBytes,
            AllowCoarseMatch: recall?.AllowCoarseMatch ?? false,
            LookupTimeout: lookupTimeout,
            ReadOnlyTools: ClaudeCodeList(file.ClaudeCode?.ReadOnlyTools, "claudeCode.readOnlyTools"),
            ReadOnlyBashPrefixes: ClaudeCodeList(file.ClaudeCode?.ReadOnlyBashPrefixes, "claudeCode.readOnlyBashPrefixes"));
    }

    private static IReadOnlyList<string>? ClaudeCodeList(IReadOnlyList<string>? values, string name)
    {
        if (values is null)
        {
            return null;
        }

        if (values.Any(value => string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)))
        {
            throw new AexpConfigException($"{name}: every entry is a non-empty name or command prefix without control characters.");
        }

        return [.. values.Select(value => value.Trim())];
    }
}

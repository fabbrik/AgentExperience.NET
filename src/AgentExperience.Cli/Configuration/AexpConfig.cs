using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentExperience.Cli.Configuration;

/// <summary>
/// The project's <c>.aexp/config.json</c>, as written. Everything the tool trusts comes from here, never from a command
/// argument or an MCP tool call: the store, who the runs belong to, the one command it may execute, and what capture may
/// keep. Unknown properties are refused, so a typo never silently turns a setting off.
/// </summary>
internal sealed record AexpConfigFile
{
    /// <summary><c>sqlite:&lt;path&gt;</c> (default <c>sqlite:.aexp/experience.db</c>) or <c>postgres:&lt;ENV_VAR&gt;</c>.</summary>
    public string? Store { get; init; }

    /// <summary>The tenancy scope every run and lesson is kept under.</summary>
    public IdentitySection? Identity { get; init; }

    /// <summary>The task identifier runs are stored under when <c>run start</c> names none.</summary>
    public string? TaskId { get; init; }

    /// <summary>The verifier: the only command the tool runs.</summary>
    public VerifySection? Verify { get; init; }

    /// <summary>What capture keeps beyond the library's defaults.</summary>
    public SanitizationSection? Sanitization { get; init; }

    /// <summary>The recipe consents (story 28.4), per tool.</summary>
    public IReadOnlyList<RecipeSection>? Recipes { get; init; }

    /// <summary>The block's limits and the holdout.</summary>
    public InjectionSection? Injection { get; init; }

    /// <summary>Recall at the moment of failure.</summary>
    public RecallSection? Recall { get; init; }

    internal sealed record IdentitySection
    {
        public string? Tenant { get; init; }

        public string? Application { get; init; }

        public string? Project { get; init; }

        public string? Team { get; init; }

        public string? Agent { get; init; }

        public string? User { get; init; }
    }

    internal sealed record VerifySection
    {
        /// <summary>The verifier as an argument vector: the program, then its arguments. Never run through a shell.</summary>
        public IReadOnlyList<string>? Command { get; init; }

        public int? TimeoutSeconds { get; init; }

        public string? CheckId { get; init; }

        /// <summary>The working directory, relative to the project root; the root when unset.</summary>
        public string? WorkingDirectory { get; init; }

        /// <summary>The artifact revision the evidence names; <c>workspace</c> when unset.</summary>
        public string? Revision { get; init; }

        /// <summary>Whether <c>aexp finalize --exit-code</c> may report a verdict the host obtained itself (off by default).</summary>
        public bool? AcceptExitCode { get; init; }
    }

    internal sealed record SanitizationSection
    {
        /// <summary>Tool argument names capture keeps (secret-looking names and values are still redacted).</summary>
        public IReadOnlyList<string>? AllowArguments { get; init; }
    }

    internal sealed record RecipeSection
    {
        public string? Tool { get; init; }

        public IReadOnlyList<string>? Arguments { get; init; }

        public bool? ResultExcerpt { get; init; }
    }

    internal sealed record InjectionSection
    {
        public int? MaxRecords { get; init; }

        public int? MaxBytes { get; init; }

        public double? HoldoutRate { get; init; }

        public string? HoldoutSalt { get; init; }
    }

    internal sealed record RecallSection
    {
        public bool? Enabled { get; init; }

        public int? MaxHintsPerRun { get; init; }

        public int? MaxHintBytes { get; init; }

        public bool? AllowCoarseMatch { get; init; }

        public int? LookupTimeoutMs { get; init; }
    }

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}

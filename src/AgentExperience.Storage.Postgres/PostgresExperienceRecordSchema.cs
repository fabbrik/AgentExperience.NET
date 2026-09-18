using Npgsql;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// Access to the schema scripts embedded in this package, for reading a script's SQL before it runs.
/// To apply them, call <see cref="ExperienceSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/>,
/// which runs them in <see cref="ScriptNames"/> order and journals what it applied; hosts do not need
/// their own apply loop.
/// </summary>
public static class PostgresExperienceRecordSchema
{
    /// <summary>
    /// The PostgreSQL schema that holds every AgentExperience.NET table, including the migration
    /// journal <c>schema_versions</c>.
    /// </summary>
    public const string SchemaName = "agent_experience";

    /// <summary>The initial script that creates the <c>experience_records</c> table.</summary>
    public const string InitialScriptName = "0001_create_experience_records.sql";

    private const string ResourcePrefix = "AgentExperience.Storage.Postgres.Migrations.";

    /// <summary>Every embedded script name, in the order they must be applied.</summary>
    public static IReadOnlyList<string> ScriptNames { get; } = [InitialScriptName];

    /// <summary>Reads an embedded script's SQL text.</summary>
    /// <param name="scriptName">One of <see cref="ScriptNames"/>.</param>
    /// <returns>The script's SQL.</returns>
    /// <exception cref="ArgumentException"><paramref name="scriptName"/> is not an embedded script.</exception>
    public static string GetScript(string scriptName)
    {
        ArgumentNullException.ThrowIfNull(scriptName);
        if (!ScriptNames.Contains(scriptName, StringComparer.Ordinal))
        {
            throw new ArgumentException("Unknown schema script name.", nameof(scriptName));
        }

        using var stream = typeof(PostgresExperienceRecordSchema).Assembly.GetManifestResourceStream(ResourcePrefix + scriptName)
            ?? throw new InvalidOperationException($"Embedded schema script '{scriptName}' is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

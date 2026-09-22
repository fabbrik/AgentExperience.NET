using AgentExperience.Abstractions;
using Npgsql;

namespace AgentExperience.Storage.Postgres.Vectors;

/// <summary>
/// Access to the schema scripts embedded in <em>this</em> package, and the call that applies them.
/// The embedding schema is owned here rather than by <c>AgentExperience.Storage.Postgres</c> on
/// purpose: it begins with <c>CREATE EXTENSION vector</c>, which is not a trusted extension and so
/// needs a superuser (or an equivalently privileged) role. Putting it in the base adapter's script
/// list would have made that privilege a startup requirement for every host, including text-only ones
/// that never enable the vector channel at all.
/// </summary>
public static class ExperienceVectorSchema
{
    /// <summary>
    /// The script that creates the <c>vector</c> extension and the derived <c>experience_embeddings</c>
    /// table the vector retrieval channel reads. Its <c>embedding</c> column is an unconstrained
    /// <c>vector</c>: the dimension belongs to whichever model a host configured, so the
    /// dimension-specific HNSW index is created out of band by
    /// <see cref="ExperienceVectorIndexMaintenance"/> rather than by this script.
    /// </summary>
    /// <remarks>
    /// The number is this package's place in one sequence the whole family shares, so a reader can
    /// still order the entire schema at a glance even though the two packages apply their scripts
    /// separately: the base adapter owns <c>0001</c>-<c>0003</c> and <c>0005</c>
    /// (<c>experience_grants</c>), and this package owns only <c>0004</c>. A gap in either package's
    /// list is therefore expected, and neither migrator ever applies the other's scripts.
    /// </remarks>
    public const string EmbeddingsScriptName = "0004_add_experience_embeddings.sql";

    internal const string ResourcePrefix = "AgentExperience.Storage.Postgres.Vectors.Migrations.";

    /// <summary>Every embedded script name, in the order they must be applied.</summary>
    public static IReadOnlyList<string> ScriptNames { get; } = [EmbeddingsScriptName];

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

        using var stream = typeof(ExperienceVectorSchema).Assembly.GetManifestResourceStream(ResourcePrefix + scriptName)
            ?? throw new InvalidOperationException($"Embedded schema script '{scriptName}' is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// Applies this package's embedded schema scripts, journaled, exactly the way
/// <see cref="ExperienceSchemaMigrator"/> applies the base adapter's: scripts in name order, one
/// transaction per script, recorded in <c>agent_experience.schema_versions</c>, and serialized across
/// processes by the same PostgreSQL session advisory lock, so the two migrators can run concurrently
/// on one database without racing.
/// </summary>
/// <remarks>
/// <para>
/// The two migrators share a journal table but never share an entry: a journal row records DbUp's
/// script name, which is the full embedded-resource name, and this package's resources live under a
/// different prefix from the base adapter's.
/// </para>
/// <para>
/// <b>Run the base migration first.</b> <c>0004</c> declares a foreign key to
/// <c>agent_experience.experience_records</c>, so
/// <see cref="ExperienceSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/> must have
/// created that table before this call.
/// </para>
/// <para>
/// <b>Privileges.</b> The role running this needs whatever the base migration needs, plus the right
/// to <c>CREATE EXTENSION vector</c> -- pgvector is not a trusted extension, so that is ordinarily a
/// superuser (on a managed service, whichever role that provider designates). A deployment whose
/// operators install the extension out of band can run this as an ordinary role:
/// <c>CREATE EXTENSION IF NOT EXISTS vector</c> is a no-op once it exists. The index itself needs
/// <c>SELECT</c> and <c>INSERT</c>/<c>UPDATE</c> on <c>agent_experience.experience_embeddings</c> and
/// <c>SELECT</c> on <c>agent_experience.experience_records</c>.
/// </para>
/// </remarks>
public static class ExperienceVectorSchemaMigrator
{
    /// <summary>
    /// Applies every embedded embedding-schema script that this database has not recorded yet.
    /// </summary>
    /// <param name="dataSource">
    /// The host-owned data source for the database to migrate. Never disposed here. It must allow at
    /// least two concurrent connections and must not be multiplexing.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels opening the lock connection and waiting for the advisory lock. Once scripts start
    /// running, cancellation is ignored, so the run finishes and returns normally.
    /// </param>
    /// <returns>The scripts applied by this call. Empty when nothing was pending.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is <see langword="null"/>.</exception>
    /// <exception cref="ExperienceStoreException">A script failed, or the database was unreachable.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static Task<ExperienceSchemaMigrationResult> MigrateAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return ExperienceSchemaMigrator.MigrateAsync(
            dataSource,
            typeof(ExperienceVectorSchema).Assembly,
            ExperienceVectorSchema.ResourcePrefix,
            cancellationToken);
    }
}

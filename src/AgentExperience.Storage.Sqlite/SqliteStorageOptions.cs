namespace AgentExperience.Storage.Sqlite;

/// <summary>Options for <c>UseSqlite</c> and <see cref="DependencyInjection.AgentExperienceSqliteServiceCollectionExtensions.AddAgentExperienceSqliteStorage"/>.</summary>
public sealed class SqliteStorageOptions
{
    /// <summary>
    /// Whether the registration migrates the database's schema itself (<see cref="ExperienceSqliteSchema.MigrateAsync(string, CancellationToken)"/>)
    /// the first time a store is resolved. Off by default, because nothing in AgentExperience migrates a schema on its own:
    /// call <see cref="ExperienceSqliteSchema.MigrateAsync(string, CancellationToken)"/> at startup instead, or turn this on
    /// deliberately -- for a local tool whose database file is its own, say. Migrating is idempotent, so either is safe to
    /// repeat on every start.
    /// </summary>
    public bool MigrateSchemaOnFirstUse { get; set; }
}

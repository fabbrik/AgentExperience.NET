namespace AgentExperience.Storage.Sqlite;

/// <summary>Options for <c>UseSqlite</c> and <see cref="DependencyInjection.AgentExperienceSqliteServiceCollectionExtensions.AddAgentExperienceSqliteStorage"/>.</summary>
public sealed class SqliteStorageOptions
{
    /// <summary>
    /// Whether the registration migrates the database's schema itself (<see cref="ExperienceSqliteSchema.MigrateAsync(string, CancellationToken)"/>)
    /// the first time a store is resolved. Off by default, because nothing in AgentExperience migrates a schema on its own:
    /// call <see cref="ExperienceSqliteSchema.MigrateAsync(string, CancellationToken)"/> at startup instead, or turn this on
    /// deliberately -- for a local tool whose database file is its own, say. Migrating is idempotent, so either is safe to
    /// repeat on every start. With it on, the first operation migrates a missing or older schema (asynchronously, once, and
    /// again on the next operation if it failed); with it off, a store over a missing or older schema refuses with an
    /// <c>ExperienceStoreException</c> saying to migrate.
    /// </summary>
    public bool MigrateSchemaOnFirstUse { get; set; }

    /// <summary>
    /// How long saving a completed run waits for another connection's write lock before it gives up (reported, never
    /// failing the run). Short on purpose: a run is saved when it completes, and a busy file must not hold the agent's
    /// completion up. Default 2 seconds; at least 1 millisecond.
    /// </summary>
    public TimeSpan RunSaveBusyTimeout { get; set; } = TimeSpan.FromSeconds(2);
}

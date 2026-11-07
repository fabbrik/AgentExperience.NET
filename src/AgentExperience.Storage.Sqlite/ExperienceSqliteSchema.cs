using AgentExperience.Abstractions;
using Microsoft.Data.Sqlite;

namespace AgentExperience.Storage.Sqlite;

/// <summary>
/// Creates and upgrades the SQLite store's schema: a journaled, versioned set of scripts embedded in this package, applied
/// in order, each in one transaction with its journal row, so running it again applies nothing and two processes running
/// it at once apply each script once. Nothing in this package migrates on its own: call it once before the stores are used
/// (on every start is fine), or opt in with <see cref="SqliteStorageOptions.MigrateSchemaOnFirstUse"/>.
/// </summary>
/// <remarks>
/// It also puts the file in write-ahead-log mode (<c>journal_mode=WAL</c>, which the file keeps), so readers never wait
/// for the writer and the writer waits only for another writer. The journal is the table
/// <c>agent_experience_schema_versions</c>; a database whose journal names a version this package does not know (written by
/// a newer release) is refused rather than guessed at.
/// </remarks>
public static class ExperienceSqliteSchema
{
    private const string ResourcePrefix = "AgentExperience.Storage.Sqlite.Migrations.";

    /// <summary>The schema version this package writes and reads: the number of its newest script.</summary>
    public static int CurrentVersion => Scripts.Value[^1].Version;

    private static readonly Lazy<IReadOnlyList<(int Version, string Name, string Sql)>> Scripts = new(LoadScripts);

    /// <summary>
    /// The connection string the stores and <see cref="MigrateAsync(string, CancellationToken)"/> use for the database file
    /// at <paramref name="databasePath"/>: pooled, foreign keys on, the file created when missing.
    /// </summary>
    /// <param name="databasePath">The database file. Relative paths are relative to the process's current directory.</param>
    /// <returns>The connection string.</returns>
    /// <exception cref="ArgumentException"><paramref name="databasePath"/> is blank.</exception>
    public static string ConnectionStringFor(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = SqliteDatabase.BusyTimeoutMilliseconds / 1000,
        }.ToString();
    }

    /// <summary>
    /// Applies every script the database at <paramref name="databasePath"/> does not hold yet, creating the file (and its
    /// directory) when it is missing.
    /// </summary>
    /// <param name="databasePath">The database file.</param>
    /// <param name="cancellationToken">Cancels the migration between scripts; a script in flight rolls back.</param>
    /// <returns>The number of scripts applied: 0 when the database was already current.</returns>
    /// <exception cref="ArgumentException"><paramref name="databasePath"/> is blank.</exception>
    /// <exception cref="ExperienceStoreException">The database could not be migrated, or was written by a newer release.</exception>
    public static Task<int> MigrateAsync(string databasePath, CancellationToken cancellationToken = default) =>
        MigrateConnectionStringAsync(ConnectionStringFor(databasePath), cancellationToken);

    /// <summary>
    /// Applies every script the database <paramref name="connection"/> is open on does not hold yet. The connection is
    /// opened when it is closed, and left as it was found.
    /// </summary>
    /// <param name="connection">A connection to the database to migrate.</param>
    /// <param name="cancellationToken">Cancels the migration between scripts; a script in flight rolls back.</param>
    /// <returns>The number of scripts applied: 0 when the database was already current.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
    /// <exception cref="ExperienceStoreException">The database could not be migrated, or was written by a newer release.</exception>
    public static async Task<int> MigrateAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var opened = false;
        try
        {
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                opened = true;
            }

            return await MigrateOpenAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            throw new ExperienceStoreException("The SQLite experience schema could not be migrated.", ex);
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Migrates the database a store's connection string names, creating its file (and directory) owner-only first.</summary>
    internal static async Task<int> MigrateConnectionStringAsync(string connectionString, CancellationToken cancellationToken)
    {
        var database = new SqliteDatabase(connectionString);
        CreateOwnerOnly(database.DataSource);
        try
        {
            var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                return await MigrateOpenAsync(connection, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (SqliteException ex)
        {
            throw new ExperienceStoreException("The SQLite experience schema could not be migrated.", ex);
        }
    }

    /// <summary>
    /// Creates a missing database file before SQLite opens it, so it is never created with the process's default
    /// permissions: on Unix, a missing directory is created <c>0700</c> and the file <c>0600</c> (owner read and write
    /// only). SQLite gives the <c>-wal</c> and <c>-shm</c> files the database file's own permissions. An existing file or
    /// directory is left as it is. URIs and in-memory names are not files and are left to SQLite.
    /// </summary>
    internal static void CreateOwnerOnly(string file)
    {
        if (file.StartsWith(':') || file.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var path = Path.GetFullPath(file);
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory && !Directory.Exists(directory))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        if (File.Exists(path))
        {
            return;
        }

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using var _ = new FileStream(path, options);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another migrator created it first.
        }
    }

    /// <summary>The versions the database's journal holds, ascending; empty for a database never migrated.</summary>
    /// <param name="databasePath">The database file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The applied versions; empty for a missing file or one never migrated.</returns>
    public static async Task<IReadOnlyList<int>> AppliedVersionsAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        var database = new SqliteDatabase(ConnectionStringFor(databasePath));
        if (!File.Exists(database.DataSource))
        {
            return [];
        }

        try
        {
            var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                return await ReadJournalAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (SqliteException ex)
        {
            throw new ExperienceStoreException("The SQLite experience schema's journal could not be read.", ex);
        }
    }

    internal static async Task<int> MigrateOpenAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // Outside any transaction: journal_mode cannot change inside one. The file keeps it.
        await SqliteDatabase.ExecuteAsync(connection, null, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
        await SqliteDatabase.ExecuteAsync(
            connection,
            null,
            $"PRAGMA busy_timeout = {SqliteDatabase.BusyTimeoutMilliseconds}; PRAGMA foreign_keys = ON;",
            cancellationToken).ConfigureAwait(false);

        var applied = 0;
        foreach (var script in Scripts.Value)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // One script per transaction, the journal read again under the write lock, so a concurrent migrator that got
            // there first is seen and the script is not applied twice.
            using var transaction = connection.BeginTransaction(deferred: false);
            await SqliteDatabase.ExecuteAsync(
                connection,
                transaction,
                "CREATE TABLE IF NOT EXISTS agent_experience_schema_versions ("
                + "version INTEGER NOT NULL PRIMARY KEY, script TEXT NOT NULL, applied_at INTEGER NOT NULL)",
                cancellationToken).ConfigureAwait(false);

            var journal = await ReadJournalAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (journal.Count > 0 && journal[^1] > CurrentVersion)
            {
                throw new ExperienceStoreException(
                    $"The SQLite experience database is at schema version {journal[^1]}, newer than this package's {CurrentVersion}. Upgrade the package.");
            }

            if (journal.Contains(script.Version))
            {
                transaction.Rollback();
                continue;
            }

            await SqliteDatabase.ExecuteAsync(connection, transaction, script.Sql, cancellationToken).ConfigureAwait(false);
            await SqliteDatabase.ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO agent_experience_schema_versions (version, script, applied_at) VALUES ($version, $script, $applied_at)",
                cancellationToken,
                ("$version", script.Version),
                ("$script", script.Name),
                ("$applied_at", DateTimeOffset.UtcNow.UtcTicks)).ConfigureAwait(false);
            transaction.Commit();
            applied++;
        }

        return applied;
    }

    internal static async Task<IReadOnlyList<int>> ReadJournalAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        var exists = await SqliteDatabase.ScalarAsync(
            connection,
            transaction,
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'agent_experience_schema_versions'",
            cancellationToken).ConfigureAwait(false);
        if (exists is null)
        {
            return [];
        }

        using var command = SqliteDatabase.Command(connection, transaction, "SELECT version FROM agent_experience_schema_versions ORDER BY version");
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var versions = new List<int>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }

    private static IReadOnlyList<(int Version, string Name, string Sql)> LoadScripts()
    {
        var assembly = typeof(ExperienceSqliteSchema).Assembly;
        var scripts = new List<(int, string, string)>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var name = resource[ResourcePrefix.Length..];
            var version = int.Parse(name[..4], System.Globalization.CultureInfo.InvariantCulture);
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"The embedded migration '{name}' is missing.");
            using var reader = new StreamReader(stream);
            scripts.Add((version, name, reader.ReadToEnd()));
        }

        return scripts;
    }
}

using AgentExperience.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace AgentExperience.Storage.Sqlite.Tests;

/// <summary>A migrated database file of its own in the temporary directory, deleted on dispose.</summary>
public sealed class SqliteTestDatabase : IDisposable
{
    public SqliteTestDatabase()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"agentexperience-sqlite-{Guid.NewGuid():N}.db");
        ExperienceSqliteSchema.MigrateAsync(Path).GetAwaiter().GetResult();
    }

    /// <summary>The database file.</summary>
    public string Path { get; }

    /// <summary>The stores' connection string for it.</summary>
    public string ConnectionString => ExperienceSqliteSchema.ConnectionStringFor(Path);

    public void Dispose() => Delete(Path);

    /// <summary>Closes this file's idle pooled connections, the stores' and the migrator's (only this file's).</summary>
    public static void ReleasePool(string path)
    {
        foreach (var connectionString in new[]
        {
            new SqliteDatabase(ExperienceSqliteSchema.ConnectionStringFor(path)).ConnectionString,
            ExperienceSqliteSchema.ConnectionStringFor(path),
        })
        {
            using var connection = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(connection);
        }
    }

    /// <summary>
    /// Releases this file's pooled connections -- only this file's: clearing every pool would close connections other
    /// tests running in parallel are using -- and deletes it with its write-ahead log.
    /// </summary>
    public static void Delete(string path)
    {
        ReleasePool(path);

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                File.Delete(path + suffix);
            }
            catch (IOException)
            {
                // Best effort: a temporary file left behind is harmless.
            }
        }
    }
}

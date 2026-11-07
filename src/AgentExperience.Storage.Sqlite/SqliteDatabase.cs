using System.Text.Json;
using System.Text.Json.Serialization;
using AgentExperience.Abstractions;
using Microsoft.Data.Sqlite;

namespace AgentExperience.Storage.Sqlite;

/// <summary>
/// One SQLite database file as the stores use it: a fresh pooled connection per operation, opened with foreign keys on
/// and a busy timeout, so two connections (two stores, two processes) writing the same file wait for each other instead
/// of failing. Every infrastructure failure is wrapped in <see cref="ExperienceStoreException"/>, carrying no content;
/// cancellation propagates unwrapped.
/// </summary>
internal sealed class SqliteDatabase
{
    /// <summary>How long a statement waits for another connection's write lock before failing, in milliseconds.</summary>
    internal const int BusyTimeoutMilliseconds = 30_000;

    private readonly string _connectionString;
    private readonly bool _migrateOnFirstUse;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);

    // Set once the schema was found current (or migrated to current), and never again checked: a success only.
    private volatile bool _schemaReady;

    /// <param name="connectionString">The database's connection string.</param>
    /// <param name="migrateOnFirstUse">
    /// Whether the first operation migrates a missing or older schema instead of refusing it (the registration's opt-in).
    /// </param>
    public SqliteDatabase(string connectionString, bool migrateOnFirstUse = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        SqliteConnectionStringBuilder builder;
        try
        {
            builder = new SqliteConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("The SQLite connection string is malformed.", nameof(connectionString), ex);
        }

        if (string.IsNullOrWhiteSpace(builder.DataSource))
        {
            throw new ArgumentException("The SQLite connection string must name a database file (Data Source).", nameof(connectionString));
        }

        builder.ForeignKeys = true;
        if (builder.DefaultTimeout < BusyTimeoutMilliseconds / 1000)
        {
            builder.DefaultTimeout = BusyTimeoutMilliseconds / 1000;
        }

        // A store never creates the file: the migrator does, with owner-only permissions. A store over a missing file
        // fails on its schema check with a clear error instead of leaving an empty, world-readable file behind.
        if (builder.Mode == SqliteOpenMode.ReadWriteCreate)
        {
            builder.Mode = SqliteOpenMode.ReadWrite;
        }

        _connectionString = builder.ToString();
        _migrateOnFirstUse = migrateOnFirstUse;
    }

    /// <summary>The normalized connection string every connection is opened with.</summary>
    public string ConnectionString => _connectionString;

    /// <summary>The database file this connection string names.</summary>
    public string DataSource => new SqliteConnectionStringBuilder(_connectionString).DataSource;

    /// <summary>
    /// Opens a connection with the store's pragmas applied: foreign keys on, <c>secure_delete</c> on (freed content is
    /// overwritten with zeros, so erased text does not linger in free pages), and a busy timeout -- for the statements'
    /// own retry and for SQLite's -- of <paramref name="busyTimeoutMilliseconds"/>.
    /// </summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken, int busyTimeoutMilliseconds = BusyTimeoutMilliseconds)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            connection.DefaultTimeout = Math.Max(1, (busyTimeoutMilliseconds + 999) / 1000);
            using var pragma = connection.CreateCommand();
            pragma.CommandText =
                $"PRAGMA busy_timeout = {busyTimeoutMilliseconds}; PRAGMA foreign_keys = ON; PRAGMA secure_delete = ON;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a fresh connection, wrapping every failure but cancellation in
    /// <see cref="ExperienceStoreException"/> with the content-free <paramref name="failure"/> message. The first call on
    /// this instance checks the schema version first (and migrates it, with the opt-in).
    /// </summary>
    public async Task<T> RunAsync<T>(
        string failure,
        Func<SqliteConnection, Task<T>> work,
        CancellationToken cancellationToken,
        int busyTimeoutMilliseconds = BusyTimeoutMilliseconds)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var connection = await OpenAsync(cancellationToken, busyTimeoutMilliseconds).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
                return await work(connection).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ExperienceStoreException)
        {
            throw;
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or InvalidOperationException or FormatException or InvalidCastException)
        {
            throw new ExperienceStoreException(failure, ex);
        }
    }

    /// <summary>
    /// Once per instance, and only on success: the journal must name exactly <see cref="ExperienceSqliteSchema.CurrentVersion"/>.
    /// A missing or older schema is migrated with the opt-in and refused without it; a newer one is always refused.
    /// </summary>
    private async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (_schemaReady)
        {
            return;
        }

        await _schemaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_schemaReady)
            {
                return;
            }

            var applied = await ExperienceSqliteSchema.ReadJournalAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false);
            var version = applied.Count == 0 ? 0 : applied[^1];
            if (version > ExperienceSqliteSchema.CurrentVersion)
            {
                throw new ExperienceStoreException(
                    $"The SQLite experience database is at schema version {version}, newer than this package's {ExperienceSqliteSchema.CurrentVersion}. Upgrade the package.");
            }

            if (version < ExperienceSqliteSchema.CurrentVersion)
            {
                if (!_migrateOnFirstUse)
                {
                    throw new ExperienceStoreException(version == 0
                        ? "The SQLite experience schema is missing: run ExperienceSqliteSchema.MigrateAsync on the file first (or opt in with SqliteStorageOptions.MigrateSchemaOnFirstUse)."
                        : $"The SQLite experience database is at schema version {version}, older than this package's {ExperienceSqliteSchema.CurrentVersion}: run ExperienceSqliteSchema.MigrateAsync.");
                }

                await ExperienceSqliteSchema.MigrateOpenAsync(connection, cancellationToken).ConfigureAwait(false);
            }

            _schemaReady = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one write transaction (<c>BEGIN IMMEDIATE</c>: the write lock is taken up front, so
    /// what the transaction reads cannot change before it writes), committing when it returns and rolling back when it
    /// throws or its result says not to.
    /// </summary>
    public Task<T> WriteAsync<T>(
        string failure,
        Func<SqliteConnection, SqliteTransaction, Task<(T Result, bool Commit)>> work,
        CancellationToken cancellationToken,
        int busyTimeoutMilliseconds = BusyTimeoutMilliseconds) =>
        RunAsync(
            failure,
            async connection =>
            {
                using var transaction = connection.BeginTransaction(deferred: false);
                var (result, commit) = await work(connection, transaction).ConfigureAwait(false);
                if (commit)
                {
                    transaction.Commit();
                }
                else
                {
                    transaction.Rollback();
                }

                return result;
            },
            cancellationToken,
            busyTimeoutMilliseconds);

    /// <summary>A command on <paramref name="connection"/>, in <paramref name="transaction"/> when one is given.</summary>
    public static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    /// <summary>Executes a statement and returns the rows it changed.</summary>
    public static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Executes a query and returns its first column of its first row, or <see langword="null"/>.</summary>
    public static async Task<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is DBNull ? null : value;
    }

    // ---------------------------------------------------------------- values

    /// <summary>The scope columns' predicate, exact: ordinal text equality, a null field matching only null.</summary>
    public const string ScopePredicate =
        "tenant_id = $tenant_id AND application_id = $application_id AND project_id = $project_id "
        + "AND team_id IS $team_id AND agent_id IS $agent_id AND user_id IS $user_id";

    /// <summary>The scope columns, as the parameters <see cref="ScopePredicate"/> and the inserts read.</summary>
    public static (string, object?)[] ScopeParameters(Scope scope) =>
    [
        ("$tenant_id", scope.TenantId),
        ("$application_id", scope.ApplicationId),
        ("$project_id", scope.ProjectId),
        ("$team_id", scope.TeamId),
        ("$agent_id", scope.AgentId),
        ("$user_id", scope.UserId),
    ];

    /// <summary>The scope a row's six scope columns, starting at <paramref name="first"/>, name.</summary>
    public static Scope ReadScope(SqliteDataReader reader, int first) => new(
        reader.GetString(first),
        reader.GetString(first + 1),
        reader.GetString(first + 2),
        reader.IsDBNull(first + 3) ? null : reader.GetString(first + 3),
        reader.IsDBNull(first + 4) ? null : reader.GetString(first + 4),
        reader.IsDBNull(first + 5) ? null : reader.GetString(first + 5));

    /// <summary>The six scope column names, in the order <see cref="ReadScope"/> reads them.</summary>
    public const string ScopeColumns = "tenant_id, application_id, project_id, team_id, agent_id, user_id";

    /// <summary>The six scope parameter names, in the order <see cref="ScopeColumns"/> lists the columns.</summary>
    public const string ScopeValues = "$tenant_id, $application_id, $project_id, $team_id, $agent_id, $user_id";

    /// <summary>UTC, truncated to whole microseconds: what PostgreSQL's <c>timestamptz</c> keeps, and what this store keeps.</summary>
    public static DateTimeOffset Micro(DateTimeOffset value)
    {
        var ticks = value.UtcTicks;
        return new DateTimeOffset(ticks - (ticks % 10), TimeSpan.Zero);
    }

    /// <summary>An instant as stored: UTC ticks, truncated to whole microseconds.</summary>
    public static long Ticks(DateTimeOffset value) => Micro(value).UtcTicks;

    /// <summary>A stored instant back as a UTC <see cref="DateTimeOffset"/>.</summary>
    public static DateTimeOffset Instant(long ticks) => new(ticks, TimeSpan.Zero);

    /// <summary>A GUID as stored: its lowercase <c>D</c> form, whose text order is <see cref="Guid.CompareTo(Guid)"/>'s order.</summary>
    public static string Id(Guid value) => value.ToString("D");

    /// <summary>A stored GUID.</summary>
    public static Guid ReadId(SqliteDataReader reader, int ordinal) => Guid.Parse(reader.GetString(ordinal));

    /// <summary>A stored nullable GUID.</summary>
    public static Guid? ReadOptionalId(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));

    /// <summary>The JSON this store writes for the values it keeps whole (events, evidence, feedback, runs).</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    /// <summary>A value as this store's JSON.</summary>
    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Json);

    /// <summary>A value back from this store's JSON.</summary>
    public static T FromJson<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Json) ?? throw new ExperienceStoreException("A stored value is null.");
}

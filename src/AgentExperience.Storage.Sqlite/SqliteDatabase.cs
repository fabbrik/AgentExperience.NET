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

    public SqliteDatabase(string connectionString)
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

        _connectionString = builder.ToString();
    }

    /// <summary>The normalized connection string every connection is opened with.</summary>
    public string ConnectionString => _connectionString;

    /// <summary>Opens a connection with the store's pragmas applied.</summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var pragma = connection.CreateCommand();
            pragma.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds}; PRAGMA foreign_keys = ON;";
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
    /// <see cref="ExperienceStoreException"/> with the content-free <paramref name="failure"/> message.
    /// </summary>
    public async Task<T> RunAsync<T>(string failure, Func<SqliteConnection, Task<T>> work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
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
    /// Runs <paramref name="work"/> in one write transaction (<c>BEGIN IMMEDIATE</c>: the write lock is taken up front, so
    /// what the transaction reads cannot change before it writes), committing when it returns and rolling back when it
    /// throws or its result says not to.
    /// </summary>
    public Task<T> WriteAsync<T>(
        string failure,
        Func<SqliteConnection, SqliteTransaction, Task<(T Result, bool Commit)>> work,
        CancellationToken cancellationToken) =>
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
            cancellationToken);

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

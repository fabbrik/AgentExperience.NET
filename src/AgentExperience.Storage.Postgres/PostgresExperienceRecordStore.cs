using System.Data.Common;
using System.Net.Sockets;
using AgentExperience.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// <see cref="IExperienceRecordStore"/> over PostgreSQL with plain Npgsql. Each operation validates
/// the request, checks it against the host-established <see cref="AuthorizationContext"/>, and only
/// then opens a connection and runs parameterized SQL whose predicates apply the exact scope. The
/// schema must already exist: the host applies it once by calling
/// <see cref="ExperienceSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/>. The store
/// never migrates, on construction or otherwise.
/// </summary>
/// <remarks>
/// PostgreSQL <c>timestamptz</c> stores microseconds, so <see cref="ExperienceRecord.CreatedAt"/> and
/// <see cref="ExperienceRecord.UpdatedAt"/> are truncated to whole microseconds (in UTC) on write.
/// Nested timestamps live in the JSONB payload at full precision and are also returned in UTC.
/// Tool-call argument values read back JSON-normalized: <see cref="string"/>, <see cref="bool"/>,
/// <see cref="long"/>, <see cref="double"/>, <see langword="null"/>,
/// <see cref="Dictionary{TKey,TValue}"/> of <see cref="string"/> to <see cref="object"/>, and
/// <see cref="List{T}"/> of <see cref="object"/>. Dictionary key order is not preserved, and whole-number
/// doubles read back as <see cref="long"/>. Query ties on <c>CreatedAt</c> are broken by PostgreSQL <c>uuid</c>
/// byte order, which differs from .NET <see cref="Guid"/> comparison.
/// </remarks>
public sealed class PostgresExperienceRecordStore : IExperienceRecordStore
{
    private const string Table = "agent_experience.experience_records";

    private const string SelectColumns =
        "experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, " +
        "status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, " +
        "payload_version, payload";

    private const string ScopePredicate =
        "tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id " +
        "AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id " +
        "AND user_id IS NOT DISTINCT FROM @user_id";

    private const string InsertSql =
        $"INSERT INTO {Table} ({SelectColumns}) VALUES (@experience_id, @source_run_id, @tenant_id, @application_id, " +
        "@project_id, @team_id, @agent_id, @user_id, @task_id, @status, @reuse_confidence, @supporting_validations, " +
        "@contradictions, @revision, @created_at, @updated_at, @payload_version, @payload)";

    private const string GetSql =
        $"SELECT {SelectColumns} FROM {Table} WHERE experience_id = @experience_id AND {ScopePredicate}";

    private const string QuerySql = $"SELECT {SelectColumns} FROM {Table} WHERE {ScopePredicate}";

    private const string QueryStatusPredicate = " AND status = ANY(@statuses)";

    private const string QueryOrderAndLimit = " ORDER BY created_at DESC, experience_id LIMIT @limit";

    private static readonly IReadOnlyList<StoreValidationError> NoErrors = [];

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates a store over a host-owned data source. The store never disposes it.</summary>
    /// <param name="dataSource">The Npgsql data source to open connections from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is <see langword="null"/>.</exception>
    public PostgresExperienceRecordStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public async Task<ExperienceRecordCreateResult> CreateAsync(
        AuthorizationContext authorization,
        ExperienceRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(record);

        var errors = ExperienceRecordValidator.ValidateRecord(record);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, errors);
        }

        if (!authorization.Permits(record.Scope))
        {
            return new(ExperienceStoreOutcome.Denied, NoErrors);
        }

        string payload;
        try
        {
            payload = ExperiencePayload.Serialize(record);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any serialization failure (non-finite doubles, invalid UTF-16, cycles, throwing getters)
            // is a malformed request, never an infrastructure failure.
            return new(
                ExperienceStoreOutcome.Invalid,
                [new StoreValidationError("Attempts", "tool-call arguments could not be serialized to JSON.")]);
        }

        if (ContainsEscapedNul(payload))
        {
            return new(
                ExperienceStoreOutcome.Invalid,
                [new StoreValidationError("Payload", "must not contain the NUL character (U+0000), which PostgreSQL jsonb cannot store.")]);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var command = _dataSource.CreateCommand(InsertSql);
            var parameters = command.Parameters;
            parameters.Add(new NpgsqlParameter<Guid>("experience_id", record.ExperienceId));
            parameters.Add(new NpgsqlParameter<Guid>("source_run_id", record.SourceRunId));
            AddScopeParameters(parameters, record.Scope);
            parameters.Add(new NpgsqlParameter<string>("task_id", record.TaskId));
            parameters.Add(new NpgsqlParameter<string>("status", record.Status.ToString()));
            parameters.Add(new NpgsqlParameter<double>("reuse_confidence", record.ReuseConfidence));
            parameters.Add(new NpgsqlParameter<int>("supporting_validations", record.SupportingValidations));
            parameters.Add(new NpgsqlParameter<int>("contradictions", record.Contradictions));
            parameters.Add(new NpgsqlParameter<long>("revision", record.Revision));
            parameters.Add(new NpgsqlParameter<DateTimeOffset>("created_at", ToStoredTimestamp(record.CreatedAt)));
            parameters.Add(new NpgsqlParameter<DateTimeOffset>("updated_at", ToStoredTimestamp(record.UpdatedAt)));
            parameters.Add(new NpgsqlParameter<int>("payload_version", ExperiencePayload.CurrentVersion));
            parameters.Add(new NpgsqlParameter<string>("payload", NpgsqlDbType.Jsonb) { TypedValue = payload });

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new(ExperienceStoreOutcome.Created, NoErrors);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && !cancellationToken.IsCancellationRequested)
        {
            // Identical regardless of which scope owns the existing ID: no record data is revealed.
            return new(ExperienceStoreOutcome.Conflict, NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "create", cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<ExperienceRecordGetResult> GetAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);

        var errors = ExperienceRecordValidator.ValidateGet(scope, experienceId);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, null, errors);
        }

        if (!authorization.Permits(scope))
        {
            return new(ExperienceStoreOutcome.Denied, null, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var command = _dataSource.CreateCommand(GetSql);
            command.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
            AddScopeParameters(command.Parameters, scope);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new(ExperienceStoreOutcome.NotFound, null, NoErrors);
            }

            return new(ExperienceStoreOutcome.Found, ReadRecord(reader), NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "get", cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<ExperienceRecordQueryResult> QueryAsync(
        AuthorizationContext authorization,
        ExperienceRecordQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(query);

        var errors = ExperienceRecordValidator.ValidateQuery(query);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, [], errors);
        }

        if (!authorization.Permits(query.Scope))
        {
            return new(ExperienceStoreOutcome.Denied, [], NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var sql = query.Statuses is null
                ? QuerySql + QueryOrderAndLimit
                : QuerySql + QueryStatusPredicate + QueryOrderAndLimit;

            await using var command = _dataSource.CreateCommand(sql);
            AddScopeParameters(command.Parameters, query.Scope);
            if (query.Statuses is not null)
            {
                var statuses = query.Statuses.Distinct().Select(s => s.ToString()).ToArray();
                command.Parameters.Add(new NpgsqlParameter<string[]>("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = statuses });
            }

            command.Parameters.Add(new NpgsqlParameter<int>("limit", query.Limit));

            var records = new List<ExperienceRecord>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                records.Add(ReadRecord(reader));
            }

            return new(ExperienceStoreOutcome.Found, records, NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "query", cancellationToken);
        }
    }

    private static void AddScopeParameters(NpgsqlParameterCollection parameters, Scope scope)
    {
        parameters.Add(new NpgsqlParameter<string>("tenant_id", NpgsqlDbType.Text) { TypedValue = scope.TenantId });
        parameters.Add(new NpgsqlParameter<string>("application_id", NpgsqlDbType.Text) { TypedValue = scope.ApplicationId });
        parameters.Add(new NpgsqlParameter<string>("project_id", NpgsqlDbType.Text) { TypedValue = scope.ProjectId });
        parameters.Add(NullableText("team_id", scope.TeamId));
        parameters.Add(NullableText("agent_id", scope.AgentId));
        parameters.Add(NullableText("user_id", scope.UserId));
    }

    private static NpgsqlParameter NullableText(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = value is null ? DBNull.Value : value };

    private static DateTimeOffset ToStoredTimestamp(DateTimeOffset value)
    {
        var utcTicks = value.UtcTicks;
        return new DateTimeOffset(utcTicks - (utcTicks % 10), TimeSpan.Zero);
    }

    /// <summary>
    /// Finds a JSON <c>\u0000</c> escape whose backslash is not itself escaped (an odd run of backslashes).
    /// </summary>
    private static bool ContainsEscapedNul(string json)
    {
        const string Escape = "\\u0000";
        for (var index = json.IndexOf(Escape, StringComparison.Ordinal); index >= 0; index = json.IndexOf(Escape, index + 1, StringComparison.Ordinal))
        {
            var backslashes = 0;
            for (var i = index; i >= 0 && json[i] == '\\'; i--)
            {
                backslashes++;
            }

            if (backslashes % 2 == 1)
            {
                return true;
            }
        }

        return false;
    }

    private static ExperienceRecord ReadRecord(DbDataReader reader)
    {
        try
        {
            return DecodeRecord(reader);
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            // Schema drift or a corrupt payload (e.g. InvalidCastException, a null array element).
            throw new ExperienceStoreException("Stored Experience Record could not be decoded.", ex);
        }
    }

    private static ExperienceRecord DecodeRecord(DbDataReader reader)
    {
        var statusText = reader.GetString(9);
        if (!Enum.TryParse<ExperienceStatus>(statusText, ignoreCase: false, out var status) || !Enum.IsDefined(status)
            || !string.Equals(status.ToString(), statusText, StringComparison.Ordinal))
        {
            throw new ExperienceStoreException("Stored Experience Record has an unrecognized status.");
        }

        var payload = ExperiencePayload.Deserialize(reader.GetInt32(16), reader.GetString(17));

        var scope = new Scope(
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));

        return ExperiencePayload.ToRecord(
            payload,
            reader.GetGuid(0),
            reader.GetGuid(1),
            scope,
            reader.GetString(8),
            status,
            reader.GetDouble(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            reader.GetInt64(13),
            reader.GetFieldValue<DateTimeOffset>(14),
            reader.GetFieldValue<DateTimeOffset>(15));
    }

    /// <summary>
    /// Driver, socket, and timeout failures are translated. An <see cref="OperationCanceledException"/>
    /// caused by the caller's own token is not matched, so it propagates unwrapped with its stack.
    /// </summary>
    private static bool IsInfrastructureFailure(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        NpgsqlException or SocketException or TimeoutException => true,
        _ => false,
    };

    private static Exception Translate(Exception ex, string operation, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled while the driver reported a failure: surface cancellation, unwrapped.
            return new OperationCanceledException("The Experience Record store operation was cancelled.", ex, cancellationToken);
        }

        return new ExperienceStoreException($"Experience Record {operation} failed due to a storage infrastructure error.", ex);
    }
}

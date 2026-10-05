using System.Data;
using AgentExperience.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// Declares an operation's authorization bounds to PostgreSQL for the length of its transaction, which is what
/// <c>0019</c>'s row-level security policies read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every store operation declares, whether or not row-level security is on.</b> A store cannot know, and must
/// not need to know, whether the owner switched the policies on: with them off the settings are simply never
/// read, and the operation's SQL is exactly what it was, inside a transaction. With them on, a statement that
/// runs without a declaration sees and changes nothing -- the policies admit a row only while
/// <c>agent_experience.auth_set</c> is <c>on</c>.
/// </para>
/// <para>
/// <b>Transaction-local, never session-level.</b> Each value is set with <c>set_config(..., true)</c>, so it
/// ends with the transaction that set it, committed or rolled back. A pooled connection handed to the next
/// caller -- whatever its reset settings -- carries nothing forward.
/// </para>
/// <para>
/// <b>The values are the host's <see cref="AuthorizationContext"/> bounds, never the request's scope.</b> A
/// non-null bound is sent as <c>=</c> followed by the value; a null bound -- unrestricted, exactly as
/// <see cref="AuthorizationContext.Permits"/> reads it -- is sent as the empty string. The prefix is what keeps
/// an empty-string bound, which restricts to a value no row can hold, from ever reading as "no bound". The
/// tenant is always a bound. The values travel as parameters and are never part of the statement text.
/// </para>
/// <para>
/// <b>This is not a privilege boundary against the application role.</b> <c>set_config</c> is open to every
/// session, so a host able to run arbitrary SQL as the application role can declare whatever bounds it likes.
/// The policies guard against a mistake in a store's own SQL, not against a compromised application role; see
/// docs/guide/deployment.md, Enabling row-level security.
/// </para>
/// </remarks>
internal static class ExperienceSessionContext
{
    /// <summary>The marker the policies require before they admit anything.</summary>
    internal const string MarkerSetting = "agent_experience.auth_set";

    /// <summary>The prefix of every bound setting; the field name follows it.</summary>
    internal const string BoundSettingPrefix = "agent_experience.auth_";

    /// <summary>
    /// One statement, one round trip: the six bounds and the marker, each transaction-local. The setting names
    /// are constants; only the values are parameters.
    /// </summary>
    internal const string DeclareSql =
        "SELECT pg_catalog.set_config('agent_experience.auth_tenant', @auth_tenant, true), " +
        "pg_catalog.set_config('agent_experience.auth_application', @auth_application, true), " +
        "pg_catalog.set_config('agent_experience.auth_project', @auth_project, true), " +
        "pg_catalog.set_config('agent_experience.auth_team', @auth_team, true), " +
        "pg_catalog.set_config('agent_experience.auth_agent', @auth_agent, true), " +
        "pg_catalog.set_config('agent_experience.auth_user', @auth_user, true), " +
        "pg_catalog.set_config('agent_experience.auth_set', 'on', true)";

    /// <summary>The operation's bounds: the tenant always, each other field only when it is restricted.</summary>
    /// <param name="TenantId">The tenant. Always a bound.</param>
    /// <param name="ApplicationId">The application bound, or <see langword="null"/> for none.</param>
    /// <param name="ProjectId">The project bound, or <see langword="null"/> for none.</param>
    /// <param name="TeamId">The team bound, or <see langword="null"/> for none.</param>
    /// <param name="AgentId">The agent bound, or <see langword="null"/> for none.</param>
    /// <param name="UserId">The user bound, or <see langword="null"/> for none.</param>
    internal readonly record struct Bounds(
        string TenantId,
        string? ApplicationId,
        string? ProjectId,
        string? TeamId,
        string? AgentId,
        string? UserId)
    {
        /// <summary>The host-established authorization's own bounds.</summary>
        internal static Bounds Of(AuthorizationContext authorization) => new(
            authorization.TenantId,
            authorization.ApplicationId,
            authorization.ProjectId,
            authorization.TeamId,
            authorization.AgentId,
            authorization.UserId);

        /// <summary>
        /// Bounds pinned to one scope's fields: each field the scope carries is a bound, and each it leaves
        /// null is unrestricted. For the one write that is handed no <see cref="AuthorizationContext"/> -- the
        /// grant access log's append, whose rows already name the scope that read. A null field leaves that field
        /// unrestricted, so this can be wider than the rows; the insert policy's exact-match live-grant check is
        /// what bounds them.
        /// </summary>
        internal static Bounds Of(Scope scope) => new(
            scope.TenantId,
            scope.ApplicationId,
            scope.ProjectId,
            scope.TeamId,
            scope.AgentId,
            scope.UserId);
    }

    /// <summary>Declares <paramref name="authorization"/>'s bounds inside <paramref name="transaction"/>.</summary>
    internal static Task DeclareAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuthorizationContext authorization,
        CancellationToken cancellationToken) =>
        DeclareAsync(connection, transaction, Bounds.Of(authorization), cancellationToken);

    /// <summary>Declares <paramref name="bounds"/> inside <paramref name="transaction"/>.</summary>
    internal static async Task DeclareAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Bounds bounds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        await using var command = new NpgsqlCommand(DeclareSql, connection, transaction);
        var parameters = command.Parameters;
        parameters.Add(Setting("auth_tenant", Required(bounds.TenantId)));
        parameters.Add(Setting("auth_application", Optional(bounds.ApplicationId)));
        parameters.Add(Setting("auth_project", Optional(bounds.ProjectId)));
        parameters.Add(Setting("auth_team", Optional(bounds.TeamId)));
        parameters.Add(Setting("auth_agent", Optional(bounds.AgentId)));
        parameters.Add(Setting("auth_user", Optional(bounds.UserId)));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Begins a transaction on <paramref name="connection"/> and declares <paramref name="authorization"/>'s
    /// bounds in it before anything else runs.
    /// </summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="authorization">The host-established authorization whose bounds are declared.</param>
    /// <param name="cancellationToken">Cancels the declaration.</param>
    /// <param name="isolation">
    /// The isolation level, or <see cref="IsolationLevel.Unspecified"/> for the server's default -- which is what
    /// an operation that was a single autocommitted statement ran under before it was wrapped.
    /// </param>
    internal static Task<NpgsqlTransaction> BeginAsync(
        NpgsqlConnection connection,
        AuthorizationContext authorization,
        CancellationToken cancellationToken,
        IsolationLevel isolation = IsolationLevel.Unspecified) =>
        BeginAsync(connection, Bounds.Of(authorization), cancellationToken, isolation);

    /// <summary>Begins a transaction and declares <paramref name="bounds"/> in it.</summary>
    internal static async Task<NpgsqlTransaction> BeginAsync(
        NpgsqlConnection connection,
        Bounds bounds,
        CancellationToken cancellationToken,
        IsolationLevel isolation = IsolationLevel.Unspecified)
    {
        var transaction = await connection.BeginTransactionAsync(isolation, cancellationToken).ConfigureAwait(false);
        try
        {
            await DeclareAsync(connection, transaction, bounds, cancellationToken).ConfigureAwait(false);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> in a transaction of its own on <paramref name="connection"/>, with
    /// <paramref name="authorization"/>'s bounds declared, and commits it. For the reads a store makes on a
    /// connection it already holds, outside -- or after -- its main transaction.
    /// </summary>
    internal static async Task<T> RunAsync<T>(
        NpgsqlConnection connection,
        AuthorizationContext authorization,
        Func<NpgsqlTransaction, Task<T>> body,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginAsync(connection, authorization, cancellationToken).ConfigureAwait(false);
        var result = await body(transaction).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>A required bound: always prefixed, so it is never read as "no bound".</summary>
    private static string Required(string value) => "=" + value;

    /// <summary>An optional bound: the empty string for none, the prefixed value otherwise.</summary>
    private static string Optional(string? value) => value is null ? string.Empty : "=" + value;

    private static NpgsqlParameter<string> Setting(string name, string value) =>
        new(name, NpgsqlDbType.Text) { TypedValue = value };
}

/// <summary>
/// One connection and one transaction, with the operation's bounds already declared: what every store operation
/// that used to be a single autocommitted statement now runs in. Disposing it without
/// <see cref="CommitAsync"/> rolls the transaction back.
/// </summary>
internal sealed class AuthorizedTransaction : IAsyncDisposable
{
    private AuthorizedTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }

    /// <summary>The open connection.</summary>
    public NpgsqlConnection Connection { get; }

    /// <summary>The transaction the bounds are declared in.</summary>
    public NpgsqlTransaction Transaction { get; }

    /// <summary>Opens a connection from <paramref name="dataSource"/>, begins a transaction, and declares.</summary>
    public static Task<AuthorizedTransaction> OpenAsync(
        NpgsqlDataSource dataSource,
        AuthorizationContext authorization,
        CancellationToken cancellationToken,
        IsolationLevel isolation = IsolationLevel.Unspecified) =>
        OpenAsync(dataSource, ExperienceSessionContext.Bounds.Of(authorization), cancellationToken, isolation);

    /// <summary>Opens a connection from <paramref name="dataSource"/>, begins a transaction, and declares.</summary>
    public static async Task<AuthorizedTransaction> OpenAsync(
        NpgsqlDataSource dataSource,
        ExperienceSessionContext.Bounds bounds,
        CancellationToken cancellationToken,
        IsolationLevel isolation = IsolationLevel.Unspecified)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transaction = await ExperienceSessionContext.BeginAsync(connection, bounds, cancellationToken, isolation)
                .ConfigureAwait(false);
            return new AuthorizedTransaction(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>A command on this connection, enlisted in this transaction.</summary>
    public NpgsqlCommand CreateCommand(string sql) => new(sql, Connection, Transaction);

    /// <summary>Commits the transaction.</summary>
    public Task CommitAsync(CancellationToken cancellationToken) => Transaction.CommitAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Transaction.DisposeAsync().ConfigureAwait(false);
        await Connection.DisposeAsync().ConfigureAwait(false);
    }
}

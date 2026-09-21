using System.Net.Sockets;
using System.Reflection;
using AgentExperience.Abstractions;
using DbUp.Engine;
using DbUp.Postgresql;
using Npgsql;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// Applies this package's embedded schema scripts to a PostgreSQL database, journaled, so a host can
/// bring a database up to the schema <see cref="PostgresExperienceRecordStore"/> needs. The host calls
/// <see cref="MigrateAsync(NpgsqlDataSource, CancellationToken)"/> explicitly, once, before using the
/// store; the store never migrates on its own.
/// </summary>
/// <remarks>
/// <para>
/// Scripts run in name order, one transaction per script, and each applied script is recorded in
/// <c>agent_experience.schema_versions</c>, so a rerun applies nothing. The whole run is serialized
/// across processes by a PostgreSQL session advisory lock, held on its own connection, so concurrent
/// hosts cannot apply the same script twice.
/// </para>
/// <para>
/// The caller's data source must allow at least two concurrent connections: one for the advisory lock
/// and one for the scripts, and must not be multiplexing, because a multiplexed command does not stay
/// on one physical connection and so cannot hold a session advisory lock. The migrating role needs
/// <c>CREATE</c> on the database (to create the <c>agent_experience</c> schema) and on that schema (to
/// create its tables). The store itself only needs <c>SELECT</c>, <c>INSERT</c>, and <c>UPDATE</c> on
/// <c>agent_experience.experience_records</c> and <c>SELECT</c> and <c>INSERT</c> on
/// <c>agent_experience.lifecycle_events</c>. No script here needs a superuser, and none creates an
/// extension: this package's schema is text-only, and the derived embedding schema -- which does need
/// <c>CREATE EXTENSION vector</c> -- is applied separately by
/// <c>AgentExperience.Storage.Postgres.Vectors</c>'s own migrator, only by hosts that enable it.
/// </para>
/// <para>
/// The wait for the advisory lock is deliberately unbounded and ends only with the caller's token. Each
/// script, by contrast, runs under the data source's ordinary command timeout (30 seconds by default),
/// so a single long script fails with a timeout unless the connection string raises it.
/// </para>
/// </remarks>
public static class ExperienceSchemaMigrator
{
    /// <summary>The embedded-resource prefix that selects this assembly's migration scripts.</summary>
    private const string ResourcePrefix = "AgentExperience.Storage.Postgres.Migrations.";

    /// <summary>The journal table, inside <see cref="PostgresExperienceRecordSchema.SchemaName"/>.</summary>
    private const string JournalTable = "schema_versions";

    /// <summary>
    /// The advisory-lock key every AgentExperience.NET migration run takes, so runs in different
    /// processes serialize. It is the constant <c>0x4147455850455201</c> (ASCII <c>AGEXPER</c> plus
    /// <c>0x01</c>), scoped to the database the data source points at. Hosts that coordinate their own
    /// schema work on the same database must not reuse this key for anything else.
    /// </summary>
    internal const long AdvisoryLockKey = 0x4147455850455201L;

    /// <summary>
    /// Applies every embedded schema script that this database has not recorded yet.
    /// </summary>
    /// <param name="dataSource">
    /// The host-owned data source for the database to migrate. Never disposed here. It must allow at
    /// least two concurrent connections.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels opening the lock connection and waiting for the advisory lock. Once scripts start
    /// running, cancellation is ignored -- DbUp's upgrade has no cancellation point -- so the run
    /// finishes and returns normally.
    /// </param>
    /// <returns>The scripts applied by this call, in the order they ran. Empty when nothing was pending.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is <see langword="null"/>.</exception>
    /// <exception cref="ExperienceStoreException">
    /// A script failed, or the database was unreachable. The failing script is named; no SQL text or
    /// row data is included, and the original failure is the <see cref="Exception.InnerException"/> when
    /// DbUp reported one. Scripts that ran before the failure stay applied and journaled; the failing
    /// script's own transaction is rolled back.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled. Thrown unwrapped, and the advisory lock is
    /// not left held.
    /// </exception>
    public static Task<ExperienceSchemaMigrationResult> MigrateAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return MigrateAsync(dataSource, typeof(ExperienceSchemaMigrator).Assembly, ResourcePrefix, cancellationToken);
    }

    /// <summary>
    /// The script-selection seam: same run -- same journal table, same advisory lock -- but over an
    /// arbitrary assembly and resource prefix. It is what lets
    /// <c>AgentExperience.Storage.Postgres.Vectors</c> apply its own schema without duplicating the
    /// journalling and locking, and what lets a test drive a failing script that never ships in the
    /// package. Journal entries record DbUp's script name, which is the full resource name, so two
    /// prefixes can never claim each other's entries.
    /// </summary>
    internal static async Task<ExperienceSchemaMigrationResult> MigrateAsync(
        NpgsqlDataSource dataSource,
        Assembly scriptAssembly,
        string resourcePrefix,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        NpgsqlConnection lockConnection;
        try
        {
            lockConnection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, cancellationToken);
        }

        try
        {
            await AcquireLockAsync(lockConnection, cancellationToken).ConfigureAwait(false);

            // A cancellation request can lose the race with the server granting the lock. The upgrade
            // cannot be interrupted once it starts, so this is the last point at which a cancelled
            // caller can still be told nothing ran.
            cancellationToken.ThrowIfCancellationRequested();

            return await RunUpgradeAsync(dataSource, scriptAssembly, resourcePrefix, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Also unlocks when the acquire itself threw: a cancellation request can lose the race with
            // the server granting the lock, and the connection goes back to a pool that keeps the session
            // (and therefore its advisory locks) alive. Unlocking a lock this session does not hold is a
            // no-op that returns false.
            await ReleaseLockAsync(lockConnection).ConfigureAwait(false);
            await lockConnection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<ExperienceSchemaMigrationResult> RunUpgradeAsync(
        NpgsqlDataSource dataSource,
        Assembly scriptAssembly,
        string resourcePrefix,
        CancellationToken cancellationToken)
    {
        DatabaseUpgradeResult result;
        try
        {
            // A fresh engine per call: DbUp's builder is stateful and its connection manager owns a
            // connection. Building inside the guarded region keeps resource-loading and journal failures
            // inside the documented exception contract.
            var engine = PostgresqlExtensions
                .PostgresqlDatabase(new PostgresqlConnectionManager(dataSource), PostgresExperienceRecordSchema.SchemaName)
                .WithScriptsEmbeddedInAssembly(scriptAssembly, name => IsMigrationScript(name, resourcePrefix))
                .JournalToPostgresqlTable(PostgresExperienceRecordSchema.SchemaName, JournalTable)
                .WithTransactionPerScript()
                .WithVariablesDisabled()
                .LogToNowhere()
                .Build();

            // PerformUpgrade is synchronous and blocking, so keep it off the caller's thread. It takes no
            // token: cancelling mid-run would strand the advisory lock and a half-applied script.
            result = await Task.Run(engine.PerformUpgrade, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ExperienceStoreException
            && (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            // Anything else -- a driver failure, a missing or unreadable embedded resource, a journal the
            // role may not create -- is a storage infrastructure failure, so only the two documented
            // exception types ever leave this method.
            throw Translate(ex, cancellationToken);
        }

        if (!result.Successful)
        {
            throw Failed(result, resourcePrefix);
        }

        return new ExperienceSchemaMigrationResult(
            [.. result.Scripts.Select(script => ToScriptName(script.Name, resourcePrefix))]);
    }

    private static Exception Failed(DatabaseUpgradeResult result, string resourcePrefix)
    {
        // DbUp catches script failures and reports them on the result instead of throwing.
        var failedScript = result.ErrorScript is null ? null : ToScriptName(result.ErrorScript.Name, resourcePrefix);
        var message = failedScript is null
            ? "Experience Record schema migration failed."
            : $"Experience Record schema migration failed while applying script '{failedScript}'.";

        // DbUp can report an unsuccessful upgrade with no Error, so never promise an inner exception that
        // does not exist.
        return result.Error is null
            ? new ExperienceStoreException(message)
            : new ExperienceStoreException(message, result.Error);
    }

    private static async Task AcquireLockAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection)
            {
                // Waiting for another host's run is normal and can outlast any fixed timeout. The
                // caller's token is the only bound on the wait.
                CommandTimeout = 0,
            };
            command.Parameters.Add(new NpgsqlParameter<long>("key", AdvisoryLockKey));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, cancellationToken);
        }
    }

    private static async Task ReleaseLockAsync(NpgsqlConnection connection)
    {
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
            command.Parameters.Add(new NpgsqlParameter<long>("key", AdvisoryLockKey));
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or SocketException or TimeoutException
            or OperationCanceledException or InvalidOperationException or ObjectDisposedException)
        {
            // The unlock only fails when the lock connection is already broken, closed, or disposed
            // (Npgsql reports the last two as InvalidOperationException/ObjectDisposedException, which this
            // filter must cover because it throws from a finally). PostgreSQL releases a
            // session's advisory locks with the session. Npgsql discards broken connections rather than
            // returning them to the pool, so the lock cannot outlive this call. Swallowing here keeps the
            // original migration failure, which is the useful one, from being masked.
        }
    }

    private static bool IsMigrationScript(string resourceName, string resourcePrefix) =>
        resourceName.StartsWith(resourcePrefix, StringComparison.Ordinal)
        && resourceName.EndsWith(".sql", StringComparison.Ordinal);

    private static string ToScriptName(string resourceName, string resourcePrefix) =>
        resourceName.StartsWith(resourcePrefix, StringComparison.Ordinal)
            ? resourceName[resourcePrefix.Length..]
            : resourceName;

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

    private static Exception Translate(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled while the driver reported a failure: surface cancellation, unwrapped.
            return new OperationCanceledException("The Experience Record schema migration was cancelled.", ex, cancellationToken);
        }

        return new ExperienceStoreException("Experience Record schema migration failed due to a storage infrastructure error.", ex);
    }
}

/// <summary>What one <see cref="ExperienceSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/> call changed.</summary>
/// <param name="AppliedScripts">
/// The scripts this call applied and journaled, in the order they ran, named as in
/// <see cref="PostgresExperienceRecordSchema.ScriptNames"/>. Empty when the database was already current.
/// </param>
public sealed record ExperienceSchemaMigrationResult(IReadOnlyList<string> AppliedScripts);

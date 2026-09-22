using Npgsql;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// Why a reader stopped consulting <c>experience_grants</c> and fell back to the exact-scope
/// predicate alone.
/// </summary>
/// <param name="Operation">The read that hit it, e.g. <c>"get"</c> or <c>"candidate search"</c>.</param>
/// <param name="Reason">Which condition was detected. Content-free and safe to log.</param>
public sealed record ExperienceGrantSupportNotice(string Operation, ExperienceGrantSupportReason Reason);

/// <summary>Which condition made the grant table unusable.</summary>
public enum ExperienceGrantSupportReason
{
    /// <summary>The table does not exist: <c>0005_create_experience_grants.sql</c> has not been applied.</summary>
    TableMissing,

    /// <summary>The connecting role may not <c>SELECT</c> the table.</summary>
    NotPermitted,
}

/// <summary>
/// Decides, per reader, whether reads may consult <c>agent_experience.experience_grants</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every grant-aware read joins that table, which two supported deployments do not have: one that
/// has not applied <c>0005</c> yet, and a least-privilege role holding <c>SELECT</c> only on
/// <c>experience_records</c>. Neither is a corrupt database, so neither may turn every get and search
/// into an exception. The first read that meets an undefined table (<c>42P01</c>) or an insufficient
/// privilege (<c>42501</c>) latches this reader into degraded mode and retries the same read with the
/// exact-scope predicate alone; every later read composes the exact predicate from the start.
/// </para>
/// <para>
/// Degrading is <em>narrowing</em>, never widening: a grant that cannot be read simply does not widen
/// anything, so a record is returned only to the scope that owns it. It is still a configuration
/// problem, so the reader reports it once through the host's callback.
/// </para>
/// </remarks>
internal sealed class PostgresGrantSupport
{
    private const string UndefinedTable = "42P01";

    private const string InsufficientPrivilege = "42501";

    private readonly Action<ExperienceGrantSupportNotice>? _onUnavailable;

    private int _degraded;

    public PostgresGrantSupport(Action<ExperienceGrantSupportNotice>? onUnavailable) => _onUnavailable = onUnavailable;

    /// <summary>Whether a read may still compose the grant predicate.</summary>
    public bool Available => Volatile.Read(ref _degraded) == 0;

    /// <summary>
    /// Whether <paramref name="ex"/> is this reader's first sight of a missing or unreadable grant
    /// table. Latches degraded mode and notifies the host exactly once, so the caller can retry the
    /// same read without the grant predicate.
    /// </summary>
    public bool ShouldFallBack(Exception ex, string operation, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested
            || ex is not PostgresException postgres
            || postgres.SqlState is not (UndefinedTable or InsufficientPrivilege))
        {
            return false;
        }

        if (Interlocked.Exchange(ref _degraded, 1) == 0)
        {
            var reason = postgres.SqlState == UndefinedTable
                ? ExperienceGrantSupportReason.TableMissing
                : ExperienceGrantSupportReason.NotPermitted;

            // A throwing callback must not turn a successfully degraded read into a failure.
            try
            {
                _onUnavailable?.Invoke(new ExperienceGrantSupportNotice(operation, reason));
            }
            catch
            {
                // Intentionally swallowed: the host's own logging is not this read's problem.
            }
        }

        return true;
    }
}

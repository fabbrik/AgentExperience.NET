using System.Text;
using Npgsql;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// The application role
/// <see cref="ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(NpgsqlDataSource, ExperienceApplicationRoleOptions, CancellationToken)"/>
/// gives exactly the privileges the stores need, and the three powers a host must opt into.
/// </summary>
public sealed class ExperienceApplicationRoleOptions
{
    /// <summary>PostgreSQL's identifier limit, in bytes.</summary>
    private const int MaxRoleNameBytes = 63;

    /// <summary>Names the application role.</summary>
    /// <param name="roleName">
    /// The role the stores connect as, exactly as PostgreSQL stores it (case-sensitive, unquoted). It
    /// must already exist, must not be a superuser, must not be the role that runs the migrator, and must
    /// not be a member of any role that owns the schema or an object in it.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="roleName"/> is <see langword="null"/>, blank, longer than 63 UTF-8 bytes, or
    /// contains a NUL character.
    /// </exception>
    public ExperienceApplicationRoleOptions(string roleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleName);
        if (roleName.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A role name cannot contain a NUL character.", nameof(roleName));
        }

        if (Encoding.UTF8.GetByteCount(roleName) > MaxRoleNameBytes)
        {
            throw new ArgumentException("A PostgreSQL role name is at most 63 bytes.", nameof(roleName));
        }

        RoleName = roleName;
    }

    /// <summary>The application role's name.</summary>
    public string RoleName { get; }

    /// <summary>
    /// Grants <c>EXECUTE</c> on <c>agent_experience.purge_experience_record</c> and
    /// <c>agent_experience.purge_expired_grants</c>: what <c>DeleteAsync</c>, <c>SweepExpiredAsync</c> and
    /// <c>PurgeExpiredAsync</c> call. <see langword="false"/> by default, in which case those calls fail
    /// with a permission error and nothing is erased.
    /// </summary>
    public bool AllowErasure { get; init; }

    /// <summary>
    /// Grants <c>EXECUTE</c> on <c>agent_experience.purge_grant_access</c>: what
    /// <see cref="PostgresExperienceGrantAccessLog.PurgeOlderThanAsync"/>
    /// calls. <see langword="false"/> by default. Separate from <see cref="AllowErasure"/> because erasing
    /// the record of who read a record is a different power from erasing the record.
    /// </summary>
    public bool AllowAccessLogPurge { get; init; }

    /// <summary>
    /// Grants <c>EXECUTE</c> on <c>agent_experience.seal_experience_record</c>: what
    /// <see cref="PostgresExperienceRecordStore.SealPlaintextRecordsAsync"/>, the crypto-shredding upgrade job,
    /// calls. <see langword="false"/> by default. It admits exactly one transition -- a live plaintext record into
    /// its sealed shape, at the revision read -- and the role otherwise holds no <c>UPDATE</c> on a record's
    /// payload or task ID. It is nonetheless a content-rewrite power: the function checks the shape of what it
    /// stores, never its meaning, so a role holding it can replace a live plaintext record's content with a seal of
    /// anything. Switch it on for the upgrade and off again once
    /// <see cref="ExperienceSealingResult.MoreRemain"/> is <see langword="false"/> everywhere.
    /// </summary>
    public bool AllowSealing { get; init; }

    /// <summary>
    /// Switches on PostgreSQL row-level security, the second isolation layer behind the stores' own scope
    /// predicates (story 15.1). <see langword="false"/> by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <see langword="true"/>, the privileges call runs <c>ALTER TABLE ... ENABLE ROW LEVEL SECURITY</c> on
    /// every table the application role reads or writes by scope -- including <c>experience_embeddings</c> when
    /// the vectors package created it -- and verifies it took, in the same transaction as the privileges. It never
    /// uses <c>FORCE</c>: the owner, and so the <c>SECURITY DEFINER</c> erasure, purge and sealing functions,
    /// keeps bypassing the policies. The call refuses, changing nothing, when a table's policies are missing
    /// (run both migrators first) or when the application role can reach a role with <c>BYPASSRLS</c>.
    /// </para>
    /// <para>
    /// It also grants <c>EXECUTE</c> on <c>agent_experience.search_experience_text</c> (<c>0024</c>, story 17.7), the
    /// owner-run text search that keeps the GIN indexes usable under the policies, and refuses to enable anything unless
    /// that function is exactly the canonical definition. With row-level security off the grant is revoked.
    /// </para>
    /// <para>
    /// When <see langword="false"/>, the same call disables row-level security on those tables, so the setting
    /// is declarative: what the last call said is what holds.
    /// </para>
    /// <para>
    /// The policies admit only the rows inside the authorization bounds each store operation declares for its
    /// own transaction, plus the rows a live sharing grant lets it read. They guard against a mistake in a store's
    /// SQL, not against a compromised application role: any session can declare bounds, so a host that runs
    /// arbitrary SQL as the application role can declare wide ones. See docs/guide/deployment.md, Enabling
    /// row-level security.
    /// </para>
    /// </remarks>
    public bool EnableRowLevelSecurity { get; init; } = TestSuiteRowLevelSecurity;

    /// <summary>
    /// <b>Test-only.</b> What <see cref="EnableRowLevelSecurity"/> starts as, set once by the storage suites'
    /// module initializers from <c>AGENTEXPERIENCE_TEST_RLS</c>, so the unmodified suites run a second time with
    /// row-level security on. Never set by the library: the public default is <see langword="false"/>.
    /// </summary>
    internal static bool TestSuiteRowLevelSecurity { get; set; }
}

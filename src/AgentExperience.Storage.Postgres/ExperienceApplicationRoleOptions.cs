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
}

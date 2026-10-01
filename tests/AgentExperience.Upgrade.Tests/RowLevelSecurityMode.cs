using Npgsql;
using NpgsqlTypes;

namespace AgentExperience.Upgrade.Tests;

/// <summary>
/// Runs the upgrade suite with PostgreSQL row-level security switched on by the privileges call when
/// <c>AGENTEXPERIENCE_TEST_RLS=on</c> (story 15.1), so every upgraded database is read and written back through
/// today's stores behind the policies, and the fresh install it is compared with is switched on the same way.
/// </summary>
internal static class RowLevelSecurityMode
{
    /// <summary>The environment variable that switches the suite to row-level security mode.</summary>
    public const string Variable = "AGENTEXPERIENCE_TEST_RLS";

    /// <summary>Whether this run is the row-level security run.</summary>
    public static bool IsOn { get; } =
        string.Equals(Environment.GetEnvironmentVariable(Variable), "on", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One scalar, read as whichever role <paramref name="dataSource"/> connects as, inside a transaction that first
    /// declares <paramref name="tenantId"/> as its only bound -- the settings every store operation declares, set
    /// transaction-locally the same way. Harmless with row-level security off, where nothing reads them.
    /// </summary>
    public static async Task<T> DeclaredScalarAsync<T>(
        NpgsqlDataSource dataSource, string tenantId, string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var declare = new NpgsqlCommand(
            "SELECT set_config('agent_experience.auth_tenant', '=' || @tenant, true), " +
            "set_config('agent_experience.auth_application', '', true), set_config('agent_experience.auth_project', '', true), " +
            "set_config('agent_experience.auth_team', '', true), set_config('agent_experience.auth_agent', '', true), " +
            "set_config('agent_experience.auth_user', '', true), set_config('agent_experience.auth_set', 'on', true)",
            connection,
            transaction))
        {
            declare.Parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Text) { TypedValue = tenantId });
            await declare.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}

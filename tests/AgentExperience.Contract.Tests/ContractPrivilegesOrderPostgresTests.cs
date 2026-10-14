using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres;
using Npgsql;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Story 21.8: in a mixed fleet, the contract's privileges file (what a port's setup runs) revokes everything from the
/// application role, including the grants the .NET call gives on <c>0026</c>'s .NET-only injection table. The documented
/// order is the contract file first and the .NET call after it; this proves the .NET call is idempotent after the
/// contract file and restores what the .NET adapter needs.
/// </summary>
[Collection(ContractPostgresCollection.Name)]
public sealed class ContractPrivilegesOrderPostgresTests(ContractPostgresDatabase database)
{
    [Fact]
    public async Task The_dotnet_privileges_call_after_the_contract_script_restores_the_injection_table_grants()
    {
        const string CaseName = "privileges-order";
        var application = await database.CreateAsync(CaseName);
        var owner = database.OwnerOf(CaseName);
        const string CanRead = "SELECT has_table_privilege(@role, 'agent_experience.experience_injections', 'SELECT')";
        Assert.True(await ScalarAsync<bool>(owner, CanRead));

        // The contract script, both modes, as a port's setup runs it: it revokes the injection table's grants.
        var role = "\"" + ContractPostgresDatabase.ApplicationRole + "\"";
        await using (var connection = await owner.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            foreach (var line in ContractFiles.Read("sql/application-role-privileges.sql")!.Split('\n'))
            {
                if (line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                await using var command = new NpgsqlCommand(line.Replace(":\"application_role\"", role, StringComparison.Ordinal), connection, transaction);
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        Assert.False(await ScalarAsync<bool>(owner, CanRead));

        // The .NET call after it succeeds, and the .NET store records injections again.
        await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
            owner,
            new ExperienceApplicationRoleOptions(ContractPostgresDatabase.ApplicationRole) { EnableRowLevelSecurity = true },
            CancellationToken.None);
        Assert.True(await ScalarAsync<bool>(owner, CanRead));

        var scope = new Scope("tenant-order", "app-1", "project-1");
        var authorization = new AuthorizationContext(scope.TenantId, "principal", ["experience:write"], DateTimeOffset.UtcNow);
        var store = new PostgresExperienceRecordStore(application);
        var record = new ExperienceRecord(
            Guid.NewGuid(), Guid.NewGuid(), scope, "task-1", null, [],
            new Outcome(TaskVerificationStatus.Unknown, [], null, DateTimeOffset.UtcNow), 0, null,
            new EnvironmentFingerprint("host", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
            new Provenance("tests", null, DateTimeOffset.UtcNow, null),
            ExperienceStatus.Candidate, 0, 0, 0, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(authorization, record, CancellationToken.None)).Outcome);
        Assert.Equal(
            ExperienceStoreOutcome.Committed,
            (await store.RecordInjectionAsync(authorization, scope, [record.ExperienceId], DateTimeOffset.UtcNow, CancellationToken.None)).Outcome);
        var page = await store.QueryWithInjectionsAsync(authorization, new ExperienceRecordQuery(scope), CancellationToken.None);
        Assert.Equal(1, page.Injections![record.ExperienceId].Count);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<string>("role", ContractPostgresDatabase.ApplicationRole));
        return (T)(await command.ExecuteScalarAsync())!;
    }
}

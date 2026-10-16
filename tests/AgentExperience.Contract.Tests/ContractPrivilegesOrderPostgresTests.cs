using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres;
using Npgsql;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// In a mixed fleet, the contract's 1.0 privileges file (what a port's setup runs) revokes everything from the application
/// role, including the grants on <c>0026</c>'s injection table. Contract 1.4 adds
/// <c>sql/application-role-privileges-1.4.sql</c>, which grants them back: the documented order is the 1.0 file then the
/// 1.4 file, or the .NET call. These prove each order leaves the role able to record and read injections, and that the
/// 1.0 file alone does not.
/// </summary>
[Collection(ContractPostgresCollection.Name)]
public sealed class ContractPrivilegesOrderPostgresTests(ContractPostgresDatabase database)
{
    private const string CanRead = "SELECT has_table_privilege(@role, 'agent_experience.experience_injections', 'SELECT')";

    [Fact]
    public async Task The_dotnet_privileges_call_after_the_contract_script_restores_the_injection_table_grants()
    {
        const string CaseName = "privileges-order";
        var application = await database.CreateAsync(CaseName);
        var owner = database.OwnerOf(CaseName);
        Assert.True(await ScalarAsync<bool>(owner, CanRead));

        // The 1.0 contract script, both modes, as a port's setup runs it: it revokes the injection table's grants.
        await ApplyContractFileAsync(owner, "sql/application-role-privileges.sql");
        Assert.False(await ScalarAsync<bool>(owner, CanRead));

        // The .NET call after it succeeds, and the .NET store records injections again.
        await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
            owner,
            new ExperienceApplicationRoleOptions(ContractPostgresDatabase.ApplicationRole) { EnableRowLevelSecurity = true },
            CancellationToken.None);
        Assert.True(await ScalarAsync<bool>(owner, CanRead));
        await AssertRecordsAsync(application, "tenant-order");
    }

    [Fact]
    public async Task The_1_4_contract_script_after_the_1_0_script_grants_the_injection_table()
    {
        const string CaseName = "privileges-order-1-4";
        var application = await database.CreateAsync(CaseName);
        var owner = database.OwnerOf(CaseName);

        await ApplyContractFileAsync(owner, "sql/application-role-privileges.sql");
        Assert.False(await ScalarAsync<bool>(owner, CanRead));
        await ApplyContractFileAsync(owner, "sql/application-role-privileges-1.4.sql");
        Assert.True(await ScalarAsync<bool>(owner, CanRead));
        await AssertRecordsAsync(application, "tenant-order-1-4");

        // And the .NET call after both changes nothing it would refuse: the two files are exactly its grants.
        await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
            owner,
            new ExperienceApplicationRoleOptions(ContractPostgresDatabase.ApplicationRole) { EnableRowLevelSecurity = true },
            CancellationToken.None);
        await AssertRecordsAsync(application, "tenant-order-1-4b");
    }

    /// <summary>A contract privileges file, both modes, in one transaction, as a port's setup runs it.</summary>
    internal static async Task ApplyContractFileAsync(NpgsqlDataSource owner, string file)
    {
        var role = "\"" + ContractPostgresDatabase.ApplicationRole + "\"";
        await using var connection = await owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var line in ContractFiles.Read(file)!.Split('\n'))
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

    /// <summary>The .NET store records an injection of a new record, and the lessons view reads it.</summary>
    private static async Task AssertRecordsAsync(NpgsqlDataSource application, string tenant)
    {
        var scope = new Scope(tenant, "app-1", "project-1");
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

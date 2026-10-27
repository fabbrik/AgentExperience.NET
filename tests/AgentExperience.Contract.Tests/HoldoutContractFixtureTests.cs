using AgentExperience.Storage.Postgres;

namespace AgentExperience.Contract.Tests;

/// <summary>Contract 1.8's files.</summary>
internal static class HoldoutStatements
{
    /// <summary>The statement that records a run's holdout assignment.</summary>
    public const string RecordFile = "sql/record-holdout-assignment.sql";

    /// <summary>The application role's grants on <c>0027</c>'s table.</summary>
    public const string PrivilegesFile = "sql/application-role-privileges-1.8.sql";
}

/// <summary>
/// Contract 1.8's checks that need no database: the <c>holdout-assign</c> fixture is what the .NET assignment function
/// gives, the published statement is the .NET store's, line for line, and the 1.8 privileges file is the .NET privileges
/// call's grants on the holdout table.
/// </summary>
public sealed partial class ContractFixtureTests
{
    [Fact]
    public void The_holdout_assign_fixture_is_what_the_assignment_function_gives()
    {
        var drift = new List<string>();
        var path = $"fixtures/{HoldoutAssignContract.Family}/{HoldoutAssignContract.FileName}";
        ContractFiles.Verify(path, ContractJson.Write(HoldoutAssignContract.Run()), drift);
        if (ContractFiles.Read(path) is { } text)
        {
            HoldoutAssignContract.Recheck(path, text, drift);
        }

        ContractFiles.VerifyNoStray(HoldoutAssignContract.Family, [], [], drift, HoldoutAssignContract.FileName);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }

    [Fact]
    public void The_holdout_record_statement_is_the_one_the_store_runs()
    {
        // Contract 1.8: the insert the .NET store runs is the contract file's statement, line for line.
        Assert.Equal(PostgresExperienceRecordStore.RecordHoldoutAssignmentSql, Statement(ContractFiles.Read(HoldoutStatements.RecordFile)!));
    }

    [Fact]
    public void The_1_8_privileges_file_is_what_the_migrator_grants_on_the_holdout_table()
    {
        // On a schema migrated to 0027 or later, the .NET call's grants on 0027's table alone, each mode in turn.
        const string role = ":\"application_role\"";
        var tables = new HashSet<string>(StringComparer.Ordinal) { ApplicationRolePrivileges.HoldoutAssignmentsTable };
        var fresh = tables.ToDictionary(
            table => table,
            _ => new ApplicationRolePrivileges.RowSecurityState(Enabled: false, Forced: false, Policies: []),
            StringComparer.Ordinal);
        var (always18, rowLevelSecurity18) = PrivilegeStatements(ContractFiles.Read(HoldoutStatements.PrivilegesFile)!);
        Assert.NotEmpty(always18);
        Assert.NotEmpty(rowLevelSecurity18);
        Assert.All(
            [.. always18, .. rowLevelSecurity18],
            statement => Assert.Contains("agent_experience.experience_holdout_assignments", statement, StringComparison.Ordinal));

        foreach (var enable in new[] { false, true })
        {
            var options = new ExperienceApplicationRoleOptions("contract_app") { EnableRowLevelSecurity = enable };
            var expected = ApplicationRolePrivileges.Statements(role, tables, options)
                .Concat(ApplicationRolePrivileges.RowLevelSecurityStatements(fresh, tables, enable))
                .Where(statement => statement.Contains("experience_holdout_assignments", StringComparison.Ordinal))
                .ToList();
            List<string> contract = enable ? [.. always18, .. rowLevelSecurity18] : [.. always18];
            Assert.Equal(expected.Order(StringComparer.Ordinal), contract.Order(StringComparer.Ordinal));
        }
    }
}

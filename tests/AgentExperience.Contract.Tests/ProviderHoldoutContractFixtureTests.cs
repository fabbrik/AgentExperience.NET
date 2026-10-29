using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Holdout;
using AgentExperience.Storage.Postgres;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Contract 1.11's checks that need no database: the provider-holdout-assign fixture is what the .NET wrapper's salt and
/// the assignment function give, every provider-benefit-estimate fixture is what <c>EstimateProviderAsync</c>'s fold
/// gives, the published statements are the .NET store's, line for line, and the 1.11 privileges file is the .NET
/// privileges call's grants on the provider holdout table.
/// </summary>
public sealed partial class ContractFixtureTests
{
    [Fact]
    public void The_provider_holdout_assign_fixture_is_what_the_wrapper_assigns()
    {
        var drift = new List<string>();
        var path = $"fixtures/{ProviderHoldoutAssignContract.Family}/{ProviderHoldoutAssignContract.FileName}";
        ContractFiles.Verify(path, ContractJson.Write(ProviderHoldoutAssignContract.Run()), drift);
        if (ContractFiles.Read(path) is { } text)
        {
            ProviderHoldoutAssignContract.Recheck(path, text, drift);
        }

        ContractFiles.VerifyNoStray(ProviderHoldoutAssignContract.Family, [], [], drift, ProviderHoldoutAssignContract.FileName);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }

    [Fact]
    public void The_provider_holdout_assign_cases_keep_ambiguous_keys_and_salts_apart()
    {
        // Contract 1.11: the length prefix gives every (key, salt) pair its own salt, so a key and salt whose plain
        // concatenation collides with another pair's never shares its arms.
        var salts = ProviderHoldoutAssignContract.Pairs
            .Select(pair => Injection(pair.Key, pair.Salt))
            .ToList();
        Assert.Equal(salts.Count, salts.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("3:a/b/c", Injection("a/b", "c"));
        Assert.Equal("1:a/b/c", Injection("a", "b/c"));
        Assert.False(ProviderHoldoutAssignment.IsValidProviderKey("a/b"));

        static string Injection(string key, string salt) =>
            MicrosoftAgentFramework.Injection.ExperienceHoldoutContextProvider.ProviderSalt(key, salt);
    }

    [Fact]
    public void Every_provider_benefit_estimate_fixture_is_what_the_provider_estimate_folds()
    {
        var drift = new List<string>();
        foreach (var (name, definition) in ProviderBenefitEstimateContract.Cases)
        {
            var directory = $"fixtures/{ProviderBenefitEstimateContract.Family}/{name}";
            var rowsJson = ContractJson.Write(ProviderBenefitEstimateContract.ToRowsJson(definition));
            var optionsJson = ContractJson.Write(definition.Options);
            ContractFiles.Verify($"{directory}/rows.json", rowsJson, drift);
            ContractFiles.Verify($"{directory}/options.json", optionsJson, drift);

            var read = ProviderBenefitEstimateContract.Read(
                JsonNode.Parse(ContractFiles.Read($"{directory}/rows.json") ?? rowsJson)!,
                ContractJson.Read<ProviderBenefitEstimateOptions>(ContractFiles.Read($"{directory}/options.json") ?? optionsJson));
            var estimate = ProviderBenefitEstimateContract.EstimateJson(ProviderBenefitEstimateContract.Estimate(read));
            ContractFiles.Verify($"{directory}/expected.json", ContractJson.Write(estimate), drift);

            // The paged form gives the same estimate, whatever the page size.
            foreach (var pageSize in new[] { 1, 3 })
            {
                if (!JsonNode.DeepEquals(estimate, ProviderBenefitEstimateContract.EstimateJson(ProviderBenefitEstimateContract.Estimate(read, pageSize))))
                {
                    drift.Add($"{directory}: {pageSize} row(s) a page gives another estimate.");
                }
            }
        }

        ContractFiles.VerifyNoStray(
            ProviderBenefitEstimateContract.Family,
            ProviderBenefitEstimateContract.Cases.Select(entry => entry.Name),
            ["rows.json", "options.json", "expected.json"],
            drift);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }

    [Fact]
    public void The_provider_benefit_estimate_cases_cover_every_verdict_and_count()
    {
        var estimates = ProviderBenefitEstimateContract.Cases.ToDictionary(
            entry => entry.Name,
            entry => ProviderBenefitEstimateContract.Estimate(entry.Case).Overall);
        Assert.Equal(ExperienceBenefitVerdict.Helps, estimates["helps"].Verdict);
        Assert.Equal(ExperienceBenefitVerdict.Hurts, estimates["hurts"].Verdict);
        Assert.Equal(ExperienceBenefitVerdict.NoEvidence, estimates["spans-zero"].Verdict);
        Assert.Equal(ExperienceBenefitVerdict.NoEvidence, estimates["below-the-run-minimum"].Verdict);
        Assert.True(estimates["below-the-run-minimum"].Lower > 0);
        Assert.Equal(ExperienceBenefitVerdict.Helps, estimates["custom-minimum"].Verdict);
        Assert.True(estimates["pending-and-unverified"].Pending > 0);
        Assert.True(estimates["pending-and-unverified"].Unverified > 0);
        Assert.Null(estimates["one-arm-empty"].Difference);
        Assert.Equal(0, estimates["no-rows"].Treatment.Runs + estimates["no-rows"].Holdout.Runs);
    }

    [Fact]
    public void The_provider_holdout_statements_are_the_ones_the_store_runs()
    {
        // Contract 1.11: the insert and both pages of the read, each the .NET store's statement line for line.
        Assert.Equal(PostgresExperienceRecordStore.RecordProviderHoldoutAssignmentSql, Statement(ContractFiles.Read(ProviderHoldoutStatements.RecordFile)!));
        var statements = VectorStatements.Read(ProviderHoldoutStatements.QueryFile);
        Assert.Equal(new[] { "first-page", "next-page" }, statements.Keys.ToArray());
        Assert.Equal(
            PostgresExperienceRecordStore.QueryProviderHoldoutOutcomesPrefix + PostgresExperienceRecordStore.QueryProviderHoldoutOutcomesSuffix,
            statements["first-page"]);
        Assert.Equal(
            PostgresExperienceRecordStore.QueryProviderHoldoutOutcomesPrefix + PostgresExperienceRecordStore.QueryProviderHoldoutOutcomesAfterPredicate + PostgresExperienceRecordStore.QueryProviderHoldoutOutcomesSuffix,
            statements["next-page"]);
    }

    [Fact]
    public void The_1_11_privileges_file_is_what_the_migrator_grants_on_the_provider_holdout_table()
    {
        // On a schema migrated to 0029 or later, the .NET call's grants on 0029's table alone, each mode in turn.
        const string role = ":\"application_role\"";
        var tables = new HashSet<string>(StringComparer.Ordinal) { ApplicationRolePrivileges.ProviderHoldoutAssignmentsTable };
        var fresh = tables.ToDictionary(
            table => table,
            _ => new ApplicationRolePrivileges.RowSecurityState(Enabled: false, Forced: false, Policies: []),
            StringComparer.Ordinal);
        var (always, rowLevelSecurity) = PrivilegeStatements(ContractFiles.Read(ProviderHoldoutStatements.PrivilegesFile)!);
        Assert.NotEmpty(always);
        Assert.NotEmpty(rowLevelSecurity);
        Assert.All(
            [.. always, .. rowLevelSecurity],
            statement => Assert.Contains("agent_experience.experience_provider_holdout_assignments", statement, StringComparison.Ordinal));

        foreach (var enable in new[] { false, true })
        {
            var options = new ExperienceApplicationRoleOptions("contract_app") { EnableRowLevelSecurity = enable };
            var expected = ApplicationRolePrivileges.Statements(role, tables, options)
                .Concat(ApplicationRolePrivileges.RowLevelSecurityStatements(fresh, tables, enable))
                .Where(statement => statement.Contains(ProviderHoldoutStatements.Table, StringComparison.Ordinal))
                .ToList();
            List<string> contract = enable ? [.. always, .. rowLevelSecurity] : [.. always];
            Assert.Equal(expected.Order(StringComparer.Ordinal), contract.Order(StringComparer.Ordinal));
        }
    }
}

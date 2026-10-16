using System.Globalization;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Contract 1.6's checks that need no database: the published statements are the .NET index's, line for line; the 1.0,
/// 1.4 and 1.6 privileges files together are the .NET privileges call's grants; and the <c>embedding-summary</c> and
/// <c>hybrid-merge</c> fixtures are what the library computes from their checked-in inputs.
/// </summary>
public sealed partial class ContractFixtureTests
{
    [Fact]
    public void The_embedding_write_statements_are_the_ones_the_index_runs()
    {
        var statements = VectorStatements.Read(VectorStatements.WriteFile);
        Assert.Equal(new[] { "write", "probe-revision" }, statements.Keys.ToArray());
        Assert.Equal(VectorStatements.DotnetWrite, OneLine(statements["write"]));
        Assert.Equal(VectorStatements.DotnetProbeRevision, OneLine(statements["probe-revision"]));
    }

    [Fact]
    public void The_vector_search_statements_are_the_ones_the_index_runs_for_every_dimension()
    {
        var statements = VectorStatements.Read(VectorStatements.SearchFile);
        Assert.Equal(new[] { "search", "search-excluding-model-authored", "compatibility", "compatibility-excluding-model-authored" }, statements.Keys.ToArray());
        foreach (var dimension in new[] { 1, 4, 5, 1536, ExperienceEmbeddingDescriptor.MaxDimension })
        {
            Assert.Equal(VectorStatements.DotnetSearch(dimension, excludeModelAuthored: false), OneLine(VectorStatements.WithDimension(statements["search"], dimension)));
            Assert.Equal(VectorStatements.DotnetSearch(dimension, excludeModelAuthored: true), OneLine(VectorStatements.WithDimension(statements["search-excluding-model-authored"], dimension)));
        }

        Assert.Equal(VectorStatements.DotnetCompatibility(excludeModelAuthored: false), OneLine(statements["compatibility"]));
        Assert.Equal(VectorStatements.DotnetCompatibility(excludeModelAuthored: true), OneLine(statements["compatibility-excluding-model-authored"]));
        Assert.DoesNotContain(VectorStatements.DimensionPlaceholder, statements["compatibility"], StringComparison.Ordinal);
    }

    [Fact]
    public void The_1_0_1_4_and_1_6_privileges_files_together_are_what_the_migrator_grants_on_a_schema_with_the_embeddings_table()
    {
        // On a schema migrated to 0026 or later with the vectors migrations: every non-optional table, the injection table and
        // the embeddings table exist, and none has row-level security yet. The 1.0, 1.4 and 1.6 files are the .NET call's
        // grants, each mode in turn; grants commute, so the set is compared, and neither side may repeat a statement.
        const string role = ":\"application_role\"";
        var tables = ApplicationRolePrivileges.Tables
            .Where(table => !table.Optional || table.Name is ApplicationRolePrivileges.InjectionsTable or ApplicationRolePrivileges.EmbeddingsTable)
            .Select(table => table.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(ApplicationRolePrivileges.EmbeddingsTable, tables);
        var fresh = tables.ToDictionary(
            table => table,
            _ => new ApplicationRolePrivileges.RowSecurityState(Enabled: false, Forced: false, Policies: []),
            StringComparer.Ordinal);
        var (always, rowLevelSecurity) = PrivilegeStatements(ContractFiles.Read("sql/application-role-privileges.sql")!);
        var (always14, rowLevelSecurity14) = PrivilegeStatements(ContractFiles.Read("sql/application-role-privileges-1.4.sql")!);
        var (always16, rowLevelSecurity16) = PrivilegeStatements(ContractFiles.Read(VectorStatements.PrivilegesFile)!);
        Assert.NotEmpty(always16);
        Assert.NotEmpty(rowLevelSecurity16);
        Assert.All(
            [.. always16, .. rowLevelSecurity16],
            statement => Assert.Contains("agent_experience.experience_embeddings", statement, StringComparison.Ordinal));

        foreach (var enable in new[] { false, true })
        {
            var options = new ExperienceApplicationRoleOptions("contract_app") { EnableRowLevelSecurity = enable };
            List<string> expected =
            [
                .. ApplicationRolePrivileges.Statements(role, tables, options),
                .. ApplicationRolePrivileges.RowLevelSecurityStatements(fresh, tables, enable),
            ];
            List<string> contract = enable
                ? [.. always, .. rowLevelSecurity, .. always14, .. rowLevelSecurity14, .. always16, .. rowLevelSecurity16]
                : [.. always, .. always14, .. always16];
            Assert.Equal(expected.Count, expected.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(contract.Count, contract.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(expected.Order(StringComparer.Ordinal), contract.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void The_embedding_summary_fixture_is_what_the_library_derives_and_hashes()
    {
        var drift = new List<string>();
        var path = $"fixtures/{EmbeddingSummaryContract.Family}/{EmbeddingSummaryContract.FileName}";
        ContractFiles.Verify(path, ContractJson.Write(EmbeddingSummaryContract.Run()), drift);

        if (ContractFiles.Read(path) is { } text)
        {
            foreach (var (entry, index) in ContractJson.Read<List<EmbeddingSummaryCase>>(text).Select((entry, index) => (entry, index)))
            {
                var again = EmbeddingSummaryContract.Rerun(entry);
                if (again != entry)
                {
                    drift.Add($"{path}[{index}]: the library now derives '{again.Summary}' ({again.ContentHash}).");
                }
            }
        }

        ContractFiles.VerifyNoStray(EmbeddingSummaryContract.Family, [], [], drift, EmbeddingSummaryContract.FileName);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }

    [Fact]
    public void A_summary_separator_is_exactly_a_character_of_category_Cc_Zs_Zl_or_Zp()
    {
        // README-1.6 states the summary's separator rule by Unicode general category, so a port need not know .NET's
        // char.IsWhiteSpace: this pins that the two are the same set, over every UTF-16 code unit.
        for (var unit = 0; unit <= char.MaxValue; unit++)
        {
            var c = (char)unit;
            var category = char.GetUnicodeCategory(c);
            var byCategory = category is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.SpaceSeparator
                or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator;
            Assert.True(byCategory == (char.IsWhiteSpace(c) || char.IsControl(c)), $"U+{unit:X4}");
        }
    }

    [Fact]
    public void The_hybrid_merge_fixture_is_what_the_retrieval_service_floors_and_merges()
    {
        var drift = new List<string>();
        var path = $"fixtures/{HybridMergeContract.Family}/{HybridMergeContract.FileName}";
        ContractFiles.Verify(path, ContractJson.Write(HybridMergeContract.Run()), drift);

        if (ContractFiles.Read(path) is { } text)
        {
            foreach (var (entry, index) in JsonNode.Parse(text)!.AsArray().Select((entry, index) => (entry, index)))
            {
                var again = JsonNode.Parse(ContractJson.Write(HybridMergeContract.Rerun(ContractJson.Read<HybridMergeCase>(entry!.ToJsonString()))));
                if (!JsonNode.DeepEquals(again!["merged"], entry["merged"]))
                {
                    drift.Add($"{path}[{index.ToString(CultureInfo.InvariantCulture)}]: the retrieval service now merges {again["merged"]!.ToJsonString()}.");
                }
            }
        }

        ContractFiles.VerifyNoStray(HybridMergeContract.Family, [], [], drift, HybridMergeContract.FileName);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }

    [Fact]
    public void Every_vector_seed_record_validates_against_the_schema()
    {
        // The embedding-write and vector-search families' results need PostgreSQL with pgvector; their seeds do not.
        var problems = new List<string>();
        var directories = VectorContract.WriteCases.Select(entry => $"fixtures/{VectorContract.WriteFamily}/{entry.Name}")
            .Concat(VectorContract.SearchCases.Select(entry => $"fixtures/{VectorContract.SearchFamily}/{entry.Name}"));
        foreach (var directory in directories)
        {
            var seed = ContractFiles.Read($"{directory}/seed.json");
            Assert.True(seed is not null, $"{directory}/seed.json is missing; run the PostgreSQL contract tests with {ContractFiles.UpdateVariable}=1.");
            foreach (var (entry, index) in JsonNode.Parse(seed)!.AsArray().Select((entry, index) => (entry, index)))
            {
                RecordSchema.Check($"{directory}/seed.json[{index}]", entry!, problems);
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>A contract statement as one line: its lines joined by one space, which is the .NET statement exactly.</summary>
    private static string OneLine(string statement)
    {
        Assert.DoesNotContain("  ", statement, StringComparison.Ordinal);
        Assert.All(statement.Split('\n'), line => Assert.True(line.Length > 0 && line.Trim() == line, $"A blank or padded statement line: '{line}'"));
        return statement.Replace('\n', ' ');
    }
}

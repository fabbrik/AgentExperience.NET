using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentExperience.Storage.Postgres;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// The conformance runner for the families that need no database: every checked-in fixture under
/// <c>contract/fixtures/</c> is produced byte for byte by the library from its checked-in inputs, every stored record in
/// them validates against the record schema and survives the adapter's read and write, and the version lock holds.
/// </summary>
/// <remarks>
/// Each family's case definitions write its inputs; the runner then reads the inputs back <em>from disk</em>, runs them
/// through the library, and compares what comes out with the checked-in expected files. Set
/// <c>AGENTEXPERIENCE_CONTRACT_FIXTURES_UPDATE=1</c> to rewrite them instead. The PostgreSQL contract test does the
/// same for the search family against PostgreSQL.
/// </remarks>
public sealed partial class ContractFixtureTests
{
    [Fact]
    public async Task Every_finalize_fixture_is_what_capture_and_finalization_store()
    {
        var drift = new List<string>();
        foreach (var (name, definition) in FinalizeContract.Cases)
        {
            var directory = $"fixtures/{FinalizeContract.Family}/{name}";
            ContractFiles.Verify($"{directory}/input.json", ContractJson.Write(definition), drift);

            var input = ContractJson.Read<FinalizeInput>(ContractFiles.Read($"{directory}/input.json") ?? ContractJson.Write(definition));
            var (record, lifecycleEvent, capture) = await FinalizeContract.RunWithCaptureAsync(input);
            ContractFiles.Verify($"{directory}/expected-capture.json", ContractJson.Write(capture), drift);
            ContractFiles.Verify($"{directory}/expected-record.json", ContractJson.Write(record), drift);
            ContractFiles.Verify($"{directory}/expected-event.json", ContractJson.Write(lifecycleEvent), drift);

            if (ContractFiles.Read($"{directory}/expected-record.json") is { } stored)
            {
                RecordSchema.Check($"{directory}/expected-record.json", JsonNode.Parse(stored)!, drift);
            }
        }

        ContractFiles.VerifyNoStray(FinalizeContract.Family, FinalizeContract.Cases.Select(entry => entry.Name), FinalizeContract.Files, drift);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }

    [Fact]
    public async Task Every_inject_fixture_is_the_block_the_writer_renders()
    {
        var drift = new List<string>();
        var cases = await InjectContract.CasesAsync();
        foreach (var (name, definition) in cases)
        {
            var directory = $"fixtures/{InjectContract.Family}/{name}";
            var recordsJson = ContractJson.Write(InjectContract.ToRecordsJson(definition.Records));
            ContractFiles.Verify($"{directory}/records.json", recordsJson, drift);
            ContractFiles.Verify($"{directory}/options.json", ContractJson.Write(definition.Options), drift);

            var recordsText = ContractFiles.Read($"{directory}/records.json") ?? recordsJson;
            foreach (var (entry, index) in JsonNode.Parse(recordsText)!.AsArray().Select((entry, index) => (entry, index)))
            {
                RecordSchema.Check($"{directory}/records.json[{index}]", entry!["record"]!, drift);
            }

            var records = InjectContract.FromRecordsJson(recordsText);
            var options = ContractJson.Read<InjectOptions>(ContractFiles.Read($"{directory}/options.json") ?? ContractJson.Write(definition.Options));
            ContractFiles.Verify($"{directory}/expected-block.txt", InjectContract.Render(records, options), drift);
        }

        ContractFiles.VerifyNoStray(InjectContract.Family, cases.Select(entry => entry.Name), ["records.json", "options.json", "expected-block.txt"], drift);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }

    [Fact]
    public void Every_task_text_fixture_is_what_the_derivation_returns()
    {
        var drift = new List<string>();
        foreach (var (name, definition) in TaskTextContract.Cases)
        {
            var directory = $"fixtures/{TaskTextContract.Family}/{name}";
            ContractFiles.Verify($"{directory}/messages.json", ContractJson.Write(definition), drift);

            var input = ContractJson.Read<TaskTextInput>(ContractFiles.Read($"{directory}/messages.json") ?? ContractJson.Write(definition));
            ContractFiles.Verify($"{directory}/expected.json", ContractJson.Write(new TaskTextExpected(TaskTextContract.Derive(input))), drift);
        }

        ContractFiles.VerifyNoStray(TaskTextContract.Family, TaskTextContract.Cases.Select(entry => entry.Name), ["messages.json", "expected.json"], drift);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }

    [Fact]
    public void The_error_class_fixtures_are_what_the_classifier_returns()
    {
        // Inputs and answers in one file: the errors are the case definitions, the classes are the library's. cases.json is
        // contract 1.0's; cases-1.1.json holds the cases 1.1 added, so 1.0's file stays as it was published.
        var drift = new List<string>();
        foreach (var (file, cases) in new[] { ("cases.json", ErrorClassContract.Run()), (ErrorClassContract.AddedFileName, ErrorClassContract.RunAdded()) })
        {
            var path = $"fixtures/{ErrorClassContract.Family}/{file}";
            ContractFiles.Verify(path, ContractJson.Write(cases), drift);

            if (ContractFiles.Read(path) is { } text)
            {
                foreach (var entry in ContractJson.Read<List<ErrorClassCase>>(text))
                {
                    var actual = AgentExperience.Core.Reflections.ErrorClass.Of(entry.Error);
                    if (actual != entry.Class)
                    {
                        drift.Add($"{path}: the class of {entry.Error?[..Math.Min(entry.Error.Length, 40)]} is now '{actual}', not '{entry.Class}'.");
                    }
                }
            }
        }

        ContractFiles.VerifyNoStray(ErrorClassContract.Family, [], [], drift, "cases.json", ErrorClassContract.AddedFileName);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }

    [Fact]
    public void The_encoding_fixture_is_what_the_library_reads_and_derives()
    {
        var drift = new List<string>();
        var path = $"fixtures/{EncodingContract.Family}/{EncodingContract.FileName}";
        ContractFiles.Verify(path, ContractJson.Write(EncodingContract.Run()), drift);

        if (ContractFiles.Read(path) is { } text)
        {
            foreach (var (entry, index) in ContractJson.Read<List<EncodingCase>>(text).Select((entry, index) => (entry, index)))
            {
                var again = EncodingContract.Rerun(entry);
                if (again.Valid != entry.Valid || !JsonNode.DeepEquals(again.Canonical, entry.Canonical))
                {
                    drift.Add($"{path}[{index}]: the library now answers {again.Valid} {again.Canonical?.ToJsonString()} for this {entry.Kind}.");
                }
            }
        }

        ContractFiles.VerifyNoStray(EncodingContract.Family, [], [], drift, EncodingContract.FileName);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }

    [Fact]
    public void Every_search_seed_record_validates_against_the_schema()
    {
        // The search family's expected rows need PostgreSQL (the PostgreSQL contract test); its seeds do not.
        var problems = new List<string>();
        foreach (var (name, _) in SearchContract.Cases)
        {
            var directory = $"fixtures/{SearchContract.Family}/{name}";
            var seed = ContractFiles.Read($"{directory}/seed.json");
            Assert.True(seed is not null, $"{directory}/seed.json is missing; run the PostgreSQL contract tests with {ContractFiles.UpdateVariable}=1.");
            foreach (var (entry, index) in JsonNode.Parse(seed)!.AsArray().Select((entry, index) => (entry, index)))
            {
                RecordSchema.Check($"{directory}/seed.json[{index}]", entry!, problems);
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void The_contract_lock_is_the_published_one_and_covers_every_contract_file()
    {
        if (ContractFiles.Updating)
        {
            // Regenerating: write the lock once every family has written its files (each family relocks too).
            var owed = new List<string>();
            ContractLock.Relock(owed);
            ContractFiles.AssertNoDrift(owed);
            return;
        }

        var drift = new List<string>();
        ContractLock.Verify(drift);
        ContractFiles.AssertNoDrift(drift);
    }

    /// <summary>In update mode, relocks after this family's writes; otherwise the lock test alone checks it.</summary>
    internal static void Relock(List<string> drift)
    {
        if (ContractFiles.Updating)
        {
            ContractLock.Relock(drift);
        }
    }

    [Fact]
    public void The_bounds_declaration_is_the_one_the_adapter_runs()
    {
        Assert.Equal(ExperienceSessionContext.DeclareSql, Statement(ContractFiles.Read("sql/declare-bounds.sql")!));
    }

    [Fact]
    public void The_search_statement_names_every_argument_and_column_of_the_function_in_order()
    {
        var sql = Statement(ContractFiles.Read("sql/search-experience-text.sql")!);

        var arguments = TextSearchFunction.Arguments.Split(", ").Select(argument => argument.Split(' ')[0]).ToList();
        Assert.Equal(arguments, NamedArgument().Matches(sql).Select(match => match.Groups[1].Value).ToList());

        var columns = TextSearchFunction.Columns.Split(", ").Select(column => column.Split(' ')[0]).ToList();
        var selected = sql[("SELECT ".Length)..sql.IndexOf("FROM", StringComparison.Ordinal)]
            .Split(',')
            .Select(column => column.Trim())
            .ToList();
        Assert.Equal(columns, selected);
        Assert.Contains("FROM agent_experience.search_experience_text(", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_application_role_privileges_are_the_statements_the_migrator_runs()
    {
        // On a schema the migrator has just applied: every non-optional table exists, and none has row-level security yet.
        const string role = ":\"application_role\"";
        var tables = ApplicationRolePrivileges.Tables.Where(table => !table.Optional).Select(table => table.Name).ToHashSet(StringComparer.Ordinal);
        var fresh = tables.ToDictionary(
            table => table,
            _ => new ApplicationRolePrivileges.RowSecurityState(Enabled: false, Forced: false, Policies: []),
            StringComparer.Ordinal);
        var (always, rowLevelSecurity) = PrivilegeStatements(ContractFiles.Read("sql/application-role-privileges.sql")!);

        foreach (var enable in new[] { false, true })
        {
            var options = new ExperienceApplicationRoleOptions("contract_app") { EnableRowLevelSecurity = enable };
            List<string> expected =
            [
                .. ApplicationRolePrivileges.Statements(role, tables, options),
                .. ApplicationRolePrivileges.RowLevelSecurityStatements(fresh, tables, enable),
            ];
            Assert.Equal(expected, enable ? [.. always, .. rowLevelSecurity] : always);
        }
    }

    [Fact]
    public void The_injection_record_statement_is_the_one_the_store_runs()
    {
        // Contract 1.4: the upsert the .NET store runs is the contract file's statement, line for line.
        Assert.Equal(PostgresExperienceRecordStore.RecordInjectionSql, Statement(ContractFiles.Read("sql/record-injection.sql")!));
    }

    [Fact]
    public void The_1_0_and_1_4_privileges_files_together_are_what_the_migrator_grants_on_a_schema_with_the_injection_table()
    {
        // On a schema migrated to 0026 or later: every non-optional table and the injection table exist, and none has
        // row-level security yet. The 1.0 file, then the 1.4 file, is the .NET call's grants, each mode in turn; grants
        // commute, so the set is compared, and neither side may repeat a statement.
        const string role = ":\"application_role\"";
        var tables = ApplicationRolePrivileges.Tables
            .Where(table => !table.Optional || table.Name == ApplicationRolePrivileges.InjectionsTable)
            .Select(table => table.Name)
            .ToHashSet(StringComparer.Ordinal);
        var fresh = tables.ToDictionary(
            table => table,
            _ => new ApplicationRolePrivileges.RowSecurityState(Enabled: false, Forced: false, Policies: []),
            StringComparer.Ordinal);
        var (always, rowLevelSecurity) = PrivilegeStatements(ContractFiles.Read("sql/application-role-privileges.sql")!);
        var (always14, rowLevelSecurity14) = PrivilegeStatements(ContractFiles.Read("sql/application-role-privileges-1.4.sql")!);
        Assert.NotEmpty(always14);
        Assert.NotEmpty(rowLevelSecurity14);
        Assert.All(
            [.. always14, .. rowLevelSecurity14],
            statement => Assert.Contains("agent_experience.experience_injections", statement, StringComparison.Ordinal));

        foreach (var enable in new[] { false, true })
        {
            var options = new ExperienceApplicationRoleOptions("contract_app") { EnableRowLevelSecurity = enable };
            List<string> expected =
            [
                .. ApplicationRolePrivileges.Statements(role, tables, options),
                .. ApplicationRolePrivileges.RowLevelSecurityStatements(fresh, tables, enable),
            ];
            List<string> contract = enable ? [.. always, .. rowLevelSecurity, .. always14, .. rowLevelSecurity14] : [.. always, .. always14];
            Assert.Equal(expected.Count, expected.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(contract.Count, contract.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(expected.Order(StringComparer.Ordinal), contract.Order(StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// <c>sql/application-role-privileges.sql</c>'s statements: those under <c>-- mode: always</c>, and those under
    /// <c>-- mode: row-level-security</c>. Each is one line ending in <c>;</c>, returned without it; other comment lines are skipped.
    /// </summary>
    private static (List<string> Always, List<string> RowLevelSecurity) PrivilegeStatements(string sql)
    {
        List<string> always = [];
        List<string> rowLevelSecurity = [];
        List<string>? current = null;
        foreach (var line in sql.Split('\n').Where(line => line.Length > 0))
        {
            if (ModeMarker().IsMatch(line))
            {
                current = line switch
                {
                    "-- mode: always" => always,
                    "-- mode: row-level-security" => rowLevelSecurity,
                    _ => throw new Xunit.Sdk.XunitException(
                        $"Unknown mode marker '{line}': the markers are '-- mode: always' and '-- mode: row-level-security'."),
                };
                continue;
            }

            if (line.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.True(current is not null, $"A statement before any mode marker: {line}");
            Assert.True(line.EndsWith(';') && line.IndexOf(';') == line.Length - 1, $"Not one statement on one line ending in ';': {line}");
            current.Add(line[..^1]);
        }

        return (always, rowLevelSecurity);
    }

    [Fact]
    public void The_readmes_name_the_major_version_and_every_minor_version_the_fixtures_are_locked_under()
    {
        // The README is the major version's and never changes within it; each minor version N adds README-major.N.md.
        var parts = ContractFiles.ContractVersion.Split('.');
        var (major, minor) = (int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        var readme = ContractFiles.Read("README.md")!;
        Assert.Contains($"**Version: `{major}.0`.**", readme, StringComparison.Ordinal);
        Assert.Contains($"`payload_version` {ExperiencePayload.CurrentVersion}", readme, StringComparison.Ordinal);
        for (var version = 1; version <= minor; version++)
        {
            var additions = ContractFiles.Read($"README-{major}.{version}.md");
            Assert.True(additions is not null, $"README-{major}.{version}.md is missing.");
            Assert.StartsWith($"# Contract {major}.{version}: additions\n", additions, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_contract_file_is_utf8_without_a_bom_and_lf_only()
    {
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(ContractFiles.Root, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            var relative = Path.GetRelativePath(ContractFiles.Root, file);
            if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            {
                problems.Add($"{relative}: starts with a byte-order mark.");
            }

            if (bytes.Contains((byte)'\r'))
            {
                problems.Add($"{relative}: holds a carriage return.");
            }

            try
            {
                _ = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (System.Text.DecoderFallbackException)
            {
                problems.Add($"{relative}: is not valid UTF-8.");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>A SQL file's statement: its leading <c>--</c> comment lines removed, and the trailing line break.</summary>
    internal static string Statement(string sql) =>
        string.Join('\n', sql.Split('\n').SkipWhile(line => line.StartsWith("--", StringComparison.Ordinal))).TrimEnd('\n');

    [GeneratedRegex(@"^--\s*mode\b", RegexOptions.IgnoreCase)]
    private static partial Regex ModeMarker();

    [GeneratedRegex(@"(p_\w+) => @\w+")]
    private static partial Regex NamedArgument();
}

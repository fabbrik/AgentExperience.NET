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
    public void The_error_class_fixture_is_what_the_classifier_returns()
    {
        // Inputs and answers in one file: the errors are the case definitions, the classes are the library's.
        var drift = new List<string>();
        var path = $"fixtures/{ErrorClassContract.Family}/cases.json";
        ContractFiles.Verify(path, ContractJson.Write(ErrorClassContract.Run()), drift);

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

        ContractFiles.VerifyNoStray(ErrorClassContract.Family, [], [], drift, "cases.json");
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
    public void The_readme_names_the_version_the_fixtures_are_locked_under()
    {
        var readme = ContractFiles.Read("README.md")!;
        Assert.Contains($"**Version: `{ContractFiles.ContractVersion}`.**", readme, StringComparison.Ordinal);
        Assert.Contains($"`payload_version` {ExperiencePayload.CurrentVersion}", readme, StringComparison.Ordinal);
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

    [GeneratedRegex(@"(p_\w+) => @\w+")]
    private static partial Regex NamedArgument();
}

using System.Globalization;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.MicrosoftAgentFramework;
using AgentExperience.Storage.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// The conformance runner for contract 1.7's lessons-view family, against a schema the .NET migrator applied with
/// row-level security on. Each case seeds a database as a port would (the record columns and the injection rows), then
/// lists the lessons through the .NET lessons view over the .NET PostgreSQL store (which generates <c>expected.json</c>),
/// and runs <c>contract/sql/list-experience-lessons.sql</c> exactly as written, as a port runs it: the rows it returns
/// must be the ones the store read (the same records in the same order, with the same injection summaries), and so must the
/// statements that leave the injections out.
/// </summary>
[Collection(ContractPostgresCollection.Name)]
public sealed class LessonsViewContractPostgresTests(ContractPostgresDatabase database)
{
    [Fact]
    public async Task Every_lessons_view_fixture_is_what_the_dotnet_view_lists_and_the_contract_statement_reads()
    {
        var drift = new List<string>();
        foreach (var (name, definition) in LessonsViewContract.Cases)
        {
            var directory = $"fixtures/{LessonsViewContract.Family}/{name}";
            var seedJson = ContractJson.Write(LessonsViewContract.ToSeedJson(definition));
            var queryJson = ContractJson.Write(definition.Query);
            ContractFiles.Verify($"{directory}/seed.json", seedJson, drift);
            ContractFiles.Verify($"{directory}/query.json", queryJson, drift);

            var seed = JsonNode.Parse(ContractFiles.Read($"{directory}/seed.json") ?? seedJson)!.AsObject();
            var query = ContractJson.Read<LessonsViewQuery>(ContractFiles.Read($"{directory}/query.json") ?? queryJson);

            var application = await database.CreateAsync("lessons_" + name);
            var owner = database.OwnerOf("lessons_" + name);
            foreach (var record in seed["records"]!.AsArray())
            {
                await SearchContractPostgresTests.InsertAsync(application, record!);
            }

            foreach (var row in seed["injections"]!.AsArray())
            {
                await InjectionRecordContractPostgresTests.InsertInjectionAsync(owner, row!.AsObject());
            }

            var page = await ListAsync(application, query);
            ContractFiles.Verify($"{directory}/expected.json", ContractJson.Write(LessonsViewContract.PageJson(page)), drift);

            // The statements as written, against what the .NET store read for the same probe.
            var store = new PostgresExperienceRecordStore(application);
            var probe = LessonsViewContract.Probe(query);
            var storeQuery = new ExperienceRecordQuery(query.Scope, query.Statuses?.ToArray(), probe);
            var read = await store.QueryWithInjectionsAsync(SearchContractPostgresTests.Authorization(query.Scope), storeQuery, CancellationToken.None);
            Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
            var expected = read.Records.Select(record => (record.ExperienceId, InjectionText(read.Injections?.GetValueOrDefault(record.ExperienceId)))).ToList();
            var withInjections = await RunStatementAsync(application, query, probe, injections: true);
            if (!expected.SequenceEqual(withInjections))
            {
                drift.Add($"{directory}: the contract statement reads {Describe(withInjections)}, the .NET store {Describe(expected)}.");
            }

            var without = await RunStatementAsync(application, query, probe, injections: false);
            var plain = expected.Select(entry => (entry.ExperienceId, (string?)null)).ToList();
            if (!plain.SequenceEqual(without))
            {
                drift.Add($"{directory}: the contract statement without injections reads {Describe(without)}, not {Describe(plain)}.");
            }
        }

        ContractFiles.VerifyNoStray(
            LessonsViewContract.Family,
            LessonsViewContract.Cases.Select(entry => entry.Name),
            ["seed.json", "query.json", "expected.json"],
            drift);
        if (ContractFiles.Updating)
        {
            ContractLock.Relock(drift);
        }

        ContractFiles.AssertNoDrift(drift);
    }

    [Fact]
    public async Task A_page_without_the_injections_table_is_the_statement_without_injections()
    {
        // Contract 1.7: undefined_table and insufficient_privilege answer the page alone, injections not tracked.
        var application = await database.CreateAsync("lessons_untracked");
        var owner = database.OwnerOf("lessons_untracked");
        var record = SearchContract.Seed(0x78, 1, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt.");
        await SearchContractPostgresTests.InsertAsync(application, ContractJson.ToStored(record));

        var query = new LessonsViewQuery(string.Empty, Fixed.Scope, null, 50, true, LessonsViewContract.Now);
        var tracked = await ListAsync(application, query);
        Assert.True(tracked.InjectionsTracked);

        await using (var revoke = owner.CreateCommand(
            $"REVOKE SELECT ON agent_experience.experience_injections FROM \"{ContractPostgresDatabase.ApplicationRole}\""))
        {
            await revoke.ExecuteNonQueryAsync();
        }

        var statement = await Assert.ThrowsAsync<PostgresException>(() => RunStatementAsync(application, query, 51, injections: true));
        Assert.Equal("42501", statement.SqlState);
        Assert.Equal([(record.ExperienceId, (string?)null)], await RunStatementAsync(application, query, 51, injections: false));

        var untracked = await ListAsync(application, query);
        Assert.False(untracked.InjectionsTracked);
        Assert.Equal([record.ExperienceId], untracked.Lessons.Select(lesson => lesson.ExperienceId));
        Assert.All(untracked.Lessons, lesson => Assert.Null(lesson.LastInjectedAt));

        await using (var drop = owner.CreateCommand("DROP TABLE agent_experience.experience_injections CASCADE"))
        {
            await drop.ExecuteNonQueryAsync();
        }

        var missing = await Assert.ThrowsAsync<PostgresException>(() => RunStatementAsync(application, query, 51, injections: true));
        Assert.Equal("42P01", missing.SqlState);
        Assert.False((await ListAsync(application, query)).InjectionsTracked);
    }

    /// <summary>The page the .NET lessons view lists for the case's query, with the case's clock.</summary>
    private static async Task<ExperienceLessonsPage> ListAsync(NpgsqlDataSource application, LessonsViewQuery query)
    {
        var services = new ServiceCollection();
        services.AddAgentExperience(options =>
            {
                options.ResolveIdentity = (_, _) => ValueTask.FromResult<ExperienceIdentity?>(null);
                options.TimeProvider = new LessonsViewContract.FixedClock(query.Now);
            })
            .UsePostgres(application);
        await using var provider = services.BuildServiceProvider();
        return await provider.GetAgentExperienceLessons().ListAsync(
            new ExperienceIdentity(SearchContractPostgresTests.Authorization(query.Scope), query.Scope),
            new ExperienceLessonsQuery { Statuses = query.Statuses, Limit = query.Limit, IncludeIneligible = query.IncludeIneligible });
    }

    /// <summary>
    /// <c>declare-bounds.sql</c> for the query's scope, then the statement of <c>list-experience-lessons.sql</c> the query
    /// calls for, as written. Returns each row's ID and injection summary (<c>null</c> for none, or for a statement without).
    /// </summary>
    private static async Task<List<(Guid ExperienceId, string? Injection)>> RunStatementAsync(
        NpgsqlDataSource application, LessonsViewQuery query, int limit, bool injections)
    {
        var statements = VectorStatements.Read(LessonsViewContract.File);
        var key = (injections ? "page" : "page-without-injections") + (query.Statuses is null ? string.Empty : "-with-statuses");
        await using var connection = await application.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SearchContractPostgresTests.DeclareAsync(connection, transaction, query.Scope);
        await using var command = new NpgsqlCommand(statements[key], connection, transaction);
        var scope = query.Scope;
        var parameters = command.Parameters;
        parameters.Add(new("tenant_id", NpgsqlDbType.Text) { Value = scope.TenantId });
        parameters.Add(new("application_id", NpgsqlDbType.Text) { Value = scope.ApplicationId });
        parameters.Add(new("project_id", NpgsqlDbType.Text) { Value = scope.ProjectId });
        parameters.Add(new("team_id", NpgsqlDbType.Text) { Value = (object?)scope.TeamId ?? DBNull.Value });
        parameters.Add(new("agent_id", NpgsqlDbType.Text) { Value = (object?)scope.AgentId ?? DBNull.Value });
        parameters.Add(new("user_id", NpgsqlDbType.Text) { Value = (object?)scope.UserId ?? DBNull.Value });
        if (query.Statuses is not null)
        {
            parameters.Add(new("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = query.Statuses.Select(status => status.ToString()).Distinct().ToArray() });
        }

        parameters.Add(new("limit", NpgsqlDbType.Integer) { Value = limit });
        var rows = new List<(Guid, string?)>();
        await using var reader = await command.ExecuteReaderAsync();
        Assert.Equal("experience_id", reader.GetName(0));
        Assert.Equal("payload", reader.GetName(17));
        Assert.Equal(injections ? 20 : 18, reader.FieldCount);
        while (await reader.ReadAsync())
        {
            string? injection = null;
            if (injections && !reader.IsDBNull(18))
            {
                injection = InjectionText(new ExperienceInjectionSummary(reader.GetFieldValue<DateTimeOffset>(18), reader.GetInt64(19)));
            }

            rows.Add((reader.GetGuid(0), injection));
        }

        return rows;
    }

    private static string? InjectionText(ExperienceInjectionSummary? summary) =>
        summary is null ? null : InjectionRecordContract.PostgresText(summary.LastInjectedAt) + " x" + summary.Count.ToString(CultureInfo.InvariantCulture);

    private static string Describe(IEnumerable<(Guid ExperienceId, string? Injection)> rows) =>
        "[" + string.Join(", ", rows.Select(row => row.ExperienceId.ToString("D", CultureInfo.InvariantCulture) + (row.Injection is null ? string.Empty : " " + row.Injection))) + "]";
}

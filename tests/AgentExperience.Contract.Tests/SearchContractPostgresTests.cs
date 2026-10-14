using System.Globalization;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Retrieval;
using AgentExperience.Storage.Postgres;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// The conformance runner for the search family, against a schema the .NET migrator applied with row-level security on
/// -- the configuration in which the application role may execute <c>agent_experience.search_experience_text</c>. Each
/// case gets its own database: its seed is inserted as a port would insert it (the record columns, as the application
/// role, inside a transaction that declared its bounds), the contract's two statements are run exactly as written in
/// <c>contract/sql/</c>, and the rows must come back in the checked-in order. The .NET candidate source must agree with
/// them, and Core's ranking over it must give the checked-in ranked order.
/// </summary>
[Collection(ContractPostgresCollection.Name)]
public sealed class SearchContractPostgresTests(ContractPostgresDatabase database)
{
    [Fact]
    public async Task Every_search_fixture_is_what_the_migrated_schema_returns()
    {
        var drift = new List<string>();
        foreach (var (name, definition) in SearchContract.Cases)
        {
            var directory = $"fixtures/{SearchContract.Family}/{name}";
            var seedJson = ContractJson.Write(SearchContract.ToSeedJson(definition.Seed));
            ContractFiles.Verify($"{directory}/seed.json", seedJson, drift);
            ContractFiles.Verify($"{directory}/query.json", ContractJson.Write(definition.Query), drift);

            var seed = JsonNode.Parse(ContractFiles.Read($"{directory}/seed.json") ?? seedJson)!.AsArray();
            var query = ContractJson.Read<SearchQuery>(ContractFiles.Read($"{directory}/query.json") ?? ContractJson.Write(definition.Query));

            var application = await database.CreateAsync(name);
            foreach (var record in seed)
            {
                await InsertAsync(application, record!);
            }

            var rows = await SearchAsync(application, query);
            var (ranked, truncated) = await RankAsync(application, query);

            // The .NET candidate source runs the same function under row-level security: it must agree with the contract's statement.
            var candidates = await new PostgresExperienceCandidateSource(application).SearchAsync(
                Authorization(query.Scope),
                new ExperienceCandidateQuery(query.Scope, query.TaskText, query.Statuses, query.MinConfidence, query.Limit)
                {
                    ExcludeModelAuthored = query.ExcludeModelAuthored,
                    MinimumMatchedTerms = query.MinimumMatchedTerms,
                },
                CancellationToken.None);
            Assert.Equal(ExperienceStoreOutcome.Found, candidates.Outcome);
            Assert.Equal(rows.Select(row => row.ExperienceId), candidates.Candidates.Select(candidate => candidate.Record.ExperienceId));

            ContractFiles.Verify($"{directory}/expected.json", ContractJson.Write(new SearchExpected(rows, truncated, ranked)), drift);
        }

        ContractFiles.VerifyNoStray(SearchContract.Family, SearchContract.Cases.Select(entry => entry.Name), ["seed.json", "query.json", "expected.json"], drift);
        if (ContractFiles.Updating)
        {
            ContractLock.Relock(drift);
        }
        ContractFiles.AssertNoDrift(drift);
    }

    /// <summary>Inserts one stored record as a port writes it: the eighteen record columns, under declared bounds.</summary>
    internal static async Task InsertAsync(NpgsqlDataSource application, JsonNode stored)
    {
        var columns = stored["columns"]!.AsObject();
        await using var connection = await application.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await DeclareAsync(connection, transaction, new Scope(
            columns["tenant_id"]!.GetValue<string>(),
            columns["application_id"]!.GetValue<string>(),
            columns["project_id"]!.GetValue<string>(),
            columns["team_id"]?.GetValue<string>(),
            columns["agent_id"]?.GetValue<string>(),
            columns["user_id"]?.GetValue<string>()));

        await using var insert = new NpgsqlCommand(
            "INSERT INTO agent_experience.experience_records (experience_id, source_run_id, tenant_id, application_id, project_id, " +
            "team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, " +
            "created_at, updated_at, payload_version, payload) VALUES (@experience_id, @source_run_id, @tenant_id, @application_id, " +
            "@project_id, @team_id, @agent_id, @user_id, @task_id, @status, @reuse_confidence, @supporting_validations, " +
            "@contradictions, @revision, @created_at, @updated_at, @payload_version, @payload)",
            connection,
            transaction);
        var parameters = insert.Parameters;
        parameters.Add(new("experience_id", NpgsqlDbType.Uuid) { Value = Guid.Parse(columns["experience_id"]!.GetValue<string>()) });
        parameters.Add(new("source_run_id", NpgsqlDbType.Uuid) { Value = Guid.Parse(columns["source_run_id"]!.GetValue<string>()) });
        foreach (var text in new[] { "tenant_id", "application_id", "project_id", "team_id", "agent_id", "user_id", "task_id", "status" })
        {
            parameters.Add(new(text, NpgsqlDbType.Text) { Value = (object?)columns[text]?.GetValue<string>() ?? DBNull.Value });
        }

        parameters.Add(new("reuse_confidence", NpgsqlDbType.Double) { Value = columns["reuse_confidence"]!.GetValue<double>() });
        parameters.Add(new("supporting_validations", NpgsqlDbType.Integer) { Value = columns["supporting_validations"]!.GetValue<int>() });
        parameters.Add(new("contradictions", NpgsqlDbType.Integer) { Value = columns["contradictions"]!.GetValue<int>() });
        parameters.Add(new("revision", NpgsqlDbType.Bigint) { Value = columns["revision"]!.GetValue<long>() });
        parameters.Add(new("created_at", NpgsqlDbType.TimestampTz) { Value = Instant(columns["created_at"]!) });
        parameters.Add(new("updated_at", NpgsqlDbType.TimestampTz) { Value = Instant(columns["updated_at"]!) });
        parameters.Add(new("payload_version", NpgsqlDbType.Integer) { Value = columns["payload_version"]!.GetValue<int>() });
        parameters.Add(new("payload", NpgsqlDbType.Jsonb) { Value = stored["payload"]!.ToJsonString() });
        await insert.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    /// <summary>Runs <c>contract/sql/declare-bounds.sql</c> then <c>contract/sql/search-experience-text.sql</c>, as written.</summary>
    private static async Task<IReadOnlyList<SearchRow>> SearchAsync(NpgsqlDataSource application, SearchQuery query)
    {
        await using var connection = await application.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await DeclareAsync(connection, transaction, query.Scope);

        await using var search = new NpgsqlCommand(ContractFiles.Read("sql/search-experience-text.sql"), connection, transaction);
        var scope = query.Scope;
        var parameters = search.Parameters;
        parameters.Add(new("tenant_id", NpgsqlDbType.Text) { Value = scope.TenantId });
        parameters.Add(new("application_id", NpgsqlDbType.Text) { Value = scope.ApplicationId });
        parameters.Add(new("project_id", NpgsqlDbType.Text) { Value = scope.ProjectId });
        parameters.Add(new("team_id", NpgsqlDbType.Text) { Value = (object?)scope.TeamId ?? DBNull.Value });
        parameters.Add(new("agent_id", NpgsqlDbType.Text) { Value = (object?)scope.AgentId ?? DBNull.Value });
        parameters.Add(new("user_id", NpgsqlDbType.Text) { Value = (object?)scope.UserId ?? DBNull.Value });
        parameters.Add(new("task_text", NpgsqlDbType.Text) { Value = query.TaskText });
        parameters.Add(new("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = query.Statuses.Select(status => status.ToString()).ToArray() });
        parameters.Add(new("min_confidence", NpgsqlDbType.Double) { Value = query.MinConfidence });
        parameters.Add(new("limit", NpgsqlDbType.Integer) { Value = query.Limit });
        parameters.Add(new("exclude_model_authored", NpgsqlDbType.Boolean) { Value = query.ExcludeModelAuthored });
        parameters.Add(new("with_grants", NpgsqlDbType.Boolean) { Value = query.WithGrants });
        parameters.Add(new("minimum_matched_terms", NpgsqlDbType.Integer) { Value = query.MinimumMatchedTerms });

        var rows = new List<SearchRow>();
        await using (var reader = await search.ExecuteReaderAsync())
        {
            Assert.Equal("experience_id", reader.GetName(0));
            Assert.Equal("relevance", reader.GetName(18));
            while (await reader.ReadAsync())
            {
                rows.Add(new SearchRow(reader.GetGuid(0), reader.GetFloat(18)));

            }
        }

        await transaction.CommitAsync();
        return rows;
    }

    /// <summary>Core's ranking over the .NET candidate source, at the case's clock, with the default policy and weights.</summary>
    private static async Task<(IReadOnlyList<RankedRow> Ranked, bool Truncated)> RankAsync(NpgsqlDataSource application, SearchQuery query)
    {
        var retrieval = new ExperienceRetrievalService(
            new PostgresExperienceCandidateSource(application),
            RetrievalPolicy.Default with { MinimumConfidence = query.MinConfidence, MinimumMatchedTerms = query.MinimumMatchedTerms },
            RankingWeights.Default,
            new FixedTimeProvider(query.Now));
        var result = await retrieval.RetrieveAsync(
            new RetrieveExperienceRequest(Authorization(query.Scope), query.Scope, query.TaskText)
            {
                PreferredEnvironmentAttributes = query.PreferredEnvironment,
                ExcludeModelAuthored = query.ExcludeModelAuthored,
            },
            CancellationToken.None);
        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);

        IReadOnlyList<RankedRow> ranked = [.. result.Records.Select(entry => new RankedRow(
            entry.Record.ExperienceId,
            entry.Score,
            [.. entry.Components.Select(component => new RankingComponentInput(component.Kind, component.Value, component.Weight))]))];
        return (ranked, result.Truncated);
    }

    private static async Task DeclareAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Scope bounds)
    {
        await using var declare = new NpgsqlCommand(ContractFiles.Read("sql/declare-bounds.sql"), connection, transaction);
        declare.Parameters.Add(new("auth_tenant", NpgsqlDbType.Text) { Value = "=" + bounds.TenantId });
        declare.Parameters.Add(new("auth_application", NpgsqlDbType.Text) { Value = Bound(bounds.ApplicationId) });
        declare.Parameters.Add(new("auth_project", NpgsqlDbType.Text) { Value = Bound(bounds.ProjectId) });
        declare.Parameters.Add(new("auth_team", NpgsqlDbType.Text) { Value = Bound(bounds.TeamId) });
        declare.Parameters.Add(new("auth_agent", NpgsqlDbType.Text) { Value = Bound(bounds.AgentId) });
        declare.Parameters.Add(new("auth_user", NpgsqlDbType.Text) { Value = Bound(bounds.UserId) });
        await declare.ExecuteNonQueryAsync();
    }

    /// <summary>A bound as the policies read it: <c>=</c> and the value, or the empty string for no bound.</summary>
    private static string Bound(string? value) => value is null ? string.Empty : "=" + value;

    private static DateTimeOffset Instant(JsonNode value) =>
        DateTimeOffset.Parse(value.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    internal static AuthorizationContext Authorization(Scope scope) =>
        new(scope.TenantId, "contract-principal", [], Fixed.T0, scope.ApplicationId, scope.ProjectId, scope.TeamId, scope.AgentId, scope.UserId);
}

/// <summary>The one PostgreSQL container every contract test that needs one shares.</summary>
[CollectionDefinition(Name)]
public sealed class ContractPostgresCollection : ICollectionFixture<ContractPostgresDatabase>
{
    public const string Name = "Contract PostgreSQL";
}

/// <summary>
/// One stock PostgreSQL container for the search family and the stored-record round trip, with an owner role that runs the migrator and an application
/// role the privileges call grants, row-level security on.
/// </summary>
public sealed class ContractPostgresDatabase : IAsyncLifetime
{
    private const string Owner = "contract_owner";
    private const string Application = "contract_app";
    private const string Password = "contract-role-password";

    private readonly List<NpgsqlDataSource> _sources = [];
    private PostgreSqlContainer? _container;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder(AgentExperience.Tests.Shared.PostgresTestImage.Stock).Build();
        await _container.StartAsync();
        await ExecuteAsync(_container.GetConnectionString(), $"CREATE ROLE {Owner} LOGIN PASSWORD '{Password}'");
        await ExecuteAsync(_container.GetConnectionString(), $"CREATE ROLE {Application} LOGIN PASSWORD '{Password}'");

        // What a non-superuser owner needs to run the migrator (its erasure scripts set these parameters).
        await ExecuteAsync(
            _container.GetConnectionString(),
            $"GRANT SET ON PARAMETER agent_experience.purge_authorized, agent_experience.access_purge_authorized TO {Owner}");
    }

    /// <summary>A fresh database for one case, migrated, with the application role's privileges applied; its application-role data source.</summary>
    public async Task<NpgsqlDataSource> CreateAsync(string caseName)
    {
        var name = "contract_" + caseName.Replace('-', '_');
        Assert.True(name.Length <= 63 && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'), name);
        await ExecuteAsync(_container!.GetConnectionString(), $"CREATE DATABASE {name} OWNER {Owner}");

        // Disposed at once: every case gets a database, and pooled owner connections would exhaust the server's slots.
        await using (var owner = NpgsqlDataSource.Create(ConnectionString(name, Owner)))
        {
            await ExperienceSchemaMigrator.MigrateAsync(owner, CancellationToken.None);
            await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
                owner,
                new ExperienceApplicationRoleOptions(Application) { EnableRowLevelSecurity = true },
                CancellationToken.None);
        }

        return Source(name, Application);
    }

    /// <summary>The owner's data source on <paramref name="caseName"/>'s database, which <see cref="CreateAsync"/> made.</summary>
    public NpgsqlDataSource OwnerOf(string caseName) => Source("contract_" + caseName.Replace('-', '_'), Owner);

    /// <summary>The application role's name, as the privileges statements name it.</summary>
    public static string ApplicationRole => Application;

    public async Task DisposeAsync()
    {
        foreach (var source in _sources)
        {
            await source.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private NpgsqlDataSource Source(string database, string role)
    {
        var source = NpgsqlDataSource.Create(ConnectionString(database, role));
        _sources.Add(source);
        return source;
    }

    private string ConnectionString(string database, string role) =>
        new NpgsqlConnectionStringBuilder(_container!.GetConnectionString())
        {
            Database = database,
            Username = role,
            Password = Password,
        }.ConnectionString;

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var command = source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}

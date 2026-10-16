using System.Globalization;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres;
using AgentExperience.Storage.Postgres.Vectors;
using Npgsql;
using NpgsqlTypes;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// The conformance runner for contract 1.6's PostgreSQL families, <c>embedding-write</c> and <c>vector-search</c>, against
/// a schema the .NET migrator and the vectors migrator applied, with row-level security on. Each case runs twice, each time
/// in a database of its own: once with its writes through the .NET embedding index (which generates
/// <c>expected.json</c>), and once with the contract's write statements run exactly as written, as a port runs them. Both
/// must give the checked-in results. Every search step runs the contract's search statements as written, and the .NET
/// index's search must agree with them.
/// </summary>
[Collection(ContractVectorPostgresCollection.Name)]
public sealed class VectorContractPostgresTests(ContractVectorPostgresDatabase database)
{
    [Fact]
    public async Task Every_embedding_write_fixture_is_what_the_dotnet_index_and_the_contract_statements_give() =>
        await RunFamilyAsync(VectorContract.WriteFamily, "ew", VectorContract.WriteCases);

    [Fact]
    public async Task Every_vector_search_fixture_is_what_the_dotnet_index_and_the_contract_statements_give() =>
        await RunFamilyAsync(VectorContract.SearchFamily, "vs", VectorContract.SearchCases);

    [Fact]
    public async Task The_1_6_contract_script_after_the_1_0_and_1_4_scripts_grants_the_embeddings_table()
    {
        const string CaseName = "privileges-order-1-6";
        var application = await database.CreateAsync(CaseName);
        var owner = database.OwnerOf(CaseName);
        const string CanRead = "SELECT has_table_privilege(@role, 'agent_experience.experience_embeddings', 'SELECT')";

        await ContractPrivilegesOrderPostgresTests.ApplyContractFileAsync(owner, "sql/application-role-privileges.sql");
        Assert.False(await ScalarAsync<bool>(owner, CanRead));
        await ContractPrivilegesOrderPostgresTests.ApplyContractFileAsync(owner, "sql/application-role-privileges-1.4.sql");
        Assert.False(await ScalarAsync<bool>(owner, CanRead));
        await ContractPrivilegesOrderPostgresTests.ApplyContractFileAsync(owner, VectorStatements.PrivilegesFile);
        Assert.True(await ScalarAsync<bool>(owner, CanRead));

        // The contract write and the .NET index both work for the application role, and the .NET call after the three files
        // changes nothing it would refuse: the three files are exactly its grants.
        var record = SearchContract.Seed(0x7a, 1, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt.");
        await SearchContractPostgresTests.InsertAsync(application, ContractJson.ToStored(record));
        var write = new WriteEmbeddingStep(record.ExperienceId, record.Scope, VectorContract.Model, 4, "hash", record.Revision, [1f, 0f, 0f, 0f], Fixed.T0);
        Assert.Equal("Written", (await WriteAsWrittenAsync(application, write))["outcome"]!.GetValue<string>());
        await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
            owner,
            new ExperienceApplicationRoleOptions(ContractPostgresDatabase.ApplicationRole) { EnableRowLevelSecurity = true },
            CancellationToken.None);
        Assert.Equal("Written", (await WriteThroughIndexAsync(application, write with { Vector = [0f, 1f, 0f, 0f] }))["outcome"]!.GetValue<string>());
    }

    private async Task RunFamilyAsync(string family, string prefix, IReadOnlyList<(string Name, VectorCase Case)> cases)
    {
        var drift = new List<string>();
        foreach (var (name, definition) in cases)
        {
            var directory = $"fixtures/{family}/{name}";
            var seedJson = ContractJson.Write(VectorContract.ToSeedJson(definition));
            var stepsJson = ContractJson.Write(VectorContract.ToStepsJson(definition));
            ContractFiles.Verify($"{directory}/seed.json", seedJson, drift);
            ContractFiles.Verify($"{directory}/steps.json", stepsJson, drift);

            var seed = JsonNode.Parse(ContractFiles.Read($"{directory}/seed.json") ?? seedJson)!.AsArray();
            var steps = VectorContract.ReadSteps(JsonNode.Parse(ContractFiles.Read($"{directory}/steps.json") ?? stepsJson)!);

            var byIndex = await RunAsync($"{prefix}_{name}_net", seed, steps, throughIndex: true);
            ContractFiles.Verify($"{directory}/expected.json", ContractJson.Write(byIndex), drift);

            var byStatement = await RunAsync($"{prefix}_{name}_sql", seed, steps, throughIndex: false);
            if (!JsonNode.DeepEquals(byIndex, byStatement))
            {
                drift.Add($"{directory}: the contract statements give {byStatement.ToJsonString()}, the .NET index {byIndex.ToJsonString()}.");
            }
        }

        ContractFiles.VerifyNoStray(family, cases.Select(entry => entry.Name), ["seed.json", "steps.json", "expected.json"], drift);
        if (ContractFiles.Updating)
        {
            ContractLock.Relock(drift);
        }

        ContractFiles.AssertNoDrift(drift);
    }

    /// <summary>Seeds a fresh database, runs the steps, and returns what <c>expected.json</c> holds.</summary>
    private async Task<JsonObject> RunAsync(string caseName, JsonArray seed, IReadOnlyList<VectorStep> steps, bool throughIndex)
    {
        var name = caseName.Replace('-', '_');
        var application = await database.CreateAsync(name);
        var owner = database.OwnerOf(name);
        foreach (var record in seed)
        {
            await SearchContractPostgresTests.InsertAsync(application, record!);
        }

        var results = new JsonArray();
        foreach (var step in steps)
        {
            switch (step)
            {
                case WriteEmbeddingStep write:
                    results.Add(throughIndex ? await WriteThroughIndexAsync(application, write) : await WriteAsWrittenAsync(application, write));
                    break;
                case VectorSearchStep search:
                    results.Add(await SearchAsync(application, search));
                    break;
                case SetRevisionStep set:
                    await using (var command = owner.CreateCommand("UPDATE agent_experience.experience_records SET revision = @revision WHERE experience_id = @id"))
                    {
                        command.Parameters.Add(new("revision", NpgsqlDbType.Bigint) { Value = set.Revision });
                        command.Parameters.Add(new("id", NpgsqlDbType.Uuid) { Value = set.ExperienceId });
                        Assert.Equal(1, await command.ExecuteNonQueryAsync());
                    }

                    results.Add(null);
                    break;
                case EraseEmbeddingStep erase:
                    await InjectionRecordContractPostgresTests.EraseAsync(owner, erase.Erase);
                    results.Add(null);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown step {step}.");
            }
        }

        return new JsonObject
        {
            ["steps"] = results,
            ["experience_embeddings"] = await RowsAsync(owner),
        };
    }

    /// <summary>The .NET index's write, its clock fixed at the step's instant.</summary>
    private static async Task<JsonObject> WriteThroughIndexAsync(NpgsqlDataSource application, WriteEmbeddingStep write)
    {
        var index = new PostgresExperienceEmbeddingIndex(application, timeProvider: new FixedTimeProvider(write.Now));
        var result = await index.WriteAsync(
            SearchContractPostgresTests.Authorization(write.Scope),
            new ExperienceIndexWrite(
                write.Scope,
                write.ExperienceId,
                new ExperienceEmbeddingDescriptor(write.ModelId, write.Dimension, write.ContentHash, write.SourceRevision),
                write.Vector),
            CancellationToken.None);
        return result.Outcome switch
        {
            ExperienceIndexOutcome.Written => WriteResult("Written", null),
            ExperienceIndexOutcome.Stale => WriteResult("Stale", result.CurrentRevision),
            ExperienceIndexOutcome.Missing => WriteResult("Missing", null),
            _ => throw new Xunit.Sdk.XunitException($"The .NET index answered {result.Outcome} for a contract write."),
        };
    }

    /// <summary>
    /// <c>declare-bounds.sql</c> for the write's scope, then <c>write-experience-embedding.sql</c>'s <c>write</c> as written,
    /// and when it wrote nothing its <c>probe-revision</c>, in the same transaction.
    /// </summary>
    private static async Task<JsonObject> WriteAsWrittenAsync(NpgsqlDataSource application, WriteEmbeddingStep write)
    {
        var statements = VectorStatements.Read(VectorStatements.WriteFile);
        await using var connection = await application.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await DeclareAsync(connection, transaction, write.Scope);

        int written;
        await using (var command = new NpgsqlCommand(statements["write"], connection, transaction))
        {
            AddScope(command.Parameters, write.Scope);
            var ticks = write.Now.UtcTicks;
            command.Parameters.Add(new("experience_id", NpgsqlDbType.Uuid) { Value = write.ExperienceId });
            command.Parameters.Add(new("model_id", NpgsqlDbType.Text) { Value = write.ModelId });
            command.Parameters.Add(new("dimension", NpgsqlDbType.Integer) { Value = write.Dimension });
            command.Parameters.Add(new("content_hash", NpgsqlDbType.Text) { Value = write.ContentHash });
            command.Parameters.Add(new("source_revision", NpgsqlDbType.Bigint) { Value = write.SourceRevision });
            command.Parameters.Add(new("embedding", NpgsqlDbType.Text) { Value = VectorContract.Literal(write.Vector) });
            command.Parameters.Add(new("now", NpgsqlDbType.TimestampTz) { Value = new DateTimeOffset(ticks - (ticks % 10), TimeSpan.Zero) });
            written = await command.ExecuteNonQueryAsync();
        }

        JsonObject result;
        if (written > 0)
        {
            result = WriteResult("Written", null);
        }
        else
        {
            await using var probe = new NpgsqlCommand(statements["probe-revision"], connection, transaction);
            AddScope(probe.Parameters, write.Scope);
            probe.Parameters.Add(new("experience_id", NpgsqlDbType.Uuid) { Value = write.ExperienceId });
            result = await probe.ExecuteScalarAsync() is long revision ? WriteResult("Stale", revision) : WriteResult("Missing", null);
        }

        await transaction.CommitAsync();
        return result;
    }

    private static JsonObject WriteResult(string outcome, long? currentRevision)
    {
        var result = new JsonObject { ["outcome"] = outcome };
        if (currentRevision is { } revision)
        {
            result["currentRevision"] = revision;
        }

        return result;
    }

    /// <summary>
    /// <c>declare-bounds.sql</c>, then the contract's search statement as written (its <c>{dim}</c> replaced), and when it
    /// returned nothing the matching compatibility probe; the rows ordered by distance, then by ID. The .NET index's search
    /// must answer the same outcome and the same records with the same relevances.
    /// </summary>
    private static async Task<JsonObject> SearchAsync(NpgsqlDataSource application, VectorSearchStep search)
    {
        var statements = VectorStatements.Read(VectorStatements.SearchFile);
        var statuses = search.Statuses.Select(status => status.ToString()).ToArray();
        var rows = new List<(Guid Id, double Distance)>();
        string outcome;
        await using (var connection = await application.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await DeclareAsync(connection, transaction, search.Scope);
            var sql = VectorStatements.WithDimension(
                statements[search.ExcludeModelAuthored ? "search-excluding-model-authored" : "search"],
                search.Vector.Length);
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                AddSearch(command.Parameters, search, statuses);
                command.Parameters.Add(new("query_vector", NpgsqlDbType.Text) { Value = VectorContract.Literal(search.Vector) });
                command.Parameters.Add(new("limit", NpgsqlDbType.Integer) { Value = search.Limit });
                await using var reader = await command.ExecuteReaderAsync();
                Assert.Equal("experience_id", reader.GetName(0));
                var distance = reader.GetOrdinal("distance");
                while (await reader.ReadAsync())
                {
                    rows.Add((reader.GetGuid(0), reader.GetDouble(distance)));
                }
            }

            if (rows.Count > 0)
            {
                outcome = nameof(ExperienceVectorSearchOutcome.Found);
            }
            else
            {
                var probeSql = statements[search.ExcludeModelAuthored ? "compatibility-excluding-model-authored" : "compatibility"];
                Assert.DoesNotContain(VectorStatements.DimensionPlaceholder, probeSql, StringComparison.Ordinal);
                await using var probe = new NpgsqlCommand(probeSql, connection, transaction);
                AddSearch(probe.Parameters, search, statuses);
                await using var reader = await probe.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                var (anything, thisModel) = (reader.GetBoolean(0), reader.GetBoolean(1));
                outcome = !anything
                    ? nameof(ExperienceVectorSearchOutcome.Found)
                    : thisModel ? nameof(ExperienceVectorSearchOutcome.DimensionMismatch) : nameof(ExperienceVectorSearchOutcome.ModelMismatch);
            }

            await transaction.CommitAsync();
        }

        // No tie-break in SQL: equal distances (NaN equal to NaN, after every number) are ordered by ID.
        var ordered = rows
            .OrderBy(row => double.IsNaN(row.Distance) ? 1 : 0)
            .ThenBy(row => double.IsNaN(row.Distance) ? 0d : row.Distance)
            .ThenBy(row => row.Id.ToString("D", CultureInfo.InvariantCulture), StringComparer.Ordinal)
            .ToList();

        var dotnet = await new PostgresExperienceEmbeddingIndex(application).SearchAsync(
            SearchContractPostgresTests.Authorization(search.Scope),
            new ExperienceVectorQuery(search.Scope, search.ModelId, search.Vector, search.Statuses, search.MinConfidence, search.Limit)
            {
                ExcludeModelAuthored = search.ExcludeModelAuthored,
            },
            CancellationToken.None);
        Assert.Equal(outcome, dotnet.Outcome.ToString());
        Assert.Equal(
            ByRelevance(ordered.Select(row => (row.Id, VectorContract.Relevance(row.Distance)))),
            ByRelevance(dotnet.Candidates.Select(candidate => (candidate.Record.ExperienceId, candidate.Relevance))));

        return new JsonObject
        {
            ["outcome"] = outcome,
            ["rows"] = new JsonArray([.. ordered.Select(row => (JsonNode)new JsonObject
            {
                ["experienceId"] = row.Id.ToString("D", CultureInfo.InvariantCulture),
                ["distance"] = double.IsNaN(row.Distance) ? null : JsonValue.Create(row.Distance),
                ["relevance"] = VectorContract.Relevance(row.Distance),
            })]),
        };

        static List<(Guid, double)> ByRelevance(IEnumerable<(Guid Id, double Relevance)> entries) =>
        [
            .. entries.OrderByDescending(entry => entry.Relevance)
                .ThenBy(entry => entry.Id.ToString("D", CultureInfo.InvariantCulture), StringComparer.Ordinal)
                .Select(entry => (entry.Id, entry.Relevance)),
        ];
    }

    /// <summary>Every row of <c>agent_experience.experience_embeddings</c>, read as the owner, ordered by <c>experience_id</c>, each column in PostgreSQL's text form in UTC.</summary>
    private static async Task<JsonArray> RowsAsync(NpgsqlDataSource owner)
    {
        string[] columns =
        [
            "experience_id", "tenant_id", "application_id", "project_id", "team_id", "agent_id", "user_id", "model_id", "dimension",
            "content_hash", "source_revision", "embedding", "created_at", "updated_at",
        ];
        await using var connection = await owner.OpenConnectionAsync();
        await using (var zone = new NpgsqlCommand("SET TIME ZONE 'UTC'", connection))
        {
            await zone.ExecuteNonQueryAsync();
        }

        await using var select = new NpgsqlCommand(
            "SELECT " + string.Join(", ", columns.Select(column => column + "::text")) +
            " FROM agent_experience.experience_embeddings ORDER BY experience_id",
            connection);
        var rows = new JsonArray();
        await using var reader = await select.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new JsonObject();
            for (var i = 0; i < columns.Length; i++)
            {
                row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetString(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static void AddSearch(NpgsqlParameterCollection parameters, VectorSearchStep search, string[] statuses)
    {
        AddScope(parameters, search.Scope);
        parameters.Add(new("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = statuses });
        parameters.Add(new("min_confidence", NpgsqlDbType.Double) { Value = search.MinConfidence });
        parameters.Add(new("model_id", NpgsqlDbType.Text) { Value = search.ModelId });
    }

    private static void AddScope(NpgsqlParameterCollection parameters, Scope scope)
    {
        parameters.Add(new("tenant_id", NpgsqlDbType.Text) { Value = scope.TenantId });
        parameters.Add(new("application_id", NpgsqlDbType.Text) { Value = scope.ApplicationId });
        parameters.Add(new("project_id", NpgsqlDbType.Text) { Value = scope.ProjectId });
        parameters.Add(new("team_id", NpgsqlDbType.Text) { Value = (object?)scope.TeamId ?? DBNull.Value });
        parameters.Add(new("agent_id", NpgsqlDbType.Text) { Value = (object?)scope.AgentId ?? DBNull.Value });
        parameters.Add(new("user_id", NpgsqlDbType.Text) { Value = (object?)scope.UserId ?? DBNull.Value });
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

    private static string Bound(string? value) => value is null ? string.Empty : "=" + value;

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<string>("role", ContractPostgresDatabase.ApplicationRole));
        return (T)(await command.ExecuteScalarAsync())!;
    }
}

/// <summary>The one pgvector container every contract 1.6 PostgreSQL test shares.</summary>
[CollectionDefinition(Name)]
public sealed class ContractVectorPostgresCollection : ICollectionFixture<ContractVectorPostgresDatabase>
{
    public const string Name = "Contract PostgreSQL with pgvector";
}

/// <summary>
/// A pgvector PostgreSQL container for contract 1.6: every case's database migrated by the .NET migrator and the vectors
/// migrator, with the application role's privileges applied, row-level security on.
/// </summary>
public sealed class ContractVectorPostgresDatabase() : ContractPostgresDatabase(AgentExperience.Tests.Shared.PostgresTestImage.Pgvector, vectors: true);

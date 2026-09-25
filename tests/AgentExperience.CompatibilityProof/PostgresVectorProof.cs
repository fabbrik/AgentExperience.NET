using CommunityToolkit.VectorData.PgVector;
using Microsoft.Extensions.VectorData;
using Npgsql;
using Pgvector;
using Testcontainers.PostgreSql;

namespace AgentExperience.CompatibilityProof;

/// <summary>
/// Story 1.7, Track 3 (AC3): proves scoped text+vector retrieval via <c>CommunityToolkit.VectorData.PgVector</c>
/// 1.0.1 against an ephemeral Testcontainers-spun PostgreSQL + pgvector, and proves (by actually running two
/// connections against the same pooled <see cref="NpgsqlDataSource"/>, not just by reading the source) that a
/// plain-<c>Npgsql</c> canonical write cannot share a transaction with the vector connector.
/// </summary>
/// <remarks>
/// Grounding: <c>story-1-7-research-digest.md</c>, Track 3. Packages (versions per that digest, 2026-09-07, except the VectorData abstractions, which story 5.1
/// moved to the train <c>Microsoft.Agents.AI</c> 1.22.0 requires; <c>CommunityToolkit.VectorData.PgVector</c>
/// 1.0.1 declares <c>&gt;= 10.8.2</c> of them, and this proof passing against 10.10.0 is the evidence that it still works):
/// <c>Npgsql</c> 10.0.3 (https://www.nuget.org/packages/npgsql/), <c>Pgvector</c> 0.3.2
/// (https://www.nuget.org/packages/Pgvector/), <c>Microsoft.Extensions.VectorData.Abstractions</c> 10.10.0
/// (https://www.nuget.org/packages/Microsoft.Extensions.VectorData.Abstractions/), and
/// <c>CommunityToolkit.VectorData.PgVector</c> 1.0.1 -- the current, actively-maintained connector, successor to
/// the now-legacy <c>Microsoft.SemanticKernel.Connectors.PgVector</c>
/// (https://www.nuget.org/packages/CommunityToolkit.VectorData.PgVector/). Container image:
/// <c>pgvector/pgvector</c> (at the major <c>AGENTEXPERIENCE_POSTGRES_MAJOR</c> selects).
/// </remarks>
/// <remarks>
/// This is the one track this story allows to be blocked independently (e.g. a Docker-unavailable CI runner):
/// the container starts once for the whole class via <see cref="PostgresContainerFixture"/>; if that start
/// fails, only this class's tests are affected (they fail together at fixture setup) -- <c>MafHooksProof</c>,
/// <c>ContextProviderFitProof</c>, and <c>EvaluationRedactionProof</c> live in separate, independent test
/// classes with no shared fixture, so they are structurally unaffected either way (AC3's isolation clause). On
/// this machine Docker was confirmed available (`docker info`) before this story was implemented, so these
/// tests are expected to actually run and pass, not merely be inert.
/// </remarks>
public sealed class PostgresVectorProof : IClassFixture<PostgresVectorProof.PostgresContainerFixture>
{
    private readonly PostgreSqlContainer _container;

    public PostgresVectorProof(PostgresContainerFixture fixture) => _container = fixture.Container;

    /// <summary>
    /// Story 6.3: this proof really ran on the PostgreSQL major <c>AGENTEXPERIENCE_POSTGRES_MAJOR</c> selected,
    /// so a CI leg cannot report it as evidence for a major it did not reach.
    /// </summary>
    [Fact]
    public async Task The_container_reports_the_major_version_this_run_selected()
    {
        await using var dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        await using var command = dataSource.CreateCommand("SELECT current_setting('server_version_num')::int");

        Assert.Equal(AgentExperience.Tests.Shared.PostgresTestImage.Major, (int)(await command.ExecuteScalarAsync())! / 10000);
    }

    /// <summary>
    /// Starts one ephemeral <c>pgvector/pgvector</c> (at the major <c>AGENTEXPERIENCE_POSTGRES_MAJOR</c> selects) container for every test in this class, and tears it
    /// down once the class's tests complete -- per this story's "Track 3's Postgres container is ephemeral
    /// (Testcontainers), torn down after the test run" constraint.
    /// </summary>
    public sealed class PostgresContainerFixture : IAsyncLifetime
    {
        private PostgreSqlContainer? _container;

        public PostgreSqlContainer Container => _container ?? throw new InvalidOperationException("Container not initialized.");

        public async Task InitializeAsync()
        {
            var container = new PostgreSqlBuilder(AgentExperience.Tests.Shared.PostgresTestImage.Pgvector).Build();
            await container.StartAsync();
            _container = container;
        }

        public async Task DisposeAsync()
        {
            if (_container is not null)
            {
                await _container.DisposeAsync();
            }
        }
    }

    /// <summary>A minimal experience-retrieval record: scoped text + a fixed-dimension vector.</summary>
    private sealed class ExperienceSnippet
    {
        [VectorStoreKey]
        public Guid Id { get; set; }

        [VectorStoreData(IsIndexed = true)]
        public string Scope { get; set; } = string.Empty;

        [VectorStoreData]
        public string Text { get; set; } = string.Empty;

        [VectorStoreVector(3, DistanceFunction = DistanceFunction.CosineDistance)]
        public ReadOnlyMemory<float> Embedding { get; set; }
    }

    private static async Task<List<VectorSearchResult<ExperienceSnippet>>> CollectAsync(
        IAsyncEnumerable<VectorSearchResult<ExperienceSnippet>> source)
    {
        var results = new List<VectorSearchResult<ExperienceSnippet>>();
        await foreach (var item in source)
        {
            results.Add(item);
        }

        return results;
    }

    [Fact]
    public async Task Scoped_vector_search_returns_only_records_within_the_requested_scope_closest_first()
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(_container.GetConnectionString());
        dataSourceBuilder.UseVector();
        await using var dataSource = dataSourceBuilder.Build();

        var store = new PostgresVectorStore(dataSource, ownsDataSource: false);
        var collectionName = $"experience_snippets_{Guid.NewGuid():N}";
        var collection = store.GetCollection<Guid, ExperienceSnippet>(collectionName);
        await collection.EnsureCollectionExistsAsync();

        try
        {
            // Deterministic fixed vectors -- no embedding model involved.
            var tenantAClose = new ExperienceSnippet
            {
                Id = Guid.NewGuid(), Scope = "tenant-a", Text = "tenant-a close match", Embedding = new ReadOnlyMemory<float>([1f, 0f, 0f]),
            };
            var tenantAFar = new ExperienceSnippet
            {
                Id = Guid.NewGuid(), Scope = "tenant-a", Text = "tenant-a far match", Embedding = new ReadOnlyMemory<float>([0f, 0f, 1f]),
            };
            var tenantBClose = new ExperienceSnippet
            {
                Id = Guid.NewGuid(), Scope = "tenant-b", Text = "tenant-b close match, wrong scope", Embedding = new ReadOnlyMemory<float>([0.99f, 0.01f, 0f]),
            };

            await collection.UpsertAsync([tenantAClose, tenantAFar, tenantBClose]);

            var queryVector = new ReadOnlyMemory<float>([1f, 0f, 0f]);
            var results = await CollectAsync(collection.SearchAsync(
                queryVector,
                top: 10,
                new VectorSearchOptions<ExperienceSnippet> { Filter = r => r.Scope == "tenant-a" }));

            // Scoped: exactly the 2 tenant-a records come back, never the closer-vector tenant-b one.
            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.Equal("tenant-a", r.Record.Scope));
            Assert.DoesNotContain(results, r => r.Record.Id == tenantBClose.Id);

            // Closest-first ordering within scope.
            Assert.Equal(tenantAClose.Id, results[0].Record.Id);
            Assert.Equal(tenantAFar.Id, results[1].Record.Id);
        }
        finally
        {
            await collection.EnsureCollectionDeletedAsync();
        }
    }

    [Fact]
    public void Vector_connector_constructors_accept_only_a_NpgsqlDataSource_or_connection_string_never_an_ambient_transaction()
    {
        // Compile-time-verifiable API-shape proof of the digest's inference: neither PostgresVectorStore nor
        // PostgresCollection<,> exposes any constructor accepting NpgsqlConnection/NpgsqlTransaction -- every
        // public constructor takes only a NpgsqlDataSource (a pooled connection factory) or a raw connection
        // string (from which it builds its own NpgsqlDataSource). There is therefore no API surface through
        // which a caller could hand the connector an ambient transaction to participate in.
        var sawDataSourceOrConnectionStringParameter = false;

        foreach (var type in new[] { typeof(PostgresVectorStore), typeof(PostgresCollection<Guid, ExperienceSnippet>) })
        {
            var constructors = type.GetConstructors();
            Assert.NotEmpty(constructors);

            foreach (var constructor in constructors)
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    Assert.False(
                        parameter.ParameterType == typeof(NpgsqlConnection) || parameter.ParameterType == typeof(NpgsqlTransaction),
                        $"{type.Name} constructor unexpectedly accepts {parameter.ParameterType.Name} -- " +
                        "this would contradict the documented NpgsqlDataSource-only boundary.");

                    if (parameter.ParameterType == typeof(NpgsqlDataSource) || parameter.ParameterType == typeof(string))
                    {
                        sawDataSourceOrConnectionStringParameter = true;
                    }
                }
            }
        }

        // Positive half of the claim in this test's name: the accepted constructor shape actually exists.
        Assert.True(
            sawDataSourceOrConnectionStringParameter,
            "Expected at least one constructor parameter across PostgresVectorStore/PostgresCollection<,> to be " +
            "NpgsqlDataSource or a connection string -- found neither.");
    }

    [Fact]
    public async Task Vector_upsert_on_a_pooled_data_source_does_not_participate_in_a_concurrent_plain_Npgsql_transaction()
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(_container.GetConnectionString());
        dataSourceBuilder.UseVector();
        await using var dataSource = dataSourceBuilder.Build();

        var store = new PostgresVectorStore(dataSource, ownsDataSource: false);
        var collectionName = $"experience_snippets_tx_{Guid.NewGuid():N}";
        var collection = store.GetCollection<Guid, ExperienceSnippet>(collectionName);
        await collection.EnsureCollectionExistsAsync();

        try
        {
            const string CanonicalTable = "canonical_runs_tx_proof";
            await using (var setupConnection = await dataSource.OpenConnectionAsync())
            await using (var createTable = setupConnection.CreateCommand())
            {
                createTable.CommandText = $"CREATE TABLE IF NOT EXISTS {CanonicalTable} (id uuid PRIMARY KEY, note text NOT NULL)";
                await createTable.ExecuteNonQueryAsync();
            }

            var canonicalId = Guid.NewGuid();
            var vectorSnippetId = Guid.NewGuid();

            // Plain-Npgsql canonical write, on its own connection+transaction from the SAME pooled data source the
            // vector connector uses -- and deliberately left uncommitted while the vector connector writes.
            await using (var rawConnection = await dataSource.OpenConnectionAsync())
            await using (var rawTransaction = await rawConnection.BeginTransactionAsync())
            {
                await using (var insert = new NpgsqlCommand(
                    $"INSERT INTO {CanonicalTable} (id, note) VALUES (@id, @note)", rawConnection, rawTransaction))
                {
                    insert.Parameters.AddWithValue("id", canonicalId);
                    insert.Parameters.AddWithValue("note", "uncommitted canonical write");
                    await insert.ExecuteNonQueryAsync();
                }

                // The vector connector's UpsertAsync opens its OWN connection from the pool (see
                // PostgresCollection.UpsertAsync: `_dataSource.OpenConnectionAsync(...)`) -- it cannot see or
                // enlist in rawTransaction above, even though both came from the same NpgsqlDataSource.
                await collection.UpsertAsync(new ExperienceSnippet
                {
                    Id = vectorSnippetId, Scope = "tenant-a", Text = "vector write during an open, uncommitted raw transaction", Embedding = new ReadOnlyMemory<float>([1f, 0f, 0f]),
                });

                // A third, independent connection confirms: the vector row is already visible (its own connection
                // committed automatically), while the canonical row is not (still inside rawTransaction).
                await using var observerConnection = await dataSource.OpenConnectionAsync();
                Assert.Equal(1, await CountAsync(observerConnection, $"SELECT COUNT(*) FROM {collectionName} WHERE \"Id\" = @id", vectorSnippetId));
                Assert.Equal(0, await CountAsync(observerConnection, $"SELECT COUNT(*) FROM {CanonicalTable} WHERE id = @id", canonicalId));

                // Roll back the canonical write -- it was never visible to the vector connector's connection, and
                // rolling it back does not affect the already-committed vector row either.
                await rawTransaction.RollbackAsync();
            }

            await using var finalConnection = await dataSource.OpenConnectionAsync();
            Assert.Equal(0, await CountAsync(finalConnection, $"SELECT COUNT(*) FROM {CanonicalTable} WHERE id = @id", canonicalId));
            Assert.Equal(1, await CountAsync(finalConnection, $"SELECT COUNT(*) FROM {collectionName} WHERE \"Id\" = @id", vectorSnippetId));
        }
        finally
        {
            await collection.EnsureCollectionDeletedAsync();
        }

        static async Task<long> CountAsync(NpgsqlConnection connection, string sql, Guid id)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("id", id);
            return (long)(await command.ExecuteScalarAsync())!;
        }
    }
}

using AgentExperience.RetrievalQuality.Harness;
using AgentExperience.Storage.Postgres;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AgentExperience.RetrievalQuality;

/// <summary>
/// Story 20.1: the retrieval benchmark over <see cref="PostgresExperienceCandidateSource"/>, which matches with
/// <c>websearch_to_tsquery('english')</c> and so stems words and drops stopwords. Needs Docker; without it, run the
/// project with <c>--filter "FullyQualifiedName!~Postgres"</c> as CONTRIBUTING says, and the in-memory golden still runs.
/// </summary>
[Collection(PostgresRetrievalQualityCollection.Name)]
public sealed class PostgresRetrievalQualityTests(PostgresRetrievalQualityFixture fixture) : RetrievalQualityTests
{
    protected override string Adapter => "PostgreSQL candidate source";

    protected override string GoldenFileName => "GoldenPostgresReport.txt";

    // Each run gets a database of its own: the corpus's experience IDs are fixed, so that ties order the same way on
    // every run, and an experience ID is unique across the whole table, not per tenant.
    private protected override async Task<RetrievalQualityResult> RunAsync(RetrievalCorpus corpus)
    {
        var dataSource = await fixture.CreateDatabaseAsync();
        return await RetrievalQualityRun.RunAsync(
            Adapter,
            corpus,
            new PostgresExperienceRecordStore(dataSource),
            new PostgresExperienceCandidateSource(dataSource));
    }
}

/// <summary>
/// One ephemeral stock <c>postgres</c> container for the class, handing out a new migrated database per run. Set
/// <c>TESTCONTAINERS_RYUK_DISABLED=true</c> if Ryuk fails under a local Docker setup.
/// </summary>
public sealed class PostgresRetrievalQualityFixture : IAsyncLifetime
{
    private readonly List<NpgsqlDataSource> _dataSources = [];
    private PostgreSqlContainer? _container;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder(AgentExperience.Tests.Shared.PostgresTestImage.Stock).Build();
        await _container.StartAsync();
    }

    /// <summary>
    /// Creates an empty database in the shared container, migrates it, and returns its data source, as the container's
    /// superuser. The database goes away with the container.
    /// </summary>
    public async Task<NpgsqlDataSource> CreateDatabaseAsync()
    {
        // "rq_" + 32 hex characters: inside PostgreSQL's 63-byte identifier limit, and nothing in it needs quoting.
        var name = $"rq_{Guid.NewGuid():N}";
        await using (var admin = NpgsqlDataSource.Create(_container!.GetConnectionString()))
        {
            // CREATE DATABASE takes no parameters and cannot run inside a transaction, so the identifier is
            // interpolated; it is built from a GUID above.
            await using var command = admin.CreateCommand($"CREATE DATABASE \"{name}\"");
            await command.ExecuteNonQueryAsync();
        }

        var dataSource = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString);
        lock (_dataSources)
        {
            _dataSources.Add(dataSource);
        }

        await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);
        return dataSource;
    }

    public async Task DisposeAsync()
    {
        foreach (var dataSource in _dataSources)
        {
            await dataSource.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresRetrievalQualityCollection : ICollectionFixture<PostgresRetrievalQualityFixture>
{
    public const string Name = "PostgresRetrievalQuality";
}

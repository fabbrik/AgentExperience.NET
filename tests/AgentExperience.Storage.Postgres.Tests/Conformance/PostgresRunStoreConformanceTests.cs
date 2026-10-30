using AgentExperience.Storage.Conformance;

namespace AgentExperience.Storage.Postgres.Tests.Conformance;

/// <summary>
/// Story 28.7: <see cref="PostgresExperienceRunStore"/> passes the run-store conformance suite, over the collection's shared
/// container, as the application role (under row-level security when the run is in that mode).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresRunStoreConformanceTests(PostgresFixture fixture) : RunStoreConformanceTests
{
    protected override IExperienceRunStore CreateStore() => new PostgresExperienceRunStore(fixture.DataSource);
}

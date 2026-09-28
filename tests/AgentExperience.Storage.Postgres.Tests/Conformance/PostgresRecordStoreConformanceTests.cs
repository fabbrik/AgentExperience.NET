using AgentExperience.Storage.Conformance;

namespace AgentExperience.Storage.Postgres.Tests.Conformance;

/// <summary>
/// Story 11.1: <see cref="PostgresExperienceRecordStore"/> passes the record-store conformance suite, which proves
/// the suite describes real behaviour. It runs over the collection's shared container, as the application role,
/// in whichever mode the run is in (<c>AGENTEXPERIENCE_TEST_ENCRYPTION</c>); every conformance test works in a tenant
/// of its own, so the shared database keeps the tests independent.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresRecordStoreConformanceTests(PostgresFixture fixture) : RecordStoreConformanceTests
{
    protected override IExperienceRecordStore CreateStore() => new PostgresExperienceRecordStore(fixture.DataSource);
}

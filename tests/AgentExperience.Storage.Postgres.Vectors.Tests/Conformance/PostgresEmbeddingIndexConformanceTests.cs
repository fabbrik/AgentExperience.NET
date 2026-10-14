using AgentExperience.Storage.Conformance;

namespace AgentExperience.Storage.Postgres.Vectors.Tests.Conformance;

/// <summary>
/// Story 21.3: <see cref="PostgresExperienceEmbeddingIndex"/> passes the embedding-index conformance suite the in-memory
/// index passes, over the collection's shared container, seeding through <see cref="PostgresExperienceRecordStore"/>, in
/// whichever mode the run is in (encryption, row-level security). Every conformance test works in a tenant of its own.
/// </summary>
[Collection(VectorsCollection.Name)]
public sealed class PostgresEmbeddingIndexConformanceTests(VectorsFixture fixture) : EmbeddingIndexConformanceTests
{
    protected override IExperienceRecordStore CreateRecordStore() => new PostgresExperienceRecordStore(fixture.DataSource);

    protected override IExperienceEmbeddingIndex CreateEmbeddingIndex() => new PostgresExperienceEmbeddingIndex(fixture.DataSource);
}

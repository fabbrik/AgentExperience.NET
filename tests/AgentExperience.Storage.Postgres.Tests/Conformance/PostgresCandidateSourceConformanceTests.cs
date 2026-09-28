using AgentExperience.Storage.Conformance;

namespace AgentExperience.Storage.Postgres.Tests.Conformance;

/// <summary>
/// Story 11.1: <see cref="PostgresExperienceCandidateSource"/> passes the candidate-source conformance suite, over
/// the collection's shared container, seeding through <see cref="PostgresExperienceRecordStore"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresCandidateSourceConformanceTests(PostgresFixture fixture) : CandidateSourceConformanceTests
{
    protected override IExperienceRecordStore CreateRecordStore() => new PostgresExperienceRecordStore(fixture.DataSource);

    protected override IExperienceCandidateSource CreateCandidateSource() => new PostgresExperienceCandidateSource(fixture.DataSource);
}

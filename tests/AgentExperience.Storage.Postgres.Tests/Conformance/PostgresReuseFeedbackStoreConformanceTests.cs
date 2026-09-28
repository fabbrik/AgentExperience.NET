using AgentExperience.Storage.Conformance;

namespace AgentExperience.Storage.Postgres.Tests.Conformance;

/// <summary>
/// Story 11.1: <see cref="PostgresExperienceReuseFeedbackStore"/> passes the reuse-feedback conformance suite, over
/// the collection's shared container.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresReuseFeedbackStoreConformanceTests(PostgresFixture fixture) : ReuseFeedbackStoreConformanceTests
{
    protected override IExperienceReuseFeedbackStore CreateStore() => new PostgresExperienceReuseFeedbackStore(fixture.DataSource);

    /// <summary>In crypto-shredding mode the ledger seals each rationale, which the contract leaves out.</summary>
    protected override bool SealsRationale => EncryptionMode.IsOn;
}

using AgentExperience.Storage.Conformance;

namespace AgentExperience.Storage.InMemory.Tests.Conformance;

/// <summary>
/// Story 11.2: <see cref="InMemoryExperienceRecordStore"/> passes the record-store conformance suite in full. Each
/// test gets a fresh store.
/// </summary>
public sealed class InMemoryRecordStoreConformanceTests : RecordStoreConformanceTests
{
    protected override IExperienceRecordStore CreateStore() => new InMemoryExperienceRecordStore();
}

/// <summary>
/// Story 11.2: <see cref="InMemoryExperienceCandidateSource"/> passes the candidate-source conformance suite, searching
/// the very <see cref="InMemoryExperienceRecordStore"/> the suite seeds through.
/// </summary>
public sealed class InMemoryCandidateSourceConformanceTests : CandidateSourceConformanceTests
{
    private readonly InMemoryExperienceRecordStore _records = new();

    protected override IExperienceRecordStore CreateRecordStore() => _records;

    protected override IExperienceCandidateSource CreateCandidateSource() => new InMemoryExperienceCandidateSource(_records);
}

/// <summary>Story 11.2: <see cref="InMemoryExperienceReuseFeedbackStore"/> passes the reuse-feedback conformance suite.</summary>
public sealed class InMemoryReuseFeedbackStoreConformanceTests : ReuseFeedbackStoreConformanceTests
{
    protected override IExperienceReuseFeedbackStore CreateStore() => new InMemoryExperienceReuseFeedbackStore();
}

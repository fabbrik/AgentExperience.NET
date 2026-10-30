using AgentExperience.Storage.Conformance;

namespace AgentExperience.Storage.Sqlite.Tests.Conformance;

/// <summary>Story 28.7: <see cref="SqliteExperienceRecordStore"/> passes the record-store conformance suite in full, on a file of its own per test.</summary>
public sealed class SqliteRecordStoreConformanceTests : RecordStoreConformanceTests, IDisposable
{
    private readonly SqliteTestDatabase _database = new();

    protected override IExperienceRecordStore CreateStore() => new SqliteExperienceRecordStore(_database.ConnectionString);

    public void Dispose() => _database.Dispose();
}

/// <summary>Story 28.7: <see cref="SqliteExperienceCandidateSource"/> passes the candidate-source conformance suite over the store the suite seeds through.</summary>
public sealed class SqliteCandidateSourceConformanceTests : CandidateSourceConformanceTests, IDisposable
{
    private readonly SqliteTestDatabase _database = new();
    private readonly SqliteExperienceRecordStore _records;

    public SqliteCandidateSourceConformanceTests()
    {
        _records = new SqliteExperienceRecordStore(_database.ConnectionString);
    }

    protected override IExperienceRecordStore CreateRecordStore() => _records;

    protected override IExperienceCandidateSource CreateCandidateSource() => new SqliteExperienceCandidateSource(_records);

    public void Dispose() => _database.Dispose();
}

/// <summary>Story 28.7: the SQLite reuse-feedback ledger passes the reuse-feedback conformance suite.</summary>
public sealed class SqliteReuseFeedbackStoreConformanceTests : ReuseFeedbackStoreConformanceTests, IDisposable
{
    private readonly SqliteTestDatabase _database = new();

    protected override IExperienceReuseFeedbackStore CreateStore() => new SqliteExperienceReuseFeedbackStore(_database.ConnectionString);

    public void Dispose() => _database.Dispose();
}

/// <summary>Story 28.7: <see cref="SqliteExperienceSignatureSource"/> passes the signature-source conformance suite.</summary>
public sealed class SqliteSignatureSourceConformanceTests : SignatureSourceConformanceTests, IDisposable
{
    private readonly SqliteTestDatabase _database = new();
    private readonly SqliteExperienceRecordStore _records;

    public SqliteSignatureSourceConformanceTests()
    {
        _records = new SqliteExperienceRecordStore(_database.ConnectionString);
    }

    protected override IExperienceRecordStore CreateRecordStore() => _records;

    protected override IExperienceSignatureSource CreateSignatureSource() => new SqliteExperienceSignatureSource(_records);

    public void Dispose() => _database.Dispose();
}

/// <summary>Story 28.7: <see cref="SqliteExperienceRunStore"/> passes the run-store conformance suite.</summary>
public sealed class SqliteRunStoreConformanceTests : RunStoreConformanceTests, IDisposable
{
    private readonly SqliteTestDatabase _database = new();

    protected override IExperienceRunStore CreateStore() => new SqliteExperienceRunStore(_database.ConnectionString);

    public void Dispose() => _database.Dispose();
}

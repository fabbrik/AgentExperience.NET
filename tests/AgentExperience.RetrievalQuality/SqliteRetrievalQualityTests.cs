using AgentExperience.RetrievalQuality.Harness;
using AgentExperience.Storage.Sqlite;

namespace AgentExperience.RetrievalQuality;

/// <summary>
/// Story 28.7: the retrieval benchmark over <see cref="SqliteExperienceCandidateSource"/>, which matches the in-memory
/// source's query terms through FTS5 with Porter stemming. It needs no Docker, so its golden is checked on every run. Each
/// run seeds a database file of its own, deleted when the test ends.
/// </summary>
public sealed class SqliteRetrievalQualityTests : RetrievalQualityTests, IDisposable
{
    private readonly List<string> _files = [];

    protected override string Adapter => "SQLite candidate source";

    protected override string GoldenFileName => "GoldenSqliteReport.txt";

    private protected override async Task<RetrievalQualityResult> RunAsync(RetrievalCorpus corpus)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentexperience-retrieval-{Guid.NewGuid():N}.db");
        _files.Add(path);
        await ExperienceSqliteSchema.MigrateAsync(path);
        var store = new SqliteExperienceRecordStore(ExperienceSqliteSchema.ConnectionStringFor(path));
        return await RetrievalQualityRun.RunAsync(
            Adapter, corpus, store, new SqliteExperienceCandidateSource(store), new SqliteExperienceSignatureSource(store));
    }

    /// <summary>
    /// Deletes the run's files. Their pooled connections are left alone (clearing pools would race other tests); on the
    /// platforms CI runs, an open file can be deleted.
    /// </summary>
    public void Dispose()
    {
        foreach (var file in _files.SelectMany(path => new[] { path, path + "-wal", path + "-shm" }))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Best effort: a temporary file left behind is harmless.
            }
        }
    }
}

using AgentExperience.Core.Indexing;
using AgentExperience.Core.Retrieval;
using Npgsql;
using NpgsqlTypes;

namespace AgentExperience.Storage.Postgres.Vectors.Tests;

/// <summary>
/// One isolated tenant's worth of the real stack over the shared container: the canonical store, the
/// text candidate source, the pgvector embedding index, and Core's indexing and retrieval services --
/// all pointed at a scope no other test uses, so tests can run in any order without seeing each
/// other's records.
/// </summary>
internal sealed class TestWorld
{
    private static readonly DateTimeOffset Stamp = new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero).AddTicks(1_234_560);

    private TestWorld(NpgsqlDataSource dataSource, Scope scope, TopicEmbeddingGenerator generator)
    {
        DataSource = dataSource;
        Scope = scope;
        Generator = generator;
        Authorization = new AuthorizationContext(scope.TenantId, "host-principal", ["experience:write"], Stamp);
        Store = new PostgresExperienceRecordStore(dataSource);
        CandidateSource = new PostgresExperienceCandidateSource(dataSource);
        Index = new PostgresExperienceEmbeddingIndex(dataSource);
        Indexing = new ExperienceIndexingService(Index, generator);
    }

    public NpgsqlDataSource DataSource { get; }

    public Scope Scope { get; }

    public AuthorizationContext Authorization { get; }

    public TopicEmbeddingGenerator Generator { get; }

    public PostgresExperienceRecordStore Store { get; }

    public PostgresExperienceCandidateSource CandidateSource { get; }

    public PostgresExperienceEmbeddingIndex Index { get; }

    public ExperienceIndexingService Indexing { get; }

    public static Task<TestWorld> CreateAsync(NpgsqlDataSource dataSource, TopicEmbeddingGenerator? generator = null) =>
        Task.FromResult(new TestWorld(
            dataSource,
            new Scope("tenant-" + Guid.NewGuid().ToString("N"), "app-1", "project-1"),
            generator ?? new TopicEmbeddingGenerator()));

    /// <summary>
    /// A retrieval service over this world's real text and vector channels. The timeout is generous on
    /// purpose: these tests are about what the channels return, not about how fast a container is.
    /// </summary>
    public ExperienceRetrievalService Retrieval(IExperienceEmbeddingGenerator? queryGenerator = null, bool hybrid = true) => new(
        CandidateSource,
        RetrievalPolicy.Default with { Timeout = TimeSpan.FromSeconds(30) },
        RankingWeights.Default,
        TimeProvider.System,
        hybrid ? Index : null,
        hybrid ? queryGenerator ?? Generator : null);

    /// <summary>
    /// Creates a record in this world's scope, already eligible for retrieval unless told otherwise.
    /// A <c>scope</c> other than <see cref="Scope"/> -- which the sharing-grant tests pass to own a
    /// record from a sibling team -- must still lie inside this world's tenant.
    /// </summary>
    public async Task<Guid> AddRecordAsync(
        string taskId,
        string? taskSummary,
        string? lesson,
        ExperienceStatus status = ExperienceStatus.Validated,
        double confidence = 0.8,
        Scope? scope = null)
    {
        var id = Guid.NewGuid();
        var record = new ExperienceRecord(
            ExperienceId: id,
            SourceRunId: Guid.NewGuid(),
            Scope: scope ?? Scope,
            TaskId: taskId,
            TaskSummary: taskSummary,
            Attempts: [],
            Outcome: new Outcome(TaskVerificationStatus.Verified, [], "checks passed", Stamp),
            CompletionScore: 1,
            Reflection: lesson is null
                ? null
                : new Reflection(
                    Guid.NewGuid(), Guid.NewGuid(), lesson, [], [], [], [], null, [],
                    TaskVerificationStatus.Verified, 1, "v1", "tests", Stamp),
            Environment: new EnvironmentFingerprint("worker-01", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
            Provenance: new Provenance("tests", null, Stamp, null),
            Status: status,
            ReuseConfidence: confidence,
            SupportingValidations: 1,
            Contradictions: 0,
            Revision: 0,
            CreatedAt: Stamp,
            UpdatedAt: Stamp);

        var created = await Store.CreateAsync(Authorization, record, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Created, created.Outcome);
        return id;
    }

    /// <summary>
    /// Issues a sharing grant over one record, through the real grant store, so the vector channel can
    /// be asked what a recipient scope actually sees.
    /// </summary>
    public async Task<ExperienceGrant> GrantAsync(Guid experienceId, Scope owner, Scope recipient)
    {
        var store = new PostgresExperienceGrantStore(DataSource);
        var result = await store.CreateAsync(
            Authorization,
            new GrantAdministration("sharing-administrator", Stamp),
            new ExperienceGrantRequest(
                Guid.NewGuid(),
                experienceId,
                owner,
                recipient,
                "sibling team owns the follow-up",
                DateTimeOffset.UtcNow.AddHours(1)),
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.Created, result.Outcome);
        return result.Grant!;
    }

    /// <summary>Revokes a grant through the real grant store.</summary>
    public async Task RevokeAsync(Guid grantId, Scope owner)
    {
        var store = new PostgresExperienceGrantStore(DataSource);
        var result = await store.RevokeAsync(
            Authorization,
            new GrantAdministration("sharing-administrator", Stamp),
            new ExperienceGrantRevocation(grantId, owner, "the collaboration ended"),
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.Revoked, result.Outcome);
    }

    /// <summary>The stored embedding row, read straight out of SQL rather than through the port.</summary>
    public async Task<StoredEmbedding> ReadEmbeddingAsync(Guid experienceId)
    {
        await using var command = DataSource.CreateCommand(
            "SELECT model_id, dimension, content_hash, source_revision, tenant_id, project_id, updated_at, " +
            "vector_dims(embedding) FROM agent_experience.experience_embeddings WHERE experience_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"No embedding row for {experienceId}.");
        return new StoredEmbedding(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetInt32(7));
    }

    public async Task<long> CountEmbeddingsAsync(Guid experienceId) =>
        await ScalarAsync<long>("SELECT count(*) FROM agent_experience.experience_embeddings WHERE experience_id = @id", experienceId);

    /// <summary>Moves a record to a new revision without going through a lifecycle commit, to stage an in-flight stale write.</summary>
    public Task BumpRevisionAsync(Guid experienceId, long revision) =>
        ExecuteAsync("UPDATE agent_experience.experience_records SET revision = @revision WHERE experience_id = @id", experienceId, ("revision", revision));

    /// <summary>Deletes a record, to stage a write that lands after the record is gone.</summary>
    public Task DeleteRecordAsync(Guid experienceId) =>
        ExecuteAsync("DELETE FROM agent_experience.experience_records WHERE experience_id = @id", experienceId);

    /// <summary>Rewrites the reflection's lesson in place, which is a content change the re-index has to notice.</summary>
    public Task RewriteLessonAsync(Guid experienceId, string lesson) =>
        ExecuteAsync(
            "UPDATE agent_experience.experience_records " +
            "SET payload = jsonb_set(payload, '{reflection,lesson}', to_jsonb(@lesson::text)) WHERE experience_id = @id",
            experienceId,
            ("lesson", lesson));

    /// <summary>
    /// Replaces a stored embedding's descriptor <em>and</em> its vector in place, to stage a model or
    /// dimension mismatch. The vector is rewritten to the stated width because the schema's
    /// <c>CHECK (vector_dims(embedding) = dimension)</c> will not let the two disagree -- which is the
    /// point of that constraint, and why the search's cast can never meet a row it cannot cast.
    /// </summary>
    public Task RestampEmbeddingAsync(Guid experienceId, string modelId, int dimension) =>
        ExecuteAsync(
            "UPDATE agent_experience.experience_embeddings " +
            "SET model_id = @model_id, dimension = @dimension, embedding = CAST(@embedding AS vector) WHERE experience_id = @id",
            experienceId,
            ("model_id", modelId),
            ("dimension", dimension),
            ("embedding", "[" + string.Join(',', Enumerable.Repeat("0.1", dimension)) + "]"));

    /// <summary>
    /// Forces the stored row's <c>source_revision</c> past the record's own, which is what a write that
    /// landed from a newer revision leaves behind for a slower racer to collide with.
    /// </summary>
    public Task ForceStoredSourceRevisionAsync(Guid experienceId, long sourceRevision) =>
        ExecuteAsync(
            "UPDATE agent_experience.experience_embeddings SET source_revision = @source_revision WHERE experience_id = @id",
            experienceId,
            ("source_revision", sourceRevision));

    /// <summary>
    /// The planner's chosen plan for exactly the statement the adapter issues, with sequential scans
    /// disabled for the transaction so "it could have used the index" and "it did" are the same claim.
    /// </summary>
    public async Task<string> ExplainSearchAsync(ReadOnlyMemory<float> queryVector)
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var setting = new NpgsqlCommand("SET LOCAL enable_seqscan = off", connection, transaction))
        {
            await setting.ExecuteNonQueryAsync();
        }

        var sql = PostgresExperienceEmbeddingIndex.SearchSqlForTesting(queryVector.Length);
        await using var command = new NpgsqlCommand("EXPLAIN " + sql, connection, transaction);
        var parameters = command.Parameters;
        parameters.Add(new NpgsqlParameter<string>("tenant_id", NpgsqlDbType.Text) { TypedValue = Scope.TenantId });
        parameters.Add(new NpgsqlParameter<string>("application_id", NpgsqlDbType.Text) { TypedValue = Scope.ApplicationId });
        parameters.Add(new NpgsqlParameter<string>("project_id", NpgsqlDbType.Text) { TypedValue = Scope.ProjectId });
        parameters.Add(new NpgsqlParameter("team_id", NpgsqlDbType.Text) { Value = DBNull.Value });
        parameters.Add(new NpgsqlParameter("agent_id", NpgsqlDbType.Text) { Value = DBNull.Value });
        parameters.Add(new NpgsqlParameter("user_id", NpgsqlDbType.Text) { Value = DBNull.Value });
        parameters.Add(new NpgsqlParameter<string[]>("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = ["Validated", "Reinforced"] });
        parameters.Add(new NpgsqlParameter<double>("min_confidence", 0.5));
        parameters.Add(new NpgsqlParameter<string>("model_id", NpgsqlDbType.Text) { TypedValue = Generator.ModelId });
        parameters.Add(new NpgsqlParameter<string>("query_vector", NpgsqlDbType.Text) { TypedValue = "[" + string.Join(',', queryVector.ToArray().Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]" });
        parameters.Add(new NpgsqlParameter<int>("limit", 50));

        var lines = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                lines.Add(reader.GetString(0));
            }
        }

        await transaction.RollbackAsync();
        return string.Join('\n', lines);
    }

    private async Task ExecuteAsync(string sql, Guid experienceId, params (string Name, object Value)[] extra)
    {
        await using var command = DataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
        foreach (var (name, value) in extra)
        {
            command.Parameters.Add(new NpgsqlParameter { ParameterName = name, Value = value });
        }

        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, Guid experienceId)
    {
        await using var command = DataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
        return (T)(await command.ExecuteScalarAsync())!;
    }

    internal sealed record StoredEmbedding(
        string ModelId,
        int Dimension,
        string ContentHash,
        long SourceRevision,
        string TenantId,
        string ProjectId,
        DateTimeOffset UpdatedAt,
        int VectorDimensions);
}

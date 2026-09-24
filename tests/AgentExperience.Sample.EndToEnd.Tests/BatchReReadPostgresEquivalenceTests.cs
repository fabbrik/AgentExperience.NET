using AgentExperience.Abstractions;
using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.Storage.Postgres;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Npgsql;

namespace AgentExperience.Sample.EndToEnd.Tests;

/// <summary>
/// Story 5.6 (KL-1), end to end against a real PostgreSQL 16: the MAF injection provider's final
/// eligibility check, run once through the PostgreSQL store's one-statement
/// <see cref="PostgresExperienceRecordStore.GetManyAsync"/> and once through the port's sequential
/// default -- one <c>GetAsync</c> per candidate, which is the re-read the provider made before this
/// story. The two must inject the same block, omit the same records for the same reasons, and leave
/// the same access rows.
/// </summary>
/// <remarks>
/// It lives in this project because this is the one test project that already has the adapter, the
/// PostgreSQL store and a PostgreSQL container together.
/// </remarks>
[Collection(SamplePostgresCollection.Name)]
public sealed class BatchReReadPostgresEquivalenceTests(SamplePostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_provider_injects_omits_and_audits_identically_through_the_batched_and_the_per_record_re_read()
    {
        await using var dataSource = NpgsqlDataSource.Create(await fixture.CreateDatabaseAsync("batch_equiv"));
        await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);

        var tenant = "tenant-" + Guid.NewGuid().ToString("N");
        var authorization = new AuthorizationContext(tenant, "host-principal", ["experience:read", "experience:write"], Now);
        var owner = new Scope(tenant, "app-1", "project-1", "team-a");
        var reader = owner with { TeamId = "team-b" };
        var sibling = owner with { TeamId = "team-c" };

        var log = new PostgresExperienceGrantAccessLog(dataSource);
        var store = new PostgresExperienceRecordStore(
            dataSource,
            onGrantsUnavailable: null,
            auditing: new ExperienceGrantAuditing(log, _ => { }));
        var grants = new PostgresExperienceGrantStore(dataSource);

        // What retrieval saw: every record live, eligible, and readable -- the snapshot the source
        // returns. What the re-read finds is what has happened to each one since.
        var candidates = new List<ExperienceCandidate>();

        async Task<ExperienceRecord> SeedAsync(Scope scope, int rank, ExperienceStatus stored = ExperienceStatus.Validated)
        {
            var record = Record(scope, rank);
            var created = await store.CreateAsync(
                new AuthorizationContext(scope.TenantId, "seed", ["experience:write"], Now), record with { Status = stored }, CancellationToken.None);
            Assert.True(
                created.Outcome == ExperienceStoreOutcome.Created,
                string.Join("; ", created.Errors.Select(error => $"{error.Path}: {error.Message}")));
            candidates.Add(new ExperienceCandidate(record, 1d - (rank * 0.05), SharedByGrant: scope != reader));
            return record;
        }

        async Task<ExperienceGrant> GrantAsync(ExperienceRecord record, ExperienceGrantDisclosure disclosure)
        {
            var created = await grants.CreateAsync(
                authorization,
                new GrantAdministration("sharing-administrator", DateTimeOffset.UtcNow),
                new ExperienceGrantRequest(Guid.NewGuid(), record.ExperienceId, owner, reader, "follow-up", Micro(DateTimeOffset.UtcNow.AddHours(1)), disclosure),
                CancellationToken.None);
            Assert.Equal(ExperienceGrantOutcome.Created, created.Outcome);
            return created.Grant!;
        }

        var own = await SeedAsync(reader, 0);
        var lessonOnly = await SeedAsync(owner, 1);
        await GrantAsync(lessonOnly, ExperienceGrantDisclosure.LessonOnly);
        var withApproach = await SeedAsync(owner, 2);
        await GrantAsync(withApproach, ExperienceGrantDisclosure.LessonAndApproach);
        await SeedAsync(reader, 3, ExperienceStatus.Revoked);
        await SeedAsync(reader, 4, ExperienceStatus.Superseded);
        var erased = await SeedAsync(reader, 5);
        Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.DeleteAsync(authorization, reader, erased.ExperienceId, CancellationToken.None)).Outcome);
        var grantRevoked = await SeedAsync(owner, 6);
        var revoked = await GrantAsync(grantRevoked, ExperienceGrantDisclosure.LessonAndApproach);
        Assert.Equal(
            ExperienceGrantOutcome.Revoked,
            (await grants.RevokeAsync(
                authorization,
                new GrantAdministration("sharing-administrator", DateTimeOffset.UtcNow),
                new ExperienceGrantRevocation(revoked.GrantId, owner, "withdrawn"),
                CancellationToken.None)).Outcome);
        var sharedThenErased = await SeedAsync(owner, 7);
        await GrantAsync(sharedThenErased, ExperienceGrantDisclosure.LessonOnly);
        Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.DeleteAsync(authorization, owner, sharedThenErased.ExperienceId, CancellationToken.None)).Outcome);
        await SeedAsync(sibling, 8);

        var source = new SnapshotSource(candidates);

        async Task<(ExperienceInjectionResult Result, string? Block, IReadOnlyList<string> Rows, int Reads, int Batches)> RunAsync(
            IExperienceRecordStore through,
            string correlationId)
        {
            ExperienceInjectionResult? reported = null;
            var client = new RecordingChatClient();
            var provider = new ExperienceContextProvider(
                new ExperienceRetrievalService(source, RetrievalPolicy.Default, RankingWeights.Default, new FixedNow(Now)),
                through,
                new ExperienceInjectionOptions
                {
                    ResolveRequest = _ => new RetrieveExperienceRequest(authorization, reader, "refund ticket stuck on a lock", CorrelationId: correlationId),
                    Limits = ExperienceInjectionLimits.Default with { MaxRecords = 12 },
                    TimeProvider = new FixedNow(Now),
                    OnContextInjected = result => reported = result,
                });

            var agent = new ChatClientAgent(client, new ChatClientAgentOptions { AIContextProviders = [provider] });
            await agent.RunAsync("refund ticket stuck on a lock");

            var block = client.LastMessages?
                .FirstOrDefault(m => m.AdditionalProperties?.ContainsKey(ExperienceContextProvider.HistoricalReferenceKey) == true)?
                .Text;
            var counting = through as CountingStore;
            return (reported!, block, await AccessRowsAsync(dataSource, correlationId), counting?.Reads ?? -1, counting?.Batches ?? -1);
        }

        var perRecordStore = new CountingStore(store, batch: false);
        var perRecord = await RunAsync(perRecordStore, tenant + "-per-record");
        var batchedStore = new CountingStore(store, batch: true);
        var batched = await RunAsync(batchedStore, tenant + "-batched");

        // The same outcome, the same injected records, and every omission with byte-identical reasons.
        Assert.Equal(InjectionOutcome.Injected, batched.Result.Outcome);
        Assert.Equal(perRecord.Result.Outcome, batched.Result.Outcome);
        Assert.Equal([own.ExperienceId, lessonOnly.ExperienceId, withApproach.ExperienceId], batched.Result.InjectedExperienceIds);
        Assert.Equal(perRecord.Result.InjectedExperienceIds, batched.Result.InjectedExperienceIds);
        Assert.Equal(perRecord.Result.Omitted, batched.Result.Omitted);
        Assert.Equal(
            [
                InjectionOmissionReason.Ineligible, // revoked since retrieval
                InjectionOmissionReason.Ineligible, // superseded since retrieval
                InjectionOmissionReason.Unreadable, // erased (its owner's own tombstone: Deleted)
                InjectionOmissionReason.Unreadable, // its grant was revoked
                InjectionOmissionReason.Unreadable, // shared, then erased
                InjectionOmissionReason.Unreadable, // a sibling's, never granted
            ],
            batched.Result.Omitted.Select(omission => omission.Reason));

        // The same block, byte for byte -- the approach line withheld for the lesson-only grant and
        // rendered for the lesson-and-approach one, from the level the re-read itself returned.
        Assert.NotNull(batched.Block);
        Assert.Equal(perRecord.Block, batched.Block);
        Assert.Contains("The grant withholds this lesson's approach.", batched.Block, StringComparison.Ordinal);

        // The same access rows: one per grant-delivered record, naming the same grant at the same level.
        Assert.Equal(2, batched.Rows.Count);
        Assert.Equal(perRecord.Rows, batched.Rows);

        // Round trips: nine store reads -- one per candidate -- became one batched read.
        Assert.Equal(0, perRecord.Batches);
        Assert.Equal(1, batched.Batches);
        Assert.Equal(0, batched.Reads);
        Assert.Equal(perRecord.Reads, batchedStore.LastBatchSize);
        Assert.Equal(9, perRecord.Reads);
    }

    private static ExperienceRecord Record(Scope scope, int rank)
    {
        var id = Guid.NewGuid();
        var runId = Guid.NewGuid();
        return new ExperienceRecord(
            ExperienceId: id,
            SourceRunId: runId,
            Scope: scope,
            TaskId: "refund-ticket",
            TaskSummary: "A refund ticket stuck on a lock.",
            Attempts:
            [
                new Attempt(
                    AttemptId: Guid.NewGuid(),
                    SequenceNumber: 0,
                    StartedAt: Now,
                    Duration: TimeSpan.FromSeconds(1),
                    ToolCalls:
                    [
                        new ToolCallRecord(Guid.NewGuid(), 0, "release_lock", new Dictionary<string, object?>(), Now, TimeSpan.FromMilliseconds(5), "ok", null),
                    ],
                    Result: "ok",
                    Error: null),
            ],
            Outcome: new Outcome(TaskVerificationStatus.Verified, [], null, Now),
            CompletionScore: 1d,
            Reflection: new Reflection(
                Guid.NewGuid(), runId, $"Lesson ranked {rank}: release the lock before retrying.", [], [], [], [], null, [],
                TaskVerificationStatus.Verified, 1d, "rules-v1", "tests", Now),
            Environment: new EnvironmentFingerprint("host", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
            Provenance: new Provenance("tests", null, Now, null),
            Status: ExperienceStatus.Validated,
            ReuseConfidence: 0.75,
            SupportingValidations: 0,
            Contradictions: 0,
            Revision: 0,
            CreatedAt: Now,
            UpdatedAt: Now);
    }

    private static DateTimeOffset Micro(DateTimeOffset value) => new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    private static async Task<IReadOnlyList<string>> AccessRowsAsync(NpgsqlDataSource dataSource, string correlationId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT grant_id, experience_id, record_revision, team_id, recipient_team_id, principal_id, disclosure " +
            "FROM agent_experience.experience_grant_access WHERE correlation_id = @correlation_id ORDER BY experience_id");
        command.Parameters.Add(new NpgsqlParameter<string>("correlation_id", correlationId));

        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "null" : reader.GetValue(i).ToString())));
        }

        return rows;
    }

    /// <summary>A clock frozen at <see cref="Now"/> for readings, with the system's real timers.</summary>
    private sealed class FixedNow(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>The candidate source retrieval saw: a fixed snapshot, so what changed since is the re-read's to find.</summary>
    private sealed class SnapshotSource(IReadOnlyList<ExperienceCandidate> candidates) : IExperienceCandidateSource
    {
        public Task<ExperienceCandidateSearchResult> SearchAsync(
            AuthorizationContext authorization,
            ExperienceCandidateQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ExperienceCandidateSearchResult(ExperienceStoreOutcome.Found, candidates, []));
    }

    /// <summary>
    /// Forwards to the PostgreSQL store and counts round trips. With <c>batch: false</c> it does not
    /// implement <see cref="IExperienceRecordStore.GetManyAsync"/>'s override, so the port's sequential
    /// default runs: the provider's pre-5.6 re-read, one <c>GetAsync</c> per candidate.
    /// </summary>
    private sealed class CountingStore(PostgresExperienceRecordStore inner, bool batch) : IExperienceRecordStore
    {
        public int Reads { get; private set; }

        public int Batches { get; private set; }

        public int LastBatchSize { get; private set; }

        public Task<ExperienceRecordCreateResult> CreateAsync(AuthorizationContext authorization, ExperienceRecord record, CancellationToken cancellationToken) =>
            inner.CreateAsync(authorization, record, cancellationToken);

        public Task<ExperienceRecordGetResult> GetAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, CancellationToken cancellationToken) =>
            GetAsync(authorization, scope, experienceId, new ExperienceReadOptions(), cancellationToken);

        public Task<ExperienceRecordGetResult> GetAsync(
            AuthorizationContext authorization,
            Scope scope,
            Guid experienceId,
            ExperienceReadOptions options,
            CancellationToken cancellationToken)
        {
            Reads++;
            return inner.GetAsync(authorization, scope, experienceId, options, cancellationToken);
        }

        public Task<ExperienceRecordGetManyResult> GetManyAsync(
            AuthorizationContext authorization,
            Scope scope,
            IReadOnlyList<Guid> experienceIds,
            ExperienceReadOptions options,
            CancellationToken cancellationToken)
        {
            if (!batch)
            {
                return this.GetManySequentiallyAsync(authorization, scope, experienceIds, options, cancellationToken);
            }

            Batches++;
            LastBatchSize = experienceIds.Count;
            return inner.GetManyAsync(authorization, scope, experienceIds, options, cancellationToken);
        }

        public Task<ExperienceRecordQueryResult> QueryAsync(AuthorizationContext authorization, ExperienceRecordQuery query, CancellationToken cancellationToken) =>
            inner.QueryAsync(authorization, query, cancellationToken);

        public Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(
            AuthorizationContext authorization,
            Scope scope,
            LifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken) =>
            inner.CommitLifecycleEventAsync(authorization, scope, lifecycleEvent, cancellationToken);

        public Task<ExperienceRecordHistoryResult> GetHistoryAsync(AuthorizationContext authorization, ExperienceRecordHistoryQuery query, CancellationToken cancellationToken) =>
            inner.GetHistoryAsync(authorization, query, cancellationToken);

        public Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(
            AuthorizationContext authorization,
            Scope scope,
            Guid experienceId,
            Guid replacementExperienceId,
            CancellationToken cancellationToken) =>
            inner.CheckSupersessionAsync(authorization, scope, experienceId, replacementExperienceId, cancellationToken);
    }

    /// <summary>A model that records the exact messages it was handed and answers with a fixed reply.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        public List<ChatMessage>? LastMessages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

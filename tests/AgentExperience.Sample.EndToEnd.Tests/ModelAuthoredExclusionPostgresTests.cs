using AgentExperience.Abstractions;
using AgentExperience.Core.KeyManagement;
using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.Storage.Postgres;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Npgsql;

namespace AgentExperience.Sample.EndToEnd.Tests;

/// <summary>
/// Story 14.4, end to end against a real PostgreSQL: with <see cref="ModelAuthoredLessonPolicy.Exclude"/>, the MAF
/// injection provider asks retrieval to leave model-authored records out, the PostgreSQL candidate source does so in
/// SQL before its limit, and five model-authored records ranked first no longer keep three deterministic ones out of a
/// retrieval limit of three. And the documented residual: a model-authored record sealed without its flag, whose
/// authorship SQL cannot read, is returned by the source, and the retrieval service excludes it once it is opened.
/// </summary>
/// <remarks>
/// It lives in this project because this is the one test project that already has the adapter, the PostgreSQL store
/// and a PostgreSQL container together.
/// </remarks>
[Collection(SamplePostgresCollection.Name)]
public sealed class ModelAuthoredExclusionPostgresTests(SamplePostgresFixture fixture)
{
    private const string TaskText = "refund ticket stuck on a lock";

    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Exclude_injects_the_three_deterministic_records_five_model_authored_ones_outranked()
    {
        var connectionString = await fixture.CreateDatabaseAsync("authorship_window");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);
        var scope = new Scope("tenant-" + Guid.NewGuid().ToString("N"), "app-1", "project-1");
        var store = new PostgresExperienceRecordStore(dataSource);

        for (var i = 0; i < 5; i++)
        {
            await SeedAsync(store, scope, ReflectionAuthorship.Model, strong: true);
        }

        var deterministic = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            deterministic.Add(await SeedAsync(store, scope, ReflectionAuthorship.Deterministic, strong: false));
        }

        var (result, block) = await InjectAsync(new PostgresExperienceCandidateSource(dataSource), store, scope, ModelAuthoredLessonPolicy.Exclude);
        var (included, _) = await InjectAsync(new PostgresExperienceCandidateSource(dataSource), store, scope, ModelAuthoredLessonPolicy.Include);

        Assert.Equal(InjectionOutcome.Injected, result.Outcome);
        Assert.Equal(deterministic.Order(), result.InjectedExperienceIds.Order());
        Assert.Empty(result.Omitted);
        Assert.DoesNotContain("Authored:", block, StringComparison.Ordinal);

        // Without the exclusion the same limit is filled by the model-authored records, labelled.
        Assert.Equal(3, included.InjectedExperienceIds.Count);
        Assert.DoesNotContain(included.InjectedExperienceIds, deterministic.Contains);
    }

    [Fact]
    public async Task A_model_authored_record_sealed_without_its_flag_is_left_out_by_its_source_and_the_limit_still_fills()
    {
        var connectionString = await fixture.CreateDatabaseAsync("authorship_residual");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);
        var scope = new Scope("tenant-" + Guid.NewGuid().ToString("N"), "app-1", "project-1");
        var encryption = new ExperienceEncryption(new EnvelopeExperienceKeyStore(
            LocalExperienceKeyEncryptionKey.Generate("sample-kek-1"),
            new InMemoryExperienceWrappedKeyRepository()));
        var store = new PostgresExperienceRecordStore(dataSource, encryption: encryption);

        var unflagged = await SeedAsync(store, scope, ReflectionAuthorship.Model, strong: true);
        var deterministic = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            deterministic.Add(await SeedAsync(store, scope, ReflectionAuthorship.Deterministic, strong: false));
        }

        // As a row sealed without its flag -- before 0021, or by an instance that predates it during a rolling deploy.
        await using (var owner = NpgsqlDataSource.Create(SamplePostgresFixture.OwnerOf(connectionString)))
        await using (var command = owner.CreateCommand(
            $"UPDATE agent_experience.experience_records SET reflection_model_authored = NULL WHERE experience_id = '{unflagged}'"))
        {
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var (result, block) = await InjectAsync(
            new PostgresExperienceCandidateSource(dataSource, encryption: encryption), store, scope, ModelAuthoredLessonPolicy.Exclude);

        // Story 17.1: SQL cannot tell who wrote it, so the source fails closed and leaves it out before its limit; it
        // takes no place in the window, and all three deterministic records are injected.
        Assert.Empty(result.Excluded);
        Assert.Empty(result.Omitted);
        Assert.Equal(deterministic.Order(), result.InjectedExperienceIds.Order());
        Assert.DoesNotContain("Authored:", block, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_sample_s_in_memory_candidate_source_honours_the_exclusion_before_its_limit()
    {
        // The sample's own double (in-memory mode) is a candidate source too, so it keeps the port's contract: model
        // authorship, an undefined value included, is left out before the limit; no reflection is kept.
        var scope = new Scope("tenant-" + Guid.NewGuid().ToString("N"), "app-1", "project-1");
        var store = new Sample.EndToEnd.Doubles.InMemoryRecordStore();
        var model = await SeedAsync(store, scope, ReflectionAuthorship.Model, strong: true);
        var undefined = await SeedAsync(store, scope, (ReflectionAuthorship)5, strong: true);
        var legacy = await SeedAsync(
            store, scope, ReflectionAuthorship.Deterministic, strong: true,
            producer: ReflectionAuthorshipConventions.LibraryModelReflectorProducerPrefix + "1.0.0 (some-model)");
        var deterministic = await SeedAsync(store, scope, ReflectionAuthorship.Deterministic, strong: false);
        var source = new Sample.EndToEnd.Doubles.InMemoryCandidateSource(store);
        var authorization = new AuthorizationContext(scope.TenantId, "host-principal", ["experience:read"], Now);
        var query = new ExperienceCandidateQuery(scope, TaskText, [ExperienceStatus.Validated], 0d, Limit: 1);

        var excluding = await source.SearchAsync(authorization, query with { ExcludeModelAuthored = true }, CancellationToken.None);
        var including = await source.SearchAsync(authorization, query with { Limit = 4 }, CancellationToken.None);

        Assert.Equal([deterministic], excluding.Candidates.Select(c => c.Record.ExperienceId));
        Assert.Equal(new[] { model, undefined, legacy, deterministic }.Order(), including.Candidates.Select(c => c.Record.ExperienceId).Order());
    }

    private static async Task<(ExperienceInjectionResult Result, string? Block)> InjectAsync(
        IExperienceCandidateSource source,
        IExperienceRecordStore store,
        Scope scope,
        ModelAuthoredLessonPolicy policy)
    {
        ExperienceInjectionResult? reported = null;
        var client = new RecordingChatClient();
        var authorization = new AuthorizationContext(scope.TenantId, "host-principal", ["experience:read"], Now);
        var provider = new ExperienceContextProvider(
            new ExperienceRetrievalService(
                source, RetrievalPolicy.Default with { Timeout = TimeSpan.FromSeconds(30) }, RankingWeights.Default, new FixedNow(Now)),
            store,
            new ExperienceInjectionOptions
            {
                ResolveRequest = _ => new RetrieveExperienceRequest(authorization, scope, TaskText, Limit: 3),
                ModelAuthoredLessons = policy,
                Limits = ExperienceInjectionLimits.Default with { EligibilityCheckTimeout = TimeSpan.FromMinutes(5) },
                TimeProvider = new FixedNow(Now),
                OnContextInjected = result => reported = result,
            });

        var agent = new ChatClientAgent(client, new ChatClientAgentOptions { AIContextProviders = [provider] });
        await agent.RunAsync(TaskText);

        var block = client.LastMessages?
            .FirstOrDefault(m => m.AdditionalProperties?.ContainsKey(ExperienceContextProvider.HistoricalReferenceKey) == true)?
            .Text;
        return (reported!, block);
    }

    private static async Task<Guid> SeedAsync(IExperienceRecordStore store, Scope scope, ReflectionAuthorship authorship, bool strong, string producer = "tests")
    {
        var id = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var record = new ExperienceRecord(
            ExperienceId: id,
            SourceRunId: runId,
            Scope: scope,
            // Every term in the task ID too makes a record a stronger text match than one with them in its summary only.
            TaskId: strong ? "refund-ticket-stuck-lock" : "triage",
            TaskSummary: "A refund ticket stuck on a lock.",
            Attempts: [],
            Outcome: new Outcome(TaskVerificationStatus.Verified, [], null, Now),
            CompletionScore: 1d,
            Reflection: new Reflection(
                Guid.NewGuid(), runId, "Release the lock before retrying.", [], [], [], [], null, [],
                TaskVerificationStatus.Verified, 1d, "rules-v1", producer, Now) { Authorship = authorship },
            Environment: new EnvironmentFingerprint("host", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
            Provenance: new Provenance("tests", null, Now, null),
            Status: ExperienceStatus.Validated,
            ReuseConfidence: 0.75,
            SupportingValidations: 0,
            Contradictions: 0,
            Revision: 0,
            CreatedAt: Now,
            UpdatedAt: Now);

        var created = await store.CreateAsync(
            new AuthorizationContext(scope.TenantId, "seed", ["experience:write"], Now), record, CancellationToken.None);
        Assert.True(
            created.Outcome == ExperienceStoreOutcome.Created,
            string.Join("; ", created.Errors.Select(error => $"{error.Path}: {error.Message}")));
        return id;
    }

    /// <summary>A clock frozen at <see cref="Now"/> for readings, with the system's real timers.</summary>
    private sealed class FixedNow(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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

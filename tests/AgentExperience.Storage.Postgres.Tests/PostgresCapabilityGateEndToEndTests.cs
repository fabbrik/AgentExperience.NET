using System.Runtime.CompilerServices;
using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Npgsql;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 21.8 (deferred from 20.8): the capability gate before the record limit, end to end over real PostgreSQL. The
/// MAF injection provider runs over the PostgreSQL candidate source and record store, both audited into the real grant
/// access log, with a receiving agent that lacks the tool a borrowed lesson's approach names. A grant that shows the
/// approach lets the gate drop the borrowed record before the limit of one, so the reader's own record takes the slot;
/// the only access row for the borrowed record is the search's, and no re-read row follows. A grant that withholds the
/// approach (<see cref="ExperienceGrantDisclosure.LessonOnly"/>) passes unprobed and is delivered, as before 20.8.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresCapabilityGateEndToEndTests(PostgresFixture fixture)
{
    private const string TaskText = "refund ticket stuck on a lock";

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Theory]
    [InlineData(ExperienceGrantDisclosure.LessonAndApproach)]
    [InlineData(ExperienceGrantDisclosure.LessonOnly)]
    public async Task A_borrowed_record_whose_approach_the_agent_cannot_act_on_takes_no_slot_and_only_its_search_is_audited(ExperienceGrantDisclosure level)
    {
        var tenant = NewTenant();
        var lender = Scope(tenant, team: "team-a");
        var reader = Scope(tenant, team: "team-b");
        var store = new PostgresExperienceRecordStore(fixture.DataSource);

        // The borrowed record matches every term and ranks first; its approach calls "c", which the agent lacks. The
        // reader's own record matches three of the four terms, ranks below it, and its approach calls "a".
        var borrowed = await SeedAsync(store, lender, strong: true, tool: "c");
        var own = await SeedAsync(store, reader, strong: false, tool: "a");
        var grant = await new PostgresExperienceGrantStore(fixture.DataSource).CreateAsync(
            Authorize(tenant),
            new GrantAdministration("sharing-administrator", DateTimeOffset.UtcNow),
            new ExperienceGrantRequest(
                Guid.NewGuid(), borrowed, lender, reader, "team-b owns the follow-up", Micro(DateTimeOffset.UtcNow.AddHours(1)), level),
            CancellationToken.None);
        Assert.Equal(ExperienceGrantOutcome.Created, grant.Outcome);

        var failures = new List<ExperienceGrantAccessFailure>();
        var auditing = new ExperienceGrantAuditing(new PostgresExperienceGrantAccessLog(fixture.DataSource), failures.Add, ExperienceGrantAuditingMode.Required);
        var audited = new PostgresExperienceRecordStore(fixture.DataSource, onGrantsUnavailable: null, auditing: auditing);
        var source = new PostgresExperienceCandidateSource(fixture.DataSource, onGrantsUnavailable: null, auditing: auditing);

        var (result, block) = await InjectAsync(source, audited, Authorize(tenant), reader);

        Assert.Empty(failures);
        Assert.Equal(InjectionOutcome.Injected, result.Outcome);
        Assert.Null(result.InjectionNotRecorded);
        if (level == ExperienceGrantDisclosure.LessonAndApproach)
        {
            // Gated before the limit: omitted as ToolUnavailable, and the reader's own record backfills the one slot.
            Assert.Equal([own], result.InjectedExperienceIds);
            Assert.Equal(new OmittedExperience(borrowed, InjectionOmissionReason.ToolUnavailable), Assert.Single(result.Omitted));

            // The search's access row only: the record was never re-read for delivery.
            Assert.Equal(1L, await AccessRowsAsync(borrowed));
        }
        else
        {
            // Withheld approach: nothing for the gate to probe, so it takes the slot and is delivered, as before 20.8.
            Assert.Equal([borrowed], result.InjectedExperienceIds);
            Assert.Equal(InjectionOmissionReason.OverRecordLimit, Assert.Single(result.Omitted).Reason);
            Assert.DoesNotContain("c [returned]", block, StringComparison.Ordinal);

            // The search's row and the delivering re-read's.
            Assert.Equal(2L, await AccessRowsAsync(borrowed));
        }

        Assert.Equal(0L, await AccessRowsAsync(own));

        // Story 21.8 on real PostgreSQL: the injection of the reader's own record is recorded in its scope; a borrowed
        // record injected here is the lender's, and is counted in neither scope.
        var readerPage = await store.QueryWithInjectionsAsync(Authorize(tenant), new ExperienceRecordQuery(reader), CancellationToken.None);
        var lenderPage = await store.QueryWithInjectionsAsync(Authorize(tenant), new ExperienceRecordQuery(lender), CancellationToken.None);
        Assert.Equal(level == ExperienceGrantDisclosure.LessonAndApproach ? [own] : [], readerPage.Injections!.Keys);
        Assert.Empty(lenderPage.Injections!);
    }

    private static async Task<(ExperienceInjectionResult Result, string? Block)> InjectAsync(
        IExperienceCandidateSource source,
        IExperienceRecordStore store,
        AuthorizationContext authorization,
        Scope scope)
    {
        ExperienceInjectionResult? reported = null;
        var client = new RecordingChatClient();
        var clock = new FixedNow(Now);
        var provider = new ExperienceContextProvider(
            new ExperienceRetrievalService(source, RetrievalPolicy.Default with { Timeout = TimeSpan.FromSeconds(30) }, RankingWeights.Default, clock),
            store,
            new ExperienceInjectionOptions
            {
                ResolveRequest = _ => new RetrieveExperienceRequest(authorization, scope, TaskText, CorrelationId: "gate-e2e"),
                ReceivingAgent = new ReceivingAgentCapabilities { AvailableTools = new HashSet<string>(["a"], StringComparer.Ordinal) },
                Limits = ExperienceInjectionLimits.Default with { MaxRecords = 1, EligibilityCheckTimeout = TimeSpan.FromMinutes(5) },
                SessionLimits = null,
                TimeProvider = clock,
                OnContextInjected = result => reported = result,
            });

        var agent = new ChatClientAgent(client, new ChatClientAgentOptions { AIContextProviders = [provider] });
        await agent.RunAsync(TaskText);

        var block = client.LastMessages?
            .FirstOrDefault(m => m.AdditionalProperties?.ContainsKey(ExperienceContextProvider.HistoricalReferenceKey) == true)?
            .Text;
        return (reported!, block);
    }

    private static async Task<Guid> SeedAsync(IExperienceRecordStore store, Scope scope, bool strong, string tool)
    {
        var id = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var at = Micro(Now.AddMinutes(-5));
        var record = new ExperienceRecord(
            ExperienceId: id,
            SourceRunId: runId,
            Scope: scope,
            // The strong record contains every query term; the other lacks "ticket", so it covers three of the four and
            // ranks below it on text relevance, while still meeting the default minimum of three.
            TaskId: strong ? "refund-ticket-stuck-lock" : "triage",
            TaskSummary: strong ? "A refund ticket stuck on a lock." : "A refund stuck on a lock.",
            Attempts:
            [
                new Attempt(
                    Guid.NewGuid(),
                    0,
                    at,
                    TimeSpan.FromSeconds(1),
                    [new ToolCallRecord(Guid.NewGuid(), 0, tool, new Dictionary<string, object?>(), at, TimeSpan.FromMilliseconds(10), "ok", null)],
                    "released the lock",
                    null),
            ],
            Outcome: new Outcome(TaskVerificationStatus.Verified, [], null, at),
            CompletionScore: 1d,
            Reflection: new Reflection(
                Guid.NewGuid(), runId, "Release the lock before retrying.", [], [], [], [], null, [],
                TaskVerificationStatus.Verified, 1d, "rules-v1", "tests", at) { Authorship = ReflectionAuthorship.Deterministic },
            Environment: new EnvironmentFingerprint("host", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
            Provenance: new Provenance("tests", null, at, null),
            Status: ExperienceStatus.Validated,
            ReuseConfidence: 0.75,
            SupportingValidations: 0,
            Contradictions: 0,
            Revision: 0,
            CreatedAt: at,
            UpdatedAt: at);

        var created = await store.CreateAsync(Authorize(scope.TenantId), record, CancellationToken.None);
        Assert.True(
            created.Outcome == ExperienceStoreOutcome.Created,
            string.Join("; ", created.Errors.Select(error => $"{error.Path}: {error.Message}")));
        return id;
    }

    private async Task<long> AccessRowsAsync(Guid experienceId)
    {
        await using var command = fixture.RawDataSource.CreateCommand(
            "SELECT count(*) FROM agent_experience.experience_grant_access WHERE experience_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static DateTimeOffset Micro(DateTimeOffset value) => new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    /// <summary>A clock frozen for readings, with the system's real timers.</summary>
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
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

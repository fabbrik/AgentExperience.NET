using System.Runtime.CompilerServices;
using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 28.3: recall at the moment of failure, end to end over real PostgreSQL. A captured <see cref="ChatClientAgent"/>
/// run whose tool throws looks the failure up through <see cref="PostgresExperienceSignatureSource"/> and migration
/// <c>0030</c>'s table, re-reads the lesson through the PostgreSQL record store, and shows the model one hint naming
/// what worked, in the instructions of its next call; a failure no lesson resolved gets none. Each test uses its own
/// random tenant.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresRecallEndToEndTests(PostgresFixture fixture)
{
    private const string Error = "System.InvalidOperationException";

    private const string TaskText = "unrelated words that match no stored lesson";

    [Fact]
    public async Task A_failure_a_stored_lesson_resolved_is_hinted_through_the_real_signature_source()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var store = new PostgresExperienceRecordStore(fixture.DataSource);
        var resolved = Reusable(WithFailedCall(Minimal(scope, status: ExperienceStatus.Validated), error: Error));
        var unresolved = Reusable(WithFailedCall(Minimal(scope, status: ExperienceStatus.Validated), error: Error, resolved: false));
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(Authorize(tenant), resolved, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(Authorize(tenant), unresolved, CancellationToken.None)).Outcome);

        var (recalls, model) = await RunAsync(store, tenant, scope);

        var recall = Assert.Single(recalls);
        Assert.Equal(ExperienceRecallOutcome.Hinted, recall.Outcome);
        Assert.Equal([resolved.ExperienceId], recall.ExperienceIds);
        Assert.Equal(ExperienceSignatureMatch.Exact, recall.Match);
        Assert.Contains(recall.Omitted, omitted => omitted.ExperienceId == unresolved.ExperienceId);

        Assert.Equal(2, model.Instructions.Count);
        Assert.Null(model.Instructions[0]);
        var hint = Assert.IsType<string>(model.Instructions[1]);
        Assert.StartsWith(HistoricalReferenceWriter.RecallBegin, hint, StringComparison.Ordinal);
        Assert.Contains("An earlier verified run hit this same failure (run_tests [failed: InvalidOperationException]) and then:", hint, StringComparison.Ordinal);
        Assert.Contains("  - worked: attempt 1: run_tests [returned] → completed", hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_no_lesson_resolved_gets_no_hint()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var store = new PostgresExperienceRecordStore(fixture.DataSource);
        var unresolved = Reusable(WithFailedCall(Minimal(scope, status: ExperienceStatus.Validated), error: Error, resolved: false));
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(Authorize(tenant), unresolved, CancellationToken.None)).Outcome);

        var (recalls, model) = await RunAsync(store, tenant, scope);

        var recall = Assert.Single(recalls);
        Assert.Equal(ExperienceRecallOutcome.NoMatch, recall.Outcome);
        Assert.Equal([unresolved.ExperienceId], recall.Omitted.Select(omitted => omitted.ExperienceId));
        Assert.All(model.Instructions, instructions => Assert.Null(instructions));
    }

    [Fact]
    public async Task A_held_out_run_gets_no_hint_and_its_would_be_hinted_record_is_its_assignment_row()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var store = new PostgresExperienceRecordStore(fixture.DataSource);
        var resolved = Reusable(WithFailedCall(Minimal(scope, status: ExperienceStatus.Validated), error: Error));
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(Authorize(tenant), resolved, CancellationToken.None)).Outcome);

        var (recalls, model) = await RunAsync(store, tenant, scope, new ExperienceHoldoutOptions { Rate = 1 });

        var recall = Assert.Single(recalls);
        Assert.Equal(ExperienceRecallOutcome.Withheld, recall.Outcome);
        Assert.Equal([resolved.ExperienceId], recall.WithheldExperienceIds);
        Assert.All(model.Instructions, instructions => Assert.Null(instructions));

        var rows = await store.QueryHoldoutOutcomesAsync(Authorize(tenant), new HoldoutOutcomeQuery(scope), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, rows.Outcome);
        var row = Assert.Single(rows.Rows);
        Assert.Equal(HoldoutArm.Holdout, row.Arm);
        Assert.Equal(resolved.ExperienceId, row.ExperienceId);
        Assert.Equal(recall.RunId, row.RunId);
    }

    private static ExperienceRecord Reusable(ExperienceRecord record) => record with { ReuseConfidence = 0.8 };

    private async Task<(List<ExperienceRecallResult> Recalls, Model Model)> RunAsync(PostgresExperienceRecordStore store, string tenant, Scope scope, ExperienceHoldoutOptions? holdout = null)
    {
        var recalls = new List<ExperienceRecallResult>();
        var clock = new NeverFiringTimers();
        var provider = new ExperienceContextProvider(
            new ExperienceRetrievalService(new PostgresExperienceCandidateSource(fixture.DataSource), RetrievalPolicy.Default with { Timeout = TimeSpan.FromMinutes(5) }, RankingWeights.Default, clock),
            store,
            new ExperienceInjectionOptions
            {
                ResolveRequest = _ => new RetrieveExperienceRequest(Authorize(tenant), scope, TaskText, CorrelationId: "recall-e2e"),
                Limits = ExperienceInjectionLimits.Default with { EligibilityCheckTimeout = TimeSpan.FromMinutes(5) },
                SessionLimits = null,
                TimeProvider = clock,
                Holdout = holdout ?? ExperienceHoldoutOptions.Off,
                Recall = new ExperienceRecallOptions
                {
                    SignatureSource = new PostgresExperienceSignatureSource(fixture.DataSource),
                    LookupTimeout = TimeSpan.FromMinutes(5),
                    OnRecall = recalls.Add,
                },
            });

        var model = new Model();
        var tool = AIFunctionFactory.Create(new Func<string>(() => throw new InvalidOperationException("tests failed")), "run_tests");
        var agent = new ChatClientAgent(model, new ChatClientAgentOptions
        {
            AIContextProviders = [provider],
            ChatOptions = new ChatOptions { Tools = [tool] },
        })
            .AsBuilder()
            .UseExperienceCapture(
                new InMemoryExperienceCaptureService(new DefaultSanitizer(AgentExperience.Core.DependencyInjection.AgentExperienceDefaults.Sanitization), AgentExperience.Core.DependencyInjection.AgentExperienceDefaults.CaptureLimits),
                new ExperienceCaptureOptions { ResolveRun = _ => new ExperienceRunDescriptor("task-1", scope, TaskText) })
            .Build();

        await agent.RunAsync(TaskText);
        await provider.InjectionRecordsSettledAsync();
        return (recalls, model);
    }

    /// <summary>The system clock, with timers that never fire, so no bound races a loaded machine.</summary>
    private sealed class NeverFiringTimers : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            System.CreateTimer(static _ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Calls the tool on the first turn and answers on the second; records each call's instructions.</summary>
    private sealed class Model : IChatClient
    {
        public List<string?> Instructions { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Instructions.Add(options?.Instructions);
            return Task.FromResult(messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any()
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "run_tests", new Dictionary<string, object?>())])));
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

using System.Runtime.CompilerServices;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Lifecycle;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// Story 28.6 over the one-call setup: three real runs that failed the same way and were fixed the same way, then
/// <c>GetAgentExperienceLessons().ConsolidateAsync(identity)</c>. The view lists one lesson, "consolidated from 3", and a
/// fourth run is given that one lesson, saying how many runs confirmed it.
/// </summary>
public sealed class ExperienceConsolidationTests
{
    private const string RunTests = "run_tests";

    private static readonly ExperienceIdentity Caller = new(
        new AuthorizationContext("contoso", "svc", [], DateTimeOffset.UnixEpoch),
        new Scope("contoso", "support", "tickets"));

    [Fact]
    public async Task Three_runs_consolidate_into_one_lesson_the_view_and_the_next_run_see()
    {
        var finalized = new List<FinalizeExperienceResult>();
        var injections = new List<ExperienceInjectionResult>();
        await using var services = Build(finalized, injections);
        var lessons = services.GetAgentExperienceLessons();
        var model = new TwoCallModel();

        // Each run: run_tests exits 3, then 0. The second and third runs are given the earlier lessons, as usual.
        for (var n = 0; n < 3; n++)
        {
            await Agent(services, model, ["exit=3", "exit=0"]).RunAsync($"Ticket #{4810 + n}: a refund is stuck on a lock. Triage it.");
        }

        Assert.Equal(3, finalized.Count(result => result.Outcome == FinalizationOutcome.Validated));
        Assert.Equal(3, (await lessons.ListAsync(Caller)).Lessons.Count(lesson => lesson.IsEligible));

        var result = await lessons.ConsolidateAsync(Caller);

        var group = Assert.Single(result.Groups);
        Assert.Equal(ExperienceConsolidationGroupOutcome.Created, group.Outcome);
        Assert.Equal(3, group.IndependentRuns);
        Assert.All(group.Sources, source => Assert.True(source.Superseded));

        var page = await lessons.ListAsync(Caller);
        var lesson = Assert.Single(page.Lessons, item => item.IsEligible);
        Assert.Equal(group.ConsolidatedExperienceId, lesson.ExperienceId);
        Assert.Equal(3, lesson.ConsolidatedFrom);
        Assert.Contains("Lesson: Verified in 3 independent runs (latest ", lesson.BlockText, StringComparison.Ordinal);
        Assert.Contains("Failed first with exit 3 from run_tests in 3 of them. Worked: run_tests → run_tests.", lesson.BlockText, StringComparison.Ordinal);
        Assert.Contains("run_tests [failed: exit 3], run_tests [returned]", lesson.BlockText, StringComparison.Ordinal);
        Assert.All(page.Lessons.Where(item => item.ExperienceId != lesson.ExperienceId), item =>
        {
            Assert.Equal(ExperienceStatus.Superseded, item.Status);
            Assert.Null(item.ConsolidatedFrom);
        });
        Assert.Contains("consolidated from 3", ExperienceLessonsHtml.Render(page), StringComparison.Ordinal);

        // The next run is given the one consolidated lesson, not three.
        await Agent(services, model, ["exit=0", "exit=0"]).RunAsync("Ticket #4999: a refund is stuck on a lock. Triage it.");
        Assert.Equal([group.ConsolidatedExperienceId], injections[^1].InjectedExperienceIds);
        var block = Assert.Single(model.FirstCalls[^1], message =>
            message.AdditionalProperties?.ContainsKey(ExperienceContextProvider.HistoricalReferenceKey) == true).Text;
        Assert.Contains("Verified in 3 independent runs", block, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Consolidating_lessons_of_an_identity_outside_the_authorization_is_denied_and_writes_nothing()
    {
        await using var services = Build([], []);
        var outsider = new ExperienceIdentity(Caller.Authorization, new Scope("fabrikam", "support", "tickets"));

        var result = await services.GetAgentExperienceLessons().ConsolidateAsync(outsider);

        Assert.Equal(ExperienceStoreOutcome.Denied, result.Outcome);
        Assert.Empty(result.Groups);
    }

    private static ServiceProvider Build(List<FinalizeExperienceResult> finalized, List<ExperienceInjectionResult> injections)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<IHostEnvironment>(new AgentExperienceFiveLineSetupTests.FakeHostEnvironment(Environments.Development));
        collection
            .AddAgentExperience(options =>
            {
                options.ResolveIdentity = (_, _) => ValueTask.FromResult<ExperienceIdentity?>(Caller);
                options.Verify = ExperienceVerifiers.ToolSucceeded(RunTests);
                options.Capture = capture =>
                {
                    capture.ClassifyToolResult = ExperienceToolResultClassifiers.ExitCode(RunTests);
                    capture.OnRunFinalized = finalized.Add;
                };
                options.Injection = injection => injection.OnContextInjected = injections.Add;
            })
            .UseInMemoryStorageForDevelopment();
        return collection.BuildServiceProvider();
    }

    private static AIAgent Agent(IServiceProvider services, TwoCallModel model, IEnumerable<string> exits)
    {
        var queue = new Queue<string>(exits);
        var runTests = AIFunctionFactory.Create(() => queue.Dequeue(), RunTests);
        return new ChatClientAgent(model, name: "triage-ticket", tools: [runTests]).AsBuilder().UseAgentExperience(services).Build();
    }

    /// <summary>A model that calls <c>run_tests</c> twice, one call per turn, then answers.</summary>
    private sealed class TwoCallModel : IChatClient
    {
        public List<List<ChatMessage>> FirstCalls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            var results = list.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Count();
            if (results == 0)
            {
                FirstCalls.Add(list);
            }

            return Task.FromResult(new ChatResponse(results < 2
                ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call-{results}", RunTests, new Dictionary<string, object?>())])
                : new ChatMessage(ChatRole.Assistant, "Done.")));
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

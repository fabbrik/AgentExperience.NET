using AgentExperience.Core.DependencyInjection;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Story 28.7: the capture service keeps each run it completes in a run store, write-through at completion, and a run store
/// that throws or refuses never fails the completion: it is reported on the result.
/// </summary>
public sealed class CaptureRunStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly Scope Scope = new("tenant-1", "app-1", "project-1", TeamId: "team-1");

    [Fact]
    public async Task A_completed_run_is_saved_under_its_completion_event_in_exactly_its_own_scope()
    {
        var store = new RecordingRunStore();
        var capture = Capture(store);
        var runId = Start(capture);
        var completion = Guid.NewGuid();

        var completed = await capture.CompleteRunAsync(runId, completion, RunExecutionStatus.Failed, Now.AddSeconds(1));
        var again = await capture.CompleteRunAsync(runId, completion, RunExecutionStatus.Failed, Now.AddSeconds(1));

        Assert.Equal(CompleteRunOutcome.Recorded, completed.Outcome);
        Assert.Null(completed.RunStoreFailure);
        Assert.Equal(CompleteRunOutcome.DuplicateNoOp, again.Outcome);
        Assert.Equal(2, store.Saves.Count);
        var (authorization, run, eventId) = store.Saves[0];
        Assert.Equal(completion, eventId);
        Assert.Equal(RunExecutionStatus.Failed, run.ExecutionStatus);
        Assert.Equal(Now.AddSeconds(1), run.EndedAt);
        Assert.True(authorization.Permits(Scope));
        Assert.False(authorization.Permits(Scope with { TeamId = "team-2" }));
        Assert.False(authorization.Permits(Scope with { TeamId = null }));
    }

    [Fact]
    public async Task A_run_store_that_throws_is_reported_and_the_completion_stands()
    {
        var store = new RecordingRunStore { Throw = new InvalidOperationException("down") };
        var capture = Capture(store);
        var runId = Start(capture);

        var completed = await capture.CompleteRunAsync(runId, Guid.NewGuid(), RunExecutionStatus.Completed, Now.AddSeconds(1));

        Assert.Equal(CompleteRunOutcome.Recorded, completed.Outcome);
        Assert.Contains("InvalidOperationException", completed.RunStoreFailure, StringComparison.Ordinal);
        Assert.Same(store.Throw, completed.RunStoreException);
        Assert.True(capture.TryGetRun(runId, out var run));
        Assert.Equal(RunExecutionStatus.Completed, run.ExecutionStatus);
    }

    [Fact]
    public async Task A_run_store_that_refuses_is_reported_without_an_exception()
    {
        var store = new RecordingRunStore { Answer = ExperienceRunStoreOutcome.Conflict };
        var capture = Capture(store);
        var runId = Start(capture);

        var completed = await capture.CompleteRunAsync(runId, Guid.NewGuid(), RunExecutionStatus.Completed, Now.AddSeconds(1));

        Assert.Equal(CompleteRunOutcome.Recorded, completed.Outcome);
        Assert.Contains("Conflict", completed.RunStoreFailure, StringComparison.Ordinal);
        Assert.Null(completed.RunStoreException);
    }

    [Fact]
    public async Task Without_a_run_store_nothing_is_saved_and_nothing_is_reported()
    {
        var capture = new InMemoryExperienceCaptureService(
            new DefaultSanitizer(AgentExperienceDefaults.Sanitization), AgentExperienceDefaults.CaptureLimits, TimeProvider.System, runStore: null);
        var runId = Start(capture);

        var completed = await capture.CompleteRunAsync(runId, Guid.NewGuid(), RunExecutionStatus.Completed, Now.AddSeconds(1));

        Assert.Equal(CompleteRunOutcome.Recorded, completed.Outcome);
        Assert.Null(completed.RunStoreFailure);
    }

    private static InMemoryExperienceCaptureService Capture(IExperienceRunStore store) =>
        new(new DefaultSanitizer(AgentExperienceDefaults.Sanitization), AgentExperienceDefaults.CaptureLimits, TimeProvider.System, store);

    private static Guid Start(InMemoryExperienceCaptureService capture)
    {
        var runId = Guid.NewGuid();
        capture.StartRun(
            runId,
            "task-1",
            null,
            Scope,
            new EnvironmentFingerprint("host", "net10.0", "linux", null, new Dictionary<string, string>()),
            new Provenance("tests", null, Now, null),
            Now);
        return runId;
    }

    private sealed class RecordingRunStore : IExperienceRunStore
    {
        public List<(AuthorizationContext Authorization, ExperienceRun Run, Guid CompletionEventId)> Saves { get; } = [];

        public Exception? Throw { get; init; }

        public ExperienceRunStoreOutcome Answer { get; init; } = ExperienceRunStoreOutcome.Saved;

        public Task<ExperienceRunStoreResult> SaveAsync(AuthorizationContext authorization, ExperienceRun run, Guid completionEventId, CancellationToken cancellationToken)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Saves.Add((authorization, run, completionEventId));
            return Task.FromResult(new ExperienceRunStoreResult(Answer, []));
        }

        public Task<ExperienceRunGetResult> GetAsync(AuthorizationContext authorization, Guid runId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExperienceRunListResult> ListUnfinalizedAsync(AuthorizationContext authorization, ExperienceRunQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExperienceRunStoreResult> MarkFinalizedAsync(AuthorizationContext authorization, Scope scope, Guid runId, DateTimeOffset finalizedAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExperienceRunStoreResult> DeleteAsync(AuthorizationContext authorization, Scope scope, Guid runId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

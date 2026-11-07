using static AgentExperience.Storage.Conformance.ConformanceData;

namespace AgentExperience.Storage.Conformance;

/// <summary>
/// The behaviour every <see cref="IExperienceRunStore"/> must show, observed through the port alone: a completed run reads
/// back as it was saved, saving is idempotent per completion event and conflicts across events and scopes without revealing
/// anything, reads stay within the authorization, the unfinalized listing pages by run in exactly one scope, and marking
/// finalized and deleting act only in exactly the run's scope. A subclass supplies the store through
/// <see cref="CreateStore"/>.
/// </summary>
public abstract class RunStoreConformanceTests
{
    private readonly Lazy<IExperienceRunStore> _store;

    protected RunStoreConformanceTests()
    {
        _store = new Lazy<IExperienceRunStore>(CreateStore);
    }

    /// <summary>The store under test.</summary>
    protected IExperienceRunStore Store => _store.Value;

    /// <summary>Creates the store under test. Called at most once per test; every test works in a tenant of its own.</summary>
    protected abstract IExperienceRunStore CreateStore();

    /// <summary>A completed run with every part populated, from <see cref="ConformanceData.FullRecord"/>'s attempts, environment and provenance.</summary>
    public static ExperienceRun CompletedRun(Scope scope, Guid? runId = null, DateTimeOffset? endedAt = null)
    {
        var record = FullRecord(scope);
        return new ExperienceRun(
            runId ?? Guid.NewGuid(),
            "conformance-task",
            "a task worth remembering",
            scope,
            record.Environment,
            record.Provenance,
            record.Attempts,
            RunExecutionStatus.Failed,
            null,
            Time,
            endedAt ?? Time.AddMinutes(1));
    }

    [Fact]
    public async Task A_saved_run_reads_back_exactly_as_it_was_saved()
    {
        var tenant = NewTenant();
        var run = CompletedRun(Scope(tenant, team: "team-1"));
        var completion = Guid.NewGuid();

        var saved = await Store.SaveAsync(Authorize(tenant), run, completion, CancellationToken.None);
        var read = await Store.GetAsync(Authorize(tenant), run.RunId, CancellationToken.None);

        Assert.Equal(ExperienceRunStoreOutcome.Saved, saved.Outcome);
        Assert.Equal(ExperienceRunStoreOutcome.Found, read.Outcome);
        Assert.Equal(Json(run), Json(read.Run!.Run));
        Assert.Equal(completion, read.Run.CompletionEventId);
        Assert.Null(read.Run.FinalizedAt);
        Assert.NotEmpty(read.Run.Run.Provenance.ExposedTo);
    }

    [Fact]
    public async Task Saving_again_under_the_same_event_is_AlreadySaved_and_under_another_is_Conflict()
    {
        var tenant = NewTenant();
        var run = CompletedRun(Scope(tenant));
        var completion = Guid.NewGuid();
        await Store.SaveAsync(Authorize(tenant), run, completion, CancellationToken.None);

        var again = await Store.SaveAsync(Authorize(tenant), run, completion, CancellationToken.None);
        var other = await Store.SaveAsync(Authorize(tenant), run with { TaskId = "another" }, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(ExperienceRunStoreOutcome.AlreadySaved, again.Outcome);
        Assert.Equal(ExperienceRunStoreOutcome.Conflict, other.Outcome);
        Assert.Equal("conformance-task", (await Store.GetAsync(Authorize(tenant), run.RunId, CancellationToken.None)).Run!.Run.TaskId);
    }

    [Fact]
    public async Task A_run_id_held_in_another_scope_is_Conflict_and_neither_scope_sees_the_other()
    {
        var tenant = NewTenant();
        var otherTenant = NewTenant();
        var run = CompletedRun(Scope(tenant));
        var completion = Guid.NewGuid();
        await Store.SaveAsync(Authorize(tenant), run, completion, CancellationToken.None);

        var foreign = await Store.SaveAsync(Authorize(otherTenant), run with { Scope = Scope(otherTenant) }, completion, CancellationToken.None);
        var sameTenantOtherProject = await Store.SaveAsync(Authorize(tenant), run with { Scope = Scope(tenant, "project-2") }, completion, CancellationToken.None);

        Assert.Equal(ExperienceRunStoreOutcome.Conflict, foreign.Outcome);
        Assert.Equal(ExperienceRunStoreOutcome.Conflict, sameTenantOtherProject.Outcome);
        Assert.Equal(ExperienceRunStoreOutcome.NotFound, (await Store.GetAsync(Authorize(otherTenant), run.RunId, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_read_outside_the_authorization_s_bounds_is_NotFound()
    {
        var tenant = NewTenant();
        var run = CompletedRun(Scope(tenant, team: "team-1"));
        await Store.SaveAsync(Authorize(tenant), run, Guid.NewGuid(), CancellationToken.None);

        var otherProject = Authorize(tenant) with { ProjectId = "project-2" };
        var otherTeam = Authorize(tenant) with { TeamId = "team-2" };
        var sameTeam = Authorize(tenant) with { ProjectId = "project-1", TeamId = "team-1" };

        Assert.Equal(ExperienceRunStoreOutcome.NotFound, (await Store.GetAsync(otherProject, run.RunId, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceRunStoreOutcome.NotFound, (await Store.GetAsync(otherTeam, run.RunId, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceRunStoreOutcome.Found, (await Store.GetAsync(sameTeam, run.RunId, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceRunStoreOutcome.NotFound, (await Store.GetAsync(Authorize(tenant), Guid.NewGuid(), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Malformed_and_unauthorized_requests_are_refused_before_storage()
    {
        var tenant = NewTenant();
        var run = CompletedRun(Scope(tenant));

        Assert.Equal(ExperienceRunStoreOutcome.Invalid, (await Store.SaveAsync(Authorize(tenant), run with { ExecutionStatus = null }, Guid.NewGuid(), CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceRunStoreOutcome.Invalid, (await Store.SaveAsync(Authorize(tenant), run with { EndedAt = null }, Guid.NewGuid(), CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceRunStoreOutcome.Invalid, (await Store.SaveAsync(Authorize(tenant), run, Guid.Empty, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceRunStoreOutcome.Invalid, (await Store.SaveAsync(Authorize(tenant), run with { RunId = Guid.Empty }, Guid.NewGuid(), CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceRunStoreOutcome.Denied, (await Store.SaveAsync(Authorize(NewTenant()), run, Guid.NewGuid(), CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceRunStoreOutcome.Invalid, (await Store.GetAsync(Authorize(tenant), Guid.Empty, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceRunStoreOutcome.Invalid, (await Store.ListUnfinalizedAsync(Authorize(tenant), new ExperienceRunQuery(Scope(tenant), Limit: 0), CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceRunStoreOutcome.Denied, (await Store.ListUnfinalizedAsync(Authorize(tenant), new ExperienceRunQuery(Scope(NewTenant())), CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceRunStoreOutcome.Invalid, (await Store.MarkFinalizedAsync(Authorize(tenant), Scope(tenant), run.RunId, default, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceRunStoreOutcome.Denied, (await Store.DeleteAsync(Authorize(tenant), Scope(NewTenant()), run.RunId, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceRunStoreOutcome.NotFound, (await Store.GetAsync(Authorize(tenant), run.RunId, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task The_unfinalized_listing_pages_by_run_in_exactly_one_scope_and_honours_the_age_bound()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var runs = Enumerable.Range(0, 5)
            .Select(i => CompletedRun(scope, endedAt: Time.AddMinutes(i + 1)))
            .ToList();
        foreach (var run in runs)
        {
            await Store.SaveAsync(Authorize(tenant), run, Guid.NewGuid(), CancellationToken.None);
        }

        await Store.SaveAsync(Authorize(tenant), CompletedRun(Scope(tenant, team: "team-1")), Guid.NewGuid(), CancellationToken.None);
        await Store.MarkFinalizedAsync(Authorize(tenant), scope, runs[4].RunId, Time.AddHours(1), CancellationToken.None);

        var first = await Store.ListUnfinalizedAsync(Authorize(tenant), new ExperienceRunQuery(scope, Limit: 3), CancellationToken.None);
        var second = await Store.ListUnfinalizedAsync(
            Authorize(tenant), new ExperienceRunQuery(scope, Limit: 3, AfterRunId: first.Runs[^1].Run.RunId), CancellationToken.None);
        var old = await Store.ListUnfinalizedAsync(Authorize(tenant), new ExperienceRunQuery(scope, EndedBefore: Time.AddMinutes(3)), CancellationToken.None);

        var expected = runs.Take(4).Select(run => run.RunId).Order().ToList();
        Assert.Equal(ExperienceRunStoreOutcome.Found, first.Outcome);
        Assert.Equal(expected, first.Runs.Concat(second.Runs).Select(stored => stored.Run.RunId).ToList());
        Assert.Equal(3, first.Runs.Count);
        Assert.Equal(runs.Take(2).Select(run => run.RunId).Order(), old.Runs.Select(stored => stored.Run.RunId));
    }

    [Fact]
    public async Task Marking_finalized_keeps_the_first_instant_and_acts_only_in_the_run_s_scope()
    {
        var tenant = NewTenant();
        var run = CompletedRun(Scope(tenant));
        await Store.SaveAsync(Authorize(tenant), run, Guid.NewGuid(), CancellationToken.None);

        var elsewhere = await Store.MarkFinalizedAsync(Authorize(tenant), Scope(tenant, "project-2"), run.RunId, Time, CancellationToken.None);
        var first = await Store.MarkFinalizedAsync(Authorize(tenant), run.Scope, run.RunId, Time.AddHours(1), CancellationToken.None);
        var second = await Store.MarkFinalizedAsync(Authorize(tenant), run.Scope, run.RunId, Time.AddHours(2), CancellationToken.None);

        Assert.Equal(ExperienceRunStoreOutcome.NotFound, elsewhere.Outcome);
        Assert.Equal(ExperienceRunStoreOutcome.Updated, first.Outcome);
        Assert.Equal(ExperienceRunStoreOutcome.Updated, second.Outcome);
        Assert.Equal(Time.AddHours(1), (await Store.GetAsync(Authorize(tenant), run.RunId, CancellationToken.None)).Run!.FinalizedAt);
    }

    [Fact]
    public async Task Deleting_removes_the_run_only_in_its_own_scope()
    {
        var tenant = NewTenant();
        var run = CompletedRun(Scope(tenant));
        await Store.SaveAsync(Authorize(tenant), run, Guid.NewGuid(), CancellationToken.None);

        var elsewhere = await Store.DeleteAsync(Authorize(tenant), Scope(tenant, "project-2"), run.RunId, CancellationToken.None);
        Assert.Equal(ExperienceRunStoreOutcome.Found, (await Store.GetAsync(Authorize(tenant), run.RunId, CancellationToken.None)).Outcome);
        var deleted = await Store.DeleteAsync(Authorize(tenant), run.Scope, run.RunId, CancellationToken.None);
        var again = await Store.DeleteAsync(Authorize(tenant), run.Scope, run.RunId, CancellationToken.None);

        Assert.Equal(ExperienceRunStoreOutcome.NotFound, elsewhere.Outcome);
        Assert.Equal(ExperienceRunStoreOutcome.Deleted, deleted.Outcome);
        Assert.Equal(ExperienceRunStoreOutcome.NotFound, again.Outcome);
        Assert.Equal(ExperienceRunStoreOutcome.NotFound, (await Store.GetAsync(Authorize(tenant), run.RunId, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Caller_cancellation_surfaces_unwrapped()
    {
        var tenant = NewTenant();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.SaveAsync(Authorize(tenant), CompletedRun(Scope(tenant)), Guid.NewGuid(), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.GetAsync(Authorize(tenant), Guid.NewGuid(), cancelled.Token));
    }
}

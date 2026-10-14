using System.Runtime.CompilerServices;
using AgentExperience.Core.Confidence;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// Story 21.4: the read-only lessons view (<see cref="AgentExperienceLessons"/>) and its HTML rendering
/// (<see cref="ExperienceLessonsHtml"/>), over the one-call setup with in-memory storage and lessons stored by real runs.
/// </summary>
public sealed class AgentExperienceLessonsTests
{
    private const string RunTests = "run_tests";
    private const string Ticket = "Ticket #4812: a refund is stuck on a lock. Triage it.";

    private static readonly ExperienceIdentity Caller = new(
        new AuthorizationContext("contoso", "svc", [], DateTimeOffset.UnixEpoch),
        new Scope("contoso", "support", "tickets"));

    private static readonly ExperienceIdentity OtherScope = new(
        new AuthorizationContext("contoso", "svc", [], DateTimeOffset.UnixEpoch),
        new Scope("contoso", "support", "billing"));

    /// <summary>
    /// The acceptance criterion: the README quick start's setup (development identity, <c>ToolSucceeded</c>, the exit-code
    /// classifier), two runs, then <c>GetAgentExperienceLessons().ListAsync(identity)</c> lists the first run's lesson as
    /// Validated, with the block the second run received, its task-dependent ranking line aside.
    /// </summary>
    [Fact]
    public async Task After_the_quick_starts_two_runs_the_view_lists_the_lesson_with_the_block_the_second_run_received()
    {
        var host = new Host();
        await using var services = host.Build(identity: null);
        var model = new TwoCallModel();
        var agent = Agent(services, model, ["exit=3", "exit=0", "exit=0", "exit=0"]);

        await agent.RunAsync(Ticket);
        await agent.RunAsync("Ticket #4977: a refund is stuck on a lock again. Triage it.");

        Assert.Empty(host.Failures);
        var first = host.Finalized[0];
        Assert.Equal([first.ExperienceId!.Value], host.Injections[1].InjectedExperienceIds);
        var injected = Assert.Single(model.FirstCalls[1], IsHistoricalReference).Text;

        // Story 21.8: the injection is recorded in the background, after the block is handed over.
        await services.GetAgentExperienceContextProvider().InjectionRecordsSettledAsync();
        var page = await services.GetAgentExperienceLessons().ListForDevelopmentIdentityAsync();

        Assert.Equal(ExperienceStoreOutcome.Found, page.Outcome);
        Assert.Equal("local", page.Scope.TenantId);
        Assert.Equal(2, page.Lessons.Count);
        Assert.False(page.HasMore);
        var lesson = Assert.Single(page.Lessons, item => item.ExperienceId == first.ExperienceId);
        Assert.Equal(ExperienceStatus.Validated, lesson.Status);
        Assert.True(lesson.IsEligible);
        Assert.Null(lesson.IneligibleReason);
        Assert.Null(lesson.Ineligibility);
        Assert.Equal("triage-ticket", lesson.TaskId);
        var check = Assert.Single(lesson.Checks);
        Assert.Equal(RunTests, check.CheckId);
        Assert.Equal(CheckResult.Pass, check.Result);

        // The same text line for line, but for the ranking line, which depends on the task the agent was given.
        Assert.Contains("run_tests [failed: exit 3], run_tests [returned]", lesson.BlockText, StringComparison.Ordinal);
        Assert.Equal(WithoutRanking(injected), WithoutRanking(lesson.BlockText));
        Assert.Contains("Matched: " + HistoricalReferenceWriter.MatchedWithoutRanking, lesson.BlockText, StringComparison.Ordinal);

        // Story 21.8: the second run's injection of the first lesson is recorded; the second lesson was never injected.
        Assert.True(page.InjectionsTracked);
        Assert.NotNull(lesson.LastInjectedAt);
        Assert.Equal(1, lesson.InjectionCount);
        var newer = Assert.Single(page.Lessons, item => item.ExperienceId != first.ExperienceId);
        Assert.Null(newer.LastInjectedAt);
        Assert.Equal(0, newer.InjectionCount);
        var html = ExperienceLessonsHtml.Render(page);
        Assert.Contains("<span>last injected never</span>", html, StringComparison.Ordinal);
        Assert.Contains(" UTC (1 time)</span>", html, StringComparison.Ordinal);

        // The same service the container hands out.
        Assert.Same(services.GetRequiredService<AgentExperienceLessons>(), services.GetAgentExperienceLessons());
    }

    /// <summary>
    /// Story 21.8: lessons injected at different times show each one's latest injection and how many, and the rest show
    /// "never"; the whole page, injections included, is one bounded store read.
    /// </summary>
    [Fact]
    public async Task Each_lesson_shows_its_latest_injection_and_never_for_one_never_injected_from_one_bounded_read()
    {
        var store = new Storage.InMemory.InMemoryExperienceRecordStore(new FrozenTimeProvider(InjectionRecords.Now));
        var counting = new CountingStore(store);
        var authorization = Eligibility.Identity.Authorization;
        var scope = Eligibility.Identity.Scope;
        for (var n = 1; n <= 3; n++)
        {
            Assert.Equal(
                ExperienceStoreOutcome.Created,
                (await store.CreateAsync(authorization, Eligibility.Ok(n) with { CreatedAt = InjectionRecords.Now.AddMinutes(-n) }, CancellationToken.None)).Outcome);
        }

        var t1 = InjectionRecords.Now.AddHours(-2);
        var t2 = InjectionRecords.Now.AddHours(-1);
        await store.RecordInjectionAsync(authorization, scope, [InjectionRecords.Id(1)], t1, CancellationToken.None);
        await store.RecordInjectionAsync(authorization, scope, [InjectionRecords.Id(1), InjectionRecords.Id(2)], t2, CancellationToken.None);

        var page = await Lessons(counting, store).ListAsync(Eligibility.Identity, new ExperienceLessonsQuery { Limit = ExperienceRecordQuery.MaxLimit });

        Assert.Equal(1, counting.Calls);
        Assert.True(page.InjectionsTracked);
        Assert.Equal([InjectionRecords.Id(1), InjectionRecords.Id(2), InjectionRecords.Id(3)], page.Lessons.Select(lesson => lesson.ExperienceId));
        Assert.Equal([t2, t2, null], page.Lessons.Select(lesson => lesson.LastInjectedAt));
        Assert.Equal([2L, 1L, 0L], page.Lessons.Select(lesson => lesson.InjectionCount));

        var html = ExperienceLessonsHtml.Render(page);
        // Every agent records its injections (the Python and Go ports since contract 1.4), so the page carries no caveat.
        Assert.DoesNotContain(".NET agents", html, StringComparison.Ordinal);
        Assert.Contains(
            "<span>last injected " + t2.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " UTC (2 times)</span>",
            html,
            StringComparison.Ordinal);
        Assert.Equal(1, Count(html, "<span>last injected never</span>"));
        Assert.DoesNotContain(ExperienceLessonsHtml.NotTracked, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_store_that_does_not_track_injections_shows_not_tracked_rather_than_never()
    {
        var world = new Eligibility(budget: null);
        world.Publish(Eligibility.Ok(1));

        var page = await world.Lessons().ListAsync(Eligibility.Identity);

        Assert.False(page.InjectionsTracked);
        var lesson = Assert.Single(page.Lessons);
        Assert.Null(lesson.LastInjectedAt);
        Assert.Equal(0, lesson.InjectionCount);
        var html = ExperienceLessonsHtml.Render(page);
        Assert.Contains("<span>last injected not tracked</span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<span>last injected never</span>", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Story 21.8: a decorator that forwards the injection read but not the capability reads as not tracked, never as
    /// "never": the view takes "tracked" from the store's capability as well as from its read.
    /// </summary>
    [Fact]
    public async Task A_decorator_that_does_not_forward_the_capability_reads_as_not_tracked()
    {
        var store = new Storage.InMemory.InMemoryExperienceRecordStore(new FrozenTimeProvider(InjectionRecords.Now));
        Assert.Equal(
            ExperienceStoreOutcome.Created,
            (await store.CreateAsync(Eligibility.Identity.Authorization, Eligibility.Ok(1), CancellationToken.None)).Outcome);

        var page = await Lessons(new ReadOnlyForwarder(store), store).ListAsync(Eligibility.Identity);

        Assert.False(page.InjectionsTracked);
        Assert.Contains("<span>last injected not tracked</span>", ExperienceLessonsHtml.Render(page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_validated_and_a_quarantined_lesson_are_both_listed_and_only_the_quarantined_one_is_marked_ineligible()
    {
        var host = new Host();
        await using var services = host.Build(Caller);
        var agent = Agent(services, new TwoCallModel(), ["exit=3", "exit=0", "exit=1", "exit=1"]);

        await agent.RunAsync(Ticket);
        await agent.RunAsync(Ticket);

        Assert.Equal([FinalizationOutcome.Validated, FinalizationOutcome.Quarantined], host.Finalized.Select(result => result.Outcome));
        var page = await services.GetAgentExperienceLessons().ListAsync(Caller);

        Assert.Equal(2, page.Lessons.Count);
        var validated = Assert.Single(page.Lessons, lesson => lesson.Status == ExperienceStatus.Validated);
        var quarantined = Assert.Single(page.Lessons, lesson => lesson.Status == ExperienceStatus.Quarantined);
        Assert.Equal(quarantined, page.Lessons[0]);  // newest first
        Assert.True(validated.IsEligible);
        Assert.False(quarantined.IsEligible);
        Assert.Equal(ExperienceLessonIneligibility.Status, quarantined.Ineligibility);
        Assert.False(string.IsNullOrWhiteSpace(quarantined.IneligibleReason));
        Assert.Contains(HistoricalReferenceWriter.BlockBegin, validated.BlockText, StringComparison.Ordinal);
        Assert.Contains(HistoricalReferenceWriter.BlockBegin, quarantined.BlockText, StringComparison.Ordinal);
        Assert.Contains("Quarantined", quarantined.BlockText, StringComparison.Ordinal);
        Assert.Equal(CheckResult.Fail, Assert.Single(quarantined.Checks).Result);

        var eligibleOnly = await services.GetAgentExperienceLessons().ListAsync(Caller, new ExperienceLessonsQuery { IncludeIneligible = false });
        Assert.Equal([validated.ExperienceId], eligibleOnly.Lessons.Select(lesson => lesson.ExperienceId));

        var quarantinedOnly = await services.GetAgentExperienceLessons().ListAsync(Caller, new ExperienceLessonsQuery { Statuses = [ExperienceStatus.Quarantined] });
        Assert.Equal([quarantined.ExperienceId], quarantinedOnly.Lessons.Select(lesson => lesson.ExperienceId));

        var limited = await services.GetAgentExperienceLessons().ListAsync(Caller, new ExperienceLessonsQuery { Limit = 1 });
        Assert.Equal([quarantined.ExperienceId], limited.Lessons.Select(lesson => lesson.ExperienceId));

        var html = ExperienceLessonsHtml.Render(page);
        Assert.Contains("not eligible for retrieval:", html, StringComparison.Ordinal);
        Assert.Contains("eligible for retrieval", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_scopes_lesson_is_not_listed()
    {
        var host = new Host();
        var current = Caller;
        await using var services = host.Build(Caller, () => current);
        var agent = Agent(services, new TwoCallModel(), ["exit=0", "exit=0", "exit=0", "exit=0"]);

        await agent.RunAsync(Ticket);
        current = OtherScope;
        await agent.RunAsync(Ticket);

        Assert.Equal(2, host.Finalized.Count);
        var page = await services.GetAgentExperienceLessons().ListAsync(Caller);

        Assert.Equal([host.Finalized[0].ExperienceId!.Value], page.Lessons.Select(lesson => lesson.ExperienceId));
    }

    [Fact]
    public async Task A_store_that_denies_gives_an_empty_page_with_the_outcome_and_never_throws()
    {
        var host = new Host();
        await using var services = host.Build(Caller);
        var agent = Agent(services, new TwoCallModel(), ["exit=0", "exit=0"]);
        await agent.RunAsync(Ticket);

        var outsider = new ExperienceIdentity(new AuthorizationContext("fabrikam", "svc", [], DateTimeOffset.UnixEpoch), Caller.Scope);
        var page = await services.GetAgentExperienceLessons().ListAsync(outsider);

        Assert.Equal(ExperienceStoreOutcome.Denied, page.Outcome);
        Assert.Empty(page.Lessons);
        var html = ExperienceLessonsHtml.Render(page);
        Assert.Contains("Denied", html, StringComparison.Ordinal);
        Assert.DoesNotContain(ExperienceLessonsHtml.NoLessons, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_store_gives_a_found_page_that_says_no_lessons_yet()
    {
        await using var services = new Host().Build(Caller);

        var page = await services.GetAgentExperienceLessons().ListAsync(Caller);

        Assert.Equal(ExperienceStoreOutcome.Found, page.Outcome);
        Assert.Empty(page.Lessons);
        var html = ExperienceLessonsHtml.Render(page);
        Assert.Contains(ExperienceLessonsHtml.NoLessons, html, StringComparison.Ordinal);
        Assert.Contains(ExperienceLessonsHtml.DevelopmentBanner, html, StringComparison.Ordinal);
        Assert.Contains("contoso", html, StringComparison.Ordinal);
        Assert.Contains("tickets", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Argument_errors_throw()
    {
        await using var services = new Host().Build(Caller);
        var lessons = services.GetAgentExperienceLessons();

        await Assert.ThrowsAsync<ArgumentNullException>(() => lessons.ListAsync(null!));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => lessons.ListAsync(Caller, new ExperienceLessonsQuery { Limit = 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => lessons.ListAsync(Caller, new ExperienceLessonsQuery { Limit = ExperienceRecordQuery.MaxLimit + 1 }));
        await Assert.ThrowsAsync<ArgumentException>(() => lessons.ListAsync(Caller, new ExperienceLessonsQuery { Statuses = [] }));
        await Assert.ThrowsAsync<ArgumentException>(() => lessons.ListAsync(Caller, new ExperienceLessonsQuery { Statuses = [(ExperienceStatus)99] }));
        Assert.Throws<ArgumentNullException>(() => ExperienceLessonsHtml.Render(null!));
    }

    [Fact]
    public async Task Listing_for_the_development_identity_without_it_throws_a_clear_error()
    {
        await using var services = new Host().Build(Caller);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => services.GetAgentExperienceLessons().ListForDevelopmentIdentityAsync());
        Assert.Contains("UseDevelopmentIdentity", error.Message, StringComparison.Ordinal);
    }

    // ---- Eligibility, against retrieval's own rules ----------------------------------------------------------

    public static TheoryData<string, ExperienceLessonIneligibility> Ineligible() => new()
    {
        { "quarantined", ExperienceLessonIneligibility.Status },
        { "below floor", ExperienceLessonIneligibility.BelowConfidenceFloor },
        { "NaN confidence", ExperienceLessonIneligibility.BelowConfidenceFloor },
        { "model-authored", ExperienceLessonIneligibility.ModelAuthored },
        { "unconfirmed", ExperienceLessonIneligibility.ContentUnconfirmed },
        { "expired", ExperienceLessonIneligibility.Expired },
        { "over budget", ExperienceLessonIneligibility.OverByteBudget },
    };

    [Theory]
    [MemberData(nameof(Ineligible))]
    public async Task An_ineligible_lesson_is_marked_with_its_code_and_reason_and_left_out_on_request(string scenario, ExperienceLessonIneligibility expected)
    {
        var world = new Eligibility(budget: scenario == "over budget" ? 1500 : null);
        world.Publish(Eligibility.Ok(1));
        world.Publish(Eligibility.Case(2, scenario));
        var lessons = world.Lessons();

        var page = await lessons.ListAsync(Eligibility.Identity);

        var lesson = Assert.Single(page.Lessons, item => item.ExperienceId == InjectionRecords.Id(2));
        Assert.False(lesson.IsEligible);
        Assert.Equal(expected, lesson.Ineligibility);
        Assert.False(string.IsNullOrWhiteSpace(lesson.IneligibleReason));
        Assert.Contains(HistoricalReferenceWriter.BlockBegin, lesson.BlockText, StringComparison.Ordinal);
        if (scenario != "over budget")
        {
            Assert.True(Assert.Single(page.Lessons, item => item.ExperienceId == InjectionRecords.Id(1)).IsEligible);
        }

        var eligibleOnly = await lessons.ListAsync(Eligibility.Identity, new ExperienceLessonsQuery { IncludeIneligible = false });
        Assert.DoesNotContain(eligibleOnly.Lessons, item => item.ExperienceId == InjectionRecords.Id(2));
    }

    /// <summary>The view's eligible set is exactly what retrieval returns for the same records, when the task matches them all.</summary>
    [Fact]
    public async Task The_eligible_set_matches_what_retrieval_returns_for_the_same_records()
    {
        var world = new Eligibility(budget: null);
        world.Publish(Eligibility.Ok(1));
        world.Publish(Eligibility.Ok(2) with { ReuseConfidence = 0.61 });
        var scenarios = new[] { "quarantined", "below floor", "NaN confidence", "model-authored", "unconfirmed", "expired" };
        for (var i = 0; i < scenarios.Length; i++)
        {
            world.Publish(Eligibility.Case(10 + i, scenarios[i]));
        }

        var page = await world.Lessons().ListAsync(Eligibility.Identity);
        var retrieved = await world.Retrieval.RetrieveAsync(
            new RetrieveExperienceRequest(Eligibility.Identity.Authorization, Eligibility.Identity.Scope, "refund ticket stuck on a lock") { ExcludeModelAuthored = true });

        Assert.Equal(RetrievalOutcome.Completed, retrieved.Outcome);
        Assert.Equal(8, page.Lessons.Count);
        Assert.Equal(
            retrieved.Records.Select(record => record.Record.ExperienceId).Order(),
            page.Lessons.Where(lesson => lesson.IsEligible).Select(lesson => lesson.ExperienceId).Order());
        Assert.Equal([InjectionRecords.Id(1), InjectionRecords.Id(2)], page.Lessons.Where(lesson => lesson.IsEligible).Select(lesson => lesson.ExperienceId).Order());
    }

    [Fact]
    public async Task A_record_that_cannot_be_rendered_is_marked_and_the_rest_are_still_listed()
    {
        var world = new Eligibility(budget: null);
        world.Publish(Eligibility.Ok(1));
        world.Store(Eligibility.Ok(2) with { Outcome = null! });

        var page = await world.Lessons().ListAsync(Eligibility.Identity);

        Assert.Equal(2, page.Lessons.Count);
        var broken = Assert.Single(page.Lessons, item => item.ExperienceId == InjectionRecords.Id(2));
        Assert.Equal(ExperienceLessonIneligibility.Unrenderable, broken.Ineligibility);
        Assert.False(broken.IsEligible);
        Assert.Empty(broken.BlockText);
        Assert.True(Assert.Single(page.Lessons, item => item.ExperienceId == InjectionRecords.Id(1)).IsEligible);
    }

    [Fact]
    public async Task HasMore_says_whether_more_lessons_match_than_the_page_shows()
    {
        var world = new Eligibility(budget: null);
        world.Publish(Eligibility.Ok(1) with { CreatedAt = InjectionRecords.Now.AddMinutes(-1) });
        world.Publish(Eligibility.Case(2, "quarantined") with { CreatedAt = InjectionRecords.Now.AddMinutes(-2) });
        world.Publish(Eligibility.Ok(3) with { CreatedAt = InjectionRecords.Now.AddMinutes(-3) });
        world.Publish(Eligibility.Ok(4) with { CreatedAt = InjectionRecords.Now.AddMinutes(-4) });
        var lessons = world.Lessons();

        var two = await lessons.ListAsync(Eligibility.Identity, new ExperienceLessonsQuery { Limit = 2 });
        Assert.Equal([InjectionRecords.Id(1), InjectionRecords.Id(2)], two.Lessons.Select(lesson => lesson.ExperienceId));
        Assert.True(two.HasMore);

        var all = await lessons.ListAsync(Eligibility.Identity, new ExperienceLessonsQuery { Limit = 4 });
        Assert.Equal(4, all.Lessons.Count);
        Assert.False(all.HasMore);

        var eligibleTwo = await lessons.ListAsync(Eligibility.Identity, new ExperienceLessonsQuery { Limit = 2, IncludeIneligible = false });
        Assert.Equal([InjectionRecords.Id(1), InjectionRecords.Id(3)], eligibleTwo.Lessons.Select(lesson => lesson.ExperienceId));
        Assert.True(eligibleTwo.HasMore);

        var eligibleThree = await lessons.ListAsync(Eligibility.Identity, new ExperienceLessonsQuery { Limit = 3, IncludeIneligible = false });
        Assert.Equal(3, eligibleThree.Lessons.Count);
        Assert.False(eligibleThree.HasMore);

        var html = ExperienceLessonsHtml.Render(two);
        Assert.Contains("Showing 2 lessons, newest first.", html, StringComparison.Ordinal);
        Assert.Contains("More exist", html, StringComparison.Ordinal);
        Assert.DoesNotContain("More exist", ExperienceLessonsHtml.Render(all), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Records_a_store_returns_from_another_scope_are_counted_and_left_out()
    {
        var world = new Eligibility(budget: null) { QueryIgnoresScope = true };
        world.Publish(Eligibility.Ok(1));
        world.Publish(Eligibility.Ok(2) with { Scope = new Scope("tenant-1", "support", "billing") });

        var page = await world.Lessons().ListAsync(Eligibility.Identity);

        Assert.Equal([InjectionRecords.Id(1)], page.Lessons.Select(lesson => lesson.ExperienceId));
        Assert.Equal(1, page.OutOfScopeDropped);
        Assert.Contains("from outside this scope", ExperienceLessonsHtml.Render(page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_listing_throws()
    {
        var world = new Eligibility(budget: null);
        world.Publish(Eligibility.Ok(1));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => world.Lessons().ListAsync(Eligibility.Identity, cancellationToken: cancelled.Token));
    }

    [Fact]
    public void GetAgentExperienceLessons_without_the_one_call_setup_says_what_is_missing()
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => services.GetAgentExperienceLessons());
    }

    /// <summary>Lesson text is untrusted: markup in any value drawn from a record renders as inert text.</summary>
    [Fact]
    public void Markup_in_a_lesson_renders_inert()
    {
        const string Payload = "<script>alert(1)</script>\"><img src=x onerror=alert(2)></pre></code><a href=\"https://evil.example\">x</a>'";
        var page = new ExperienceLessonsPage(
            new Scope("contoso", "support<script>", "tickets\"><img onerror=alert(3)>"),
            ExperienceStoreOutcome.Found,
            [
                new ExperienceLesson(
                    Guid.Parse("21040000-0000-0000-0000-000000000001"),
                    Payload,
                    ExperienceStatus.Quarantined,
                    0.25,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch,
                    [new ExperienceLessonCheck(Payload, Payload, CheckResult.Fail)],
                    "Lesson: " + Payload + "\n",
                    false,
                    Payload,
                    ExperienceLessonIneligibility.Status),
                null!,
            ],
            DateTimeOffset.UnixEpoch);

        var html = ExperienceLessonsHtml.Render(page);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<a ", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"<[^>]*\s(src|href|on[a-z]+)\s*=", html);  // no tag carries a link, a source or a handler
        Assert.Equal(1, Count(html, "</pre>"));
        Assert.Equal(1, Count(html, "<pre>"));
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&quot;&gt;&lt;img src=x onerror=alert(2)&gt;&lt;/pre&gt;", html, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
        Assert.Contains("Showing 1 lesson,", html, StringComparison.Ordinal);
        Assert.Contains("prefers-color-scheme:dark", html, StringComparison.Ordinal);
    }

    // ---- Helpers --------------------------------------------------------------------------------------------

    /// <summary>A lessons view over <paramref name="store"/>, rendering with a provider over the same in-memory records.</summary>
    private static AgentExperienceLessons Lessons(IExperienceRecordStore store, Storage.InMemory.InMemoryExperienceRecordStore records)
    {
        var clock = new FrozenTimeProvider(InjectionRecords.Now);
        var retrieval = new ExperienceRetrievalService(
            new Storage.InMemory.InMemoryExperienceCandidateSource(records), RetrievalPolicy.Default, RankingWeights.Default, clock);
        var provider = new ExperienceContextProvider(retrieval, store, new ExperienceInjectionOptions { ResolveRequest = _ => null, TimeProvider = clock });
        return new AgentExperienceLessons(store, provider, clock);
    }

    /// <summary>A decorator that forwards the reads, the injection read included, but not <see cref="IExperienceRecordStore.TracksInjections"/>.</summary>
    private sealed class ReadOnlyForwarder(IExperienceRecordStore inner) : IExperienceRecordStore
    {
        public Task<ExperienceRecordCreateResult> CreateAsync(AuthorizationContext authorization, ExperienceRecord record, CancellationToken cancellationToken) =>
            inner.CreateAsync(authorization, record, cancellationToken);

        public Task<ExperienceRecordGetResult> GetAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, CancellationToken cancellationToken) =>
            inner.GetAsync(authorization, scope, experienceId, cancellationToken);

        public Task<ExperienceRecordQueryResult> QueryAsync(AuthorizationContext authorization, ExperienceRecordQuery query, CancellationToken cancellationToken) =>
            inner.QueryAsync(authorization, query, cancellationToken);

        public Task<ExperienceRecordQueryResult> QueryWithInjectionsAsync(AuthorizationContext authorization, ExperienceRecordQuery query, CancellationToken cancellationToken) =>
            inner.QueryWithInjectionsAsync(authorization, query, cancellationToken);

        public Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(AuthorizationContext authorization, Scope scope, LifecycleEvent lifecycleEvent, CancellationToken cancellationToken) =>
            inner.CommitLifecycleEventAsync(authorization, scope, lifecycleEvent, cancellationToken);

        public Task<ExperienceRecordHistoryResult> GetHistoryAsync(AuthorizationContext authorization, ExperienceRecordHistoryQuery query, CancellationToken cancellationToken) =>
            inner.GetHistoryAsync(authorization, query, cancellationToken);

        public Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, Guid replacementExperienceId, CancellationToken cancellationToken) =>
            inner.CheckSupersessionAsync(authorization, scope, experienceId, replacementExperienceId, cancellationToken);
    }

    /// <summary>A store that counts every call the lessons view makes, and forwards each.</summary>
    private sealed class CountingStore(IExperienceRecordStore inner) : IExperienceRecordStore
    {
        private int _calls;

        public int Calls => _calls;

        private T Count<T>(T value)
        {
            Interlocked.Increment(ref _calls);
            return value;
        }

        public Task<ExperienceRecordCreateResult> CreateAsync(AuthorizationContext authorization, ExperienceRecord record, CancellationToken cancellationToken) =>
            Count(inner.CreateAsync(authorization, record, cancellationToken));

        public Task<ExperienceRecordGetResult> GetAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, CancellationToken cancellationToken) =>
            Count(inner.GetAsync(authorization, scope, experienceId, cancellationToken));

        public Task<ExperienceRecordGetResult> GetAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, ExperienceReadOptions options, CancellationToken cancellationToken) =>
            Count(inner.GetAsync(authorization, scope, experienceId, options, cancellationToken));

        public Task<ExperienceRecordGetManyResult> GetManyAsync(AuthorizationContext authorization, Scope scope, IReadOnlyList<Guid> experienceIds, ExperienceReadOptions options, CancellationToken cancellationToken) =>
            Count(inner.GetManyAsync(authorization, scope, experienceIds, options, cancellationToken));

        public Task<ExperienceRecordQueryResult> QueryAsync(AuthorizationContext authorization, ExperienceRecordQuery query, CancellationToken cancellationToken) =>
            Count(inner.QueryAsync(authorization, query, cancellationToken));

        public Task<ExperienceRecordQueryResult> QueryWithInjectionsAsync(AuthorizationContext authorization, ExperienceRecordQuery query, CancellationToken cancellationToken) =>
            Count(inner.QueryWithInjectionsAsync(authorization, query, cancellationToken));

        public bool TracksInjections => inner.TracksInjections;

        public Task<ExperienceInjectionRecordResult> RecordInjectionAsync(AuthorizationContext authorization, Scope scope, IReadOnlyList<Guid> experienceIds, DateTimeOffset injectedAt, CancellationToken cancellationToken) =>
            Count(inner.RecordInjectionAsync(authorization, scope, experienceIds, injectedAt, cancellationToken));

        public Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(AuthorizationContext authorization, Scope scope, LifecycleEvent lifecycleEvent, CancellationToken cancellationToken) =>
            Count(inner.CommitLifecycleEventAsync(authorization, scope, lifecycleEvent, cancellationToken));

        public Task<ExperienceRecordHistoryResult> GetHistoryAsync(AuthorizationContext authorization, ExperienceRecordHistoryQuery query, CancellationToken cancellationToken) =>
            Count(inner.GetHistoryAsync(authorization, query, cancellationToken));

        public Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, Guid replacementExperienceId, CancellationToken cancellationToken) =>
            Count(inner.CheckSupersessionAsync(authorization, scope, experienceId, replacementExperienceId, cancellationToken));
    }

    private static AIAgent Agent(IServiceProvider services, TwoCallModel model, IEnumerable<string> exits)
    {
        var queue = new Queue<string>(exits);
        var runTests = AIFunctionFactory.Create(() => queue.Dequeue(), RunTests);
        return new ChatClientAgent(model, name: "triage-ticket", tools: [runTests]).AsBuilder().UseAgentExperience(services).Build();
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>The block's lines, without the compact layout's <c>Matched:</c> line, which ranking writes.</summary>
    private static string[] WithoutRanking(string block) =>
        block.Split('\n').Where(line => !line.StartsWith("Matched: ", StringComparison.Ordinal)).ToArray();

    private static bool IsHistoricalReference(ChatMessage message) =>
        message.AdditionalProperties?.ContainsKey(ExperienceContextProvider.HistoricalReferenceKey) == true;

    /// <summary>A model that calls <c>run_tests</c> twice, one call per turn, then answers.</summary>
    private sealed class TwoCallModel : IChatClient
    {
        /// <summary>The messages of each run's first model call.</summary>
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

    /// <summary>
    /// A store and candidate source answering the same records, a retrieval service over it with a raised confidence
    /// floor, a maximum age and provenance signing, and a provider that excludes model-authored lessons.
    /// </summary>
    private sealed class Eligibility : FakeExperienceWorldHolder
    {
        private static readonly byte[] SigningKey = [.. Enumerable.Range(0, 32).Select(value => (byte)(value * 7 + 3))];

        public static readonly ExperienceIdentity Identity = new(
            new AuthorizationContext("tenant-1", "svc", [], DateTimeOffset.UnixEpoch),
            new Scope("tenant-1", "support", "tickets"));

        private readonly HashSet<Guid> _trusted = [];
        private readonly int? _budget;

        public Eligibility(int? budget)
        {
            _budget = budget;
            World.AnswersQueries = true;
            _retrieval = new Lazy<ExperienceRetrievalService>(() => new ExperienceRetrievalService(
                World,
                RetrievalPolicy.Default with { MinimumConfidence = 0.6, MaxAge = TimeSpan.FromDays(1) },
                RankingWeights.Default,
                new FrozenTimeProvider(InjectionRecords.Now),
                embeddingIndex: null,
                embeddingGenerator: null,
                environmentScorer: null,
                confidenceDecay: null,
                new ExperienceProvenanceSigningOptions(new Dictionary<string, byte[]> { ["key-1"] = SigningKey }, "key-1")
                {
                    TrustUnsignedRecordIds = _trusted,
                }));
        }

        private readonly Lazy<ExperienceRetrievalService> _retrieval;

        public bool QueryIgnoresScope
        {
            get => World.QueryIgnoresScope;
            init => World.QueryIgnoresScope = value;
        }

        /// <summary>Built on first use, after the records are published: the signing options snapshot the trusted set.</summary>
        public ExperienceRetrievalService Retrieval => _retrieval.Value;

        /// <summary>A deterministic, verified record, eligible on every rule.</summary>
        public static ExperienceRecord Ok(int n)
        {
            var record = InjectionRecords.Record(InjectionRecords.Id(n), Identity.Scope);
            return record with { Reflection = record.Reflection! with { Authorship = ReflectionAuthorship.Deterministic } };
        }

        public static ExperienceRecord Case(int n, string scenario)
        {
            var record = Ok(n);
            return scenario switch
            {
                "quarantined" => record with { Status = ExperienceStatus.Quarantined },
                "below floor" => record with { ReuseConfidence = 0.55 },
                "NaN confidence" => record with { ReuseConfidence = double.NaN },
                "model-authored" => record with { Reflection = record.Reflection! with { Authorship = ReflectionAuthorship.Model } },
                "expired" => record with { UpdatedAt = InjectionRecords.Now.AddDays(-2) },
                "unconfirmed" => record with { TaskId = UnconfirmedTask },
                "over budget" => record with { Reflection = record.Reflection! with { Lesson = new string('x', 2000) } },
                _ => record,
            };
        }

        /// <summary>Stores and indexes <paramref name="record"/>; its content counts as confirmed unless it is the unconfirmed case.</summary>
        public void Publish(ExperienceRecord record)
        {
            Trust(record);
            World.Publish(record);
        }

        /// <summary>Stores <paramref name="record"/> without indexing it.</summary>
        public void Store(ExperienceRecord record)
        {
            Trust(record);
            World.Store(record);
        }

        public AgentExperienceLessons Lessons()
        {
            var options = new ExperienceInjectionOptions
            {
                ResolveRequest = _ => null,
                TimeProvider = new FrozenTimeProvider(InjectionRecords.Now),
                ModelAuthoredLessons = ModelAuthoredLessonPolicy.Exclude,
            };
            if (_budget is { } budget)
            {
                options.Limits = ExperienceInjectionLimits.Default with { MaxBytes = budget };
                options.SessionLimits = null;
            }

            return new AgentExperienceLessons(World, new ExperienceContextProvider(Retrieval, World, options), new FrozenTimeProvider(InjectionRecords.Now));
        }

        private void Trust(ExperienceRecord record)
        {
            // The unconfirmed case is the one record this ring does not confirm: unsigned, and not listed.
            if (record.TaskId != UnconfirmedTask)
            {
                _trusted.Add(record.ExperienceId);
            }
        }

        public const string UnconfirmedTask = "unconfirmed-task";
    }

    private abstract class FakeExperienceWorldHolder
    {
        protected FakeExperienceWorld World { get; } = new();
    }

    /// <summary>The README quick start's setup, reporting everything back; with an explicit identity when one is given.</summary>
    private sealed class Host
    {
        public List<ExperienceCaptureFailure> Failures { get; } = [];

        public List<FinalizeExperienceResult> Finalized { get; } = [];

        public List<ExperienceInjectionResult> Injections { get; } = [];

        public ServiceProvider Build(ExperienceIdentity? identity, Func<ExperienceIdentity>? current = null)
        {
            var collection = new ServiceCollection();
            collection.AddSingleton<IHostEnvironment>(new AgentExperienceFiveLineSetupTests.FakeHostEnvironment(Environments.Development));
            collection
                .AddAgentExperience(options =>
                {
                    options.UseDevelopmentIdentity();
                    if (identity is not null)
                    {
                        options.ResolveIdentity = (_, _) => ValueTask.FromResult<ExperienceIdentity?>(current?.Invoke() ?? identity);
                    }

                    options.Verify = ExperienceVerifiers.ToolSucceeded(RunTests);
                    options.Capture = capture =>
                    {
                        capture.ClassifyToolResult = ExperienceToolResultClassifiers.ExitCode(RunTests);
                        capture.OnCaptureFailure = Failures.Add;
                        capture.OnRunFinalized = Finalized.Add;
                    };
                    options.Injection = injection => injection.OnContextInjected = Injections.Add;
                })
                .UseInMemoryStorageForDevelopment();
            return collection.BuildServiceProvider();
        }
    }
}

using AgentExperience.Core.Confidence;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Storage.InMemory;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Story 28.6: one living lesson per situation. The consolidation pass over the in-memory store, with records finalized
/// from real captured runs where independence and signing matter and hand-written ones where only the grouping does.
/// </summary>
public sealed class ExperienceConsolidationServiceTests
{
    private const string TaskOfRuns = "triage-flaky-build";
    private const string Timeout = "TimeoutException: the test host did not answer after 30 seconds.";
    private const string ArtifactRevision = "rev-1";

    private static readonly DateTimeOffset Now = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);
    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");
    private static readonly Scope OtherScope = new("tenant-1", "app-1", "project-2");
    private static readonly AuthorizationContext Authorization = new("tenant-1", "host-principal", ["experience:write"], Now);
    private static readonly byte[] SigningKey = [.. Enumerable.Range(0, 32).Select(value => (byte)(value * 11 + 5))];

    private static readonly SanitizationOptions Sanitization = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        ["ToolArguments"] = new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), 2, 5, 1_000, 100),
        ["ToolResult"] = new(new HashSet<string>(StringComparer.Ordinal) { "value" }, new HashSet<string>(StringComparer.Ordinal), 2, 5, 1_000, 100),
    });

    private static readonly ExperienceConsolidationOptions Defaults = new();

    [Fact]
    public async Task Three_runs_with_the_same_failure_and_fix_become_one_validated_lesson_that_supersedes_them()
    {
        var world = new World();
        var sources = new List<ExperienceRecord>();
        for (var n = 0; n < 3; n++)
        {
            sources.Add(await world.LessonAsync(at: Now.AddDays(n)));
        }

        var result = await world.Consolidation.ConsolidateAsync(Authorization, TestScope, Defaults, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, result.Outcome);
        Assert.Equal(3, result.RecordsRead);
        Assert.False(result.MayHaveMore);
        var group = Assert.Single(result.Groups);
        Assert.Equal(ExperienceConsolidationGroupOutcome.Created, group.Outcome);
        Assert.Equal(3, group.IndependentRuns);
        Assert.Equal(sources.Select(source => source.ExperienceId).Order(), group.Sources.Select(source => source.ExperienceId));
        Assert.All(group.Sources, source => Assert.True(source.Superseded));
        Assert.Equal(ExperienceConsolidation.ConsolidatedIdFor(TestScope, sources.Select(source => source.ExperienceId)), group.ConsolidatedExperienceId);

        var consolidated = await world.ReadAsync(group.ConsolidatedExperienceId);
        Assert.Equal(ExperienceStatus.Validated, consolidated.Status);
        Assert.True(ExperienceConsolidation.IsConsolidated(consolidated));
        Assert.Equal(sources.Select(source => source.ExperienceId).Order(), ExperienceConsolidation.SourcesOf(consolidated));
        Assert.Equal(ExperienceRecordOrigin.HostWritten, consolidated.Origin);
        Assert.Equal(ExperienceConsolidation.ProvenanceSource, consolidated.Provenance.Source);
        Assert.Equal(ExperienceConsolidation.TemplateVersion, consolidated.Provenance.SourceVersion);
        Assert.Equal(ExperienceConsolidation.Producer, consolidated.Reflection!.Producer);
        Assert.Equal(ReflectionAuthorship.Deterministic, consolidated.Reflection.Authorship);
        Assert.Equal(
            "Verified in 3 independent runs (latest 2026-10-07). Failed first with TimeoutException from run_tests in 3 of them. Worked: run_tests → run_tests.",
            consolidated.Reflection.Lesson);
        Assert.DoesNotContain(sources, source => source.SourceRunId == consolidated.SourceRunId);

        // The most recent source's attempts and outcome, so its Tried: and Worked: lines render as that source's did.
        var latest = sources[^1];
        Assert.Equal(latest.TaskId, consolidated.TaskId);
        Assert.Equal(latest.Attempts.Select(attempt => attempt.AttemptId), consolidated.Attempts.Select(attempt => attempt.AttemptId));
        Assert.Equal(latest.Outcome.Evidence.Count, consolidated.Outcome.Evidence.Count);

        // Three runs, each its own independent validation: the heuristic's (3 + 1) / (3 + 2).
        Assert.Equal(3, consolidated.SupportingValidations);
        Assert.Equal(0, consolidated.Contradictions);
        Assert.Equal(4d / 5d, consolidated.ReuseConfidence, precision: 12);

        // Each source is superseded by it, and its history says so.
        foreach (var source in sources)
        {
            var stored = await world.ReadAsync(source.ExperienceId);
            Assert.Equal(ExperienceStatus.Superseded, stored.Status);
            var history = await world.Store.GetFirstHistoryPageAsync(Authorization, TestScope, source.ExperienceId, CancellationToken.None);
            var last = history.Events[^1].Event;
            Assert.Equal(ExperienceStatus.Superseded, last.CurrentStatus);
            Assert.Equal(group.ConsolidatedExperienceId, last.ReplacementExperienceId);
            Assert.Equal(ExperienceConsolidation.Producer, last.Producer);
        }

        // Retrieval sees one lesson now.
        var live = await world.Store.QueryAsync(Authorization, new ExperienceRecordQuery(TestScope, ExperienceStatuses.EligibleForReuse), CancellationToken.None);
        Assert.Equal([group.ConsolidatedExperienceId], live.Records.Select(record => record.ExperienceId));
    }

    [Fact]
    public async Task A_second_pass_finds_nothing_to_do_and_writes_nothing()
    {
        var world = new World();
        for (var n = 0; n < 3; n++)
        {
            await world.LessonAsync(at: Now.AddDays(n));
        }

        var first = await world.Consolidation.ConsolidateAsync(Authorization, TestScope, Defaults, CancellationToken.None);
        var consolidated = await world.ReadAsync(Assert.Single(first.Groups).ConsolidatedExperienceId);

        var second = await world.Consolidation.ConsolidateAsync(Authorization, TestScope, Defaults, CancellationToken.None);

        Assert.Empty(second.Groups);
        Assert.Equal(1, second.RecordsRead);
        var again = await world.ReadAsync(consolidated.ExperienceId);
        Assert.Equal((consolidated.Status, consolidated.Revision, consolidated.SupportingValidations), (again.Status, again.Revision, again.SupportingValidations));
    }

    [Fact]
    public async Task A_later_verified_run_is_folded_in_by_a_new_consolidated_record_that_supersedes_the_old_one()
    {
        var world = new World();
        var sources = new List<ExperienceRecord>();
        for (var n = 0; n < 3; n++)
        {
            sources.Add(await world.LessonAsync(at: Now.AddDays(n)));
        }

        var first = Assert.Single((await world.Consolidation.ConsolidateAsync(Authorization, TestScope, Defaults, CancellationToken.None)).Groups);
        var later = await world.LessonAsync(at: Now.AddDays(10));
        sources.Add(later);

        var second = Assert.Single((await world.Consolidation.ConsolidateAsync(Authorization, TestScope, Defaults, CancellationToken.None)).Groups);

        Assert.Equal(ExperienceConsolidationGroupOutcome.Created, second.Outcome);
        Assert.NotEqual(first.ConsolidatedExperienceId, second.ConsolidatedExperienceId);
        Assert.Equal(ExperienceConsolidation.ConsolidatedIdFor(TestScope, sources.Select(source => source.ExperienceId)), second.ConsolidatedExperienceId);
        Assert.Equal(4, second.IndependentRuns);
        Assert.Equal(new[] { first.ConsolidatedExperienceId, later.ExperienceId }.Order(), second.Sources.Select(source => source.ExperienceId));
        Assert.All(second.Sources, source => Assert.True(source.Superseded));

        var folded = await world.ReadAsync(second.ConsolidatedExperienceId);
        Assert.Equal(sources.Select(source => source.ExperienceId).Order(), ExperienceConsolidation.SourcesOf(folded));
        Assert.StartsWith("Verified in 4 independent runs (latest 2026-10-15).", folded.Reflection!.Lesson, StringComparison.Ordinal);
        Assert.Equal(4, folded.SupportingValidations);
        Assert.Equal(later.Attempts.Select(attempt => attempt.AttemptId), folded.Attempts.Select(attempt => attempt.AttemptId));

        var old = await world.ReadAsync(first.ConsolidatedExperienceId);
        Assert.Equal(ExperienceStatus.Superseded, old.Status);
        var history = await world.Store.GetFirstHistoryPageAsync(Authorization, TestScope, old.ExperienceId, CancellationToken.None);
        Assert.Equal(second.ConsolidatedExperienceId, history.Events[^1].Event.ReplacementExperienceId);
    }

    [Fact]
    public async Task Counters_are_the_sources_distinct_independence_keys_not_the_sum_of_their_counters()
    {
        // Reuse evidence on: the second and third runs were given the first lesson, and each counted as support for it.
        var world = new World(new ExperienceFinalizationOptions { ReuseEvidence = ReuseEvidenceMode.SameTask });
        var first = await world.LessonAsync(at: Now);
        var second = await world.LessonAsync(at: Now.AddDays(1), exposedTo: [first]);
        var third = await world.LessonAsync(at: Now.AddDays(2), exposedTo: [first]);
        first = await world.ReadAsync(first.ExperienceId);
        Assert.Equal(3, first.SupportingValidations);
        Assert.Equal(5, first.SupportingValidations + second.SupportingValidations + third.SupportingValidations);

        var group = Assert.Single((await world.Consolidation.ConsolidateAsync(Authorization, TestScope, Defaults, CancellationToken.None)).Groups);

        // Three runs, three independent confirmations: each later run's own validation and its support of the first
        // lesson are the same run under the same closed round, so they count once.
        var consolidated = await world.ReadAsync(group.ConsolidatedExperienceId);
        Assert.Equal(3, consolidated.SupportingValidations);
        Assert.Equal(4d / 5d, consolidated.ReuseConfidence, precision: 12);
    }

    [Fact]
    public async Task Different_values_of_an_allowlisted_argument_stay_two_lessons_and_without_the_allowlist_merge()
    {
        var world = new World();
        for (var n = 0; n < 3; n++)
        {
            await world.CreateAsync(HandWritten(TestScope, Now.AddMinutes(n), delay: 0));
            await world.CreateAsync(HandWritten(TestScope, Now.AddMinutes(10 + n), delay: 30));
        }

        var apart = await world.Consolidation.ConsolidateAsync(
            Authorization,
            TestScope,
            new ExperienceConsolidationOptions { ArgumentKeys = new Dictionary<string, IReadOnlyList<string>> { ["retry"] = ["delay"] } },
            CancellationToken.None);

        Assert.Equal(2, apart.Groups.Count);
        Assert.All(apart.Groups, group => Assert.Equal(3, group.Sources.Count));
        var merged = await Task.WhenAll(apart.Groups.Select(group => world.ReadAsync(group.ConsolidatedExperienceId)));
        Assert.Equal(
            ["0", "30"],
            merged.Select(record => record.Attempts[^1].ToolCalls[0].Arguments["delay"]!.ToString()).Order(StringComparer.Ordinal));

        // The same records in another scope-identical world, grouped on tool names alone: one lesson of six.
        var other = new World();
        for (var n = 0; n < 3; n++)
        {
            await other.CreateAsync(HandWritten(TestScope, Now.AddMinutes(n), delay: 0));
            await other.CreateAsync(HandWritten(TestScope, Now.AddMinutes(10 + n), delay: 30));
        }

        var together = Assert.Single((await other.Consolidation.ConsolidateAsync(Authorization, TestScope, Defaults, CancellationToken.None)).Groups);
        Assert.Equal(6, together.Sources.Count);
        Assert.Equal(6, together.IndependentRuns);
    }

    [Fact]
    public async Task A_different_failure_or_a_different_fix_is_another_group()
    {
        var world = new World();
        for (var n = 0; n < 3; n++)
        {
            await world.CreateAsync(HandWritten(TestScope, Now.AddMinutes(n), delay: 0));
        }

        await world.CreateAsync(HandWritten(TestScope, Now.AddMinutes(5), delay: 0, error: "IOException: disk full"));
        await world.CreateAsync(HandWritten(TestScope, Now.AddMinutes(6), delay: 0, fix: "restart"));

        var group = Assert.Single((await world.Consolidation.ConsolidateAsync(Authorization, TestScope, Defaults, CancellationToken.None)).Groups);

        Assert.Equal(3, group.Sources.Count);
        var live = await world.Store.QueryAsync(Authorization, new ExperienceRecordQuery(TestScope, ExperienceStatuses.EligibleForReuse), CancellationToken.None);
        Assert.Equal(3, live.Records.Count);
    }

    [Fact]
    public async Task Fewer_than_the_minimum_distinct_runs_is_a_no_op()
    {
        var world = new World();
        var shared = Guid.NewGuid();
        await world.CreateAsync(HandWritten(TestScope, Now, delay: 0));
        await world.CreateAsync(HandWritten(TestScope, Now.AddMinutes(1), delay: 0));

        // Two records from one run count as one run.
        await world.CreateAsync(HandWritten(TestScope, Now.AddMinutes(2), delay: 0, runId: shared));
        await world.CreateAsync(HandWritten(TestScope, Now.AddMinutes(3), delay: 0, runId: shared));

        var result = await world.Consolidation.ConsolidateAsync(
            Authorization, TestScope, new ExperienceConsolidationOptions { MinimumSources = 4 }, CancellationToken.None);

        Assert.Empty(result.Groups);
        Assert.Equal(4, result.RecordsRead);
        var live = await world.Store.QueryAsync(Authorization, new ExperienceRecordQuery(TestScope, ExperienceStatuses.EligibleForReuse), CancellationToken.None);
        Assert.Equal(4, live.Records.Count);
        Assert.Single((await world.Consolidation.ConsolidateAsync(Authorization, TestScope, new ExperienceConsolidationOptions { MinimumSources = 3 }, CancellationToken.None)).Groups);
    }

    [Fact]
    public async Task Records_in_another_scope_are_never_merged_and_a_scope_outside_the_authorization_is_denied()
    {
        var world = new World();
        await world.CreateAsync(HandWritten(TestScope, Now, delay: 0));
        await world.CreateAsync(HandWritten(TestScope, Now.AddMinutes(1), delay: 0));
        await world.CreateAsync(HandWritten(OtherScope, Now.AddMinutes(2), delay: 0));
        await world.CreateAsync(HandWritten(OtherScope, Now.AddMinutes(3), delay: 0));

        Assert.Empty((await world.Consolidation.ConsolidateAsync(Authorization, TestScope, Defaults, CancellationToken.None)).Groups);
        Assert.Empty((await world.Consolidation.ConsolidateAsync(Authorization, OtherScope, Defaults, CancellationToken.None)).Groups);

        var denied = await world.Consolidation.ConsolidateAsync(
            Authorization, new Scope("tenant-2", "app-1", "project-1"), Defaults, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Denied, denied.Outcome);
        Assert.Empty(denied.Groups);
    }

    [Fact]
    public async Task Model_authored_records_are_never_merged()
    {
        var world = new World();
        await world.CreateAsync(HandWritten(TestScope, Now, delay: 0));
        await world.CreateAsync(HandWritten(TestScope, Now.AddMinutes(1), delay: 0));
        await world.CreateAsync(HandWritten(TestScope, Now.AddMinutes(2), delay: 0, authorship: ReflectionAuthorship.Model));

        Assert.Empty((await world.Consolidation.ConsolidateAsync(Authorization, TestScope, Defaults, CancellationToken.None)).Groups);
    }

    [Fact]
    public async Task With_signing_unconfirmed_records_are_left_out_and_the_consolidated_record_is_signed_over_its_source_list()
    {
        var signing = new ExperienceProvenanceSigningOptions(new Dictionary<string, byte[]> { ["key-1"] = SigningKey }, "key-1");
        var world = new World(signing: signing);
        var signed = new List<ExperienceRecord>();
        for (var n = 0; n < 3; n++)
        {
            signed.Add(await world.LessonAsync(at: Now.AddDays(n)));
        }

        // A hand-written record no signature confirms: excluded, as injection would exclude it.
        var unconfirmed = HandWritten(TestScope, Now.AddDays(5), delay: 0) with { Attempts = signed[0].Attempts };
        await world.CreateAsync(unconfirmed);

        var group = Assert.Single((await world.Consolidation.ConsolidateAsync(Authorization, TestScope, Defaults, CancellationToken.None)).Groups);

        Assert.Equal(signed.Select(record => record.ExperienceId).Order(), group.Sources.Select(source => source.ExperienceId));
        Assert.Equal(ExperienceStatus.Validated, (await world.ReadAsync(unconfirmed.ExperienceId)).Status);
        var consolidated = await world.ReadAsync(group.ConsolidatedExperienceId);
        var signer = ProvenanceSigner.Create(signing)!;
        Assert.NotNull(consolidated.ProvenanceSignature);
        Assert.True(signer.ConfirmsContent(consolidated));

        // The source list is a signed claim: changing it breaks the signature.
        var tampered = consolidated with
        {
            Provenance = consolidated.Provenance with { ExposedTo = consolidated.Provenance.ExposedTo.Skip(1).ToList() },
        };
        Assert.False(signer.ConfirmsContent(tampered));
    }

    [Fact]
    public async Task A_source_changed_after_the_pass_read_it_is_left_untouched_and_reported_and_the_rest_are_superseded()
    {
        var inner = new InMemoryExperienceRecordStore();
        var racing = new RacingStore(inner);
        var world = new World(store: racing, inMemory: inner);
        var sources = new List<ExperienceRecord>();
        for (var n = 0; n < 3; n++)
        {
            sources.Add(await world.LessonAsync(at: Now.AddDays(n)));
        }

        // Just before its supersession commits, another writer revokes the second source.
        var raced = sources[1];
        racing.BeforeSupersede = async id =>
        {
            if (id == raced.ExperienceId)
            {
                var current = await world.ReadAsync(id);
                var revoke = await inner.CommitLifecycleEventAsync(
                    Authorization,
                    TestScope,
                    new LifecycleEvent(Guid.NewGuid(), id, current.Status, ExperienceStatus.Revoked, "withdrawn by a reviewer", "reviewer", Now.AddDays(3), current.Revision),
                    CancellationToken.None);
                Assert.Equal(ExperienceStoreOutcome.Committed, revoke.Outcome);
            }
        };

        var group = Assert.Single((await world.Consolidation.ConsolidateAsync(Authorization, TestScope, Defaults, CancellationToken.None)).Groups);

        Assert.Equal(ExperienceConsolidationGroupOutcome.Created, group.Outcome);
        var lost = Assert.Single(group.Sources, source => !source.Superseded);
        Assert.Equal(raced.ExperienceId, lost.ExperienceId);
        Assert.Contains(lost.Outcome, new LifecycleTransitionOutcome?[] { LifecycleTransitionOutcome.StaleRevision, LifecycleTransitionOutcome.StatusMismatch });
        Assert.Equal(ExperienceStatus.Revoked, (await world.ReadAsync(raced.ExperienceId)).Status);
        Assert.Equal(2, group.Sources.Count(source => source.Superseded));

        // The next pass has nothing left to do: the revoked source is out of every group.
        racing.BeforeSupersede = null;
        Assert.Empty((await world.Consolidation.ConsolidateAsync(Authorization, TestScope, Defaults, CancellationToken.None)).Groups);
    }

    [Fact]
    public async Task A_pass_interrupted_before_the_supersessions_is_finished_by_the_next_one()
    {
        var inner = new InMemoryExperienceRecordStore();
        var racing = new RacingStore(inner) { FailSupersessions = true };
        var world = new World(store: racing, inMemory: inner);
        for (var n = 0; n < 3; n++)
        {
            await world.LessonAsync(at: Now.AddDays(n));
        }

        var broken = Assert.Single((await world.Consolidation.ConsolidateAsync(Authorization, TestScope, Defaults, CancellationToken.None)).Groups);
        Assert.All(broken.Sources, source => Assert.Equal(nameof(ExperienceStoreException), source.ExceptionType));
        Assert.All(broken.Sources, source => Assert.False(source.Superseded));

        racing.FailSupersessions = false;
        var resumed = Assert.Single((await world.Consolidation.ConsolidateAsync(Authorization, TestScope, Defaults, CancellationToken.None)).Groups);

        Assert.Equal(ExperienceConsolidationGroupOutcome.Existing, resumed.Outcome);
        Assert.Equal(broken.ConsolidatedExperienceId, resumed.ConsolidatedExperienceId);
        Assert.Equal(3, resumed.IndependentRuns);
        Assert.All(resumed.Sources, source => Assert.True(source.Superseded));
        Assert.Empty((await world.Consolidation.ConsolidateAsync(Authorization, TestScope, Defaults, CancellationToken.None)).Groups);
    }

    [Fact]
    public async Task Contesting_the_consolidated_record_works_as_for_any_record_and_its_sources_stay_superseded()
    {
        var world = new World();
        var sources = new List<ExperienceRecord>();
        for (var n = 0; n < 3; n++)
        {
            sources.Add(await world.LessonAsync(at: Now.AddDays(n)));
        }

        var group = Assert.Single((await world.Consolidation.ConsolidateAsync(Authorization, TestScope, Defaults, CancellationToken.None)).Groups);
        var consolidated = await world.ReadAsync(group.ConsolidatedExperienceId);

        var contest = await world.Lifecycle.CommitAsync(
            Authorization,
            new CommitLifecycleTransitionRequest(
                Guid.NewGuid(), consolidated.ExperienceId, TestScope, consolidated.Status, ExperienceStatus.Contested, "a reviewer disputes it", "reviewer", Now.AddDays(20), consolidated.Revision),
            CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Committed, contest.Outcome);
        foreach (var source in sources)
        {
            Assert.Equal(ExperienceStatus.Superseded, (await world.ReadAsync(source.ExperienceId)).Status);
        }

        Assert.Empty((await world.Consolidation.ConsolidateAsync(Authorization, TestScope, Defaults, CancellationToken.None)).Groups);
    }

    [Fact]
    public async Task Options_out_of_range_are_refused()
    {
        var world = new World();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => world.Consolidation.ConsolidateAsync(
            Authorization, TestScope, new ExperienceConsolidationOptions { MinimumSources = 1 }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Consolidation.ConsolidateAsync(
            Authorization,
            TestScope,
            new ExperienceConsolidationOptions { ArgumentKeys = new Dictionary<string, IReadOnlyList<string>> { [" "] = ["delay"] } },
            CancellationToken.None));
    }

    [Fact]
    public void The_derived_ID_depends_on_the_scope_and_the_source_set_not_its_order()
    {
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        var forward = ExperienceConsolidation.ConsolidatedIdFor(TestScope, ids);

        Assert.Equal(forward, ExperienceConsolidation.ConsolidatedIdFor(TestScope, ids.Reverse()));
        Assert.NotEqual(forward, ExperienceConsolidation.ConsolidatedIdFor(OtherScope, ids));
        Assert.NotEqual(forward, ExperienceConsolidation.ConsolidatedIdFor(TestScope, ids.Take(2)));
        Assert.Equal(8, forward.Version);
    }

    /// <summary>A verified, hand-written record: run_tests timed out, then <paramref name="fix"/>(delay) worked.</summary>
    private static ExperienceRecord HandWritten(
        Scope scope,
        DateTimeOffset at,
        int delay,
        string error = Timeout,
        string fix = "retry",
        Guid? runId = null,
        ReflectionAuthorship authorship = ReflectionAuthorship.Deterministic)
    {
        var round = Guid.NewGuid();
        var run = runId ?? Guid.NewGuid();
        return new ExperienceRecord(
            ExperienceId: Guid.NewGuid(),
            SourceRunId: run,
            Scope: scope,
            TaskId: TaskOfRuns,
            TaskSummary: "keep the build green",
            Attempts:
            [
                new Attempt(Guid.NewGuid(), 0, at, TimeSpan.FromSeconds(1), [Call("run_tests", null, error)], null, null),
                new Attempt(
                    Guid.NewGuid(),
                    1,
                    at,
                    TimeSpan.FromSeconds(1),
                    [Call(fix, new Dictionary<string, object?> { ["delay"] = delay }, null)],
                    "done",
                    null),
            ],
            Outcome: new Outcome(
                TaskVerificationStatus.Verified,
                [new Evidence(Guid.NewGuid(), round, ArtifactRevision, "tests", "TestResult", CheckResult.Pass, "ci", null, at)],
                null,
                at),
            CompletionScore: 1,
            Reflection: new Reflection(
                Guid.NewGuid(), run, "Retry after a timeout.", [], [], ["the test host is local"], [], null, [], TaskVerificationStatus.Verified, 1, "1.0.0", "tests", at)
            {
                Authorship = authorship,
            },
            Environment: new EnvironmentFingerprint("host-1", "net10.0", "test-os", null, new Dictionary<string, string>()),
            Provenance: new Provenance("unit-tests", "1.0.0", at, null),
            Status: ExperienceStatus.Validated,
            ReuseConfidence: 2d / 3d,
            SupportingValidations: 1,
            Contradictions: 0,
            Revision: 0,
            CreatedAt: at,
            UpdatedAt: at);

        ToolCallRecord Call(string tool, Dictionary<string, object?>? arguments, string? failure) =>
            new(Guid.NewGuid(), 0, tool, arguments ?? [], at, TimeSpan.FromSeconds(1), failure is null ? "ok" : null, failure);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A store that lets a test act just before a supersession commits, or make every supersession throw.</summary>
    private sealed class RacingStore(InMemoryExperienceRecordStore inner) : DelegatingStore(inner)
    {
        public Func<Guid, Task>? BeforeSupersede { get; set; }

        public bool FailSupersessions { get; set; }

        public override async Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(
            AuthorizationContext authorization, Scope scope, LifecycleEvent lifecycleEvent, CancellationToken cancellationToken)
        {
            if (lifecycleEvent.CurrentStatus == ExperienceStatus.Superseded)
            {
                if (FailSupersessions)
                {
                    throw new ExperienceStoreException("The store is unavailable.");
                }

                if (BeforeSupersede is { } before)
                {
                    await before(lifecycleEvent.ExperienceRecordId);
                }
            }

            return await base.CommitLifecycleEventAsync(authorization, scope, lifecycleEvent, cancellationToken);
        }
    }

    /// <summary>Forwards every port member to the in-memory store.</summary>
    private class DelegatingStore(InMemoryExperienceRecordStore inner) : IExperienceRecordStore
    {
        public Task<ExperienceRecordCreateResult> CreateAsync(AuthorizationContext authorization, ExperienceRecord record, CancellationToken cancellationToken) =>
            inner.CreateAsync(authorization, record, cancellationToken);

        public Task<ExperienceRecordGetResult> GetAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, CancellationToken cancellationToken) =>
            inner.GetAsync(authorization, scope, experienceId, cancellationToken);

        public Task<ExperienceRecordGetResult> GetAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, ExperienceReadOptions options, CancellationToken cancellationToken) =>
            inner.GetAsync(authorization, scope, experienceId, options, cancellationToken);

        public Task<ExperienceRecordQueryResult> QueryAsync(AuthorizationContext authorization, ExperienceRecordQuery query, CancellationToken cancellationToken) =>
            inner.QueryAsync(authorization, query, cancellationToken);

        public virtual Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(
            AuthorizationContext authorization, Scope scope, LifecycleEvent lifecycleEvent, CancellationToken cancellationToken) =>
            inner.CommitLifecycleEventAsync(authorization, scope, lifecycleEvent, cancellationToken);

        public Task<ExperienceRecordHistoryResult> GetHistoryAsync(AuthorizationContext authorization, ExperienceRecordHistoryQuery query, CancellationToken cancellationToken) =>
            inner.GetHistoryAsync(authorization, query, cancellationToken);

        public Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(
            AuthorizationContext authorization, Scope scope, Guid experienceId, Guid replacementExperienceId, CancellationToken cancellationToken) =>
            inner.CheckSupersessionAsync(authorization, scope, experienceId, replacementExperienceId, cancellationToken);
    }

    /// <summary>The capture service, finalization, lifecycle and consolidation over one store, in one scope.</summary>
    private sealed class World
    {
        private static readonly Guid Round = Guid.Parse("33333333-3333-3333-3333-333333333333");

        public World(
            ExperienceFinalizationOptions? options = null,
            ExperienceProvenanceSigningOptions? signing = null,
            IExperienceRecordStore? store = null,
            InMemoryExperienceRecordStore? inMemory = null)
        {
            Store = inMemory ?? new InMemoryExperienceRecordStore();
            var used = store ?? Store;
            Capture = new InMemoryExperienceCaptureService(new DefaultSanitizer(Sanitization), new CaptureLimits(10, 10, 1_000, 1_000));
            Lifecycle = new ExperienceLifecycleService(
                used,
                indexingService: null,
                new ExperienceIndependenceOptions(),
                Capture,
                deindexingTimeout: null,
                confidenceEngine: null,
                signing);
            Finalization = new ExperienceFinalizationService(
                Capture,
                new DefaultExperienceReflector(),
                used,
                Lifecycle,
                indexingService: null,
                indexingTimeout: null,
                provenanceSigning: null,
                reflectionSanitizer: null,
                options);
            Consolidation = new ExperienceConsolidationService(used, Lifecycle, Clock);
            UsedStore = used;
        }

        public InMemoryExperienceRecordStore Store { get; }

        public IExperienceRecordStore UsedStore { get; }

        public InMemoryExperienceCaptureService Capture { get; }

        public ExperienceLifecycleService Lifecycle { get; }

        public ExperienceFinalizationService Finalization { get; }

        public ExperienceConsolidationService Consolidation { get; }

        public FixedClock Clock { get; } = new(Now.AddDays(30));

        public async Task CreateAsync(ExperienceRecord record) =>
            Assert.Equal(ExperienceStoreOutcome.Created, (await Store.CreateAsync(Authorization, record, CancellationToken.None)).Outcome);

        public async Task<ExperienceRecord> ReadAsync(Guid id) =>
            (await Store.GetAsync(Authorization, TestScope, id, CancellationToken.None)).Record!;

        /// <summary>A verified run -- run_tests timed out, then passed on a second call -- finalized into a Validated lesson at <paramref name="at"/>.</summary>
        public async Task<ExperienceRecord> LessonAsync(DateTimeOffset at, IReadOnlyList<ExperienceRecord>? exposedTo = null)
        {
            var runId = Guid.NewGuid();
            Assert.Equal(StartRunOutcome.Started, Capture.StartRun(
                runId,
                TaskOfRuns,
                "keep the build green",
                TestScope,
                new EnvironmentFingerprint("host-1", "net10.0", "test-os", null, new Dictionary<string, string>()),
                new Provenance("unit-tests", "1.0.0", at, null),
                at).Outcome);

            if (exposedTo is { Count: > 0 })
            {
                Assert.Equal(
                    RecordExposureOutcome.Recorded,
                    Capture.RecordExposure(runId, [.. exposedTo.Select(record => new RunExposure(record.ExperienceId, record.Revision))]).Outcome);
            }

            Assert.Equal(AppendAttemptOutcome.Recorded, (await Capture.AppendAttemptAsync(
                runId,
                new AppendAttemptRequest(
                    Guid.NewGuid(),
                    at,
                    TimeSpan.FromSeconds(1),
                    [
                        new RawToolCall(Guid.NewGuid(), "run_tests", new Dictionary<string, object?>(), at, TimeSpan.FromSeconds(1), null, Timeout),
                        new RawToolCall(Guid.NewGuid(), "run_tests", new Dictionary<string, object?>(), at, TimeSpan.FromSeconds(1), "passed", null),
                    ],
                    "done",
                    null))).Outcome);
            Assert.Equal(CompleteRunOutcome.Recorded, (await Capture.CompleteRunAsync(runId, Guid.NewGuid(), RunExecutionStatus.Completed, at.AddMinutes(1))).Outcome);

            var result = await Finalization.FinalizeAsync(
                new FinalizeExperienceRequest(
                    RunId: runId,
                    Authorization: Authorization,
                    ClosedRound: new ClosedVerificationRound(Round, ArtifactRevision),
                    RequiredChecks: [new RequiredCheck("tests", "TestResult")],
                    Evidence: [new Evidence(Guid.NewGuid(), Round, ArtifactRevision, "tests", "TestResult", CheckResult.Pass, "ci", null, at)],
                    CurrentArtifactRevision: ArtifactRevision,
                    StorageDecision: StorageDecision.Permit,
                    FinalizedAt: at.AddMinutes(2)),
                CancellationToken.None);
            Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
            return result.Record!;
        }
    }
}

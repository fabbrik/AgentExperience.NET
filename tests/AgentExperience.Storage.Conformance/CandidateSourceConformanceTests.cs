using static AgentExperience.Storage.Conformance.ConformanceData;

namespace AgentExperience.Storage.Conformance;

/// <summary>
/// The behaviour every <see cref="IExperienceCandidateSource"/> must show, observed through the ports alone: it
/// filters by exact scope, by the requested statuses and by the minimum confidence; it ranks before it applies the
/// limit; it returns the strongest match first with a relevance in [0, 1]; it never returns a record that matches
/// none of the query's terms; it returns each matched record exactly as stored; it sees a committed write on
/// the next search; and, asked to, it leaves model-authored records out before the limit. Exact relevance values are not part of the contract. Records are seeded through the record
/// store the source reads from, so a subclass supplies both: <see cref="CreateRecordStore"/> and
/// <see cref="CreateCandidateSource"/> must see the same records.
/// </summary>
/// <remarks>
/// Every seeded record carries its matching words in <see cref="ExperienceRecord.TaskSummary"/> (and, where it
/// helps, in its task ID and reflection lesson too), so the suite assumes only that the task summary is searched.
/// </remarks>
public abstract class CandidateSourceConformanceTests
{
    private static readonly ExperienceStatus[] Eligible = [ExperienceStatus.Validated, ExperienceStatus.Reinforced];

    private readonly Lazy<IExperienceRecordStore> _store;
    private readonly Lazy<IExperienceCandidateSource> _source;

    protected CandidateSourceConformanceTests()
    {
        _store = new Lazy<IExperienceRecordStore>(CreateRecordStore);
        _source = new Lazy<IExperienceCandidateSource>(CreateCandidateSource);
    }

    /// <summary>The record store the test seeds records through.</summary>
    protected IExperienceRecordStore RecordStore => _store.Value;

    /// <summary>The candidate source under test.</summary>
    protected IExperienceCandidateSource Source => _source.Value;

    /// <summary>Creates the record store whose records <see cref="CreateCandidateSource"/> searches. Called at most once per test.</summary>
    protected abstract IExperienceRecordStore CreateRecordStore();

    /// <summary>Creates the candidate source under test. Called at most once per test.</summary>
    protected abstract IExperienceCandidateSource CreateCandidateSource();

    [Fact]
    public async Task Only_records_in_exactly_the_requested_scope_are_returned()
    {
        var tenant = NewTenant();
        var foreignTenant = NewTenant();
        var scope = Scope(tenant);
        var mine = await SeedAsync(tenant, scope);

        // Identical text in every neighbouring scope, including ones that differ only by an optional field or by case.
        await SeedAsync(foreignTenant, Scope(foreignTenant));
        await SeedAsync(tenant, Scope(tenant, project: "project-2"));
        await SeedAsync(tenant, Scope(tenant, project: "Project-1"));
        await SeedAsync(tenant, Scope(tenant, team: "team-1"));

        var result = await SearchAsync(tenant, scope, "refund");

        Assert.Equal(ExperienceStoreOutcome.Found, result.Outcome);
        Assert.Empty(result.Errors);
        Assert.Equal([mine], Ids(result));
        Assert.All(result.Candidates, candidate =>
        {
            Assert.False(candidate.SharedByGrant);
            Assert.Null(candidate.PermittingGrantId);
        });
    }

    [Fact]
    public async Task A_search_outside_the_authorization_is_Denied_with_no_candidates()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        await SeedAsync(tenant, scope);

        var result = await Source.SearchAsync(
            Authorize(NewTenant()), new ExperienceCandidateQuery(scope, "refund", Eligible, 0d), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Denied, result.Outcome);
        Assert.Empty(result.Candidates);
    }

    [Theory]
    [InlineData("blank-text")]
    [InlineData("text-too-long")]
    [InlineData("no-statuses")]
    [InlineData("undefined-status")]
    [InlineData("confidence-below-zero")]
    [InlineData("confidence-above-one")]
    [InlineData("limit-below-min")]
    [InlineData("limit-above-max")]
    public async Task A_malformed_search_is_Invalid_with_no_candidates(string malformation)
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        await SeedAsync(tenant, scope);
        var valid = new ExperienceCandidateQuery(scope, "refund", Eligible, 0d);
        var query = malformation switch
        {
            "blank-text" => valid with { TaskText = "   " },
            "text-too-long" => valid with { TaskText = "refund " + new string('x', ExperienceCandidateQuery.MaxTaskTextLength) },
            "no-statuses" => valid with { EligibleStatuses = [] },
            "undefined-status" => valid with { EligibleStatuses = [(ExperienceStatus)999] },
            "confidence-below-zero" => valid with { MinimumConfidence = -0.01 },
            "confidence-above-one" => valid with { MinimumConfidence = 1.01 },
            "limit-below-min" => valid with { Limit = ExperienceCandidateQuery.MinLimit - 1 },
            _ => valid with { Limit = ExperienceCandidateQuery.MaxLimit + 1 },
        };

        var result = await Source.SearchAsync(Authorize(tenant), query, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, result.Outcome);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task Only_the_requested_statuses_are_returned()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var byStatus = new Dictionary<ExperienceStatus, Guid>();
        foreach (var status in Enum.GetValues<ExperienceStatus>())
        {
            byStatus[status] = await SeedAsync(tenant, scope, status: status);
        }

        var eligible = await SearchAsync(tenant, scope, "refund");
        var onlyQuarantined = await Source.SearchAsync(
            Authorize(tenant), new ExperienceCandidateQuery(scope, "refund", [ExperienceStatus.Quarantined], 0d), CancellationToken.None);

        // The caller decides which statuses are eligible; the source applies exactly that list.
        Assert.Equal(
            new[] { byStatus[ExperienceStatus.Validated], byStatus[ExperienceStatus.Reinforced] }.Order(),
            Ids(eligible).Order());
        Assert.Equal([byStatus[ExperienceStatus.Quarantined]], Ids(onlyQuarantined));
    }

    [Fact]
    public async Task A_committed_status_change_is_seen_by_the_next_search()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var id = await SeedAsync(tenant, scope);
        Assert.Equal([id], Ids(await SearchAsync(tenant, scope, "refund")));

        var commit = await RecordStore.CommitLifecycleEventAsync(
            Authorize(tenant), scope, Event(id, ExperienceStatus.Validated, ExperienceStatus.Revoked, 0), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Committed, commit.Outcome);

        Assert.Empty((await SearchAsync(tenant, scope, "refund")).Candidates);
    }

    [Fact]
    public async Task A_record_below_the_minimum_confidence_is_not_returned_and_the_floor_is_inclusive()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var at = await SeedAsync(tenant, scope, confidence: 0.5);
        var above = await SeedAsync(tenant, scope, confidence: 0.9);
        var below = await SeedAsync(tenant, scope, confidence: 0.49);

        var result = await SearchAsync(tenant, scope, "refund", minimumConfidence: 0.5);

        Assert.Equal(new[] { at, above }.Order(), Ids(result).Order());
        Assert.DoesNotContain(below, Ids(result));
    }

    [Fact]
    public async Task The_limit_bounds_how_many_candidates_are_returned()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        for (var i = 0; i < 5; i++)
        {
            await SeedAsync(tenant, scope);
        }

        var limited = await SearchAsync(tenant, scope, "refund", limit: 2);
        var unlimited = await SearchAsync(tenant, scope, "refund");

        Assert.Equal(2, limited.Candidates.Count);
        Assert.Equal(5, unlimited.Candidates.Count);
    }

    [Fact]
    public async Task The_limit_is_applied_after_ranking_so_the_strongest_match_survives_it()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);

        // Weak matches first, so a store that cut before ranking would keep one of them.
        for (var i = 0; i < 4; i++)
        {
            await SeedWeakAsync(tenant, scope);
        }

        var strong = await SeedStrongAsync(tenant, scope);

        var result = await SearchAsync(tenant, scope, "refund policy invoice", limit: 1);

        Assert.Equal([strong], Ids(result));
    }

    [Fact]
    public async Task The_strongest_match_comes_first_with_a_strictly_higher_relevance_and_every_relevance_is_in_zero_to_one()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);

        // The weak match is stored first, so insertion order cannot pass for ranking.
        var weak = await SeedWeakAsync(tenant, scope);
        var strong = await SeedStrongAsync(tenant, scope);

        var result = await SearchAsync(tenant, scope, "refund policy invoice");

        Assert.Equal([strong, weak], Ids(result));
        Assert.All(result.Candidates, candidate => Assert.InRange(candidate.Relevance, 0d, 1d));
        Assert.True(
            result.Candidates[0].Relevance > result.Candidates[1].Relevance,
            $"The stronger match must report a strictly higher relevance; got {result.Candidates[0].Relevance} and {result.Candidates[1].Relevance}.");
    }

    [Fact]
    public async Task A_record_that_matches_none_of_the_query_s_terms_is_not_returned()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var matching = await SeedAsync(tenant, scope);
        var unrelated = await SeedAsync(
            tenant, scope, taskId: "cluster-rollout", summary: "Roll out the cluster", lesson: "Drain nodes before rolling");

        var result = await SearchAsync(tenant, scope, "refund");
        var nothing = await SearchAsync(tenant, scope, "kubernetes autoscaling");

        Assert.Equal([matching], Ids(result));
        Assert.DoesNotContain(unrelated, Ids(result));
        Assert.Equal(ExperienceStoreOutcome.Found, nothing.Outcome);
        Assert.Empty(nothing.Candidates);
    }

    [Fact]
    public async Task A_candidate_carries_the_record_exactly_as_it_was_created()
    {
        var tenant = NewTenant();
        var scope = new Scope(tenant, "app-1", "project-1", "team-1", "agent-1", "user-1");
        var record = FullRecord(scope); // Validated, confidence 5/7, task summary "Resolve refund ticket".
        var created = await RecordStore.CreateAsync(Authorize(tenant), record, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Created, created.Outcome);

        var candidate = Assert.Single((await SearchAsync(tenant, scope, "refund ticket")).Candidates);

        Assert.Equal(Json(record), Json(candidate.Record));
    }

    [Fact]
    public async Task An_excluding_search_fills_its_limit_with_the_strongest_records_that_are_not_model_authored()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);

        // Story 14.4: model-authored records rank above every deterministic one, so a store that excluded them after
        // its limit would return nothing, or fewer than the limit.
        var model = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            model.Add(await SeedStrongAsync(tenant, scope, ReflectionAuthorship.Model));
        }

        var medium = new[] { await SeedMediumAsync(tenant, scope), await SeedMediumAsync(tenant, scope) };
        var weak = new[] { await SeedWeakAsync(tenant, scope), await SeedWeakAsync(tenant, scope) };

        var excluding = await SearchAsync(tenant, scope, "refund policy invoice", limit: 2, excludeModelAuthored: true);
        var including = await SearchAsync(tenant, scope, "refund policy invoice", limit: 3);
        var excludingAll = await SearchAsync(tenant, scope, "refund policy invoice", excludeModelAuthored: true);
        var includingAll = await SearchAsync(tenant, scope, "refund policy invoice");

        Assert.Equal(ExperienceStoreOutcome.Found, excluding.Outcome);
        Assert.Equal(medium.Order(), Ids(excluding).Order());
        Assert.Equal(model.Order(), Ids(including).Order());
        Assert.Equal(medium.Concat(weak).Order(), Ids(excludingAll).Order());
        Assert.Equal(model.Concat(medium).Concat(weak).Order(), Ids(includingAll).Order());
        Assert.False(new ExperienceCandidateQuery(scope, "refund", Eligible, 0d).ExcludeModelAuthored);
    }

    [Fact]
    public async Task An_excluding_search_keeps_a_record_with_no_reflection_and_a_deterministic_one()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var model = await SeedAsync(tenant, scope, authorship: ReflectionAuthorship.Model);
        var deterministic = await SeedAsync(tenant, scope);
        var unreflected = Record(scope, ExperienceStatus.Validated, taskId: "refund-ticket", summary: "Resolve a refund", confidence: 0.75);
        Assert.Equal(ExperienceStoreOutcome.Created, (await RecordStore.CreateAsync(Authorize(tenant), unreflected, CancellationToken.None)).Outcome);

        var excluding = await SearchAsync(tenant, scope, "refund", excludeModelAuthored: true);

        Assert.Equal(new[] { deterministic, unreflected.ExperienceId }.Order(), Ids(excluding).Order());
        Assert.DoesNotContain(model, Ids(excluding));
        Assert.All(excluding.Candidates, candidate => Assert.NotEqual(ReflectionAuthorship.Model, candidate.Record.Reflection?.Authorship));
    }

    [Fact]
    public async Task An_excluding_search_leaves_out_a_record_of_the_library_s_own_model_reflector_whatever_authorship_it_declares()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);

        // Story 17.1: the library's ChatClientExperienceReflector, before it declared authorship, wrote Deterministic.
        // Its producer prefix makes such a record model-authored in every store. A third-party producer is never read.
        var legacy = await SeedAsync(tenant, scope, producer: "AgentExperience.ChatClientExperienceReflector/1.0.0 (some-model)");
        var thirdParty = await SeedAsync(tenant, scope, producer: "Contoso.ModelReflector/1.0 (some-model)");

        var excluding = await SearchAsync(tenant, scope, "refund", excludeModelAuthored: true);
        var including = await SearchAsync(tenant, scope, "refund");

        Assert.Equal(ExperienceStoreOutcome.Found, excluding.Outcome);
        Assert.Equal([thirdParty], Ids(excluding));
        Assert.Equal(new[] { legacy, thirdParty }.Order(), Ids(including).Order());
    }

    [Fact]
    public async Task A_search_with_a_cancelled_token_throws_an_unwrapped_OperationCanceledException()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        await SeedAsync(tenant, scope);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Source.SearchAsync(
            Authorize(tenant), new ExperienceCandidateQuery(scope, "refund", Eligible, 0d), cancelled.Token));
    }

    private Task<ExperienceCandidateSearchResult> SearchAsync(
        string tenant,
        Scope scope,
        string taskText,
        double minimumConfidence = 0d,
        int limit = ExperienceCandidateQuery.DefaultLimit,
        bool excludeModelAuthored = false) =>
        Source.SearchAsync(
            Authorize(tenant),
            new ExperienceCandidateQuery(scope, taskText, Eligible, minimumConfidence, limit) { ExcludeModelAuthored = excludeModelAuthored },
            CancellationToken.None);

    private static Guid[] Ids(ExperienceCandidateSearchResult result) =>
        [.. result.Candidates.Select(candidate => candidate.Record.ExperienceId)];

    /// <summary>Every query term, repeated, in the task summary, the task ID and the lesson.</summary>
    private Task<Guid> SeedStrongAsync(string tenant, Scope scope, ReflectionAuthorship authorship = ReflectionAuthorship.Deterministic) => SeedAsync(
        tenant,
        scope,
        taskId: "refund-policy-invoice",
        summary: "Refund policy invoice: apply the refund policy to the disputed invoice",
        lesson: "Apply the refund policy before reissuing the invoice",
        authorship: authorship);

    /// <summary>The strong match's summary and lesson under an unrelated task ID: weaker than it, stronger than the weak one.</summary>
    private Task<Guid> SeedMediumAsync(string tenant, Scope scope) => SeedAsync(
        tenant,
        scope,
        taskId: "billing-follow-up",
        summary: "Refund policy invoice: apply the refund policy to the disputed invoice",
        lesson: "Apply the refund policy before reissuing the invoice");

    /// <summary>
    /// Every query term once, in the task summary only, among many unrelated words. (Whether a record matching only
    /// some of the terms is returned at all is the implementation's choice, so the weak match carries them all.)
    /// </summary>
    private Task<Guid> SeedWeakAsync(string tenant, Scope scope) => SeedAsync(
        tenant,
        scope,
        taskId: "cluster-rollout",
        summary: "Weekly cluster rollout checklist: drain nodes, rotate certificates, update dashboards, and note any refund, invoice or policy change for finance",
        lesson: "Drain nodes before rolling out");

    /// <summary>Creates one searchable record through the record store and returns its ID.</summary>
    private async Task<Guid> SeedAsync(
        string tenant,
        Scope scope,
        string taskId = "refund-ticket",
        string summary = "Resolve a refund",
        string lesson = "Retry the refund once the lock clears",
        ExperienceStatus status = ExperienceStatus.Validated,
        double confidence = 0.75,
        ReflectionAuthorship authorship = ReflectionAuthorship.Deterministic,
        string? producer = null)
    {
        var record = Record(scope, status, taskId: taskId, summary: summary, lesson: lesson, confidence: confidence);
        record = record with
        {
            Reflection = record.Reflection! with { Authorship = authorship, Producer = producer ?? record.Reflection!.Producer },
        };
        var created = await RecordStore.CreateAsync(Authorize(tenant), record, CancellationToken.None);
        Assert.True(
            created.Outcome == ExperienceStoreOutcome.Created,
            $"Seeding a record returned {created.Outcome}: {string.Join("; ", created.Errors.Select(e => $"{e.Path}: {e.Message}"))}");
        return record.ExperienceId;
    }
}

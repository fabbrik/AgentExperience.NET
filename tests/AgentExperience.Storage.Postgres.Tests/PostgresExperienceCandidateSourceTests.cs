using AgentExperience.Core.Retrieval;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Integration tests for the SQL behind <see cref="PostgresExperienceCandidateSource"/>, against a
/// real PostgreSQL 16 container: what the generated <c>search_vector</c> indexes, that scope, status,
/// and confidence are all decided inside the query, and that a matched record still decodes into the
/// full canonical record. Each test uses its own random tenant, so tests sharing the container never
/// see each other's rows.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresExperienceCandidateSourceTests
{
    private static readonly DateTimeOffset Now = ColumnTime;

    private readonly PostgresExperienceRecordStore _store;
    private readonly PostgresExperienceCandidateSource _source;

    public PostgresExperienceCandidateSourceTests(PostgresFixture fixture)
    {
        _store = new PostgresExperienceRecordStore(fixture.DataSource);
        _source = new PostgresExperienceCandidateSource(fixture.DataSource);
    }

    [Fact]
    public async Task The_task_id_the_task_summary_and_the_reflection_lesson_are_each_searchable()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var byTaskId = await SeedAsync(scope, taskId: "refund-ticket-triage", summary: "Nothing else to say", lesson: "Nothing else to say");
        var bySummary = await SeedAsync(scope, taskId: "unrelated-a", summary: "Resolve a customer refund", lesson: "Nothing else to say");
        var byLesson = await SeedAsync(scope, taskId: "unrelated-b", summary: "Nothing else to say", lesson: "Issue the refund once the lock clears");
        var unrelated = await SeedAsync(scope, taskId: "deploy-cluster", summary: "Roll out the cluster", lesson: "Drain nodes before rolling");

        var result = await SearchAsync(tenant, scope, "refund");

        Assert.Equal(ExperienceStoreOutcome.Found, result.Outcome);
        Assert.Empty(result.Errors);
        Assert.Equal(
            new[] { byTaskId, bySummary, byLesson }.Order(),
            result.Candidates.Select(candidate => candidate.Record.ExperienceId).Order());
        Assert.DoesNotContain(unrelated, result.Candidates.Select(candidate => candidate.Record.ExperienceId));
    }

    [Fact]
    public async Task Text_that_matches_nothing_is_Found_with_no_candidates()
    {
        var tenant = NewTenant();
        await SeedAsync(Scope(tenant), taskId: "refund-ticket", summary: "Resolve a refund", lesson: "Retry the refund");

        var result = await SearchAsync(tenant, Scope(tenant), "kubernetes autoscaling");

        Assert.Equal(ExperienceStoreOutcome.Found, result.Outcome);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task A_record_in_another_tenant_project_or_team_is_never_returned()
    {
        var tenant = NewTenant();
        var otherTenant = NewTenant();
        var scope = Scope(tenant);
        var mine = await SeedAsync(scope, taskId: "refund-ticket", summary: "Resolve a refund", lesson: "Retry the refund");

        // Identical text in every neighbouring scope, including one that only differs by an optional field.
        await SeedAsync(Scope(otherTenant), taskId: "refund-ticket", summary: "Resolve a refund", lesson: "Retry the refund");
        await SeedAsync(Scope(tenant, project: "project-2"), taskId: "refund-ticket", summary: "Resolve a refund", lesson: "Retry the refund");
        await SeedAsync(Scope(tenant, team: "team-1"), taskId: "refund-ticket", summary: "Resolve a refund", lesson: "Retry the refund");

        var result = await SearchAsync(tenant, scope, "refund");

        Assert.Equal([mine], result.Candidates.Select(candidate => candidate.Record.ExperienceId));

        // And the authorization the host established still bounds the search, whatever scope is asked for.
        var denied = await _source.SearchAsync(
            Authorize(otherTenant),
            new ExperienceCandidateQuery(scope, "refund", [ExperienceStatus.Validated], 0d),
            CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Denied, denied.Outcome);
        Assert.Empty(denied.Candidates);
    }

    [Fact]
    public async Task Only_the_requested_statuses_come_back()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var validated = await SeedAsync(scope, taskId: "refund-ticket", summary: "Resolve a refund", lesson: "Retry the refund", status: ExperienceStatus.Validated);
        var reinforced = await SeedAsync(scope, taskId: "refund-ticket", summary: "Resolve a refund", lesson: "Retry the refund", status: ExperienceStatus.Reinforced);

        foreach (var ineligible in new[]
        {
            ExperienceStatus.Candidate,
            ExperienceStatus.Quarantined,
            ExperienceStatus.Contested,
            ExperienceStatus.Stale,
            ExperienceStatus.Superseded,
            ExperienceStatus.Revoked,
        })
        {
            await SeedAsync(scope, taskId: "refund-ticket", summary: "Resolve a refund", lesson: "Retry the refund", status: ineligible);
        }

        var result = await SearchAsync(tenant, scope, "refund");

        Assert.Equal(
            new[] { validated, reinforced }.Order(),
            result.Candidates.Select(candidate => candidate.Record.ExperienceId).Order());
    }

    [Fact]
    public async Task A_status_change_committed_through_the_store_takes_a_record_out_of_the_eligible_set()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var id = await SeedAsync(scope, taskId: "refund-ticket", summary: "Resolve a refund", lesson: "Retry the refund", status: ExperienceStatus.Validated);

        Assert.Equal([id], (await SearchAsync(tenant, scope, "refund")).Candidates.Select(candidate => candidate.Record.ExperienceId));

        var commit = await _store.CommitLifecycleEventAsync(
            Authorize(tenant),
            scope,
            Event(id, ExperienceStatus.Validated, ExperienceStatus.Revoked, expectedRevision: 0, reason: "withdrawn", producer: "tests"),
            CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Committed, commit.Outcome);

        // The projection update is all it takes: the generated search vector still indexes the same
        // text, and the status predicate is what removes the record.
        Assert.Empty((await SearchAsync(tenant, scope, "refund")).Candidates);
    }

    [Fact]
    public async Task A_record_below_the_confidence_floor_is_filtered_in_SQL()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var above = await SeedAsync(scope, taskId: "refund-ticket", summary: "Resolve a refund", lesson: "Retry the refund", confidence: 0.5);
        var below = await SeedAsync(scope, taskId: "refund-ticket", summary: "Resolve a refund", lesson: "Retry the refund", confidence: 0.49);

        var result = await SearchAsync(tenant, scope, "refund", minimumConfidence: 0.5);

        // The floor is inclusive: a record exactly at the threshold is still a candidate.
        Assert.Equal([above], result.Candidates.Select(candidate => candidate.Record.ExperienceId));
        Assert.DoesNotContain(below, result.Candidates.Select(candidate => candidate.Record.ExperienceId));
    }

    [Fact]
    public async Task Relevance_is_normalized_positive_for_a_match_and_orders_the_candidates()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var strong = await SeedAsync(
            scope,
            taskId: "refund-policy-triage",
            summary: "Refund policy questions from customers",
            lesson: "Apply the refund policy once the ticket lock clears");
        var weak = await SeedAsync(
            scope,
            taskId: "deployment-review",
            summary: "Weekly deployment checklist for the cluster",
            lesson: "A refund may be needed when the policy changes after a rollout");

        var result = await SearchAsync(tenant, scope, "refund policy");

        Assert.Equal(2, result.Candidates.Count);
        Assert.All(result.Candidates, candidate => Assert.InRange(candidate.Relevance, 0d, 1d));
        Assert.All(result.Candidates, candidate => Assert.True(candidate.Relevance > 0d, "a matched record must report a positive relevance."));

        // Strongest match first, and the reported order is the relevance order.
        Assert.Equal([strong, weak], result.Candidates.Select(candidate => candidate.Record.ExperienceId));
        Assert.True(result.Candidates[0].Relevance > result.Candidates[1].Relevance);
    }

    [Fact]
    public async Task Relevance_stays_strictly_below_one_however_heavily_the_text_repeats_the_query()
    {
        // ts_rank_cd is unbounded above; only the normalization flag keeps it inside [0, 1). Without it a
        // document this saturated ranks far above 1, and the clamp in C# would hide that by reporting
        // exactly 1 -- so this asserts a value strictly below 1, which only the flag can produce.
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var repeated = string.Join(' ', Enumerable.Repeat("refund policy ticket", 200));
        await SeedAsync(scope, taskId: "refund-policy-ticket", summary: repeated, lesson: repeated);

        var result = await SearchAsync(tenant, scope, "refund policy ticket");

        var candidate = Assert.Single(result.Candidates);
        Assert.True(candidate.Relevance > 0d, "a saturated match must report a positive relevance.");
        Assert.True(
            candidate.Relevance < 1d,
            $"relevance must stay strictly below 1; ts_rank_cd's normalization is what bounds it, got {candidate.Relevance}.");
    }

    [Fact]
    public async Task The_limit_bounds_how_many_candidates_come_back()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        for (var i = 0; i < 5; i++)
        {
            await SeedAsync(scope, taskId: "refund-ticket", summary: "Resolve a refund", lesson: "Retry the refund");
        }

        var result = await SearchAsync(tenant, scope, "refund", limit: 2);

        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public async Task A_matched_candidate_decodes_into_the_full_canonical_record()
    {
        var tenant = NewTenant();
        var scope = new Scope(tenant, "app-1", "project-1", "team-1", "agent-1", "user-1");
        var record = Full(scope); // Validated, reuse confidence 2/3, task summary "Resolve refund ticket"
        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None)).Outcome);

        var result = await SearchAsync(tenant, scope, "refund ticket");

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(Canonical(record), Canonical(candidate.Record));
        Assert.Equal(record.UpdatedAt, candidate.Record.UpdatedAt);
    }

    [Fact]
    public async Task Search_text_that_looks_like_query_syntax_is_still_just_text()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var id = await SeedAsync(scope, taskId: "refund-ticket", summary: "Resolve a refund", lesson: "Retry the refund");

        // websearch_to_tsquery accepts arbitrary user text: quotes, operators, and punctuation are
        // parsed as a search, never as a syntax error the caller has to sanitize around.
        foreach (var text in new[] { "\"refund", "refund or", "refund -", "refund & ticket |", "((refund))" })
        {
            var result = await SearchAsync(tenant, scope, text);
            Assert.Equal(ExperienceStoreOutcome.Found, result.Outcome);
        }

        Assert.Equal([id], (await SearchAsync(tenant, scope, "\"refund\"")).Candidates.Select(candidate => candidate.Record.ExperienceId));
    }

    [Fact]
    public async Task Text_made_only_of_stopwords_matches_nothing_and_looks_like_nothing_relevant_exists()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        await SeedAsync(scope, taskId: "refund-ticket", summary: "Resolve a refund", lesson: "Retry the refund");

        // The english configuration drops stopwords, so this parses to an empty query, and an empty
        // query matches no row by construction -- reported as an ordinary Found with no candidates,
        // indistinguishable from "nothing relevant is stored".
        var stopwords = await SearchAsync(tenant, scope, "the of and");
        var nothingRelevant = await SearchAsync(tenant, scope, "kubernetes autoscaling");

        Assert.Equal(ExperienceStoreOutcome.Found, stopwords.Outcome);
        Assert.Empty(stopwords.Candidates);
        Assert.Equal(nothingRelevant.Outcome, stopwords.Outcome);
        Assert.Equal(nothingRelevant.Candidates.Count, stopwords.Candidates.Count);
    }

    [Fact]
    public async Task Core_retrieval_over_the_real_search_ranks_only_the_eligible_records()
    {
        // The full path: Core's service, the real SQL, and a real database.
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var reinforced = await SeedAsync(
            scope,
            taskId: "refund-ticket",
            summary: "Resolve a refund",
            lesson: "Retry the refund",
            status: ExperienceStatus.Reinforced,
            confidence: 0.9,
            metadata: new Dictionary<string, string> { ["region"] = "us-east" });
        var validated = await SeedAsync(
            scope,
            taskId: "refund-ticket",
            summary: "Resolve a refund",
            lesson: "Retry the refund",
            status: ExperienceStatus.Validated,
            confidence: 0.9,
            metadata: new Dictionary<string, string> { ["region"] = "us-east" });
        var wrongRegion = await SeedAsync(
            scope,
            taskId: "refund-ticket",
            summary: "Resolve a refund",
            lesson: "Retry the refund",
            status: ExperienceStatus.Validated,
            confidence: 0.9,
            metadata: new Dictionary<string, string> { ["region"] = "eu-west" });
        var quarantined = await SeedAsync(
            scope,
            taskId: "refund-ticket",
            summary: "Resolve a refund",
            lesson: "Retry the refund",
            status: ExperienceStatus.Quarantined,
            confidence: 0.9,
            metadata: new Dictionary<string, string> { ["region"] = "us-east" });
        var lowConfidence = await SeedAsync(
            scope,
            taskId: "refund-ticket",
            summary: "Resolve a refund",
            lesson: "Retry the refund",
            confidence: 0.1,
            metadata: new Dictionary<string, string> { ["region"] = "us-east" });

        var retrieval = new ExperienceRetrievalService(
            _source,
            RetrievalPolicy.Default with { Timeout = TimeSpan.FromSeconds(30) },
            RankingWeights.Default,
            TimeProvider.System);

        var result = await retrieval.RetrieveAsync(
            new RetrieveExperienceRequest(
                Authorize(tenant),
                scope,
                "refund ticket",
                new Dictionary<string, string> { ["region"] = "us-east" },
                CorrelationId: "corr-integration"),
            CancellationToken.None);

        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        Assert.Equal("corr-integration", result.CorrelationId);
        Assert.False(result.EnvironmentUnrestricted);

        // Equal on every other axis, so the reinforced record outranks the validated one.
        Assert.Equal([reinforced, validated], result.Records.Select(ranked => ranked.Record.ExperienceId));
        Assert.True(result.Records[0].Score > result.Records[1].Score);
        Assert.All(result.Records, ranked => Assert.Equal(5, ranked.Components.Count));

        // The environment check ran in Core, over what SQL returned; the rest never left the database.
        Assert.Equal(
            [new ExcludedExperience(wrongRegion, RetrievalExclusionReason.EnvironmentMismatch)],
            result.Excluded);
        Assert.DoesNotContain(quarantined, result.Records.Select(ranked => ranked.Record.ExperienceId));
        Assert.DoesNotContain(lowConfidence, result.Records.Select(ranked => ranked.Record.ExperienceId));
    }

    private Task<ExperienceCandidateSearchResult> SearchAsync(
        string tenant,
        Scope scope,
        string taskText,
        double minimumConfidence = 0d,
        int limit = ExperienceCandidateQuery.DefaultLimit) =>
        _source.SearchAsync(
            Authorize(tenant),
            new ExperienceCandidateQuery(
                scope,
                taskText,
                [ExperienceStatus.Validated, ExperienceStatus.Reinforced],
                minimumConfidence,
                limit),
            CancellationToken.None);

    /// <summary>Creates one searchable record and returns its ID.</summary>
    private async Task<Guid> SeedAsync(
        Scope scope,
        string taskId,
        string summary,
        string lesson,
        ExperienceStatus status = ExperienceStatus.Validated,
        double confidence = 0.75,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var runId = Guid.NewGuid();
        var record = Minimal(scope, status: status) with
        {
            SourceRunId = runId,
            TaskId = taskId,
            TaskSummary = summary,
            ReuseConfidence = confidence,
            Environment = new EnvironmentFingerprint("worker-01", "10.0.0", "linux-x64", null, metadata ?? new Dictionary<string, string>()),
            Reflection = new Reflection(
                Guid.NewGuid(),
                runId,
                lesson,
                [],
                [],
                [],
                [],
                null,
                [],
                TaskVerificationStatus.Verified,
                1,
                "v1",
                "tests",
                Now),
        };

        var created = await _store.CreateAsync(Authorize(scope.TenantId), record, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Created, created.Outcome);
        return record.ExperienceId;
    }
}

using AgentExperience.Core.Retrieval;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Integration tests for the SQL behind <see cref="PostgresExperienceCandidateSource"/>, against a
/// real PostgreSQL container: what the generated <c>search_vector</c> indexes, that scope, status,
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

        // Both contain every query lexeme, so both cover the query fully; ts_rank_cd, which rewards the lexemes
        // occurring close together, breaks the tie in favour of the record whose summary says "refund policy".
        Assert.Equal([strong, weak], result.Candidates.Select(candidate => candidate.Record.ExperienceId));
        Assert.All(result.Candidates, candidate => Assert.Equal(1d, candidate.Relevance));
    }

    [Fact]
    public async Task Relevance_is_the_share_of_the_query_s_lexemes_a_record_contains_however_often_it_repeats_them()
    {
        // Coverage, not density: a record repeating the query's words hundreds of times covers it no more than one
        // that says each once, and one with two of the three lexemes covers two thirds of it.
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var repeated = string.Join(' ', Enumerable.Repeat("refund policy ticket", 200));
        var saturated = await SeedAsync(scope, taskId: "refund-policy-ticket", summary: repeated, lesson: repeated);
        var twoOfThree = await SeedAsync(scope, taskId: "triage", summary: "Refund tickets pile up", lesson: "Escalate");

        var result = await SearchAsync(tenant, scope, "refund policy ticket", minimumMatchedTerms: 2);

        Assert.Equal([saturated, twoOfThree], result.Candidates.Select(candidate => candidate.Record.ExperienceId));
        Assert.Equal(1d, result.Candidates[0].Relevance);
        Assert.Equal((double)(2f / 3f), result.Candidates[1].Relevance);
    }

    [Fact]
    public async Task A_partial_match_at_the_minimum_is_a_candidate_ranked_below_a_full_one_and_one_below_it_is_not()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        const string Request =
            "after the deploy the refund worker hangs because the invoice export holds the ledger lock and nothing retries it";
        var full = await SeedAsync(
            scope, taskId: "t1", summary: "deploy refund worker hangs invoice export holds ledger lock nothing retries", lesson: "x");
        var three = await SeedAsync(scope, taskId: "t2", summary: "refund ledger lock", lesson: "x");
        var one = await SeedAsync(scope, taskId: "t3", summary: "ledger reconciliation", lesson: "x");
        await SeedAsync(scope, taskId: "t4", summary: "certificate rotation", lesson: "x");

        var result = await SearchAsync(tenant, scope, Request, minimumMatchedTerms: 2);
        Assert.Equal([full, three], result.Candidates.Select(candidate => candidate.Record.ExperienceId));
        Assert.Equal(1d, result.Candidates[0].Relevance);

        // Eleven lexemes once stopwords go; refund, ledger and lock are three of them, as a real.
        Assert.Equal((double)(3f / 11f), result.Candidates[1].Relevance);

        // At minimum 1 one shared lexeme is enough, and it ranks last.
        Assert.Equal(
            [full, three, one],
            (await SearchAsync(tenant, scope, Request, minimumMatchedTerms: 1)).Candidates.Select(candidate => candidate.Record.ExperienceId));
    }

    [Fact]
    public async Task A_hyphenated_compound_is_one_term_so_sharing_only_it_does_not_meet_a_minimum_of_two()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var id = await SeedAsync(scope, taskId: "image", summary: "Use a multi-stage build", lesson: "Copy only the output");

        // "multi-stage" also yields the lexemes "multi" and "stage"; counted as three it would pass a minimum of 2 alone.
        const string Request = "why does the multi-stage pipeline keep timing out on the agent";
        Assert.Empty((await SearchAsync(tenant, scope, Request, minimumMatchedTerms: 2)).Candidates);
        Assert.Equal([id], (await SearchAsync(tenant, scope, Request, minimumMatchedTerms: 1)).Candidates.Select(candidate => candidate.Record.ExperienceId));
    }

    [Fact]
    public async Task Full_matches_are_ordered_by_ts_rank_cd_against_the_AND_of_the_terms_as_before()
    {
        // Against the AND, b (the two terms close together, twice) outranks a; against the OR, a (five refunds) would
        // outrank b. Full matches keep the order every-terms matching gave them, at every minimum.
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var a = await SeedAsync(scope, taskId: "t1", summary: "refund refund refund refund refund policy", lesson: "x");
        var b = await SeedAsync(scope, taskId: "t2", summary: "refund policy x x x x x x x x x refund policy", lesson: "x");

        Assert.Equal(
            [b, a],
            (await SearchAsync(tenant, scope, "refund policy", minimumMatchedTerms: ExperienceCandidateQuery.AllTerms)).Candidates.Select(candidate => candidate.Record.ExperienceId));
        Assert.Equal([b, a], (await SearchAsync(tenant, scope, "refund policy")).Candidates.Select(candidate => candidate.Record.ExperienceId));
    }

    [Fact]
    public async Task With_every_term_required_only_full_matches_come_back_in_the_order_they_always_had()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var strong = await SeedAsync(scope, taskId: "refund-policy", summary: "Refund policy questions", lesson: "Apply the refund policy");
        var weak = await SeedAsync(scope, taskId: "review", summary: "Weekly review", lesson: "A refund may follow a policy change");
        await SeedAsync(scope, taskId: "refund", summary: "Refund", lesson: "Refund");

        var all = await SearchAsync(tenant, scope, "refunds policies", minimumMatchedTerms: ExperienceCandidateQuery.AllTerms);

        Assert.Equal([strong, weak], all.Candidates.Select(candidate => candidate.Record.ExperienceId));
        Assert.Equal(
            [strong, weak],
            (await SearchAsync(tenant, scope, "refunds policies", minimumMatchedTerms: 2)).Candidates.Select(candidate => candidate.Record.ExperienceId));
    }

    [Fact]
    public async Task The_minimum_is_capped_at_the_query_s_lexeme_count_so_a_short_query_still_matches()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var id = await SeedAsync(scope, taskId: "refund-ticket", summary: "Resolve a refund", lesson: "Retry the refund");

        Assert.Equal([id], (await SearchAsync(tenant, scope, "refunds", minimumMatchedTerms: 2)).Candidates.Select(candidate => candidate.Record.ExperienceId));
        Assert.Equal([id], (await SearchAsync(tenant, scope, "the refund", minimumMatchedTerms: ExperienceCandidateQuery.AllTerms)).Candidates.Select(candidate => candidate.Record.ExperienceId));
        Assert.Empty((await SearchAsync(tenant, scope, "refund invoice", minimumMatchedTerms: ExperienceCandidateQuery.AllTerms)).Candidates);
    }

    [Fact]
    public async Task A_minimum_below_one_is_invalid_before_any_connection_opens()
    {
        var tenant = NewTenant();
        var result = await SearchAsync(tenant, Scope(tenant), "refund", minimumMatchedTerms: 0);

        Assert.Equal(ExperienceStoreOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Errors, error => error.Path == "MinimumMatchedTerms");
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

        // The query's lexemes come from to_tsvector, and each goes into the tsquery as a quoted literal: quotes,
        // operators, backslashes and punctuation are text, never a syntax error the caller has to sanitize around.
        foreach (var text in new[]
        {
            "\"refund", "refund or", "refund -", "refund & ticket |", "((refund))", "refund:* <-> !ticket",
            "o'neil's refund", "C:\\refund\\ticket \\' '' \\\\", "\"\" -- || ' or & ! ( ) :*",
        })
        {
            var result = await SearchAsync(tenant, scope, text);
            Assert.Equal(ExperienceStoreOutcome.Found, result.Outcome);
        }

        Assert.Equal([id], (await SearchAsync(tenant, scope, "\"refund\"")).Candidates.Select(candidate => candidate.Record.ExperienceId));

        // "-ticket" negates nothing: it is the word "ticket", which the record has, so it counts towards the minimum.
        Assert.Equal(
            [id],
            (await SearchAsync(tenant, scope, "\"refund\" -ticket or invoice", minimumMatchedTerms: 2)).Candidates.Select(candidate => candidate.Record.ExperienceId));
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
        int limit = ExperienceCandidateQuery.DefaultLimit,
        int minimumMatchedTerms = ExperienceCandidateQuery.DefaultMinimumMatchedTerms) =>
        _source.SearchAsync(
            Authorize(tenant),
            new ExperienceCandidateQuery(
                scope,
                taskText,
                [ExperienceStatus.Validated, ExperienceStatus.Reinforced],
                minimumConfidence,
                limit)
            {
                MinimumMatchedTerms = minimumMatchedTerms,
            },
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

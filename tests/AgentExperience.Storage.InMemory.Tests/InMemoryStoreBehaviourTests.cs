using static AgentExperience.Storage.Conformance.ConformanceData;

namespace AgentExperience.Storage.InMemory.Tests;

/// <summary>
/// Story 11.2: what the in-memory stores do beyond the conformance suite -- the search rules this package states,
/// the confidence ledger's idempotency and independence rules, the validation shared with the PostgreSQL store, and
/// thread safety under concurrent use.
/// </summary>
public sealed class InMemoryStoreBehaviourTests
{
    private static readonly ExperienceStatus[] Eligible = [ExperienceStatus.Validated, ExperienceStatus.Reinforced];

    private readonly InMemoryExperienceRecordStore _store = new();

    // ---------------------------------------------------------------- search

    [Fact]
    public async Task A_term_found_only_in_the_task_id_or_the_lesson_still_matches_and_ranks_below_a_summary_hit()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var inSummary = await SeedAsync(tenant, scope, taskId: "t", summary: "invoice", lesson: "x");
        var inTaskId = await SeedAsync(tenant, scope, taskId: "invoice-check", summary: "other", lesson: "x");
        var inLesson = await SeedAsync(tenant, scope, taskId: "t", summary: "other", lesson: "Check the INVOICE first");
        await SeedAsync(tenant, scope, taskId: "t", summary: "invoices", lesson: "x"); // no stemming: not a match

        var result = await SearchAsync(tenant, scope, "invoice");

        Assert.Equal(inSummary, result.Candidates[0].Record.ExperienceId);
        Assert.Equal(new[] { inTaskId, inLesson }.Order(), result.Candidates.Skip(1).Select(c => c.Record.ExperienceId).Order());
        Assert.Equal(3, result.Candidates.Count);
        Assert.Equal(3d / 5d, result.Candidates[0].Relevance);
        Assert.All(result.Candidates.Skip(1), c => Assert.Equal(1d / 5d, c.Relevance));
    }

    [Fact]
    public async Task A_record_must_contain_every_query_term_and_relevance_is_the_weighted_fraction_of_them()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var everywhere = await SeedAsync(tenant, scope, taskId: "refund-policy", summary: "refund policy", lesson: "refund policy");
        var split = await SeedAsync(tenant, scope, taskId: "ticket", summary: "refund", lesson: "check the policy");
        await SeedAsync(tenant, scope, taskId: "refund", summary: "refund", lesson: "refund"); // "policy" missing: no match

        // Repeating a term in the query does not count it twice.
        var result = await SearchAsync(tenant, scope, "Refund, policy; REFUND");

        Assert.Equal([everywhere, split], result.Candidates.Select(c => c.Record.ExperienceId));
        Assert.Equal(1d, result.Candidates[0].Relevance);

        // refund: summary (3); policy: lesson (1); out of 2 terms x 5.
        Assert.Equal(4d / 10d, result.Candidates[1].Relevance);
    }

    [Fact]
    public async Task Stopwords_and_one_character_terms_are_dropped_and_a_query_left_with_none_matches_nothing()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var refund = await SeedAsync(tenant, scope, summary: "Resolve a refund");

        Assert.Equal([refund], (await SearchAsync(tenant, scope, "the refund of a x")).Candidates.Select(c => c.Record.ExperienceId));
        Assert.Empty((await SearchAsync(tenant, scope, "a")).Candidates);
        Assert.Empty((await SearchAsync(tenant, scope, "the and of to x")).Candidates);
        Assert.Empty((await SearchAsync(tenant, scope, "?! -- ...")).Candidates);
    }

    [Fact]
    public async Task Text_is_compared_after_Unicode_compatibility_normalization_and_case_folding()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var fullWidth = await SeedAsync(tenant, scope, taskId: "t1", summary: "ＲＥＦＵＮＤ requested", lesson: "none");
        var decomposed = await SeedAsync(tenant, scope, taskId: "t2", summary: "Cafe\u0301 opening", lesson: "none");

        Assert.Equal([fullWidth], (await SearchAsync(tenant, scope, "refund")).Candidates.Select(c => c.Record.ExperienceId));

        // A combining mark stays inside its word, and the composed and decomposed forms are one word.
        Assert.Equal([decomposed], (await SearchAsync(tenant, scope, "CAFÉ")).Candidates.Select(c => c.Record.ExperienceId));
        Assert.Empty((await SearchAsync(tenant, scope, "cafe")).Candidates);
    }

    [Fact]
    public async Task Only_the_first_100000_characters_of_a_record_s_text_are_indexed()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var padding = string.Join(' ', Enumerable.Repeat("padding", 100_000 / 8));
        var id = await SeedAsync(tenant, scope, taskId: "t", summary: "early " + padding + " late", lesson: "none");

        Assert.Equal([id], (await SearchAsync(tenant, scope, "early")).Candidates.Select(c => c.Record.ExperienceId));
        Assert.Empty((await SearchAsync(tenant, scope, "late")).Candidates);
    }

    [Fact]
    public async Task Equal_relevance_is_ordered_by_id_whatever_UpdatedAt_says()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var first = await SeedAsync(tenant, scope);
        var second = await SeedAsync(tenant, scope);

        // Commit to whichever sorts last, so an UpdatedAt tie-break would put it first.
        var last = new[] { first, second }.Max();
        Assert.Equal(
            ExperienceStoreOutcome.Committed,
            (await _store.CommitLifecycleEventAsync(
                Authorize(tenant), scope, Event(last, ExperienceStatus.Validated, ExperienceStatus.Reinforced, 0), CancellationToken.None)).Outcome);

        Assert.Equal(new[] { first, second }.Order(), (await SearchAsync(tenant, scope, "refund")).Candidates.Select(c => c.Record.ExperienceId));
    }

    // ---------------------------------------------------------------- snapshots and timestamps

    [Fact]
    public async Task A_stored_record_is_a_read_only_copy_the_caller_can_no_longer_change()
    {
        var tenant = NewTenant();
        var full = FullRecord(Scope(tenant));
        var attempts = full.Attempts.ToList();
        var arguments = new Dictionary<string, object?>(full.Attempts[0].ToolCalls[0].Arguments);
        var nested = new List<object?> { "a" };
        arguments["nested"] = nested;
        var metadata = new Dictionary<string, string>(full.Environment.Metadata);
        var warnings = full.Reflection!.Warnings.ToList();
        attempts[0] = attempts[0] with { ToolCalls = [attempts[0].ToolCalls[0] with { Arguments = arguments }, attempts[0].ToolCalls[1]] };
        var record = full with
        {
            Attempts = attempts,
            Environment = full.Environment with { Metadata = metadata },
            Reflection = full.Reflection with { Warnings = warnings },
        };
        await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None);
        var before = Json((await _store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None)).Record);

        attempts.Clear();
        arguments["query"] = "changed";
        nested.Add("b");
        metadata["region"] = "changed";
        warnings.Add("changed");

        var stored = (await _store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal(before, Json(stored));
        Assert.Throws<NotSupportedException>(() => ((IList<Attempt>)stored.Attempts).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, object?>)stored.Attempts[0].ToolCalls[0].Arguments).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<object?>)stored.Attempts[0].ToolCalls[0].Arguments["nested"]!).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)stored.Environment.Metadata).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)stored.Reflection!.Warnings).Clear());
    }

    [Fact]
    public async Task A_recorded_submission_is_a_read_only_copy_the_caller_can_no_longer_change()
    {
        var tenant = NewTenant();
        var original = AttributedFeedback(Scope(tenant));
        var exposures = original.Exposures.ToList();
        var evidenceIds = original.EvidenceIds.ToList();
        var store = new InMemoryExperienceReuseFeedbackStore();
        var recorded = await store.RecordAsync(Authorize(tenant), original with { Exposures = exposures, EvidenceIds = evidenceIds }, CancellationToken.None);

        exposures.Clear();
        evidenceIds.Add(Guid.NewGuid());

        Assert.Equal(Json(original), Json(recorded.Feedback));
        Assert.Throws<NotSupportedException>(() => ((IList<ExperienceReuseExposure>)recorded.Feedback!.Exposures).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<Guid>)recorded.Feedback!.EvidenceIds).Clear());
        Assert.Equal(ExperienceReuseFeedbackStoreOutcome.AlreadyRecorded, (await store.RecordAsync(Authorize(tenant), original, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Timestamps_are_stored_in_UTC_truncated_to_whole_microseconds_as_PostgreSQL_stores_them()
    {
        var tenant = NewTenant();
        var local = new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.FromHours(2)).AddTicks(1234561);
        var record = Record(Scope(tenant), createdAt: local);
        await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None);
        var committed = Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0) with { OccurredAt = local };

        Assert.Equal(ExperienceStoreOutcome.Committed, (await _store.CommitLifecycleEventAsync(Authorize(tenant), record.Scope, committed, CancellationToken.None)).Outcome);
        var stored = (await _store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None)).Record!;
        var history = await _store.GetFirstHistoryPageAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None);

        var expected = new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero).AddTicks(1234560);
        Assert.Equal(TimeSpan.Zero, stored.CreatedAt.Offset);
        Assert.Equal(expected.UtcTicks, stored.CreatedAt.UtcTicks);
        Assert.Equal(0, stored.UpdatedAt.UtcTicks % 10);
        Assert.Equal(expected.UtcTicks, Assert.Single(history.Events).Event.OccurredAt.UtcTicks);
        Assert.Equal(0, history.Events[0].RecordedAt.UtcTicks % 10);

        // A resubmission differing only below a microsecond is the same event, as it is in PostgreSQL.
        var replay = await _store.CommitLifecycleEventAsync(
            Authorize(tenant), record.Scope, committed with { OccurredAt = local.AddTicks(3) }, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Committed, replay.Outcome);
        Assert.Equal(1, replay.Revision);
    }

    // ---------------------------------------------------------------- confidence ledger

    [Fact]
    public async Task A_second_submission_for_a_counted_independence_key_is_recorded_but_moves_nothing()
    {
        var tenant = NewTenant();
        var record = await CreateAsync(tenant, Record(Scope(tenant), ExperienceStatus.Validated));
        var first = SupportingConfidence();

        var counted = await CommitAsync(tenant, record, first, expectedRevision: 0);
        Assert.Equal(ExperienceStoreOutcome.Committed, counted.Outcome);
        Assert.Equal(1, counted.Revision);
        Assert.True(counted.AppliedConfidence!.Counted);

        // The same reviewer and run under a fresh evidence ID: the key is taken, so it is recorded only.
        var again = first with
        {
            EvidenceId = Guid.NewGuid(),
            PriorReuseConfidence = 2d / 3d,
            NewReuseConfidence = 3d / 4d,
            PriorSupportingValidations = 1,
            NewSupportingValidations = 2,
        };
        var duplicate = await CommitAsync(tenant, record, again, expectedRevision: 1);

        Assert.Equal(ExperienceStoreOutcome.Committed, duplicate.Outcome);
        Assert.Equal(1, duplicate.Revision);
        Assert.Equal(ExperienceStatus.Validated, duplicate.CurrentStatus);
        Assert.False(duplicate.AppliedConfidence!.Counted);

        var stored = (await _store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal(1, stored.SupportingValidations);
        Assert.Equal(2d / 3d, stored.ReuseConfidence);
        Assert.Equal(1, stored.Revision);
        Assert.Single((await _store.GetFirstHistoryPageAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None)).Events);
    }

    [Fact]
    public async Task A_resubmitted_evidence_id_replays_the_original_and_a_changed_one_is_Conflict()
    {
        var tenant = NewTenant();
        var record = await CreateAsync(tenant, Record(Scope(tenant), ExperienceStatus.Validated));
        var confidence = SupportingConfidence();
        var lifecycleEvent = Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Validated, 0) with { Confidence = confidence };

        var original = await _store.CommitLifecycleEventAsync(Authorize(tenant), record.Scope, lifecycleEvent, CancellationToken.None);
        var replay = await _store.CommitLifecycleEventAsync(Authorize(tenant), record.Scope, lifecycleEvent, CancellationToken.None);
        var changed = await _store.CommitLifecycleEventAsync(
            Authorize(tenant),
            record.Scope,
            lifecycleEvent with { EventId = Guid.NewGuid(), ExpectedRevision = 1, Confidence = confidence with { RuleVersion = "v2" } },
            CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Committed, original.Outcome);
        Assert.Equal(ExperienceStoreOutcome.Committed, replay.Outcome);
        Assert.Equal(original.Revision, replay.Revision);
        Assert.Equal(confidence, replay.AppliedConfidence);
        Assert.Equal(ExperienceStoreOutcome.Conflict, changed.Outcome);
        Assert.Equal(1, (await _store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None)).Record!.Revision);
    }

    [Theory]
    [InlineData("rule-version")]
    [InlineData("kind")]
    [InlineData("detail")]
    [InlineData("assessment")]
    [InlineData("run")]
    [InlineData("reviewer")]
    public async Task The_same_event_and_evidence_ids_with_one_evidence_field_changed_is_Conflict_and_changes_nothing(string difference)
    {
        var tenant = NewTenant();
        var (record, original) = await CommitEvidenceThenMoveTheCountersAsync(tenant);
        var confidence = original.Confidence!;
        var changed = difference switch
        {
            "rule-version" => confidence with { RuleVersion = "v2" },
            "kind" => confidence with { Kind = ConfidenceEvidenceKind.Contradicting },
            "detail" => confidence with { Detail = "a different account of it" },
            "assessment" => confidence with { AssessmentId = Guid.NewGuid() },
            "run" => confidence with { RunId = Guid.NewGuid() },
            _ => confidence with { ReviewerIdentity = "another-reviewer" },
        };
        var before = Json((await _store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None)).Record);

        var result = await _store.CommitLifecycleEventAsync(Authorize(tenant), record.Scope, original with { Confidence = changed }, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Conflict, result.Outcome);
        Assert.Null(result.AppliedConfidence);
        Assert.Equal(before, Json((await _store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None)).Record));
        Assert.Equal(2, (await _store.GetFirstHistoryPageAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None)).Events.Count);
    }

    [Fact]
    public async Task A_true_replay_after_the_counters_moved_reports_the_numbers_the_original_was_stored_with()
    {
        var tenant = NewTenant();
        var (record, original) = await CommitEvidenceThenMoveTheCountersAsync(tenant);

        // A genuine retry recomputed against the record as it is now carries different counters; they are not compared.
        var recomputed = original with
        {
            Confidence = original.Confidence! with
            {
                PriorReuseConfidence = 3d / 4d,
                NewReuseConfidence = 4d / 5d,
                PriorSupportingValidations = 2,
                NewSupportingValidations = 3,
            },
        };

        var replay = await _store.CommitLifecycleEventAsync(Authorize(tenant), record.Scope, recomputed, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Committed, replay.Outcome);
        Assert.Equal(1, replay.Revision);
        Assert.Equal(original.Confidence, replay.AppliedConfidence);
        var stored = (await _store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal(2, stored.Revision);
        Assert.Equal(2, stored.SupportingValidations);
    }

    /// <summary>
    /// Commits a human supporting submission (0 to 1) with an assessment and a detail, then a second, independent one
    /// from another run (1 to 2), and returns the record and the first event.
    /// </summary>
    private async Task<(ExperienceRecord Record, LifecycleEvent Original)> CommitEvidenceThenMoveTheCountersAsync(string tenant)
    {
        var record = await CreateAsync(tenant, Record(Scope(tenant), ExperienceStatus.Validated));
        var original = Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Validated, 0) with
        {
            Confidence = SupportingConfidence() with { AssessmentId = Guid.NewGuid(), Detail = "the reviewer confirmed the fix held" },
        };
        Assert.Equal(ExperienceStoreOutcome.Committed, (await _store.CommitLifecycleEventAsync(Authorize(tenant), record.Scope, original, CancellationToken.None)).Outcome);

        var next = SupportingConfidence() with
        {
            PriorReuseConfidence = 2d / 3d,
            NewReuseConfidence = 3d / 4d,
            PriorSupportingValidations = 1,
            NewSupportingValidations = 2,
        };
        Assert.Equal(ExperienceStoreOutcome.Committed, (await CommitAsync(tenant, record, next, expectedRevision: 1)).Outcome);
        return (record, original);
    }

    [Fact]
    public async Task An_assessment_lands_evidence_at_most_once_per_record()
    {
        var tenant = NewTenant();
        var record = await CreateAsync(tenant, Record(Scope(tenant), ExperienceStatus.Validated));
        var assessment = Guid.NewGuid();
        var first = SupportingConfidence() with { AssessmentId = assessment };
        Assert.Equal(ExperienceStoreOutcome.Committed, (await CommitAsync(tenant, record, first, expectedRevision: 0)).Outcome);

        // The same assessment token under another evidence ID and another run: a replayed token.
        var replayedToken = SupportingConfidence() with
        {
            AssessmentId = assessment,
            PriorReuseConfidence = 2d / 3d,
            NewReuseConfidence = 3d / 4d,
            PriorSupportingValidations = 1,
            NewSupportingValidations = 2,
        };
        var refused = await CommitAsync(tenant, record, replayedToken, expectedRevision: 1);

        Assert.Equal(ExperienceStoreOutcome.Conflict, refused.Outcome);
        Assert.Equal(ConfidenceUpdate.AssessmentIdPath, Assert.Single(refused.Errors).Path);
        Assert.Equal(1, (await _store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None)).Record!.SupportingValidations);
    }

    // ---------------------------------------------------------------- shared validation

    [Fact]
    public async Task Validation_is_the_PostgreSQL_store_s_own_including_the_text_it_reserves_for_sealed_values()
    {
        var tenant = NewTenant();
        var record = await CreateAsync(tenant, Record(Scope(tenant)));

        var reservedReason = await _store.CommitLifecycleEventAsync(
            Authorize(tenant),
            record.Scope,
            Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0, reason: "aexp-sealed:v1:not really"),
            CancellationToken.None);
        var nulInTask = await _store.CreateAsync(Authorize(tenant), Record(Scope(tenant), taskId: "a\0b"), CancellationToken.None);
        var sealedRationale = await new InMemoryExperienceReuseFeedbackStore().RecordAsync(
            Authorize(tenant), AttributedFeedback(Scope(tenant)) with { Rationale = "(sealed)" }, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, reservedReason.Outcome);
        Assert.Equal("Reason", Assert.Single(reservedReason.Errors).Path);
        Assert.Equal(ExperienceStoreOutcome.Invalid, nulInTask.Outcome);
        Assert.Equal(ExperienceReuseFeedbackStoreOutcome.Invalid, sealedRationale.Outcome);
    }

    // ---------------------------------------------------------------- concurrency

    [Fact]
    public async Task Concurrent_creates_commits_searches_and_feedback_leave_every_store_consistent()
    {
        const int Workers = 32;
        const int RecordsPerWorker = 20;
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var source = new InMemoryExperienceCandidateSource(_store);
        var feedback = new InMemoryExperienceReuseFeedbackStore();
        var sharedFeedback = Feedback(scope, [Guid.NewGuid()]);
        using var start = new ManualResetEventSlim();

        var workers = Enumerable.Range(0, Workers).Select(worker => Task.Run(async () =>
        {
            start.Wait();
            var recorded = 0;
            for (var i = 0; i < RecordsPerWorker; i++)
            {
                var record = Record(scope, ExperienceStatus.Validated, summary: $"refund worker{worker} item{i}", confidence: 0.5);
                Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None)).Outcome);
                Assert.Equal(
                    ExperienceStoreOutcome.Committed,
                    (await _store.CommitLifecycleEventAsync(
                        Authorize(tenant), scope, Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Reinforced, 0), CancellationToken.None)).Outcome);
                Assert.Equal(
                    ExperienceStoreOutcome.Found,
                    (await source.SearchAsync(Authorize(tenant), new ExperienceCandidateQuery(scope, "refund", Eligible, 0d), CancellationToken.None)).Outcome);

                if ((await feedback.RecordAsync(Authorize(tenant), sharedFeedback, CancellationToken.None)).Outcome == ExperienceReuseFeedbackStoreOutcome.Recorded)
                {
                    recorded++;
                }
            }

            return recorded;
        })).ToArray();

        start.Set();
        var recordedPerWorker = await Task.WhenAll(workers);

        // One submission of the shared feedback won; every other was a replay.
        Assert.Equal(1, recordedPerWorker.Sum());

        var all = await _store.QueryAsync(Authorize(tenant), new ExperienceRecordQuery(scope, Limit: ExperienceRecordQuery.MaxLimit), CancellationToken.None);
        Assert.Equal(ExperienceRecordQuery.MaxLimit, all.Records.Count);
        Assert.All(all.Records, r =>
        {
            Assert.Equal(ExperienceStatus.Reinforced, r.Status);
            Assert.Equal(1, r.Revision);
        });

        var found = await source.SearchAsync(
            Authorize(tenant), new ExperienceCandidateQuery(scope, "refund", Eligible, 0d, ExperienceCandidateQuery.MaxLimit), CancellationToken.None);
        Assert.Equal(ExperienceCandidateQuery.MaxLimit, found.Candidates.Count);
        var distinct = await Task.WhenAll(Enumerable.Range(0, Workers).Select(worker => source.SearchAsync(
            Authorize(tenant), new ExperienceCandidateQuery(scope, $"worker{worker}", Eligible, 0d, ExperienceCandidateQuery.MaxLimit), CancellationToken.None)));
        Assert.All(distinct, result => Assert.Equal(RecordsPerWorker, result.Candidates.Count));
    }

    // ---------------------------------------------------------------- helpers

    private async Task<ExperienceRecord> CreateAsync(string tenant, ExperienceRecord record)
    {
        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None)).Outcome);
        return record;
    }

    private Task<ExperienceLifecycleCommitResult> CommitAsync(string tenant, ExperienceRecord record, ConfidenceUpdate confidence, long expectedRevision) =>
        _store.CommitLifecycleEventAsync(
            Authorize(tenant),
            record.Scope,
            Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Validated, expectedRevision) with { Confidence = confidence },
            CancellationToken.None);

    private Task<ExperienceCandidateSearchResult> SearchAsync(string tenant, Scope scope, string text) =>
        new InMemoryExperienceCandidateSource(_store).SearchAsync(
            Authorize(tenant), new ExperienceCandidateQuery(scope, text, Eligible, 0d), CancellationToken.None);

    private async Task<Guid> SeedAsync(
        string tenant,
        Scope scope,
        string taskId = "refund-ticket",
        string summary = "Resolve a refund",
        string lesson = "Retry the refund once the lock clears")
    {
        var record = Record(scope, ExperienceStatus.Validated, taskId: taskId, summary: summary, lesson: lesson, confidence: 0.75);
        await CreateAsync(tenant, record);
        return record.ExperienceId;
    }
}

using AgentExperience.Core.Retrieval;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Covers the vector half of <see cref="ExperienceRetrievalService"/>: one test per row of the
/// story's I/O and edge-case matrix that retrieval owns -- a hybrid match, a model mismatch, a
/// dimension mismatch, a vector channel that fails, and both channels empty -- plus the rules that
/// keep the merge honest: shared eligibility, dedupe by ID, highest normalized relevance wins, one
/// timeout over both channels, and no sixth ranking weight.
/// </summary>
public class HybridRetrievalTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly Scope RequestScope = new("tenant-1", "app-1", "project-1");

    private static readonly AuthorizationContext Authorization = new("tenant-1", "host-principal", ["experience:read"], Now);

    private const string TaskText = "resolve a refund ticket";

    // ---------------------------------------------------------------- matrix: hybrid match

    [Fact]
    public async Task Both_channels_contribute_and_the_result_is_deduplicated_and_ranked_once()
    {
        var shared = Record(Id(1));
        var textOnly = Record(Id(2));
        var vectorOnly = Record(Id(3));

        var service = Service(
            text: [Candidate(shared, 0.4), Candidate(textOnly, 0.3)],
            vector: [Candidate(shared, 0.9), Candidate(vectorOnly, 0.8)]);

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        Assert.False(result.TextOnly);
        Assert.Null(result.VectorFallback);
        Assert.Equal([Id(1), Id(2), Id(3)], result.Records.Select(r => r.Record.ExperienceId).Order());

        // Deduplicated by ID: the shared record is ranked exactly once, and on its *higher*
        // normalized relevance -- the vector channel's 0.9, not the text channel's 0.4.
        var ranked = result.Records.Single(r => r.Record.ExperienceId == Id(1));
        Assert.Equal(0.9, ranked.Components.Single(c => c.Kind == RankingComponentKind.Relevance).Value);
    }

    [Fact]
    public async Task A_record_only_the_vector_channel_found_is_still_ranked_on_all_five_components()
    {
        // Being found by meaning rather than by words changes nothing about how a record is scored:
        // there is no sixth axis and no "found semantically" bonus.
        var record = Record(Id(1), confidence: 0.8);
        var service = Service(text: [], vector: [Candidate(record, 0.75)]);

        var result = await service.RetrieveAsync(Request());

        var ranked = Assert.Single(result.Records);
        Assert.Equal(
            [
                RankingComponentKind.Relevance,
                RankingComponentKind.Confidence,
                RankingComponentKind.Recency,
                RankingComponentKind.Status,
                RankingComponentKind.EnvironmentCompatibility,
            ],
            ranked.Components.Select(component => component.Kind));
        Assert.Equal(5, ranked.Components.Count);
        Assert.Equal([0.75, 0.8, 1d, ExperienceRetrievalService.ValidatedStatusScore, 1d], ranked.Components.Select(c => c.Value));
        Assert.Equal(ranked.Components.Sum(c => c.Contribution), ranked.Score, 12);
    }

    [Fact]
    public async Task The_higher_normalized_relevance_wins_whichever_channel_it_came_from()
    {
        var record = Record(Id(1));
        var textWins = await Service(text: [Candidate(record, 0.95)], vector: [Candidate(record, 0.2)]).RetrieveAsync(Request());
        var vectorWins = await Service(text: [Candidate(record, 0.2)], vector: [Candidate(record, 0.95)]).RetrieveAsync(Request());

        Assert.Equal(0.95, Relevance(textWins));
        Assert.Equal(0.95, Relevance(vectorWins));
    }

    // ---------------------------------------------------------------- matrix: shared eligibility

    [Theory]
    [InlineData(ExperienceStatus.Candidate)]
    [InlineData(ExperienceStatus.Quarantined)]
    [InlineData(ExperienceStatus.Revoked)]
    public async Task An_ineligible_status_is_excluded_from_the_vector_channel_exactly_as_from_the_text_one(ExperienceStatus status)
    {
        var ineligible = Record(Id(1), status: status, confidence: 1d);
        var result = await Service(text: [], vector: [Candidate(ineligible, 1d)]).RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        Assert.Empty(result.Records);
        Assert.Equal(
            new ExcludedExperience(ineligible.ExperienceId, RetrievalExclusionReason.IneligibleStatus),
            Assert.Single(result.Excluded));
    }

    [Fact]
    public async Task The_vector_channel_is_asked_for_the_same_statuses_confidence_floor_and_ceiling_as_the_text_one()
    {
        var index = new FakeEmbeddingIndex();
        var policy = RetrievalPolicy.Default with { MinimumConfidence = 0.75, CandidateLimit = 7 };
        var source = new RecordingCandidateSource([]);
        var service = new ExperienceRetrievalService(
            source, policy, RankingWeights.Default, new FrozenClock(Now), index, new FakeEmbeddingGenerator());

        await service.RetrieveAsync(Request());

        var textQuery = Assert.Single(source.Queries);
        var vectorQuery = Assert.Single(index.Queries);

        Assert.Equal(textQuery.Scope, vectorQuery.Scope);
        Assert.Equal(textQuery.EligibleStatuses, vectorQuery.EligibleStatuses);
        Assert.Equal(textQuery.MinimumConfidence, vectorQuery.MinimumConfidence);
        Assert.Equal(textQuery.Limit, vectorQuery.Limit);
        Assert.Equal(8, vectorQuery.Limit);
        Assert.Equal("fake-embed-v1", vectorQuery.ModelId);
        Assert.Equal(FakeEmbeddingGenerator.VectorFor(TaskText, 4).ToArray(), vectorQuery.Vector.ToArray());
    }

    [Fact]
    public async Task A_record_the_expiry_or_environment_check_removes_is_excluded_whichever_channel_found_it()
    {
        var expired = Record(Id(1), updatedAt: Now - TimeSpan.FromDays(8));
        var mismatched = Record(Id(2), metadata: new Dictionary<string, string> { ["region"] = "eu-west" });
        var policy = RetrievalPolicy.Default with { MaxAge = TimeSpan.FromDays(7) };

        var result = await Service(
            text: [],
            vector: [Candidate(expired, 1d), Candidate(mismatched, 1d)],
            policy: policy)
            .RetrieveAsync(Request(required: new Dictionary<string, string> { ["region"] = "us-east" }));

        Assert.Empty(result.Records);
        Assert.Equal(
            [
                new ExcludedExperience(Id(1), RetrievalExclusionReason.Expired),
                new ExcludedExperience(Id(2), RetrievalExclusionReason.EnvironmentMismatch),
            ],
            result.Excluded);
    }

    // ---------------------------------------------------------------- matrix: model mismatch

    [Fact]
    public async Task A_model_mismatch_gives_an_explicit_text_only_result_and_keeps_the_text_candidates()
    {
        var record = Record(Id(1));
        var service = Service(
            text: [Candidate(record, 0.4)],
            onSearch: _ => new(ExperienceVectorSearchOutcome.ModelMismatch, [], []));

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        Assert.True(result.TextOnly);
        Assert.Equal(TextOnlyReason.ModelMismatch, result.VectorFallback!.Reason);
        Assert.Null(result.Failure);
        Assert.Equal(Id(1), Assert.Single(result.Records).Record.ExperienceId);
    }

    // ---------------------------------------------------------------- matrix: dimension mismatch

    [Fact]
    public async Task A_dimension_mismatch_gives_an_explicit_text_only_result()
    {
        var service = Service(
            text: [Candidate(Record(Id(1)), 0.4)],
            onSearch: _ => new(ExperienceVectorSearchOutcome.DimensionMismatch, [], []));

        var result = await service.RetrieveAsync(Request());

        Assert.True(result.TextOnly);
        Assert.Equal(TextOnlyReason.DimensionMismatch, result.VectorFallback!.Reason);
        Assert.Single(result.Records);
    }

    [Fact]
    public async Task A_provider_whose_query_vector_is_the_wrong_width_never_reaches_the_index_at_all()
    {
        // No incompatible comparison is attempted: the mismatch is caught before a query is issued.
        var index = new FakeEmbeddingIndex();
        var service = new ExperienceRetrievalService(
            new RecordingCandidateSource([Candidate(Record(Id(1)), 0.4)]),
            RetrievalPolicy.Default,
            RankingWeights.Default,
            new FrozenClock(Now),
            index,
            new FakeEmbeddingGenerator { ReturnDimension = 3 });

        var result = await service.RetrieveAsync(Request());

        Assert.True(result.TextOnly);
        Assert.Equal(TextOnlyReason.ProviderUnavailable, result.VectorFallback!.Reason);
        Assert.Empty(index.Queries);
        Assert.Single(result.Records);
    }

    // ---------------------------------------------------------------- matrix: vector channel fails

    [Fact]
    public async Task A_provider_that_throws_gives_a_text_only_result_flagged_with_the_provider_as_the_reason()
    {
        var service = Service(
            text: [Candidate(Record(Id(1)), 0.4)],
            generator: new FakeEmbeddingGenerator { Throws = FakeEmbeddingGenerator.ThrownException });

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        Assert.True(result.TextOnly);
        Assert.Equal(TextOnlyReason.ProviderUnavailable, result.VectorFallback!.Reason);
        Assert.Same(FakeEmbeddingGenerator.ThrownException, result.VectorFallback.Exception);
        Assert.Single(result.Records);
    }

    [Fact]
    public async Task A_vector_search_that_throws_gives_a_text_only_result_and_the_text_candidates_still_come_back()
    {
        var index = new FakeEmbeddingIndex { SearchThrows = FakeEmbeddingIndex.ThrownException };
        var service = new ExperienceRetrievalService(
            new RecordingCandidateSource([Candidate(Record(Id(1)), 0.4), Candidate(Record(Id(2)), 0.3)]),
            RetrievalPolicy.Default,
            RankingWeights.Default,
            new FrozenClock(Now),
            index,
            new FakeEmbeddingGenerator());

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        Assert.True(result.TextOnly);
        Assert.Equal(TextOnlyReason.VectorSearchFailed, result.VectorFallback!.Reason);
        Assert.Same(FakeEmbeddingIndex.ThrownException, result.VectorFallback.Exception);
        Assert.Equal(2, result.Records.Count);
    }

    [Theory]
    [InlineData(ExperienceVectorSearchOutcome.Denied)]
    [InlineData(ExperienceVectorSearchOutcome.Invalid)]
    public async Task A_refused_vector_search_is_a_text_only_fallback_not_a_failed_retrieval(ExperienceVectorSearchOutcome outcome)
    {
        var service = Service(
            text: [Candidate(Record(Id(1)), 0.4)],
            onSearch: _ => new(outcome, [], []));

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        Assert.Equal(TextOnlyReason.VectorSearchFailed, result.VectorFallback!.Reason);
        Assert.Single(result.Records);
    }

    [Fact]
    public async Task A_text_channel_failure_still_ends_the_call_even_when_the_vector_channel_answered()
    {
        // The vector channel cannot stand in for the text one: answering from vectors alone would be
        // a result the caller never asked for.
        var index = new FakeEmbeddingIndex { OnSearch = _ => new(ExperienceVectorSearchOutcome.Found, [Candidate(Record(Id(1)), 0.9)], []) };
        var service = new ExperienceRetrievalService(
            new RecordingCandidateSource((_, _) => throw new InvalidOperationException("boom")),
            RetrievalPolicy.Default,
            RankingWeights.Default,
            new FrozenClock(Now),
            index,
            new FakeEmbeddingGenerator());

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Failed, result.Outcome);
        Assert.Empty(result.Records);
        Assert.NotNull(result.Failure);
    }

    // ---------------------------------------------------------------- matrix: both channels empty

    [Fact]
    public async Task Nothing_matching_either_channel_is_a_completed_empty_result_not_a_failure()
    {
        var result = await Service(text: [], vector: []).RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        Assert.Empty(result.Records);
        Assert.Empty(result.Excluded);
        Assert.Null(result.Failure);
        Assert.False(result.TextOnly);
        Assert.False(result.TimedOut);
    }

    // ---------------------------------------------------------------- fail-closed, both channels

    [Fact]
    public async Task A_vector_candidate_outside_the_requested_scope_empties_the_whole_result()
    {
        var foreign = Record(Id(1), scope: new Scope("tenant-1", "app-1", "other-project"));
        var result = await Service(text: [Candidate(Record(Id(2)), 0.4)], vector: [Candidate(foreign, 0.9)]).RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Failed, result.Outcome);
        Assert.Empty(result.Records);
        Assert.Contains("embedding index", result.Failure!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_record_twice_from_one_channel_is_still_fail_closed()
    {
        var record = Record(Id(1));
        var result = await Service(text: [], vector: [Candidate(record, 0.9), Candidate(record, 0.8)]).RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Failed, result.Outcome);
        Assert.Contains("more than once", result.Failure!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreadable_vector_candidate_empties_the_whole_result()
    {
        var result = await Service(text: [], vector: [new ExperienceCandidate(null!, 0.9)]).RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Failed, result.Outcome);
        Assert.Contains("could not be read", result.Failure!.Reason, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- bounds shared by both channels

    [Fact]
    public async Task Either_channel_reaching_the_ceiling_marks_the_result_truncated()
    {
        var policy = RetrievalPolicy.Default with { CandidateLimit = 2 };
        var overflowing = Enumerable.Range(1, 3).Select(n => Candidate(Record(Id(n)), 0.5)).ToArray();

        var fromVector = await Service(text: [], vector: overflowing, policy: policy).RetrieveAsync(Request(limit: 2));
        var fromText = await Service(text: overflowing, vector: [], policy: policy).RetrieveAsync(Request(limit: 2));
        var neither = await Service(text: [overflowing[0]], vector: [overflowing[1]], policy: policy).RetrieveAsync(Request(limit: 2));

        Assert.True(fromVector.Truncated);
        Assert.True(fromText.Truncated);
        Assert.False(neither.Truncated);

        // The probe candidate past the ceiling is never ranked, in either channel.
        Assert.Equal(2, fromVector.Records.Count);
        Assert.DoesNotContain(Id(3), fromVector.Excluded.Select(e => e.ExperienceId));
    }

    [Fact]
    public async Task The_whole_hybrid_call_is_bounded_by_the_one_timeout_and_a_hanging_provider_is_not_an_exception()
    {
        var clock = new ManualClock(Now);
        var gate = new TaskCompletionSource();
        var service = new ExperienceRetrievalService(
            new RecordingCandidateSource([Candidate(Record(Id(1)), 0.4)]),
            RetrievalPolicy.Default,
            RankingWeights.Default,
            clock,
            new FakeEmbeddingIndex(),
            new FakeEmbeddingGenerator { Gate = gate });

        var retrieval = service.RetrieveAsync(Request(correlationId: "trace-1"));
        clock.Advance(RetrievalPolicy.DefaultTimeout + TimeSpan.FromMilliseconds(1));
        var result = await retrieval;

        Assert.Equal(RetrievalOutcome.TimedOut, result.Outcome);
        Assert.True(result.TimedOut);
        Assert.Equal("trace-1", result.CorrelationId);
        Assert.Empty(result.Records);
        gate.TrySetResult();
    }

    // ---------------------------------------------------------------- no vector channel at all

    [Fact]
    public async Task A_deployment_with_no_vector_channel_says_so_explicitly_on_every_result()
    {
        var service = new ExperienceRetrievalService(
            new RecordingCandidateSource([Candidate(Record(Id(1)), 0.4)]),
            RetrievalPolicy.Default,
            RankingWeights.Default,
            new FrozenClock(Now));

        var result = await service.RetrieveAsync(Request());

        Assert.False(service.HybridEnabled);
        Assert.True(result.TextOnly);
        Assert.Equal(TextOnlyReason.NotConfigured, result.VectorFallback!.Reason);
        Assert.Single(result.Records);
    }

    [Fact]
    public async Task Half_a_vector_channel_is_no_vector_channel()
    {
        // An index with no generator (or the reverse) cannot produce a comparison, so it is the
        // not-configured fallback rather than a failure reported on every single call.
        var indexOnly = new ExperienceRetrievalService(
            new RecordingCandidateSource([]), RetrievalPolicy.Default, RankingWeights.Default, new FrozenClock(Now),
            new FakeEmbeddingIndex(), embeddingGenerator: null);
        var generatorOnly = new ExperienceRetrievalService(
            new RecordingCandidateSource([]), RetrievalPolicy.Default, RankingWeights.Default, new FrozenClock(Now),
            embeddingIndex: null, new FakeEmbeddingGenerator());

        Assert.False(indexOnly.HybridEnabled);
        Assert.False(generatorOnly.HybridEnabled);
        Assert.Equal(TextOnlyReason.NotConfigured, (await indexOnly.RetrieveAsync(Request())).VectorFallback!.Reason);
        Assert.Equal(TextOnlyReason.NotConfigured, (await generatorOnly.RetrieveAsync(Request())).VectorFallback!.Reason);
    }

    [Fact]
    public async Task A_denied_scope_never_reaches_either_channel()
    {
        var index = new FakeEmbeddingIndex();
        var source = new RecordingCandidateSource([]);
        var generator = new FakeEmbeddingGenerator();
        var service = new ExperienceRetrievalService(
            source, RetrievalPolicy.Default, RankingWeights.Default, new FrozenClock(Now), index, generator);

        var result = await service.RetrieveAsync(new RetrieveExperienceRequest(
            new AuthorizationContext("tenant-1", "p", [], Now, ProjectId: "elsewhere"), RequestScope, TaskText));

        Assert.Equal(RetrievalOutcome.Denied, result.Outcome);
        Assert.Empty(source.Queries);
        Assert.Empty(index.Queries);
        Assert.Empty(generator.Requests);
    }

    [Fact]
    public async Task A_provider_that_cancels_for_its_own_reasons_is_a_fallback_not_a_failed_retrieval()
    {
        // An HttpClient request timeout surfaces as a TaskCanceledException with the caller's token
        // untouched. Letting that escape would turn every retrieval against a merely slow provider into
        // Failed with no records -- exactly the answer the text-only fallback exists to prevent.
        var service = Service(
            text: [Candidate(Record(Id(1)), 0.4)],
            generator: new FakeEmbeddingGenerator { Throws = new TaskCanceledException("provider request timeout") });

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        Assert.Equal(TextOnlyReason.ProviderUnavailable, result.VectorFallback!.Reason);
        Assert.Equal(Id(1), Assert.Single(result.Records).Record.ExperienceId);
    }

    [Fact]
    public async Task A_vector_search_that_cancels_for_its_own_reasons_is_a_fallback_too()
    {
        var index = new FakeEmbeddingIndex { SearchThrows = new OperationCanceledException("index-side timeout") };
        var service = new ExperienceRetrievalService(
            new RecordingCandidateSource([Candidate(Record(Id(1)), 0.4)]),
            RetrievalPolicy.Default,
            RankingWeights.Default,
            new FrozenClock(Now),
            index,
            new FakeEmbeddingGenerator());

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        Assert.Equal(TextOnlyReason.VectorSearchFailed, result.VectorFallback!.Reason);
        Assert.Single(result.Records);
    }

    [Fact]
    public async Task The_callers_own_cancellation_still_propagates_from_the_vector_channel()
    {
        using var cancellation = new CancellationTokenSource();
        var gate = new TaskCompletionSource();
        var service = Service(text: [], generator: new FakeEmbeddingGenerator { Gate = gate });

        var retrieval = service.RetrieveAsync(Request(), cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retrieval);
        gate.TrySetResult();
    }

    [Fact]
    public async Task A_non_finite_query_vector_falls_back_without_a_database_round_trip()
    {
        var index = new FakeEmbeddingIndex();
        var service = new ExperienceRetrievalService(
            new RecordingCandidateSource([Candidate(Record(Id(1)), 0.4)]),
            RetrievalPolicy.Default,
            RankingWeights.Default,
            new FrozenClock(Now),
            index,
            new FakeEmbeddingGenerator { ReturnNonFinite = true });

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(TextOnlyReason.ProviderUnavailable, result.VectorFallback!.Reason);
        Assert.Empty(index.Queries);
        Assert.Single(result.Records);
    }

    [Fact]
    public async Task The_merge_keeps_the_first_channels_record_snapshot_and_only_raises_the_relevance()
    {
        // The two channels read the record at different instants. Taking the other snapshot because it
        // scored higher would let eligibility be decided on the staler of the two reads.
        var textSnapshot = Record(Id(1), status: ExperienceStatus.Validated, confidence: 0.9);
        var vectorSnapshot = Record(Id(1), status: ExperienceStatus.Revoked, confidence: 0.1);

        var result = await Service(text: [Candidate(textSnapshot, 0.2)], vector: [Candidate(vectorSnapshot, 0.95)])
            .RetrieveAsync(Request());

        var ranked = Assert.Single(result.Records);
        Assert.Same(textSnapshot, ranked.Record);
        Assert.Equal(0.95, ranked.Components.Single(c => c.Kind == RankingComponentKind.Relevance).Value);
        Assert.Equal(0.9, ranked.Components.Single(c => c.Kind == RankingComponentKind.Confidence).Value);
    }

    // ---------------------------------------------------------------- the flag on every outcome

    [Fact]
    public async Task A_denied_or_timed_out_result_still_says_whether_a_vector_channel_exists_at_all()
    {
        var elsewhere = new AuthorizationContext("tenant-1", "p", [], Now, ProjectId: "elsewhere");
        var deniedRequest = new RetrieveExperienceRequest(elsewhere, RequestScope, TaskText);

        var textOnlyDenied = await TextOnlyService().RetrieveAsync(deniedRequest);
        var hybridDenied = await Service(text: [], vector: []).RetrieveAsync(deniedRequest);

        Assert.Equal(RetrievalOutcome.Denied, textOnlyDenied.Outcome);
        Assert.True(textOnlyDenied.TextOnly);
        Assert.Equal(TextOnlyReason.NotConfigured, textOnlyDenied.VectorFallback!.Reason);

        // A wired-up channel simply did not contribute, and the Denied outcome already says why.
        Assert.Equal(RetrievalOutcome.Denied, hybridDenied.Outcome);
        Assert.False(hybridDenied.TextOnly);
        Assert.Null(hybridDenied.VectorFallback);

        var textOnlyTimedOut = await TimingOutService(hybrid: false);
        var hybridTimedOut = await TimingOutService(hybrid: true);

        Assert.Equal(RetrievalOutcome.TimedOut, textOnlyTimedOut.Outcome);
        Assert.Equal(TextOnlyReason.NotConfigured, textOnlyTimedOut.VectorFallback!.Reason);
        Assert.Equal(RetrievalOutcome.TimedOut, hybridTimedOut.Outcome);
        Assert.Null(hybridTimedOut.VectorFallback);
    }

    [Fact]
    public void The_ranking_weights_still_have_exactly_five_axes()
    {
        Assert.Equal(5, Enum.GetValues<RankingComponentKind>().Length);
        var weights = RankingWeights.Default;
        Assert.Equal(1d, weights.Relevance + weights.Confidence + weights.Recency + weights.Status + weights.EnvironmentCompatibility, 12);
    }

    // ---------------------------------------------------------------- helpers

    private static Guid Id(int n) => Guid.Parse(FormattableString.Invariant($"00000000-0000-0000-0000-{n:000000000000}"));

    private static double Relevance(ExperienceRetrievalResult result) =>
        Assert.Single(result.Records).Components.Single(c => c.Kind == RankingComponentKind.Relevance).Value;

    private static ExperienceCandidate Candidate(ExperienceRecord record, double relevance) => new(record, relevance);

    private static ExperienceRetrievalService TextOnlyService() => new(
        new RecordingCandidateSource([Candidate(Record(Id(1)), 0.4)]),
        RetrievalPolicy.Default,
        RankingWeights.Default,
        new FrozenClock(Now));

    /// <summary>
    /// Drives a retrieval to its timeout, hybrid or not. The text channel is what blocks in both
    /// cases, so the two differ only in whether a vector channel is wired in at all -- which is exactly
    /// what the timed-out result has to keep reporting.
    /// </summary>
    private static async Task<ExperienceRetrievalResult> TimingOutService(bool hybrid)
    {
        var clock = new ManualClock(Now);
        var blocked = new RecordingCandidateSource(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new ExperienceCandidateSearchResult(ExperienceStoreOutcome.Found, [], []);
        });

        var service = new ExperienceRetrievalService(
            blocked,
            RetrievalPolicy.Default,
            RankingWeights.Default,
            clock,
            hybrid ? new FakeEmbeddingIndex() : null,
            hybrid ? new FakeEmbeddingGenerator() : null);

        var retrieval = service.RetrieveAsync(Request());
        clock.Advance(RetrievalPolicy.DefaultTimeout + TimeSpan.FromMilliseconds(1));
        return await retrieval;
    }

    private static RetrieveExperienceRequest Request(
        IReadOnlyDictionary<string, string>? required = null,
        string? correlationId = null,
        int? limit = null) => new(Authorization, RequestScope, TaskText, required, correlationId, limit);

    private static ExperienceRetrievalService Service(
        IReadOnlyList<ExperienceCandidate> text,
        IReadOnlyList<ExperienceCandidate>? vector = null,
        Func<ExperienceVectorQuery, ExperienceVectorSearchResult>? onSearch = null,
        FakeEmbeddingGenerator? generator = null,
        RetrievalPolicy? policy = null) => new(
            new RecordingCandidateSource(text),
            policy ?? RetrievalPolicy.Default,
            RankingWeights.Default,
            new FrozenClock(Now),
            new FakeEmbeddingIndex
            {
                OnSearch = onSearch ?? (_ => new(ExperienceVectorSearchOutcome.Found, vector ?? [], [])),
            },
            generator ?? new FakeEmbeddingGenerator());

    private static ExperienceRecord Record(
        Guid id,
        Scope? scope = null,
        ExperienceStatus status = ExperienceStatus.Validated,
        double confidence = 0.5,
        DateTimeOffset? updatedAt = null,
        IReadOnlyDictionary<string, string>? metadata = null) => new(
            ExperienceId: id,
            SourceRunId: Guid.NewGuid(),
            Scope: scope ?? RequestScope,
            TaskId: "refund-ticket",
            TaskSummary: "Resolve a refund ticket",
            Attempts: [],
            Outcome: new Outcome(TaskVerificationStatus.Verified, [], "checks passed", Now),
            CompletionScore: 1,
            Reflection: null,
            Environment: new EnvironmentFingerprint("worker-01", "10.0.0", "linux-x64", null, metadata ?? new Dictionary<string, string>()),
            Provenance: new Provenance("tests", null, Now, null),
            Status: status,
            ReuseConfidence: confidence,
            SupportingValidations: 1,
            Contradictions: 0,
            Revision: 1,
            CreatedAt: updatedAt ?? Now,
            UpdatedAt: updatedAt ?? Now);

    /// <summary>A text candidate source that answers with whatever the test scripted, recording what it was asked.</summary>
    private sealed class RecordingCandidateSource(
        Func<ExperienceCandidateQuery, CancellationToken, Task<ExperienceCandidateSearchResult>> onSearch)
        : IExperienceCandidateSource
    {
        public RecordingCandidateSource(IReadOnlyList<ExperienceCandidate> candidates)
            : this((_, _) => Task.FromResult(new ExperienceCandidateSearchResult(ExperienceStoreOutcome.Found, candidates, [])))
        {
        }

        public List<ExperienceCandidateQuery> Queries { get; } = [];

        public Task<ExperienceCandidateSearchResult> SearchAsync(
            AuthorizationContext authorization,
            ExperienceCandidateQuery query,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(authorization);
            lock (Queries)
            {
                Queries.Add(query);
            }

            return onSearch(query, cancellationToken);
        }
    }

    /// <summary>A clock whose timers fire only when the test advances it, so a timeout is deterministic.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public override long GetTimestamp() => _now.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            lock (_timers)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
            ManualTimer[] due;
            lock (_timers)
            {
                due = [.. _timers];
            }

            foreach (var timer in due)
            {
                timer.MaybeFire(by);
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            private TimeSpan _remaining = dueTime;
            private bool _fired;

            public bool Change(TimeSpan due, TimeSpan period)
            {
                _remaining = due;
                return true;
            }

            public void MaybeFire(TimeSpan elapsed)
            {
                if (_fired || _remaining == Timeout.InfiniteTimeSpan)
                {
                    return;
                }

                _remaining -= elapsed;
                if (_remaining <= TimeSpan.Zero)
                {
                    _fired = true;
                    callback(state);
                }
            }

            public void Dispose() => _fired = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}

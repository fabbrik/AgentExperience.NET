using System.Globalization;
using System.Text;
using AgentExperience.Core.Retrieval;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Covers <see cref="ExperienceRetrievalService"/> against a fake <see cref="IExperienceCandidateSource"/>:
/// one test per row of the story's I/O and edge-case matrix, a golden fixture pinning the documented
/// default ordering together with every component value and effective weight, and the validation that
/// keeps an impossible weighting from ever reaching a retrieval call.
/// </summary>
public class ExperienceRetrievalServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly Scope RequestScope = new("tenant-1", "app-1", "project-1");

    private static readonly AuthorizationContext Authorization = new("tenant-1", "host-principal", ["experience:read"], Now);

    private const string TaskText = "resolve a refund ticket";

    // ---------------------------------------------------------------- matrix: relevant match

    [Fact]
    public async Task Matching_records_come_back_ranked_with_every_component_and_its_effective_weight()
    {
        var record = Record(Id(1), confidence: 0.8, updatedAt: Now);
        var service = Service(Found(new ExperienceCandidate(record, 0.6)));

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        Assert.False(result.TimedOut);
        Assert.Null(result.Failure);
        var ranked = Assert.Single(result.Records);
        Assert.Same(record, ranked.Record);

        Assert.Equal(
            [
                RankingComponentKind.Relevance,
                RankingComponentKind.Confidence,
                RankingComponentKind.Recency,
                RankingComponentKind.Status,
                RankingComponentKind.EnvironmentCompatibility,
            ],
            ranked.Components.Select(component => component.Kind));

        var weights = RankingWeights.Default;
        Assert.Equal(
            [weights.Relevance, weights.Confidence, weights.Recency, weights.Status, weights.EnvironmentCompatibility],
            ranked.Components.Select(component => component.Weight));

        Assert.Equal([0.6, 0.8, 1d, ExperienceRetrievalService.ValidatedStatusScore, 1d], ranked.Components.Select(component => component.Value));
        Assert.Equal(ranked.Components.Sum(component => component.Contribution), ranked.Score, 12);
        Assert.All(ranked.Components, component => Assert.InRange(component.Value, 0d, 1d));
    }

    // ---------------------------------------------------------------- matrix: ineligible status

    [Theory]
    [InlineData(ExperienceStatus.Candidate)]
    [InlineData(ExperienceStatus.Quarantined)]
    [InlineData(ExperienceStatus.Contested)]
    [InlineData(ExperienceStatus.Stale)]
    [InlineData(ExperienceStatus.Superseded)]
    [InlineData(ExperienceStatus.Revoked)]
    public async Task An_ineligible_status_is_excluded_before_ranking_whatever_its_text_match(ExperienceStatus status)
    {
        // A perfect text match and full confidence: only the status keeps it out.
        var ineligible = Record(Id(1), status: status, confidence: 1d, updatedAt: Now);
        var service = Service(Found(new ExperienceCandidate(ineligible, 1d)));

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        Assert.Empty(result.Records);
        var excluded = Assert.Single(result.Excluded);
        Assert.Equal(new ExcludedExperience(ineligible.ExperienceId, RetrievalExclusionReason.IneligibleStatus), excluded);
    }

    [Fact]
    public void Only_Validated_and_Reinforced_are_ever_eligible()
    {
        Assert.Equal([ExperienceStatus.Validated, ExperienceStatus.Reinforced], ExperienceRetrievalService.EligibleStatuses);
    }

    // ---------------------------------------------------------------- matrix: low confidence

    [Fact]
    public async Task The_confidence_threshold_and_the_eligible_statuses_are_pushed_into_the_search_not_applied_afterwards()
    {
        // The confidence floor is a database predicate: what never comes back is never scored, and a
        // candidate source is never asked to return records Core would only throw away.
        var source = new FakeCandidateSource(Found());
        var policy = RetrievalPolicy.Default with { MinimumConfidence = 0.75, CandidateLimit = 7 };
        var service = new ExperienceRetrievalService(source, policy, RankingWeights.Default, new FixedTimeProvider(Now));

        await service.RetrieveAsync(Request());

        var query = Assert.Single(source.Queries);
        Assert.Equal(0.75, query.MinimumConfidence);

        // One past the ceiling, so the service can tell "exactly 7 matched" from "more than 7 matched".
        Assert.Equal(8, query.Limit);
        Assert.Equal(RequestScope, query.Scope);
        Assert.Equal(TaskText, query.TaskText);
        Assert.Equal([ExperienceStatus.Validated, ExperienceStatus.Reinforced], query.EligibleStatuses);
        Assert.Equal(0.5, RetrievalPolicy.DefaultMinimumConfidence);
    }

    // ---------------------------------------------------------------- matrix: expired

    [Fact]
    public async Task A_record_older_than_MaxAge_is_excluded_in_Core()
    {
        var fresh = Record(Id(1), updatedAt: Now - TimeSpan.FromDays(6));
        var expired = Record(Id(2), updatedAt: Now - TimeSpan.FromDays(8));
        var service = Service(
            Found(new ExperienceCandidate(fresh, 1d), new ExperienceCandidate(expired, 1d)),
            policy: RetrievalPolicy.Default with { MaxAge = TimeSpan.FromDays(7) });

        var result = await service.RetrieveAsync(Request());

        Assert.Equal([fresh.ExperienceId], result.Records.Select(ranked => ranked.Record.ExperienceId));
        Assert.Equal(
            [new ExcludedExperience(expired.ExperienceId, RetrievalExclusionReason.Expired)],
            result.Excluded);
    }

    [Fact]
    public async Task A_null_MaxAge_means_no_expiry_at_all()
    {
        var ancient = Record(Id(1), updatedAt: Now - TimeSpan.FromDays(4000));
        var service = Service(Found(new ExperienceCandidate(ancient, 1d)));

        var result = await service.RetrieveAsync(Request());

        Assert.Null(RetrievalPolicy.Default.MaxAge);
        Assert.Equal([ancient.ExperienceId], result.Records.Select(ranked => ranked.Record.ExperienceId));
        Assert.Empty(result.Excluded);
    }

    // ---------------------------------------------------------------- matrix: environment mismatch

    [Fact]
    public async Task A_differing_or_missing_required_environment_attribute_excludes_the_record_and_the_result_names_the_check()
    {
        var matching = Record(Id(1), metadata: new Dictionary<string, string> { ["region"] = "us-east", ["tier"] = "prod" });
        var differing = Record(Id(2), metadata: new Dictionary<string, string> { ["region"] = "eu-west" });
        var missingKey = Record(Id(3), metadata: new Dictionary<string, string> { ["tier"] = "prod" });
        var caseDiffering = Record(Id(4), metadata: new Dictionary<string, string> { ["region"] = "US-EAST" });

        var service = Service(Found(
            new ExperienceCandidate(matching, 1d),
            new ExperienceCandidate(differing, 1d),
            new ExperienceCandidate(missingKey, 1d),
            new ExperienceCandidate(caseDiffering, 1d)));

        var result = await service.RetrieveAsync(
            Request(required: new Dictionary<string, string> { ["region"] = "us-east" }));

        Assert.False(result.EnvironmentUnrestricted);
        Assert.Equal([matching.ExperienceId], result.Records.Select(ranked => ranked.Record.ExperienceId));
        Assert.Equal(
            [
                new ExcludedExperience(differing.ExperienceId, RetrievalExclusionReason.EnvironmentMismatch),
                new ExcludedExperience(missingKey.ExperienceId, RetrievalExclusionReason.EnvironmentMismatch),
                new ExcludedExperience(caseDiffering.ExperienceId, RetrievalExclusionReason.EnvironmentMismatch),
            ],
            result.Excluded);
    }

    // ---------------------------------------------------------------- matrix: unrestricted

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_request_with_no_required_attributes_passes_every_candidate_and_is_marked_unrestricted(bool emptyRatherThanNull)
    {
        var record = Record(Id(1), metadata: new Dictionary<string, string> { ["region"] = "anywhere" });
        var service = Service(Found(new ExperienceCandidate(record, 1d)));

        var result = await service.RetrieveAsync(
            Request(required: emptyRatherThanNull ? new Dictionary<string, string>() : null));

        Assert.True(result.EnvironmentUnrestricted);
        Assert.Single(result.Records);
        Assert.Empty(result.Excluded);
    }

    // ---------------------------------------------------------------- matrix: foreign scope

    [Fact]
    public async Task A_candidate_outside_the_requested_scope_empties_the_whole_result_rather_than_being_dropped_from_it()
    {
        // Defence in depth: the scope predicate runs in SQL, so this can only happen through a broken
        // source -- and a source that answered out of scope once cannot be trusted for the rest either.
        var mine = Record(Id(1));
        var foreign = Record(Id(2), scope: new Scope("tenant-2", "app-1", "project-1"));
        var service = Service(Found(new ExperienceCandidate(mine, 1d), new ExperienceCandidate(foreign, 1d)));

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Failed, result.Outcome);
        Assert.Empty(result.Records);
        Assert.NotNull(result.Failure);
    }

    [Theory]
    [InlineData("tenant-2", "app-1", "project-1")]
    [InlineData("tenant-1", "app-2", "project-1")]
    [InlineData("tenant-1", "app-1", "project-2")]
    public async Task A_candidate_from_another_tenant_application_or_project_empties_the_result_whatever_the_optional_fields_say(
        string tenant,
        string application,
        string project)
    {
        // The boundary a sharing grant can never cross. Relaxing the guard to accommodate grants must
        // not have relaxed it to accommodate these: each differs in exactly one required field while
        // matching the request on every optional one.
        var foreign = Record(Id(2), scope: new Scope(tenant, application, project));
        var service = Service(Found(new ExperienceCandidate(Record(Id(1)), 1d), new ExperienceCandidate(foreign, 1d)));

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Failed, result.Outcome);
        Assert.Empty(result.Records);
    }

    // ---------------------------------------------------------------- matrix: retrieval through a grant

    [Theory]
    [InlineData("team-b", null, null)]
    [InlineData(null, "agent-b", null)]
    [InlineData(null, null, "user-b")]
    public async Task A_candidate_shared_from_a_sibling_scope_is_ranked_like_any_other(string? team, string? agent, string? user)
    {
        // A record the adapter returned because an active grant permitted this scope to read it, and
        // said so. The grant itself was decided in SQL; what is under test here is that Core honours
        // the channel's declaration instead of throwing the answer away.
        var shared = Record(Id(2), scope: RequestScope with { TeamId = team, AgentId = agent, UserId = user });
        var service = Service(Found(
            new ExperienceCandidate(Record(Id(1)), 0.4d),
            new ExperienceCandidate(shared, 0.9d, SharedByGrant: true)));

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        Assert.Null(result.Failure);
        Assert.Equal([Id(2), Id(1)], result.Records.Select(ranked => ranked.Record.ExperienceId));

        // It is ranked as the record it is, carrying its owner's scope rather than the reader's, and
        // nothing about it is rewritten on the way through. The channel's declaration travels with it,
        // so a host's risk policy and the injected block can tell borrowed experience from its own.
        var ranked = result.Records[0];
        Assert.Equal(shared.Scope, ranked.Record.Scope);
        Assert.True(ranked.SharedByGrant);
        Assert.False(result.Records[1].SharedByGrant);
        Assert.Empty(result.Excluded);
    }

    [Theory]
    [InlineData("team-b", null, null)]
    [InlineData(null, "agent-b", null)]
    [InlineData(null, null, "user-b")]
    public async Task A_sibling_scope_candidate_the_channel_did_not_declare_shared_still_empties_the_whole_result(
        string? team,
        string? agent,
        string? user)
    {
        // The defence in depth grants must not cost: a third-party source, or a regression in our own
        // predicate composition, handing back a sibling scope's record without declaring a grant is
        // still a source that answered out of scope, and none of its answer is used.
        var undeclared = Record(Id(2), scope: RequestScope with { TeamId = team, AgentId = agent, UserId = user });
        var service = Service(Found(new ExperienceCandidate(Record(Id(1)), 1d), new ExperienceCandidate(undeclared, 1d)));

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Failed, result.Outcome);
        Assert.Empty(result.Records);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task A_declared_grant_can_still_not_carry_a_candidate_across_a_tenant_application_or_project()
    {
        // The flag says "a grant admitted this", not "trust this": a grant can never cross the three
        // required fields, so a channel claiming one that did is not believed.
        var service = Service(Found(
            new ExperienceCandidate(Record(Id(1), scope: new Scope("tenant-2", "app-1", "project-1")), 1d, SharedByGrant: true)));

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Failed, result.Outcome);
        Assert.Empty(result.Records);
    }

    [Fact]
    public async Task A_shared_candidate_is_excluded_by_the_same_eligibility_rules_as_an_owned_one()
    {
        // Sharing widens who may read a record, never what makes one injectable.
        var siblingScope = RequestScope with { TeamId = "team-b" };
        var revoked = Record(Id(1), scope: siblingScope, status: ExperienceStatus.Revoked);
        var neverValidated = Record(Id(2), scope: siblingScope, status: ExperienceStatus.Candidate);
        var service = Service(Found(
            new ExperienceCandidate(revoked, 1d, SharedByGrant: true),
            new ExperienceCandidate(neverValidated, 1d, SharedByGrant: true)));

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        Assert.Empty(result.Records);
        Assert.Equal(2, result.Excluded.Count);
    }

    // ---------------------------------------------------------------- matrix: beyond authority

    [Fact]
    public async Task A_scope_outside_the_authorization_is_an_empty_fail_closed_result_and_no_search_is_issued()
    {
        var source = new FakeCandidateSource(Found(new ExperienceCandidate(Record(Id(1)), 1d)));
        var service = new ExperienceRetrievalService(source, RetrievalPolicy.Default, RankingWeights.Default, new FixedTimeProvider(Now));

        var result = await service.RetrieveAsync(new RetrieveExperienceRequest(
            new AuthorizationContext("tenant-1", "host-principal", [], Now, ProjectId: "another-project"),
            RequestScope,
            TaskText,
            CorrelationId: "corr-1"));

        Assert.Equal(RetrievalOutcome.Denied, result.Outcome);
        Assert.Empty(result.Records);
        Assert.Empty(source.Queries);
        Assert.Equal("corr-1", result.CorrelationId);
        Assert.Null(result.Failure);
    }

    // ---------------------------------------------------------------- matrix: ties

    [Fact]
    public async Task Equal_scores_are_ordered_by_ExperienceId_ascending_and_ordinal()
    {
        // Identical in every scored respect, handed over in reverse order.
        var first = Record(Guid.Parse("00000000-0000-0000-0000-0000000000aa"), updatedAt: Now);
        var second = Record(Guid.Parse("00000000-0000-0000-0000-0000000000ab"), updatedAt: Now);
        var third = Record(Guid.Parse("00000000-0000-0000-0000-0000000000ba"), updatedAt: Now);
        var service = Service(Found(
            new ExperienceCandidate(third, 0.5),
            new ExperienceCandidate(second, 0.5),
            new ExperienceCandidate(first, 0.5)));

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(
            [first.ExperienceId, second.ExperienceId, third.ExperienceId],
            result.Records.Select(ranked => ranked.Record.ExperienceId));
        Assert.Single(result.Records.Select(ranked => ranked.Score).Distinct());
    }

    // ---------------------------------------------------------------- matrix: timeout

    [Fact]
    public async Task A_search_that_exceeds_the_timeout_returns_an_empty_result_with_the_timeout_signal_and_the_correlation_id()
    {
        // Real time here: the point is that the wall clock runs out, not how it is measured.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeCandidateSource(async (_, token) =>
        {
            entered.TrySetResult();
            using var registration = token.Register(() => observedCancellation.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Found();
        });

        var service = new ExperienceRetrievalService(
            source,
            RetrievalPolicy.Default with { Timeout = TimeSpan.FromMilliseconds(50) },
            RankingWeights.Default,
            TimeProvider.System);

        var result = await service.RetrieveAsync(Request(correlationId: "corr-timeout"));

        Assert.Equal(RetrievalOutcome.TimedOut, result.Outcome);
        Assert.True(result.TimedOut);
        Assert.Empty(result.Records);
        Assert.Empty(result.Excluded);
        Assert.Equal("corr-timeout", result.CorrelationId);
        Assert.Null(result.Failure); // a timeout is not a failure
        await entered.Task;

        // The abandoned search is cancelled only after the timeout has been reported.
        await observedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task The_timeout_is_measured_with_the_injected_TimeProvider()
    {
        // A 5 ms timeout against a search that takes ten times that in real time. The frozen provider's
        // clock never advances, so nothing times out; a service measuring on the wall clock would have.
        var clock = new FixedTimeProvider(Now);
        var source = new FakeCandidateSource(async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            return Found(new ExperienceCandidate(Record(Id(1)), 1d));
        });
        var service = new ExperienceRetrievalService(
            source,
            RetrievalPolicy.Default with { Timeout = TimeSpan.FromMilliseconds(5) },
            RankingWeights.Default,
            clock);

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        Assert.Single(result.Records);
        Assert.Equal(TimeSpan.Zero, result.Elapsed);
    }

    // ---------------------------------------------------------------- matrix: cancelled

    [Fact]
    public async Task Caller_cancellation_propagates_unwrapped_and_is_never_reported_as_a_timeout()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeCandidateSource(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Found();
        });

        // A timeout long enough that it cannot be what ends the call.
        var service = new ExperienceRetrievalService(
            source,
            RetrievalPolicy.Default with { Timeout = TimeSpan.FromMinutes(5) },
            RankingWeights.Default,
            TimeProvider.System);

        using var cancellation = new CancellationTokenSource();
        var retrieval = service.RetrieveAsync(Request(), cancellation.Token);
        await entered.Task;
        await cancellation.CancelAsync();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retrieval);

        // Cancellation, not a timeout: the call threw rather than returning a timed-out result.
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task An_already_cancelled_token_throws_before_any_search_is_issued()
    {
        var source = new FakeCandidateSource(Found());
        var service = new ExperienceRetrievalService(source, RetrievalPolicy.Default, RankingWeights.Default, new FixedTimeProvider(Now));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RetrieveAsync(Request(), cancellation.Token));

        Assert.Empty(source.Queries);
    }

    // ---------------------------------------------------------------- matrix: store failure

    [Fact]
    public async Task A_store_failure_inside_the_timeout_is_an_empty_fail_closed_result_carrying_the_failure()
    {
        var failure = new ExperienceStoreException("database unavailable", new InvalidOperationException("driver"));
        var service = Service(new FakeCandidateSource((_, _) => throw failure));

        var result = await service.RetrieveAsync(Request(correlationId: "corr-failed"));

        Assert.Equal(RetrievalOutcome.Failed, result.Outcome);
        Assert.False(result.TimedOut);
        Assert.Empty(result.Records);
        Assert.Equal("corr-failed", result.CorrelationId);
        Assert.NotNull(result.Failure);
        Assert.Same(failure, result.Failure!.Exception);
    }

    [Theory]
    [InlineData(ExperienceStoreOutcome.Denied)]
    [InlineData(ExperienceStoreOutcome.Invalid)]
    public async Task A_source_that_refuses_to_answer_empties_the_result_rather_than_reporting_no_matches(ExperienceStoreOutcome outcome)
    {
        var service = Service(new FakeCandidateSource(
            (_, _) => Task.FromResult(new ExperienceCandidateSearchResult(outcome, [], []))));

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Failed, result.Outcome);
        Assert.Empty(result.Records);
        Assert.NotNull(result.Failure);
        Assert.Contains(outcome.ToString(), result.Failure!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreadable_candidate_empties_the_result_rather_than_leaving_it_unfiltered()
    {
        var readable = Record(Id(1));
        var unreadable = Record(Id(2)) with { Environment = null! };
        var service = Service(Found(new ExperienceCandidate(readable, 1d), new ExperienceCandidate(unreadable, 1d)));

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Failed, result.Outcome);
        Assert.Empty(result.Records);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task The_same_record_returned_twice_empties_the_result_rather_than_ranking_it_twice()
    {
        var record = Record(Id(1), updatedAt: Now);
        var service = Service(Found(new ExperienceCandidate(record, 0.9), new ExperienceCandidate(record, 0.2)));

        var result = await service.RetrieveAsync(Request());

        // Scored twice, it would be ordered arbitrarily against itself and the ranking would stop being
        // total -- and which of the two relevances won would be undefined.
        Assert.Equal(RetrievalOutcome.Failed, result.Outcome);
        Assert.Empty(result.Records);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task A_source_that_cancels_for_its_own_reasons_is_an_empty_failed_result_not_a_throw()
    {
        // Neither the caller's token nor the timeout: a port that cancels on its own must not be able to
        // make RetrieveAsync throw at a caller who never cancelled anything.
        using var unrelated = new CancellationTokenSource();
        await unrelated.CancelAsync();
        var service = Service(new FakeCandidateSource((_, _) =>
            throw new OperationCanceledException("the source gave up", unrelated.Token)));

        var result = await service.RetrieveAsync(Request(correlationId: "corr-source-cancel"));

        Assert.Equal(RetrievalOutcome.Failed, result.Outcome);
        Assert.False(result.TimedOut);
        Assert.Empty(result.Records);
        Assert.Equal("corr-source-cancel", result.CorrelationId);
        Assert.IsType<OperationCanceledException>(result.Failure!.Exception);
    }

    [Fact]
    public async Task A_source_returning_no_result_or_no_candidate_list_is_fail_closed()
    {
        var noResult = await Service(new FakeCandidateSource(
            (_, _) => Task.FromResult<ExperienceCandidateSearchResult>(null!))).RetrieveAsync(Request());
        var noList = await Service(new FakeCandidateSource(
            (_, _) => Task.FromResult(new ExperienceCandidateSearchResult(ExperienceStoreOutcome.Found, null!, [])))).RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Failed, noResult.Outcome);
        Assert.NotNull(noResult.Failure);
        Assert.Equal(RetrievalOutcome.Failed, noList.Outcome);
        Assert.NotNull(noList.Failure);
    }

    [Fact]
    public async Task A_source_that_throws_something_other_than_a_store_failure_is_still_fail_closed()
    {
        var service = Service(new FakeCandidateSource((_, _) => throw new InvalidOperationException("boom")));

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Failed, result.Outcome);
        Assert.Empty(result.Records);
        Assert.IsType<InvalidOperationException>(result.Failure!.Exception);
    }

    // ---------------------------------------------------------------- the golden fixture

    /// <summary>
    /// Pins the documented default ordering, every normalized component, and every effective weight.
    /// Each record differs from the baseline (<c>...0004</c>) in exactly one component, so the order
    /// below <em>is</em> the statement that relevance outranks confidence outranks recency and status.
    /// A change here is a behaviour change for every host that ranks with the defaults.
    /// </summary>
    [Fact]
    public async Task Golden_fixture_pins_the_default_ordering_and_every_component_value()
    {
        const string Expected = """
            00000000-0000-0000-0000-000000000001  0.800  Relevance=1.000*0.350  Confidence=0.500*0.250  Recency=1.000*0.150  Status=0.500*0.150  EnvironmentCompatibility=1.000*0.100
            00000000-0000-0000-0000-000000000002  0.750  Relevance=0.500*0.350  Confidence=1.000*0.250  Recency=1.000*0.150  Status=0.500*0.150  EnvironmentCompatibility=1.000*0.100
            00000000-0000-0000-0000-000000000003  0.700  Relevance=0.500*0.350  Confidence=0.500*0.250  Recency=1.000*0.150  Status=1.000*0.150  EnvironmentCompatibility=1.000*0.100
            00000000-0000-0000-0000-000000000004  0.625  Relevance=0.500*0.350  Confidence=0.500*0.250  Recency=1.000*0.150  Status=0.500*0.150  EnvironmentCompatibility=1.000*0.100
            00000000-0000-0000-0000-000000000005  0.550  Relevance=0.500*0.350  Confidence=0.500*0.250  Recency=0.500*0.150  Status=0.500*0.150  EnvironmentCompatibility=1.000*0.100
            """;

        var policy = RetrievalPolicy.Default;
        var halfLifeAgo = Now - policy.RecencyHalfLife;

        // Deliberately handed over worst-first, so the assertion is about ranking and not about the
        // order the source happened to return.
        var service = Service(
            Found(
                new ExperienceCandidate(Record(Id(5), updatedAt: halfLifeAgo), 0.5),
                new ExperienceCandidate(Record(Id(4), updatedAt: Now), 0.5),
                new ExperienceCandidate(Record(Id(3), status: ExperienceStatus.Reinforced, updatedAt: Now), 0.5),
                new ExperienceCandidate(Record(Id(2), confidence: 1d, updatedAt: Now), 0.5),
                new ExperienceCandidate(Record(Id(1), updatedAt: Now), 1d)),
            policy: policy);

        var result = await service.RetrieveAsync(Request());

        Assert.Equal(RetrievalOutcome.Completed, result.Outcome);
        Assert.Equal(Expected, Render(result.Records));
    }

    private static string Render(IReadOnlyList<RankedExperience> records)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < records.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            var ranked = records[i];
            builder.Append(CultureInfo.InvariantCulture, $"{ranked.Record.ExperienceId:D}  {ranked.Score:0.000}");
            foreach (var component in ranked.Components)
            {
                builder.Append(CultureInfo.InvariantCulture, $"  {component.Kind}={component.Value:0.000}*{component.Weight:0.000}");
            }
        }

        return builder.ToString();
    }

    // ---------------------------------------------------------------- weight and policy validation

    [Theory]
    [InlineData(-0.35, 0.25, 0.15, 0.15, 0.10)] // negative
    [InlineData(0.35, 0.25, 0.15, 0.15, 0.20)] // sums to 1.1
    [InlineData(0.35, 0.25, 0.15, 0.15, 0.00)] // sums to 0.9
    [InlineData(0.00, 0.00, 0.00, 0.00, 0.00)] // sums to 0
    [InlineData(double.NaN, 0.25, 0.15, 0.15, 0.10)]
    [InlineData(double.PositiveInfinity, 0.25, 0.15, 0.15, 0.10)]
    public void Negative_non_finite_or_non_unit_sum_weights_throw_at_construction(
        double relevance,
        double confidence,
        double recency,
        double status,
        double environment)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RankingWeights(relevance, confidence, recency, status, environment));
    }

    [Fact]
    public void A_zero_weight_is_allowed_as_long_as_the_set_still_sums_to_one()
    {
        var weights = new RankingWeights(1d, 0d, 0d, 0d, 0d);

        Assert.Equal(0d, weights.Confidence);
        Assert.Equal(1d, weights.Sum, 12);
    }

    [Fact]
    public void The_default_weights_are_the_documented_ones_and_sum_to_one()
    {
        var weights = RankingWeights.Default;

        Assert.Equal(0.35, weights.Relevance);
        Assert.Equal(0.25, weights.Confidence);
        Assert.Equal(0.15, weights.Recency);
        Assert.Equal(0.15, weights.Status);
        Assert.Equal(0.10, weights.EnvironmentCompatibility);
        Assert.Equal(1d, weights.Sum, 12);
        Assert.True(Math.Abs(weights.Sum - 1d) <= RankingWeights.SumTolerance);
    }

    [Fact]
    public void Invalid_weights_mean_no_retrieval_service_can_be_built_at_all()
    {
        // The service takes a constructed RankingWeights, so an invalid set cannot reach a call.
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExperienceRetrievalService(
            new FakeCandidateSource(Found()),
            RetrievalPolicy.Default,
            new RankingWeights(0.5, 0.25, 0.15, 0.15, 0.10),
            new FixedTimeProvider(Now)));
    }

    [Fact]
    public void An_invalid_policy_value_throws_at_construction_and_on_a_with_expression()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalPolicy.Default with { Timeout = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalPolicy.Default with { Timeout = TimeSpan.FromMilliseconds(-1) });
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalPolicy.Default with { MinimumConfidence = 1.5 });
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalPolicy.Default with { MinimumConfidence = -0.1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalPolicy.Default with { MaxAge = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalPolicy.Default with { RecencyHalfLife = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalPolicy.Default with { CandidateLimit = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalPolicy.Default with { CandidateLimit = RetrievalPolicy.MaxCandidateLimit + 1 });

        // The ceiling stops one below the port's own maximum, because the service asks for one more.
        Assert.Equal(ExperienceCandidateQuery.MaxLimit - 1, RetrievalPolicy.MaxCandidateLimit);
        Assert.Equal(RetrievalPolicy.MaxCandidateLimit, (RetrievalPolicy.Default with { CandidateLimit = RetrievalPolicy.MaxCandidateLimit }).CandidateLimit);

        // A timeout past the supported span would throw out of the retrieval call instead of bounding it.
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalPolicy.Default with { Timeout = RetrievalPolicy.MaxTimeout + TimeSpan.FromSeconds(1) });
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalPolicy.Default with { Timeout = TimeSpan.MaxValue });
        Assert.Equal(RetrievalPolicy.MaxTimeout, (RetrievalPolicy.Default with { Timeout = RetrievalPolicy.MaxTimeout }).Timeout);

        Assert.Equal(TimeSpan.FromMilliseconds(500), RetrievalPolicy.Default.Timeout);
        Assert.Null((RetrievalPolicy.Default with { MaxAge = TimeSpan.FromDays(1) } with { MaxAge = null }).MaxAge);
    }

    // ---------------------------------------------------------------- request validation and limits

    [Fact]
    public async Task A_malformed_request_throws_rather_than_quietly_retrieving_nothing()
    {
        var service = Service(Found());

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.RetrieveAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.RetrieveAsync(new RetrieveExperienceRequest(null!, RequestScope, TaskText)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.RetrieveAsync(new RetrieveExperienceRequest(Authorization, null!, TaskText)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RetrieveAsync(new RetrieveExperienceRequest(Authorization, RequestScope, "  ")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RetrieveAsync(Request(limit: 0)));

        // Longer than the port will accept: rejected here rather than travelling to the database and
        // coming back as an opaque Failed result.
        await Assert.ThrowsAsync<ArgumentException>(() => service.RetrieveAsync(
            new RetrieveExperienceRequest(Authorization, RequestScope, new string('a', ExperienceCandidateQuery.MaxTaskTextLength + 1))));
    }

    [Fact]
    public async Task A_limit_larger_than_the_candidate_ceiling_is_rejected_rather_than_quietly_capped()
    {
        var source = new FakeCandidateSource(Found());
        var policy = RetrievalPolicy.Default with { CandidateLimit = 5 };
        var service = new ExperienceRetrievalService(source, policy, RankingWeights.Default, new FixedTimeProvider(Now));

        // Asking for 6 when the search will only ever consider 5 could never be satisfied; silently
        // returning 5 would hide the misconfiguration.
        await Assert.ThrowsAsync<ArgumentException>(() => service.RetrieveAsync(Request(limit: 6)));
        Assert.Empty(source.Queries);

        var atTheCeiling = await service.RetrieveAsync(Request(limit: 5));
        Assert.Equal(RetrievalOutcome.Completed, atTheCeiling.Outcome);
    }

    // ---------------------------------------------------------------- the candidate ceiling

    [Fact]
    public async Task Hitting_the_candidate_ceiling_is_reported_and_the_extra_probe_candidate_is_never_ranked()
    {
        // The service asks for CandidateLimit + 1; a full extra candidate means more matched than were
        // considered. It must not be ranked, and the caller must be told the answer is partial.
        var policy = RetrievalPolicy.Default with { CandidateLimit = 3 };
        var candidates = Enumerable.Range(1, 4)
            .Select(n => new ExperienceCandidate(Record(Id(n), updatedAt: Now), 1d - (n * 0.1)))
            .ToArray();
        var service = Service(Found(candidates), policy);

        var result = await service.RetrieveAsync(Request());

        Assert.True(result.Truncated);
        Assert.Equal([Id(1), Id(2), Id(3)], result.Records.Select(ranked => ranked.Record.ExperienceId));
        Assert.DoesNotContain(Id(4), result.Excluded.Select(excluded => excluded.ExperienceId));
    }

    [Fact]
    public async Task A_result_that_did_not_reach_the_ceiling_is_not_truncated()
    {
        var policy = RetrievalPolicy.Default with { CandidateLimit = 3 };
        var candidates = Enumerable.Range(1, 3)
            .Select(n => new ExperienceCandidate(Record(Id(n), updatedAt: Now), 0.5))
            .ToArray();

        var result = await Service(Found(candidates), policy).RetrieveAsync(Request());

        // Exactly at the ceiling, with no probe candidate: everything that matched was considered.
        Assert.False(result.Truncated);
        Assert.Equal(3, result.Records.Count);
    }

    [Fact]
    public async Task An_empty_result_is_never_marked_truncated()
    {
        var denied = await Service(Found()).RetrieveAsync(new RetrieveExperienceRequest(
            new AuthorizationContext("tenant-1", "p", [], Now, ProjectId: "elsewhere"), RequestScope, TaskText));
        var failed = await Service(new FakeCandidateSource((_, _) => throw new InvalidOperationException("boom"))).RetrieveAsync(Request());

        Assert.False(denied.Truncated);
        Assert.False(failed.Truncated);
    }

    [Fact]
    public async Task The_requests_limit_bounds_how_many_ranked_records_come_back_after_ranking()
    {
        var service = Service(Found(
            new ExperienceCandidate(Record(Id(1), updatedAt: Now), 0.1),
            new ExperienceCandidate(Record(Id(2), updatedAt: Now), 0.9),
            new ExperienceCandidate(Record(Id(3), updatedAt: Now), 0.5)));

        var result = await service.RetrieveAsync(Request(limit: 2));

        // Trimmed after ranking, so the best two survive rather than the first two returned.
        Assert.Equal([Id(2), Id(3)], result.Records.Select(ranked => ranked.Record.ExperienceId));
    }

    [Fact]
    public void Null_constructor_arguments_throw()
    {
        var source = new FakeCandidateSource(Found());
        var clock = new FixedTimeProvider(Now);

        Assert.Throws<ArgumentNullException>(() => new ExperienceRetrievalService(null!, RetrievalPolicy.Default, RankingWeights.Default, clock));
        Assert.Throws<ArgumentNullException>(() => new ExperienceRetrievalService(source, null!, RankingWeights.Default, clock));
        Assert.Throws<ArgumentNullException>(() => new ExperienceRetrievalService(source, RetrievalPolicy.Default, null!, clock));
        Assert.Throws<ArgumentNullException>(() => new ExperienceRetrievalService(source, RetrievalPolicy.Default, RankingWeights.Default, null!));
    }

    // ---------------------------------------------------------------- helpers

    private static Guid Id(int n) => Guid.Parse(FormattableString.Invariant($"00000000-0000-0000-0000-{n:000000000000}"));

    private static RetrieveExperienceRequest Request(
        IReadOnlyDictionary<string, string>? required = null,
        string? correlationId = null,
        int? limit = null) => new(Authorization, RequestScope, TaskText, required, correlationId, limit);

    private static ExperienceCandidateSearchResult Found(params ExperienceCandidate[] candidates) =>
        new(ExperienceStoreOutcome.Found, candidates, []);

    private static ExperienceRetrievalService Service(ExperienceCandidateSearchResult result, RetrievalPolicy? policy = null) =>
        Service(new FakeCandidateSource(result), policy);

    private static ExperienceRetrievalService Service(FakeCandidateSource source, RetrievalPolicy? policy = null) =>
        new(source, policy ?? RetrievalPolicy.Default, RankingWeights.Default, new FixedTimeProvider(Now));

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

    /// <summary>A candidate source that answers with whatever the test scripted, recording what it was asked.</summary>
    private sealed class FakeCandidateSource(
        Func<ExperienceCandidateQuery, CancellationToken, Task<ExperienceCandidateSearchResult>> onSearch)
        : IExperienceCandidateSource
    {
        public FakeCandidateSource(ExperienceCandidateSearchResult result)
            : this((_, _) => Task.FromResult(result))
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

    /// <summary>
    /// A clock frozen at a known instant. Its timers never fire, so a test that does not mean to
    /// exercise the timeout cannot accidentally hit one, and elapsed time is always exactly zero.
    /// </summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override long GetTimestamp() => now.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new FrozenTimer();

        private sealed class FrozenTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

using AgentExperience.Core.Retrieval;
using AgentExperience.Core.Tests.Diagnostics;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Story 28.2: Core's failure-signature lookup asks the source for what text retrieval would consider eligible, drops
/// what has expired, keeps the strict scope check, turns every refusal and failure into an answer, and reports one
/// <c>retrieve.signature</c> span tagged with its channel and match and nothing about the signature.
/// </summary>
public sealed class ExperienceSignatureRetrievalServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static readonly Scope Scope = new("tenant-1", "app-1", "project-1");

    private static readonly AuthorizationContext Authorization = new("tenant-1", "host", ["experience:read"], Now);

    private static readonly ExperienceFailureSignature Signature =
        ExperienceFailureSignature.From("run_tests", "FileNotFoundException: Could not find file '/tmp/a/orders-12.json'.")!;

    [Fact]
    public async Task It_asks_the_source_for_reusable_records_above_the_policy_floor_and_passes_the_request_through()
    {
        var source = new RecordingSource();
        var service = new ExperienceSignatureRetrievalService(source, RetrievalPolicy.Default with { MinimumConfidence = 0.7 }, new FrozenClock(Now));

        var result = await service.FindAsync(new SignatureRetrievalRequest(Authorization, Scope, Signature)
        {
            Match = ExperienceSignatureMatch.Coarse,
            Limit = 5,
            ExcludeModelAuthored = true,
            CorrelationId = "invocation-7",
        });

        Assert.Equal(SignatureRetrievalOutcome.Completed, result.Outcome);
        var (scope, query) = Assert.Single(source.Calls);
        Assert.Equal(Scope, scope);
        Assert.Equal(Signature, query.Signature);
        Assert.Equal(ExperienceStatuses.EligibleForReuse, query.EligibleStatuses);
        Assert.Equal(0.7, query.MinimumConfidence);
        Assert.Equal(ExperienceSignatureMatch.Coarse, query.Match);
        Assert.Equal(5, query.Limit);
        Assert.True(query.ExcludeModelAuthored);
        Assert.Equal("invocation-7", query.CorrelationId);
    }

    [Fact]
    public async Task Expired_records_and_records_outside_the_scope_that_no_grant_shared_are_dropped_and_the_order_is_kept()
    {
        var fresh = Candidate(Record(Scope, updatedAt: Now.AddDays(-1)));
        var old = Candidate(Record(Scope, updatedAt: Now.AddDays(-40)));
        var foreign = Candidate(Record(Scope with { ProjectId = "project-2" }, updatedAt: Now));
        var shared = Candidate(Record(Scope with { TeamId = "team-b" }, updatedAt: Now)) with { SharedByGrant = true };
        var source = new RecordingSource { Answer = [shared, fresh, old, foreign] };
        var service = new ExperienceSignatureRetrievalService(source, RetrievalPolicy.Default with { MaxAge = TimeSpan.FromDays(30) }, new FrozenClock(Now));

        var result = await service.FindAsync(new SignatureRetrievalRequest(Authorization, Scope, Signature));

        Assert.Equal([shared, fresh], result.Candidates);
    }

    [Fact]
    public async Task A_scope_outside_the_authorization_is_Denied_before_the_source_is_called()
    {
        var source = new RecordingSource();
        var service = new ExperienceSignatureRetrievalService(source);

        var result = await service.FindAsync(new SignatureRetrievalRequest(Authorization, Scope with { TenantId = "tenant-2" }, Signature));

        Assert.Equal(SignatureRetrievalOutcome.Denied, result.Outcome);
        Assert.Empty(result.Candidates);
        Assert.Empty(source.Calls);
    }

    [Fact]
    public async Task Refusals_and_failures_are_answers_and_only_the_callers_cancellation_propagates()
    {
        var invalid = await new ExperienceSignatureRetrievalService(new RecordingSource
        {
            Refusal = new(ExperienceStoreOutcome.Invalid, [], [new StoreValidationError("Limit", "must be between 1 and 200.")]),
        }).FindAsync(new SignatureRetrievalRequest(Authorization, Scope, Signature));
        Assert.Equal(SignatureRetrievalOutcome.Invalid, invalid.Outcome);
        Assert.Equal("Limit", Assert.Single(invalid.Errors).Path);

        var denied = await new ExperienceSignatureRetrievalService(new RecordingSource
        {
            Refusal = new(ExperienceStoreOutcome.Denied, [], []),
        }).FindAsync(new SignatureRetrievalRequest(Authorization, Scope, Signature));
        Assert.Equal(SignatureRetrievalOutcome.Denied, denied.Outcome);

        var thrown = new ExperienceStoreException("relation \"agent_experience.experience_failure_signatures\" does not exist");
        var failed = await new ExperienceSignatureRetrievalService(new RecordingSource { Throws = thrown })
            .FindAsync(new SignatureRetrievalRequest(Authorization, Scope, Signature));
        Assert.Equal(SignatureRetrievalOutcome.Failed, failed.Outcome);
        Assert.Same(thrown, failed.Failure);
        Assert.Empty(failed.Candidates);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ExperienceSignatureRetrievalService(new RecordingSource())
            .FindAsync(new SignatureRetrievalRequest(Authorization, Scope, Signature), cancelled.Token));
    }

    [Fact]
    public async Task The_span_carries_the_channel_the_match_and_the_outcome_and_nothing_about_the_signature()
    {
        using var probe = TelemetryProbe.All();
        var service = new ExperienceSignatureRetrievalService(new RecordingSource { Answer = [Candidate(Record(Scope, Now))] });

        await service.FindAsync(new SignatureRetrievalRequest(Authorization, Scope, Signature) { Match = ExperienceSignatureMatch.Coarse, CorrelationId = "invocation-9" });

        var span = Assert.Single(probe.LibraryActivities, a => a.OperationName == "agentexperience.retrieve.signature");
        Assert.Equal("signature", span.GetTagItem("agentexperience.retrieval.channel"));
        Assert.Equal("Coarse", span.GetTagItem("agentexperience.signature.match"));
        Assert.Equal("invocation-9", span.GetTagItem("agentexperience.correlation_id"));
        Assert.Equal("Completed", span.GetTagItem("agentexperience.outcome"));
        Assert.DoesNotContain(probe.EverySpanTagValue, value =>
            value.Contains("run_tests", StringComparison.Ordinal)
            || value.Contains(Signature.Fingerprint, StringComparison.Ordinal)
            || value.Contains("FileNotFoundException", StringComparison.Ordinal));
        Assert.NotEmpty(probe.For("agentexperience.operation.count", "retrieve.signature"));
    }

    private static ExperienceSignatureCandidate Candidate(ExperienceRecord record) =>
        new(record, ExperienceSignatureMatch.Exact, ExperienceSignatureResolution.Resolved);

    private static ExperienceRecord Record(Scope scope, DateTimeOffset updatedAt) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        scope,
        "task",
        null,
        [],
        new Outcome(TaskVerificationStatus.Verified, [], null, Now),
        1,
        null,
        new EnvironmentFingerprint("host", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
        new Provenance("test", null, Now, null),
        ExperienceStatus.Validated,
        0.9,
        0,
        0,
        0,
        updatedAt,
        updatedAt);

    private sealed class RecordingSource : IExperienceSignatureSource
    {
        public List<(Scope Scope, ExperienceSignatureQuery Query)> Calls { get; } = [];

        public IReadOnlyList<ExperienceSignatureCandidate> Answer { get; init; } = [];

        public ExperienceSignatureSearchResult? Refusal { get; init; }

        public Exception? Throws { get; init; }

        public Task<ExperienceSignatureSearchResult> FindBySignatureAsync(
            AuthorizationContext authorization,
            Scope scope,
            ExperienceSignatureQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((scope, query));
            if (Throws is not null)
            {
                throw Throws;
            }

            return Task.FromResult(Refusal ?? new ExperienceSignatureSearchResult(ExperienceStoreOutcome.Found, Answer, []));
        }
    }
}

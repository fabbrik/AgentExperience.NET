using static AgentExperience.Storage.Conformance.ConformanceData;

namespace AgentExperience.Storage.Conformance;

/// <summary>
/// Story 28.2: the behaviour every <see cref="IExperienceSignatureSource"/> must show, observed through the ports alone.
/// It finds a record by the signature of a failed call it holds -- exactly, or by tool and error class -- whatever IDs,
/// paths and numbers the two failures' messages named; it applies exact scope, the requested statuses, the minimum
/// confidence and the authorship exclusion before its limit; it returns each record once, exact matches first, then
/// resolved ones, then by confidence; it sees a committed write (a create, a lifecycle commit) on the next search; and
/// it refuses what the port documents as refused. Records are seeded through the record store the source reads from,
/// so a subclass supplies both.
/// </summary>
public abstract class SignatureSourceConformanceTests
{
    private const string FailingTool = "run_tests";

    private const string Failure = "FileNotFoundException: Could not find file '/home/alice/app/tests/fixtures/orders-1182.json'.";

    /// <summary>The same failure in a sibling run: another user, another directory, another number.</summary>
    private const string SiblingFailure = "FileNotFoundException: Could not find file '/home/bob/checkout/tests/fixtures/orders-77.json'.";

    /// <summary>The same tool and class, a different message: a coarse match only.</summary>
    private const string OtherMessage = "FileNotFoundException: The configuration file is missing.";

    private static readonly ExperienceStatus[] Eligible = [ExperienceStatus.Validated, ExperienceStatus.Reinforced];

    private readonly Lazy<IExperienceRecordStore> _store;
    private readonly Lazy<IExperienceSignatureSource> _source;

    protected SignatureSourceConformanceTests()
    {
        _store = new Lazy<IExperienceRecordStore>(CreateRecordStore);
        _source = new Lazy<IExperienceSignatureSource>(CreateSignatureSource);
    }

    /// <summary>The record store the test seeds records through.</summary>
    protected IExperienceRecordStore RecordStore => _store.Value;

    /// <summary>The signature source under test.</summary>
    protected IExperienceSignatureSource Source => _source.Value;

    /// <summary>Creates the record store whose records <see cref="CreateSignatureSource"/> searches. Called at most once per test.</summary>
    protected abstract IExperienceRecordStore CreateRecordStore();

    /// <summary>Creates the signature source under test. Called at most once per test.</summary>
    protected abstract IExperienceSignatureSource CreateSignatureSource();

    [Fact]
    public async Task A_sibling_runs_failure_finds_the_record_that_holds_it_exactly_and_only_in_exactly_the_requested_scope()
    {
        var tenant = NewTenant();
        var foreignTenant = NewTenant();
        var scope = Scope(tenant);
        var mine = await SeedAsync(tenant, scope, Failure);

        // The same failure in every neighbouring scope.
        await SeedAsync(foreignTenant, Scope(foreignTenant), Failure);
        await SeedAsync(tenant, Scope(tenant, project: "project-2"), Failure);
        await SeedAsync(tenant, Scope(tenant, project: "Project-1"), Failure);
        await SeedAsync(tenant, Scope(tenant, team: "team-1"), Failure);

        var result = await FindAsync(tenant, scope, Signature(SiblingFailure));

        Assert.Equal(ExperienceStoreOutcome.Found, result.Outcome);
        Assert.Empty(result.Errors);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(mine, candidate.Record.ExperienceId);
        Assert.Equal(ExperienceSignatureMatch.Exact, candidate.Match);
        Assert.Equal(ExperienceSignatureResolution.Resolved, candidate.Resolution);
        Assert.False(candidate.SharedByGrant);
        Assert.Null(candidate.PermittingGrantId);
        Assert.Null(candidate.GrantDisclosure);
    }

    [Fact]
    public async Task The_record_comes_back_exactly_as_stored()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var record = Failing(scope, Failure);
        Assert.Equal(ExperienceStoreOutcome.Created, (await RecordStore.CreateAsync(Authorize(tenant), record, CancellationToken.None)).Outcome);

        var found = Assert.Single((await FindAsync(tenant, scope, Signature(Failure))).Candidates);
        var stored = (await RecordStore.GetAsync(Authorize(tenant), scope, record.ExperienceId, CancellationToken.None)).Record!;

        Assert.Equal(Json(stored), Json(found.Record));
    }

    [Fact]
    public async Task An_exact_search_misses_another_message_and_another_tool_and_a_coarse_one_finds_the_message_only()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var exact = await SeedAsync(tenant, scope, Failure);
        var sameClass = await SeedAsync(tenant, scope, OtherMessage);
        await SeedAsync(tenant, scope, Failure, tool: "build");
        await SeedAsync(tenant, scope, "Process exited with code 2");

        var exactResult = await FindAsync(tenant, scope, Signature(Failure));
        var coarseResult = await FindAsync(tenant, scope, Signature(Failure), ExperienceSignatureMatch.Coarse);

        Assert.Equal([exact], Ids(exactResult));
        Assert.Equal([exact, sameClass], Ids(coarseResult));
        Assert.Equal([ExperienceSignatureMatch.Exact, ExperienceSignatureMatch.Coarse], coarseResult.Candidates.Select(c => c.Match));
    }

    [Fact]
    public async Task Only_eligible_statuses_at_or_above_the_minimum_confidence_are_returned()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var validated = await SeedAsync(tenant, scope, Failure, confidence: 0.6);
        var reinforced = await SeedAsync(tenant, scope, Failure, ExperienceStatus.Reinforced, confidence: 0.9);
        await SeedAsync(tenant, scope, Failure, ExperienceStatus.Candidate, confidence: 0.9);
        await SeedAsync(tenant, scope, Failure, ExperienceStatus.Contested, confidence: 0.9);
        await SeedAsync(tenant, scope, Failure, confidence: 0.49);

        var result = await FindAsync(tenant, scope, Signature(Failure), minimumConfidence: 0.5);

        Assert.Equal([reinforced, validated], Ids(result));
    }

    [Fact]
    public async Task A_lifecycle_commit_is_seen_by_the_next_search()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var id = await SeedAsync(tenant, scope, Failure, ExperienceStatus.Candidate);
        Assert.Empty((await FindAsync(tenant, scope, Signature(Failure))).Candidates);

        var committed = await RecordStore.CommitLifecycleEventAsync(
            Authorize(tenant), scope, Event(id, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Committed, committed.Outcome);
        Assert.Equal([id], Ids(await FindAsync(tenant, scope, Signature(Failure))));

        committed = await RecordStore.CommitLifecycleEventAsync(
            Authorize(tenant), scope, Event(id, ExperienceStatus.Validated, ExperienceStatus.Contested, 1), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Committed, committed.Outcome);
        Assert.Empty((await FindAsync(tenant, scope, Signature(Failure))).Candidates);
    }

    [Fact]
    public async Task Exact_matches_come_first_then_resolved_ones_then_higher_confidence_and_the_limit_applies_last()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var coarseResolvedStrong = await SeedAsync(tenant, scope, OtherMessage, confidence: 0.99);
        var exactUnresolvedStrong = await SeedAsync(tenant, scope, Failure, confidence: 0.95, resolved: false);
        var exactResolvedWeak = await SeedAsync(tenant, scope, Failure, confidence: 0.6);
        var exactResolvedStrong = await SeedAsync(tenant, scope, Failure, confidence: 0.9);
        await SeedAsync(tenant, scope, Failure, ExperienceStatus.Candidate, confidence: 1.0);

        var all = await FindAsync(tenant, scope, Signature(Failure), ExperienceSignatureMatch.Coarse);
        var two = await FindAsync(tenant, scope, Signature(Failure), ExperienceSignatureMatch.Coarse, limit: 2);

        Assert.Equal([exactResolvedStrong, exactResolvedWeak, exactUnresolvedStrong, coarseResolvedStrong], Ids(all));
        Assert.Equal(
            [ExperienceSignatureResolution.Resolved, ExperienceSignatureResolution.Resolved, ExperienceSignatureResolution.Unresolved, ExperienceSignatureResolution.Resolved],
            all.Candidates.Select(c => c.Resolution));
        Assert.Equal([exactResolvedStrong, exactResolvedWeak], Ids(two));
    }

    [Fact]
    public async Task A_record_holding_several_matching_signatures_comes_back_once()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var record = Failing(scope, Failure) with
        {
            Attempts =
            [
                Attempt(0, Call(FailingTool, Failure, 0), Call(FailingTool, OtherMessage, 1), Call(FailingTool, SiblingFailure, 2)),
                Attempt(1, Call(FailingTool, null, 0)),
            ],
        };
        Assert.Equal(ExperienceStoreOutcome.Created, (await RecordStore.CreateAsync(Authorize(tenant), record, CancellationToken.None)).Outcome);

        var coarse = await FindAsync(tenant, scope, Signature(OtherMessage), ExperienceSignatureMatch.Coarse);

        var candidate = Assert.Single(coarse.Candidates);
        Assert.Equal(ExperienceSignatureMatch.Exact, candidate.Match);
    }

    [Fact]
    public async Task An_excluding_search_leaves_model_authored_records_out_before_the_limit()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        await SeedAsync(tenant, scope, Failure, confidence: 0.99, authorship: ReflectionAuthorship.Model);
        var deterministic = await SeedAsync(tenant, scope, Failure, confidence: 0.6);

        var including = await FindAsync(tenant, scope, Signature(Failure), limit: 1);
        var excluding = await FindAsync(tenant, scope, Signature(Failure), limit: 1, excludeModelAuthored: true);

        Assert.NotEqual([deterministic], Ids(including));
        Assert.Equal([deterministic], Ids(excluding));
    }

    [Fact]
    public async Task A_record_with_no_failed_call_is_never_found()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var record = Failing(scope, Failure) with { Attempts = [Attempt(0, Call(FailingTool, null, 0))] };
        Assert.Equal(ExperienceStoreOutcome.Created, (await RecordStore.CreateAsync(Authorize(tenant), record, CancellationToken.None)).Outcome);

        Assert.Empty((await FindAsync(tenant, scope, Signature(Failure), ExperienceSignatureMatch.Coarse)).Candidates);
    }

    [Fact]
    public async Task A_search_outside_the_authorization_is_Denied_with_no_candidates()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        await SeedAsync(tenant, scope, Failure);

        var result = await Source.FindBySignatureAsync(
            Authorize(NewTenant()), scope, new ExperienceSignatureQuery(Signature(Failure), Eligible, 0d), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Denied, result.Outcome);
        Assert.Empty(result.Candidates);
        Assert.Empty(result.Errors);
    }

    public static TheoryData<string> Malformed => new()
    {
        "fingerprint", "uppercase-fingerprint", "tool", "error-class", "statuses", "undefined-status", "confidence", "limit", "match", "scope",
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task A_malformed_search_is_Invalid_with_errors_and_no_candidates(string malformation)
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        await SeedAsync(tenant, scope, Failure);
        var signature = Signature(Failure);
        var query = new ExperienceSignatureQuery(signature, Eligible, 0d);
        var searchScope = scope;
        switch (malformation)
        {
            case "fingerprint": query = query with { Signature = signature with { Fingerprint = "abc" } }; break;
            case "uppercase-fingerprint": query = query with { Signature = signature with { Fingerprint = signature.Fingerprint.ToUpperInvariant() } }; break;
            case "tool": query = query with { Signature = signature with { ToolName = " " } }; break;
            case "error-class": query = query with { Signature = signature with { ErrorClass = "" } }; break;
            case "statuses": query = query with { EligibleStatuses = [] }; break;
            case "undefined-status": query = query with { EligibleStatuses = [(ExperienceStatus)99] }; break;
            case "confidence": query = query with { MinimumConfidence = 1.5 }; break;
            case "limit": query = query with { Limit = ExperienceSignatureQuery.MaxLimit + 1 }; break;
            case "match": query = query with { Match = (ExperienceSignatureMatch)7 }; break;
            case "scope": searchScope = scope with { ProjectId = " " }; break;
        }

        var result = await Source.FindBySignatureAsync(Authorize(tenant), searchScope, query, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, result.Outcome);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task An_already_cancelled_search_throws_OperationCanceledException()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Source.FindBySignatureAsync(
            Authorize(tenant), scope, new ExperienceSignatureQuery(Signature(Failure), Eligible, 0d), cancelled.Token));
    }

    /// <summary>The signature of a failed call to <see cref="FailingTool"/>.</summary>
    protected static ExperienceFailureSignature Signature(string error, string tool = FailingTool) =>
        ExperienceFailureSignature.From(tool, error)!;

    /// <summary>
    /// A verified record whose first attempt's call to <paramref name="tool"/> failed with <paramref name="error"/>, and,
    /// unless <paramref name="resolved"/> is <see langword="false"/>, whose second attempt's call succeeded.
    /// </summary>
    protected static ExperienceRecord Failing(
        Scope scope,
        string error,
        string tool = FailingTool,
        ExperienceStatus status = ExperienceStatus.Validated,
        double confidence = 0.75,
        bool resolved = true,
        ReflectionAuthorship authorship = ReflectionAuthorship.Deterministic)
    {
        var record = Record(scope, status, taskId: "fixture-task", summary: "Run the order tests", lesson: "Restore the fixtures first", confidence: confidence);
        return record with
        {
            Attempts = resolved
                ? [Attempt(0, Call(tool, error, 0)), Attempt(1, Call(tool, null, 0))]
                : [Attempt(0, Call(tool, error, 0))],
            Outcome = record.Outcome with { Status = TaskVerificationStatus.Verified },
            Reflection = record.Reflection! with { Authorship = authorship },
        };
    }

    private async Task<Guid> SeedAsync(
        string tenant,
        Scope scope,
        string error,
        ExperienceStatus status = ExperienceStatus.Validated,
        double confidence = 0.75,
        string tool = FailingTool,
        bool resolved = true,
        ReflectionAuthorship authorship = ReflectionAuthorship.Deterministic)
    {
        var record = Failing(scope, error, tool, status, confidence, resolved, authorship);
        Assert.Equal(ExperienceStoreOutcome.Created, (await RecordStore.CreateAsync(Authorize(tenant), record, CancellationToken.None)).Outcome);
        return record.ExperienceId;
    }

    private Task<ExperienceSignatureSearchResult> FindAsync(
        string tenant,
        Scope scope,
        ExperienceFailureSignature signature,
        ExperienceSignatureMatch match = ExperienceSignatureMatch.Exact,
        double minimumConfidence = 0d,
        int limit = ExperienceSignatureQuery.DefaultLimit,
        bool excludeModelAuthored = false) =>
        Source.FindBySignatureAsync(
            Authorize(tenant),
            scope,
            new ExperienceSignatureQuery(signature, Eligible, minimumConfidence, match, limit) { ExcludeModelAuthored = excludeModelAuthored },
            CancellationToken.None);

    private static Guid[] Ids(ExperienceSignatureSearchResult result) =>
        [.. result.Candidates.Select(candidate => candidate.Record.ExperienceId)];

    private static Attempt Attempt(int sequence, params ToolCallRecord[] calls) =>
        new(Guid.NewGuid(), sequence, Time, TimeSpan.FromSeconds(1), calls, null, null);

    private static ToolCallRecord Call(string tool, string? error, int sequence) =>
        new(Guid.NewGuid(), sequence, tool, new Dictionary<string, object?>(), Time, TimeSpan.FromSeconds(1), error is null ? "ok" : null, error);
}

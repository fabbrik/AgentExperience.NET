using AgentExperience.Core.Confidence;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Lifecycle;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Story 10.2: the confidence engine is a port. The default reproduces the heuristic and its plain
/// <c>"1.0.0"</c> exactly; a host engine supplies the score and nothing else, is recorded as
/// <c>"{RuleId}/{RuleVersion}"</c>, has its identity checked at construction, and has every score checked
/// before anything is written.
/// </summary>
public class ConfidenceEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");
    private static readonly AuthorizationContext Authorization = new("tenant-1", "principal-7", ["experience:write"], Now);
    private const string SecretSummary = "secret-task-summary-content";

    [Fact]
    public void The_default_engine_is_the_heuristic_under_its_own_identity()
    {
        var engine = ReuseConfidenceHeuristicEngine.Instance;

        Assert.Equal("reuse-heuristic", engine.RuleId);
        Assert.Equal(ReuseConfidenceHeuristicEngine.HeuristicRuleId, engine.RuleId);
        Assert.Equal("1.0.0", engine.RuleVersion);
        Assert.Equal(ReuseConfidenceHeuristic.RuleVersion, engine.RuleVersion);

        var record = Record(1, 0);
        foreach (var (s, f) in new[] { (0, 0), (1, 0), (2, 0), (2, 1), (7, 3) })
        {
            Assert.Equal(ReuseConfidenceHeuristic.Score(s, f), engine.Score(new ExperienceConfidenceInput(record, s, f)));
        }
    }

    [Fact]
    public async Task With_no_host_engine_a_confirmation_then_a_contradiction_walk_two_thirds_to_three_quarters_to_three_fifths_under_1_0_0()
    {
        // Both the omitted engine and the explicit default must behave as the library always has.
        foreach (var engine in new IExperienceConfidenceEngine?[] { null, ReuseConfidenceHeuristicEngine.Instance })
        {
            var store = new EngineStore(Record(1, 0));
            var service = Trusting(store, engine);

            var confirmation = await service.ApplyEvidenceAsync(Authorization, Machine(store.Record.ExperienceId), CancellationToken.None);
            var contradiction = await service.ApplyEvidenceAsync(
                Authorization, Machine(store.Record.ExperienceId, ConfidenceEvidenceKind.Contradicting), CancellationToken.None);

            Assert.Equal(3d / 4d, confirmation.ReuseConfidence);
            Assert.Equal(3d / 5d, contradiction.ReuseConfidence);
            Assert.Equal(ExperienceStatus.Contested, contradiction.Status);
            Assert.All(store.Commits, commit => Assert.Equal("1.0.0", commit.Confidence!.RuleVersion));
            Assert.Equal(3d / 5d, store.Record.ReuseConfidence);
        }
    }

    [Fact]
    public async Task A_host_engine_supplies_the_stored_score_and_is_recorded_as_rule_id_slash_version()
    {
        var store = new EngineStore(Record(1, 0));
        var engine = new StubEngine("bayes", "2.1", _ => 0.9);
        var service = Trusting(store, engine);

        var result = await service.ApplyEvidenceAsync(Authorization, Machine(store.Record.ExperienceId), CancellationToken.None);

        Assert.Equal(ConfidenceUpdateOutcome.Applied, result.Outcome);
        Assert.Equal(0.9, result.ReuseConfidence);
        Assert.Equal(0.9, store.Record.ReuseConfidence);

        // On the event, and on what the store was handed for its ledger.
        Assert.Equal("bayes/2.1", result.Event!.Confidence!.RuleVersion);
        Assert.Equal("bayes/2.1", result.Update!.RuleVersion);
        Assert.Equal("bayes/2.1", Assert.Single(store.Commits).Confidence!.RuleVersion);

        // The engine owns only the score: counters and prior values are the library's.
        Assert.Equal(2d / 3d, result.Update.PriorReuseConfidence);
        Assert.Equal((1, 2), (result.Update.PriorSupportingValidations, result.Update.NewSupportingValidations));
        Assert.Equal(ExperienceStatus.Validated, result.Status);
    }

    [Fact]
    public async Task A_host_engine_cannot_change_the_status_a_contradiction_moves_a_record_to()
    {
        var store = new EngineStore(Record(1, 0));
        var service = Trusting(store, new StubEngine("optimist", "1", _ => 1d));

        var result = await service.ApplyEvidenceAsync(
            Authorization, Machine(store.Record.ExperienceId, ConfidenceEvidenceKind.Contradicting), CancellationToken.None);

        Assert.Equal(1d, result.ReuseConfidence);
        Assert.Equal(ExperienceStatus.Contested, result.Status);
        Assert.Equal(1, result.Contradictions);
    }

    [Fact]
    public async Task The_engine_is_given_the_record_it_read_and_the_counters_after_this_evidence()
    {
        var store = new EngineStore(Record(4, 2));
        var read = store.Record;
        var engine = new StubEngine("bayes", "2.1", _ => 0.5);
        var service = Trusting(store, engine);

        await service.ApplyEvidenceAsync(Authorization, Machine(read.ExperienceId), CancellationToken.None);
        var afterSupporting = Assert.Single(engine.Inputs);
        Assert.Same(read, afterSupporting.Record);
        Assert.Equal((5, 2), (afterSupporting.SupportingValidations, afterSupporting.Contradictions));

        var second = store.Record;
        await service.ApplyEvidenceAsync(Authorization, Machine(read.ExperienceId, ConfidenceEvidenceKind.Contradicting), CancellationToken.None);
        var afterContradiction = engine.Inputs[1];
        Assert.Same(second, afterContradiction.Record);
        Assert.Equal((5, 3), (afterContradiction.SupportingValidations, afterContradiction.Contradictions));
    }

    public static TheoryData<double> NotAScore => new() { 1.2, -0.1, double.NaN, double.PositiveInfinity, double.NegativeInfinity };

    [Theory]
    [MemberData(nameof(NotAScore))]
    public async Task A_score_outside_zero_and_one_throws_naming_the_rule_and_nothing_is_committed(double score)
    {
        var store = new EngineStore(Record(1, 0));
        var before = store.Record;
        var service = Trusting(store, new StubEngine("bayes", "2.1", _ => score));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApplyEvidenceAsync(Authorization, Machine(before.ExperienceId), CancellationToken.None));

        Assert.Contains("bayes/2.1", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSummary, thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(before.ExperienceId.ToString(), thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.Commits);
        Assert.Same(before, store.Record);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(1d)]
    public async Task The_bounds_themselves_are_scores(double score)
    {
        var store = new EngineStore(Record(1, 0));
        var service = Trusting(store, new StubEngine("bounds", "1", _ => score));

        var result = await service.ApplyEvidenceAsync(Authorization, Machine(store.Record.ExperienceId), CancellationToken.None);

        Assert.Equal(score, result.ReuseConfidence);
    }

    [Fact]
    public async Task An_engine_exception_propagates_unchanged_and_nothing_is_committed()
    {
        var store = new EngineStore(Record(1, 0));
        var before = store.Record;
        var failure = new FormatException("the engine's own failure");
        var service = Trusting(store, new StubEngine("bayes", "2.1", _ => throw failure));

        var thrown = await Assert.ThrowsAsync<FormatException>(
            () => service.ApplyEvidenceAsync(Authorization, Machine(before.ExperienceId), CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.Empty(store.Commits);
        Assert.Same(before, store.Record);
    }

    public static TheoryData<string?, string?> BadIdentities => new()
    {
        { "", "1.0" },
        { null, "1.0" },
        { "has space", "1.0" },
        { new string('a', 65), "1.0" },
        { "reuse-heuristic", "1.0" },
        { "reuse-heuristic", "1.0.0" },
        { "bayes/1", "1.0" },
        { "bayes", "" },
        { "bayes", null },
        { "bayes", "2 1" },
        { "bayes", new string('1', 65) },
        { "bayés", "1" },
    };

    [Theory]
    [MemberData(nameof(BadIdentities))]
    public void A_malformed_or_borrowed_identity_is_refused_at_construction(string? ruleId, string? ruleVersion)
    {
        var engine = new StubEngine(ruleId!, ruleVersion!, _ => 0.5);

        var thrown = Assert.Throws<ArgumentException>(() => Trusting(new EngineStore(Record(1, 0)), engine));

        Assert.Equal("confidenceEngine", thrown.ParamName);
    }

    [Fact]
    public async Task An_identity_at_the_limits_is_accepted()
    {
        var ruleId = new string('a', 63) + "Z";
        var store = new EngineStore(Record(1, 0));
        var service = Trusting(store, new StubEngine(ruleId, "A.b_c-9", _ => 0.5));

        var result = await service.ApplyEvidenceAsync(Authorization, Machine(store.Record.ExperienceId), CancellationToken.None);

        Assert.Equal($"{ruleId}/A.b_c-9", result.Update!.RuleVersion);
    }

    [Fact]
    public async Task A_confidence_read_that_excludes_host_trusted_evidence_recomputes_with_the_engine()
    {
        var store = new EngineStore(Record(1, 0));
        var engine = new StubEngine("bayes", "2.1", input => 0.1 + (0.1 * input.SupportingValidations) + (0.01 * input.Contradictions));
        var service = Trusting(store, engine);

        await service.ApplyEvidenceAsync(Authorization, Machine(store.Record.ExperienceId), CancellationToken.None);
        await service.ApplyEvidenceAsync(Authorization, Machine(store.Record.ExperienceId), CancellationToken.None);
        await service.ApplyEvidenceAsync(Authorization, Machine(store.Record.ExperienceId, ConfidenceEvidenceKind.Contradicting), CancellationToken.None);
        engine.Inputs.Clear();

        var all = await service.ReadConfidenceAsync(Authorization, TestScope, store.Record.ExperienceId, ConfidenceEvidenceFilter.All, CancellationToken.None);
        Assert.Equal(store.Record.ReuseConfidence, all.Report!.ReuseConfidence);
        Assert.Empty(engine.Inputs); // nothing excluded: the stored score, not a recomputation

        var excluded = await service.ReadConfidenceAsync(
            Authorization, TestScope, store.Record.ExperienceId, ConfidenceEvidenceFilter.ExcludeHostTrusted, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, excluded.Outcome);
        var input = Assert.Single(engine.Inputs);
        Assert.Equal((1, 0), (input.SupportingValidations, input.Contradictions));
        Assert.Equal(store.Record.ExperienceId, input.Record.ExperienceId);
        Assert.Equal(0.1 + 0.1, excluded.Report!.ReuseConfidence);
        Assert.NotEqual(ReuseConfidenceHeuristic.Score(1, 0), excluded.Report.ReuseConfidence);
    }

    [Fact]
    public async Task A_confidence_read_refuses_an_engine_score_outside_zero_and_one()
    {
        var store = new EngineStore(Record(1, 0));
        var score = 0.5;
        var service = Trusting(store, new StubEngine("bayes", "2.1", _ => score));
        await service.ApplyEvidenceAsync(Authorization, Machine(store.Record.ExperienceId), CancellationToken.None);

        score = double.NaN;
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadConfidenceAsync(
            Authorization, TestScope, store.Record.ExperienceId, ConfidenceEvidenceFilter.ExcludeHostTrusted, CancellationToken.None));

        Assert.Contains("bayes/2.1", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_engine_registered_in_the_container_scores_evidence()
    {
        var store = new EngineStore(Record(1, 0));
        var engine = new StubEngine("bayes", "2.1", _ => 0.9);
        var services = new ServiceCollection();
        services.AddSingleton<IExperienceRecordStore>(store);
        services.AddSingleton(new ExperienceIndependenceOptions { Verification = IndependenceVerification.TrustHostSuppliedIdentifiers });
        services.AddAgentExperienceCore(Permissive, new CaptureLimits(8, 8, 1_000, 1_000));
        services.AddSingleton<IExperienceConfidenceEngine>(engine); // after the call: order does not matter

        using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ExperienceLifecycleService>();

        var result = await service.ApplyEvidenceAsync(Authorization, Machine(store.Record.ExperienceId), CancellationToken.None);

        Assert.Equal(0.9, result.ReuseConfidence);
        Assert.Equal("bayes/2.1", result.Update!.RuleVersion);
        Assert.Single(engine.Inputs);
    }

    [Fact]
    public async Task Without_a_registered_engine_the_container_scores_with_the_heuristic()
    {
        var store = new EngineStore(Record(1, 0));
        var services = new ServiceCollection();
        services.AddSingleton<IExperienceRecordStore>(store);
        services.AddSingleton(new ExperienceIndependenceOptions { Verification = IndependenceVerification.TrustHostSuppliedIdentifiers });
        services.AddAgentExperienceCore(Permissive, new CaptureLimits(8, 8, 1_000, 1_000));

        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<ExperienceLifecycleService>()
            .ApplyEvidenceAsync(Authorization, Machine(store.Record.ExperienceId), CancellationToken.None);

        Assert.Equal(3d / 4d, result.ReuseConfidence);
        Assert.Equal("1.0.0", result.Update!.RuleVersion);
    }

    private static readonly SanitizationOptions Permissive = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        ["ToolResult"] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "value" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal),
            MaxDepth: 2,
            MaxFieldCount: 5,
            MaxValueLength: 1_000,
            MaxFieldNameLength: 100),
    });

    private static ExperienceLifecycleService Trusting(IExperienceRecordStore store, IExperienceConfidenceEngine? engine) =>
        new(
            store,
            indexingService: null,
            new ExperienceIndependenceOptions { Verification = IndependenceVerification.TrustHostSuppliedIdentifiers },
            captureService: null,
            deindexingTimeout: null,
            engine);

    private static ApplyConfidenceEvidenceRequest Machine(Guid experienceId, ConfidenceEvidenceKind kind = ConfidenceEvidenceKind.Supporting) => new(
        EventId: Guid.NewGuid(),
        ExperienceId: experienceId,
        Scope: TestScope,
        EvidenceId: Guid.NewGuid(),
        Kind: kind,
        Source: ConfidenceEvidenceSource.Machine,
        RunId: Guid.NewGuid(),
        VerificationRoundId: Guid.NewGuid(),
        Reason: "the lesson was reused",
        Producer: "tests",
        OccurredAt: Now);

    private static ExperienceRecord Record(int supporting, int contradictions) => new(
        ExperienceId: Guid.NewGuid(),
        SourceRunId: Guid.NewGuid(),
        Scope: TestScope,
        TaskId: "task-1",
        TaskSummary: SecretSummary,
        Attempts: [],
        Outcome: new Outcome(TaskVerificationStatus.Verified, [], null, Now),
        CompletionScore: 1,
        Reflection: null,
        Environment: new EnvironmentFingerprint("host", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
        Provenance: new Provenance("tests", null, Now, null),
        Status: ExperienceStatus.Validated,
        ReuseConfidence: ReuseConfidenceHeuristic.Score(supporting, contradictions),
        SupportingValidations: supporting,
        Contradictions: contradictions,
        Revision: 1,
        CreatedAt: Now,
        UpdatedAt: Now);

    /// <summary>A host engine whose score is a function, recording every input it is given.</summary>
    private sealed class StubEngine(string ruleId, string ruleVersion, Func<ExperienceConfidenceInput, double> score) : IExperienceConfidenceEngine
    {
        public List<ExperienceConfidenceInput> Inputs { get; } = [];

        public string RuleId => ruleId;

        public string RuleVersion => ruleVersion;

        public double Score(ExperienceConfidenceInput input)
        {
            Inputs.Add(input);
            return score(input);
        }
    }

    /// <summary>
    /// One record, moved by confidence commits the way the adapter moves it, with the history a confidence
    /// read pages through.
    /// </summary>
    private sealed class EngineStore(ExperienceRecord record) : IExperienceRecordStore
    {
        private readonly List<StoredLifecycleEvent> _history = [];

        public ExperienceRecord Record { get; private set; } = record;

        public List<LifecycleEvent> Commits { get; } = [];

        public Task<ExperienceRecordGetResult> GetAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, CancellationToken cancellationToken) =>
            Task.FromResult(experienceId == Record.ExperienceId && scope == Record.Scope
                ? new ExperienceRecordGetResult(ExperienceStoreOutcome.Found, Record, [])
                : new ExperienceRecordGetResult(ExperienceStoreOutcome.NotFound, null, []));

        public Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(
            AuthorizationContext authorization,
            Scope scope,
            LifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken)
        {
            if (lifecycleEvent.ExpectedRevision != Record.Revision)
            {
                return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.StaleRevision, Record.Revision, null, []));
            }

            Commits.Add(lifecycleEvent);
            var update = lifecycleEvent.Confidence!;
            var revision = Record.Revision + 1;
            Record = Record with
            {
                Status = lifecycleEvent.CurrentStatus,
                Revision = revision,
                ReuseConfidence = update.NewReuseConfidence,
                SupportingValidations = update.NewSupportingValidations,
                Contradictions = update.NewContradictions,
            };
            _history.Add(new StoredLifecycleEvent(lifecycleEvent, Now, revision));
            return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Committed, revision, null, [], update));
        }

        public Task<ExperienceRecordHistoryResult> GetHistoryAsync(AuthorizationContext authorization, ExperienceRecordHistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new ExperienceRecordHistoryResult(
                ExperienceStoreOutcome.Found,
                Record.Revision,
                [.. _history.Where(stored => stored.AppliedRevision > (query.StartAfterRevision ?? -1))],
                []));

        public Task<ExperienceRecordCreateResult> CreateAsync(AuthorizationContext authorization, ExperienceRecord record, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExperienceRecordQueryResult> QueryAsync(AuthorizationContext authorization, ExperienceRecordQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(
            AuthorizationContext authorization,
            Scope scope,
            Guid experienceId,
            Guid replacementExperienceId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

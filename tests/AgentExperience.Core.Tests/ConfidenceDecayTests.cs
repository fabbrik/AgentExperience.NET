using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Retrieval;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Covers read-time confidence decay by domain (story 10.3): one test per row of the story's I/O
/// matrix, the policy's validation, the guarantee that decay never excludes a record, and the DI
/// resolution of a registered <see cref="ConfidenceDecayPolicy"/>.
/// </summary>
public class ConfidenceDecayTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly Scope RequestScope = new("tenant-1", "app-1", "project-1");

    private static readonly AuthorizationContext Authorization = new("tenant-1", "host-principal", ["experience:read"], Now);

    private const string TaskText = "resolve a refund ticket";

    private static readonly ConfidenceDecayPolicy Tiered = new()
    {
        HalfLives = new Dictionary<string, TimeSpan?>
        {
            ["framework-api"] = TimeSpan.FromDays(30),
            ["math"] = null,
        },
    };

    // ---------------------------------------------------------------- matrix: no policy

    [Fact]
    public async Task Without_a_policy_the_confidence_component_is_the_stored_value_and_nothing_is_reported_as_undecayed()
    {
        var record = Record(Id(1), confidence: 0.8, createdAt: Now - TimeSpan.FromDays(365), domain: "framework-api");

        var result = await Service(policy: null, record).RetrieveAsync(Request());

        var ranked = Assert.Single(result.Records);
        var confidence = ConfidenceComponent(ranked);
        Assert.Equal(0.8, confidence.Value);
        Assert.Null(confidence.UndecayedValue);
        Assert.All(ranked.Components, component => Assert.Null(component.UndecayedValue));
        Assert.Equal(new RankingComponent(RankingComponentKind.Confidence, 0.8, RankingWeights.Default.Confidence), confidence);
    }

    [Fact]
    public async Task The_existing_constructors_rank_exactly_as_a_null_policy_does()
    {
        var record = Record(Id(1), confidence: 0.8, createdAt: Now - TimeSpan.FromDays(30), domain: "framework-api");
        var source = new FakeCandidateSource(new ExperienceCandidate(record, 0.6));

        var legacy = await new ExperienceRetrievalService(source, RetrievalPolicy.Default, RankingWeights.Default, new FixedTimeProvider(Now))
            .RetrieveAsync(Request());
        var explicitNull = await Service(policy: null, source).RetrieveAsync(Request());

        Assert.Equal(legacy.Records.Single().Components, explicitNull.Records.Single().Components);
        Assert.Equal(legacy.Records.Single().Score, explicitNull.Records.Single().Score);
    }

    // ---------------------------------------------------------------- matrix: one half-life old

    [Fact]
    public async Task A_record_one_half_life_old_ranks_on_half_its_stored_confidence_and_reports_the_stored_value()
    {
        var record = Record(Id(1), confidence: 0.8, createdAt: Now - TimeSpan.FromDays(30), domain: "framework-api");

        var result = await Service(Tiered, record).RetrieveAsync(Request());

        var ranked = Assert.Single(result.Records);
        var confidence = ConfidenceComponent(ranked);
        Assert.Equal(0.4, confidence.Value, 12);
        Assert.Equal(0.8, confidence.UndecayedValue);
        Assert.Equal(ranked.Components.Sum(component => component.Contribution), ranked.Score, 12);
        Assert.Equal(0.8, ranked.Record.ReuseConfidence); // a read never rewrites the stored value
        Assert.All(
            ranked.Components.Where(component => component.Kind != RankingComponentKind.Confidence),
            component => Assert.Null(component.UndecayedValue));
    }

    // ---------------------------------------------------------------- matrix: no-decay domain

    [Fact]
    public async Task A_domain_mapped_to_null_does_not_decay_even_when_a_default_half_life_is_set()
    {
        var policy = Tiered with { DefaultHalfLife = TimeSpan.FromDays(1) };
        var record = Record(Id(1), confidence: 0.8, createdAt: Now - TimeSpan.FromDays(365), domain: "math");

        var result = await Service(policy, record).RetrieveAsync(Request());

        var confidence = ConfidenceComponent(Assert.Single(result.Records));
        Assert.Equal(0.8, confidence.Value);
        Assert.Null(confidence.UndecayedValue);
    }

    // ---------------------------------------------------------------- matrix: missing domain, default null

    [Theory]
    [InlineData(null)]
    [InlineData("unlisted")]
    [InlineData("Framework-API")] // ordinal: a differently cased domain is not the listed one
    public async Task A_missing_or_unlisted_domain_does_not_decay_when_there_is_no_default(string? domain)
    {
        var record = Record(Id(1), confidence: 0.8, createdAt: Now - TimeSpan.FromDays(365), domain: domain);

        var result = await Service(Tiered, record).RetrieveAsync(Request());

        var confidence = ConfidenceComponent(Assert.Single(result.Records));
        Assert.Equal(0.8, confidence.Value);
        Assert.Null(confidence.UndecayedValue);
    }

    // ---------------------------------------------------------------- matrix: missing domain, default 10d

    [Fact]
    public async Task A_missing_domain_decays_by_the_default_half_life_when_one_is_set()
    {
        var policy = Tiered with { DefaultHalfLife = TimeSpan.FromDays(10) };
        var record = Record(Id(1), confidence: 0.6, createdAt: Now - TimeSpan.FromDays(10), domain: null);

        var result = await Service(policy, record).RetrieveAsync(Request());

        var confidence = ConfidenceComponent(Assert.Single(result.Records));
        Assert.Equal(0.3, confidence.Value, 12);
        Assert.Equal(0.6, confidence.UndecayedValue);
    }

    [Fact]
    public async Task The_domain_is_read_from_the_configured_metadata_key()
    {
        var policy = Tiered with { DomainKey = "area" };
        var keyed = Record(Id(1), confidence: 0.8, createdAt: Now - TimeSpan.FromDays(30),
            metadata: new Dictionary<string, string> { ["area"] = "framework-api", ["domain"] = "math" });

        var result = await Service(policy, keyed).RetrieveAsync(Request());

        Assert.Equal(0.4, ConfidenceComponent(Assert.Single(result.Records)).Value, 12);
    }

    // ---------------------------------------------------------------- matrix: future CreatedAt

    [Fact]
    public async Task A_record_created_in_the_future_decays_by_a_factor_of_one()
    {
        var record = Record(Id(1), confidence: 0.8, createdAt: Now + TimeSpan.FromDays(3), domain: "framework-api");

        var result = await Service(Tiered, record).RetrieveAsync(Request());

        var confidence = ConfidenceComponent(Assert.Single(result.Records));
        Assert.Equal(0.8, confidence.Value);
        Assert.Equal(0.8, confidence.UndecayedValue);
    }

    // ---------------------------------------------------------------- matrix: reorder

    [Fact]
    public async Task Decay_reorders_otherwise_equal_records_so_the_younger_lesson_ranks_first()
    {
        // The older record has the lower ID, so without decay the tie-break would put it first.
        var older = Record(Id(1), confidence: 0.8, createdAt: Now - TimeSpan.FromDays(60), domain: "framework-api");
        var younger = Record(Id(2), confidence: 0.8, createdAt: Now, domain: "framework-api");

        var undecayed = await Service(policy: null, older, younger).RetrieveAsync(Request());
        var decayed = await Service(Tiered, older, younger).RetrieveAsync(Request());

        Assert.Equal([older.ExperienceId, younger.ExperienceId], undecayed.Records.Select(ranked => ranked.Record.ExperienceId));
        Assert.Equal([younger.ExperienceId, older.ExperienceId], decayed.Records.Select(ranked => ranked.Record.ExperienceId));
        Assert.Equal(0.2, ConfidenceComponent(decayed.Records[1]).Value, 12);
    }

    // ---------------------------------------------------------------- decay never excludes

    [Fact]
    public async Task A_record_decayed_below_the_confidence_floor_is_still_returned()
    {
        var record = Record(Id(1), confidence: 0.6, createdAt: Now - TimeSpan.FromDays(3650), domain: "framework-api");
        var source = new FakeCandidateSource(new ExperienceCandidate(record, 0.5));

        var result = await Service(Tiered, source).RetrieveAsync(Request());

        var confidence = ConfidenceComponent(Assert.Single(result.Records));
        Assert.InRange(confidence.Value, 0d, RetrievalPolicy.DefaultMinimumConfidence);
        Assert.Equal(0.6, confidence.UndecayedValue);
        Assert.Empty(result.Excluded);
        Assert.Equal(RetrievalPolicy.DefaultMinimumConfidence, Assert.Single(source.Queries).MinimumConfidence);
    }

    // ---------------------------------------------------------------- matrix: invalid policy

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_zero_or_negative_half_life_is_rejected(int days)
    {
        var halfLife = TimeSpan.FromDays(days);

        Assert.Throws<ArgumentOutOfRangeException>(() => new ConfidenceDecayPolicy { DefaultHalfLife = halfLife });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConfidenceDecayPolicy
        {
            HalfLives = new Dictionary<string, TimeSpan?> { ["framework-api"] = halfLife },
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => Tiered with { DefaultHalfLife = halfLife });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_domain_key_or_half_life_key_is_rejected(string blank)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ConfidenceDecayPolicy { DomainKey = blank });
        Assert.ThrowsAny<ArgumentException>(() => Tiered with { DomainKey = blank });
        Assert.ThrowsAny<ArgumentException>(() => new ConfidenceDecayPolicy
        {
            HalfLives = new Dictionary<string, TimeSpan?> { [blank] = TimeSpan.FromDays(1) },
        });
    }

    [Fact]
    public void A_null_domain_key_null_half_life_map_or_null_key_is_rejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => new ConfidenceDecayPolicy { DomainKey = null! });
        Assert.Throws<ArgumentNullException>(() => new ConfidenceDecayPolicy { HalfLives = null! });
        Assert.ThrowsAny<ArgumentException>(() => new ConfidenceDecayPolicy { HalfLives = new NullKeyMap() });
    }

    [Fact]
    public void The_policy_defaults_to_no_decay_and_copies_its_half_life_map()
    {
        var policy = new ConfidenceDecayPolicy();
        Assert.Equal(ConfidenceDecayPolicy.DefaultDomainKey, policy.DomainKey);
        Assert.Empty(policy.HalfLives);
        Assert.Null(policy.DefaultHalfLife);

        var map = new Dictionary<string, TimeSpan?> { ["framework-api"] = TimeSpan.FromDays(30) };
        var copied = new ConfidenceDecayPolicy { HalfLives = map };
        map["framework-api"] = TimeSpan.FromDays(1);

        Assert.Equal(TimeSpan.FromDays(30), copied.HalfLifeFor("framework-api"));
    }

    [Fact]
    public void The_stored_half_life_map_cannot_be_written_through_a_cast()
    {
        var policy = new ConfidenceDecayPolicy
        {
            HalfLives = new Dictionary<string, TimeSpan?>(StringComparer.OrdinalIgnoreCase) { ["framework-api"] = TimeSpan.FromDays(30) },
        };

        if (policy.HalfLives is IDictionary<string, TimeSpan?> writable)
        {
            Assert.ThrowsAny<NotSupportedException>(() => writable.Add("math", TimeSpan.FromDays(1)));
        }

        Assert.Null(policy.HalfLifeFor("math"));
        Assert.Null(policy.HalfLifeFor("Framework-API")); // ordinal, whatever the caller's comparer was
    }

    // ---------------------------------------------------------------- DI

    [Fact]
    public async Task A_registered_policy_is_the_one_AddAgentExperienceRetrieval_builds_the_service_with()
    {
        var record = Record(Id(1), confidence: 0.8, createdAt: Now - TimeSpan.FromDays(30), domain: "framework-api");
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        services.AddAgentExperienceRetrieval();
        services.AddSingleton<IExperienceCandidateSource>(new FakeCandidateSource(new ExperienceCandidate(record, 0.5)));
        services.AddSingleton(Tiered); // after the call: resolved lazily

        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<ExperienceRetrievalService>().RetrieveAsync(Request());

        var confidence = ConfidenceComponent(Assert.Single(result.Records));
        Assert.Equal(0.4, confidence.Value, 12);
        Assert.Equal(0.8, confidence.UndecayedValue);
    }

    [Fact]
    public async Task Without_a_registered_policy_AddAgentExperienceRetrieval_does_not_decay()
    {
        var record = Record(Id(1), confidence: 0.8, createdAt: Now - TimeSpan.FromDays(30), domain: "framework-api");
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        services.AddSingleton<IExperienceCandidateSource>(new FakeCandidateSource(new ExperienceCandidate(record, 0.5)));
        services.AddAgentExperienceRetrieval();

        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<ExperienceRetrievalService>().RetrieveAsync(Request());

        var confidence = ConfidenceComponent(Assert.Single(result.Records));
        Assert.Equal(0.8, confidence.Value);
        Assert.Null(confidence.UndecayedValue);
    }

    // ---------------------------------------------------------------- helpers

    private static RankingComponent ConfidenceComponent(RankedExperience ranked) =>
        Assert.Single(ranked.Components, component => component.Kind == RankingComponentKind.Confidence);

    private static Guid Id(int n) => Guid.Parse(FormattableString.Invariant($"00000000-0000-0000-0000-{n:000000000000}"));

    private static RetrieveExperienceRequest Request() => new(Authorization, RequestScope, TaskText);

    private static ExperienceRetrievalService Service(ConfidenceDecayPolicy? policy, params ExperienceRecord[] records) =>
        Service(policy, new FakeCandidateSource(records.Select(record => new ExperienceCandidate(record, 0.5)).ToArray()));

    private static ExperienceRetrievalService Service(ConfidenceDecayPolicy? policy, FakeCandidateSource source) =>
        new(source, RetrievalPolicy.Default, RankingWeights.Default, new FixedTimeProvider(Now), null, null, null, policy);

    private static ExperienceRecord Record(
        Guid id,
        double confidence,
        DateTimeOffset createdAt,
        string? domain = null,
        IReadOnlyDictionary<string, string>? metadata = null) => new(
            ExperienceId: id,
            SourceRunId: Guid.NewGuid(),
            Scope: RequestScope,
            TaskId: "refund-ticket",
            TaskSummary: "Resolve a refund ticket",
            Attempts: [],
            Outcome: new Outcome(TaskVerificationStatus.Verified, [], "checks passed", Now),
            CompletionScore: 1,
            Reflection: null,
            Environment: new EnvironmentFingerprint(
                "worker-01",
                "10.0.0",
                "linux-x64",
                null,
                metadata ?? (domain is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string> { [ConfidenceDecayPolicy.DefaultDomainKey] = domain })),
            Provenance: new Provenance("tests", null, Now, null),
            Status: ExperienceStatus.Validated,
            ReuseConfidence: confidence,
            SupportingValidations: 1,
            Contradictions: 0,
            Revision: 1,
            CreatedAt: createdAt,
            // Recency reads UpdatedAt; holding it at now isolates the confidence component.
            UpdatedAt: Now);

    /// <summary>A map that yields a null key, which no <see cref="Dictionary{TKey, TValue}"/> can hold.</summary>
    private sealed class NullKeyMap : IReadOnlyDictionary<string, TimeSpan?>
    {
        private static readonly KeyValuePair<string, TimeSpan?>[] Entries = [new(null!, TimeSpan.FromDays(1))];

        public TimeSpan? this[string key] => throw new KeyNotFoundException();

        public IEnumerable<string> Keys => Entries.Select(entry => entry.Key);

        public IEnumerable<TimeSpan?> Values => Entries.Select(entry => entry.Value);

        public int Count => Entries.Length;

        public bool ContainsKey(string key) => false;

        public bool TryGetValue(string key, out TimeSpan? value)
        {
            value = null;
            return false;
        }

        public IEnumerator<KeyValuePair<string, TimeSpan?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, TimeSpan?>>)Entries).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class FakeCandidateSource(params ExperienceCandidate[] candidates) : IExperienceCandidateSource
    {
        public List<ExperienceCandidateQuery> Queries { get; } = [];

        public Task<ExperienceCandidateSearchResult> SearchAsync(
            AuthorizationContext authorization,
            ExperienceCandidateQuery query,
            CancellationToken cancellationToken)
        {
            lock (Queries)
            {
                Queries.Add(query);
            }

            return Task.FromResult(new ExperienceCandidateSearchResult(ExperienceStoreOutcome.Found, candidates, []));
        }
    }

    /// <summary>
    /// A clock frozen at a known instant. Its timers never fire, so the retrieval timeout cannot trip on
    /// a slow runner: without this override, <see cref="TimeProvider.CreateTimer"/> is a real-time timer.
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

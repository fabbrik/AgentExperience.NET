using System.Globalization;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Retrieval;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// One <c>retrieval-policy</c> case: the candidates a store returned (each record with its relevance, in the store's
/// order), the instant ranking runs at, the request's preferred environment and model-authored exclusion, and the
/// policy and confidence decay, as JSON (as <c>policy.json</c> holds them).
/// </summary>
internal sealed record RetrievalPolicyCase(
    string Description,
    IReadOnlyList<(ExperienceRecord Record, double Relevance)> Candidates,
    DateTimeOffset Now,
    JsonObject Policy,
    JsonObject? ConfidenceDecay,
    IReadOnlyDictionary<string, string> PreferredEnvironment,
    bool ExcludeModelAuthored = false)
{
    /// <summary>
    /// The vector channel's candidates, in the index's order (<c>vector-candidates.json</c>), or <see langword="null"/>
    /// for a text-only case (no file).
    /// </summary>
    public IReadOnlyList<(ExperienceRecord Record, double Relevance)>? VectorCandidates { get; init; }
}

/// <summary>What retrieval asked the store for and what it answered, for one case.</summary>
internal sealed record RetrievalPolicyRun(ExperienceCandidateQuery? Query, ExperienceRetrievalResult Result)
{
    /// <summary>What the embedding index was asked for, in a hybrid case.</summary>
    public ExperienceVectorQuery? VectorQuery { get; init; }
}

/// <summary>
/// Contract 1.12's <c>retrieval-policy</c> family (no database): the whole retrieval policy. The candidates a store
/// returned, the instant and the policy (<see cref="RetrievalPolicy"/> and <see cref="ConfidenceDecayPolicy"/>) go in;
/// what <see cref="ExperienceRetrievalService"/> asks the store for, its outcome, whether the result is truncated, the
/// ranked records with every component and the exclusions with their reasons come out. <c>policy-validation.json</c>
/// pins which policies .NET accepts and how it refuses the rest.
/// </summary>
internal static class RetrievalPolicyContract
{
    public const string Family = "retrieval-policy";

    public const string ValidationFile = "policy-validation.json";

    /// <summary>The task text every case retrieves for: the store is a fixture, so it only has to be valid.</summary>
    public const string TaskText = "rotate the signing keys";

    private const int F = 0xc0;

    private const long TicksPerMicrosecond = 10;

    private static readonly DateTimeOffset Now = Fixed.T0;

    private static readonly IReadOnlyDictionary<string, string> NoPreference = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Every case, by directory name.</summary>
    public static IReadOnlyList<(string Name, RetrievalPolicyCase Case)> Cases { get; } = Build();

    /// <summary>The policy the cross-language test runs in every language: an expiry and per-domain decay.</summary>
    public static JsonObject CrossLanguagePolicy() => Policy(Default(), p =>
    {
        p["maxAgeTicks"] = TimeSpan.FromDays(180).Ticks;
        p["recencyHalfLifeTicks"] = TimeSpan.FromDays(14).Ticks;
    });

    /// <summary>The confidence decay the cross-language test runs in every language.</summary>
    public static JsonObject CrossLanguageDecay() => WithDefault(
        Decay(("framework-api", TimeSpan.FromDays(30)), ("security", TimeSpan.FromDays(90)), ("math", null)),
        TimeSpan.FromDays(365));

    private static List<(string Name, RetrievalPolicyCase Case)> Build() =>
    [
        ("default-policy", DefaultPolicy()),
        ("max-age", MaxAge()),
        ("decay-one-domain", DecayOneDomain()),
        ("decay-several-domains", DecaySeveralDomains()),
        ("decay-domain-without-a-policy", DecayDomainWithoutAPolicy()),
        ("decay-custom-domain-key", DecayCustomDomainKey()),
        ("decay-and-minimum-confidence", DecayAndMinimumConfidence()),
        ("minimum-confidence-after-decay", MinimumConfidenceAfterDecay()),
        ("recency-half-life", RecencyHalfLife()),
        ("candidate-limit-truncates", CandidateLimitTruncates()),
        ("candidate-limit-exactly-reached", CandidateLimitExactlyReached()),
        ("max-age-and-decay", MaxAgeAndDecay()),
        ("model-authored-excluded", ModelAuthoredExcluded()),
        ("hybrid-vector-floor-from-the-policy", HybridVectorFloor()),
        ("hybrid-per-channel-candidate-limit", HybridPerChannelCandidateLimit()),
        ("hybrid-floor-before-truncation", HybridFloorBeforeTruncation()),
    ];

    private static RetrievalPolicyCase ModelAuthoredExcluded() => new(
        "Model-authored lessons excluded, under MaxAge 90 days. Each candidate is excluded by the first rule that applies, " +
        "in .NET's order: a model-authored Candidate is IneligibleStatus; a model-authored record, expired or not, is " +
        "ModelAuthored; an expired deterministic one is Expired. A record whose producer is the library's own model-backed " +
        "reflector counts as model-authored whatever authorship it declares. The exclusions are listed in the store's order.",
        [
            (Lesson(1, created: Now.AddDays(-10), updated: Now.AddDays(-1)), 1),
            (Lesson(2, created: Now.AddDays(-10), updated: Now.AddDays(-1), modelAuthored: true), 1),
            (Lesson(3, created: Now.AddDays(-10), updated: Now.AddDays(-1), status: ExperienceStatus.Candidate, modelAuthored: true), 1),
            (Lesson(4, created: Now.AddDays(-200), updated: Now.AddDays(-100), modelAuthored: true), 1),
            (Lesson(5, created: Now.AddDays(-200), updated: Now.AddDays(-100)), 1),
            (LibraryReflected(Lesson(6, created: Now.AddDays(-10), updated: Now.AddDays(-1))), 1),
            (Lesson(7, created: Now.AddDays(-10), updated: Now.AddDays(-2), status: ExperienceStatus.Reinforced), 0.75),
        ],
        Now,
        Policy(Default(), p => p["maxAgeTicks"] = TimeSpan.FromDays(90).Ticks),
        ConfidenceDecay: null,
        NoPreference,
        ExcludeModelAuthored: true);

    private static RetrievalPolicyCase HybridVectorFloor() => new(
        "Hybrid, with the policy's minimum vector relevance (0.75): the vector candidates below it are dropped before the " +
        "merge (one exactly at it is kept), text candidates are never floored, a record both channels found keeps its text " +
        "position and the higher relevance, and the index is asked for the policy's floor, confidence and limit plus one.",
        [
            (Lesson(1, created: Now.AddDays(-10), updated: Now.AddDays(-1)), 0.5),
            (Lesson(2, created: Now.AddDays(-10), updated: Now.AddDays(-1)), 0.25),
        ],
        Now,
        Policy(Default(), p => p["minimumVectorRelevance"] = 0.75),
        ConfidenceDecay: null,
        NoPreference)
    {
        VectorCandidates =
        [
            (Lesson(1, created: Now.AddDays(-10), updated: Now.AddDays(-1)), 0.9),
            (Lesson(3, created: Now.AddDays(-10), updated: Now.AddDays(-1)), 0.75),
            (Lesson(4, created: Now.AddDays(-10), updated: Now.AddDays(-1)), 0.7),
            (Lesson(2, created: Now.AddDays(-10), updated: Now.AddDays(-1)), 0.74),
        ],
    };

    private static RetrievalPolicyCase HybridPerChannelCandidateLimit() => new(
        "Hybrid, with a candidate limit of 2: each channel is asked for 3 and considered to its first 2. The text channel " +
        "returned 3, so the result is truncated; the vector channel's two are merged after the text channel's, one of them " +
        "already found by text. MaxAge and decay apply to the merged list as to text candidates.",
        [
            (Lesson(1, created: Now.AddDays(-40), updated: Now.AddDays(-1), domain: "framework-api"), 1),
            (Lesson(2, created: Now.AddDays(-40), updated: Now.AddDays(-200)), 0.75),
            (Lesson(3, created: Now.AddDays(-40), updated: Now.AddDays(-1)), 0.5),
        ],
        Now,
        Policy(Default(), p =>
        {
            p["candidateLimit"] = 2;
            p["maxAgeTicks"] = TimeSpan.FromDays(90).Ticks;
        }),
        Decay(("framework-api", TimeSpan.FromDays(30))),
        NoPreference)
    {
        VectorCandidates =
        [
            (Lesson(4, created: Now.AddDays(-40), updated: Now.AddDays(-3)), 0.8),
            (Lesson(1, created: Now.AddDays(-40), updated: Now.AddDays(-1), domain: "framework-api"), 0.6),
        ],
    };

    private static RetrievalPolicyCase HybridFloorBeforeTruncation() => new(
        "Hybrid, with a candidate limit of 2 and a vector floor of 0.6: the index, asked for 3, returns 3, but the floor " +
        "drops one before the channel is counted, so the vector channel is not truncated, and neither is the result. The " +
        "merged list holds three eligible records, and the ranked result is cut to the candidate limit: the best two.",
        [
            (Lesson(1, created: Now.AddDays(-10), updated: Now.AddDays(-1)), 1),
        ],
        Now,
        Policy(Default(), p =>
        {
            p["candidateLimit"] = 2;
            p["minimumVectorRelevance"] = 0.6;
        }),
        ConfidenceDecay: null,
        NoPreference)
    {
        VectorCandidates =
        [
            (Lesson(2, created: Now.AddDays(-10), updated: Now.AddDays(-2)), 0.9),
            (Lesson(3, created: Now.AddDays(-10), updated: Now.AddDays(-3)), 0.5),
            (Lesson(4, created: Now.AddDays(-10), updated: Now.AddDays(-4)), 0.7),
        ],
    };

    /// <summary><paramref name="record"/> with its reflection produced by the library's own model-backed reflector, declared deterministic.</summary>
    private static ExperienceRecord LibraryReflected(ExperienceRecord record) => record with
    {
        Reflection = record.Reflection! with { Producer = ReflectionAuthorshipConventions.LibraryModelReflectorProducerPrefix + "1.0" },
    };

    private static RetrievalPolicyCase DefaultPolicy() => new(
        "The default policy: a 500 ms timeout, a 0.5 confidence floor, no expiry, a 30-day recency half-life, 50 candidates " +
        "and no confidence decay. The ranking is contract 1.0's, exactly: a year-old record is not expired and a record " +
        "naming a domain does not decay. A record the store should never have returned (a Candidate) is excluded as " +
        "IneligibleStatus.",
        [
            (Lesson(1, created: Now.AddDays(-40), updated: Now.AddDays(-1)), 1),
            (Lesson(2, created: Now.AddDays(-100), updated: Now.AddDays(-30), status: ExperienceStatus.Reinforced, confidence: 0.9), 0.75),
            (Lesson(3, created: Now.AddDays(-400), updated: Now.AddDays(-400), confidence: 0.55), 1),
            (Lesson(4, created: Now.AddDays(-2), updated: Now.AddDays(-2), status: ExperienceStatus.Candidate), 1),
            (Lesson(5, created: Now.AddDays(-300), updated: Now.AddDays(-10), domain: "framework-api"), 0.5),
        ],
        Now,
        Default(),
        ConfidenceDecay: null,
        NoPreference);

    private static RetrievalPolicyCase MaxAge() => new(
        "MaxAge 90 days, measured from updated_at (the last lifecycle activity). A record 89 days old and one exactly 90 " +
        "days old are kept (expiry is strictly older); one a microsecond past 90 days and one a year old are excluded as " +
        "Expired. A record learned 400 days ago but updated yesterday is kept. An expired Candidate is excluded as " +
        "IneligibleStatus: status is checked first.",
        [
            (Lesson(1, created: Now.AddDays(-120), updated: Now.AddDays(-89)), 1),
            (Lesson(2, created: Now.AddDays(-120), updated: Now.AddDays(-90)), 1),
            (Lesson(3, created: Now.AddDays(-120), updated: Now.AddDays(-90).AddTicks(-TicksPerMicrosecond)), 1),
            (Lesson(4, created: Now.AddDays(-400), updated: Now.AddDays(-365)), 1),
            (Lesson(5, created: Now.AddDays(-400), updated: Now.AddDays(-1)), 1),
            (Lesson(6, created: Now.AddDays(-400), updated: Now.AddDays(-200), status: ExperienceStatus.Candidate), 1),
        ],
        Now,
        Policy(Default(), p => p["maxAgeTicks"] = TimeSpan.FromDays(90).Ticks),
        ConfidenceDecay: null,
        NoPreference);

    private static RetrievalPolicyCase DecayOneDomain() => new(
        "One domain decays, with a 30-day half-life measured from created_at: one half-life old ranks on half its stored " +
        "confidence, two half-lives on a quarter, and its Confidence component reports the stored value as undecayedValue. " +
        "A created_at in the future (clock skew) decays nothing but still reports undecayedValue. Domains match ordinally " +
        "(\"Framework-API\" is not listed), and a record with no domain does not decay: their undecayedValue is null.",
        [
            (Lesson(1, created: Now.AddDays(-30), updated: Now.AddDays(-1), domain: "framework-api", confidence: 0.8), 1),
            (Lesson(2, created: Now.AddDays(-60), updated: Now.AddDays(-1), domain: "framework-api", confidence: 0.8), 1),
            (Lesson(3, created: Now.AddHours(1), updated: Now.AddHours(1), domain: "framework-api", confidence: 0.8), 1),
            (Lesson(4, created: Now.AddDays(-60), updated: Now.AddDays(-1), domain: "Framework-API", confidence: 0.8), 1),
            (Lesson(5, created: Now.AddDays(-60), updated: Now.AddDays(-1), confidence: 0.8), 1),
        ],
        Now,
        Default(),
        Decay(("framework-api", TimeSpan.FromDays(30))),
        NoPreference);

    private static RetrievalPolicyCase DecaySeveralDomains() => new(
        "Several domains, each with its own half-life (framework-api 30 days, security 60, business-rule 365), one mapped " +
        "to null (math: never decays, whatever the default says) and a 180-day default for a domain that is not listed " +
        "(ops) or a record with none. Every record is 90 days old, so each decays by its own domain's rate.",
        [
            (Lesson(1, created: Now.AddDays(-90), updated: Now.AddDays(-5), domain: "framework-api", confidence: 0.9), 1),
            (Lesson(2, created: Now.AddDays(-90), updated: Now.AddDays(-5), domain: "security", confidence: 0.9), 1),
            (Lesson(3, created: Now.AddDays(-90), updated: Now.AddDays(-5), domain: "business-rule", confidence: 0.9), 1),
            (Lesson(4, created: Now.AddDays(-90), updated: Now.AddDays(-5), domain: "math", confidence: 0.9), 1),
            (Lesson(5, created: Now.AddDays(-90), updated: Now.AddDays(-5), domain: "ops", confidence: 0.9), 1),
            (Lesson(6, created: Now.AddDays(-90), updated: Now.AddDays(-5), confidence: 0.9), 1),
        ],
        Now,
        Default(),
        WithDefault(
            Decay(
                ("framework-api", TimeSpan.FromDays(30)),
                ("security", TimeSpan.FromDays(60)),
                ("business-rule", TimeSpan.FromDays(365)),
                ("math", null)),
            TimeSpan.FromDays(180)),
        NoPreference);

    private static RetrievalPolicyCase DecayDomainWithoutAPolicy() => new(
        "A decay policy with no default half-life: only the listed domain decays. A record whose domain is not listed, and " +
        "one with no domain, rank on their stored confidence exactly as with no decay policy, with a null undecayedValue.",
        [
            (Lesson(1, created: Now.AddDays(-45), updated: Now.AddDays(-3), domain: "framework-api", confidence: 0.7), 1),
            (Lesson(2, created: Now.AddDays(-45), updated: Now.AddDays(-3), domain: "ops", confidence: 0.7), 1),
            (Lesson(3, created: Now.AddDays(-45), updated: Now.AddDays(-3), confidence: 0.7), 1),
        ],
        Now,
        Default(),
        Decay(("framework-api", TimeSpan.FromDays(30))),
        NoPreference);

    private static RetrievalPolicyCase DecayCustomDomainKey() => new(
        "The domain is read from the metadata key the policy names (area here): a record whose area is listed decays, and " +
        "one that only carries the default key (domain) does not.",
        [
            (Lesson(1, created: Now.AddDays(-20), updated: Now.AddDays(-2), metadata: new() { ["area"] = "framework-api" }), 1),
            (Lesson(2, created: Now.AddDays(-20), updated: Now.AddDays(-2), domain: "framework-api"), 1),
        ],
        Now,
        Default(),
        WithKey(Decay(("framework-api", TimeSpan.FromDays(10))), "area"),
        NoPreference);

    private static RetrievalPolicyCase DecayAndMinimumConfidence() => new(
        "Decay never excludes. The confidence floor (0.5) is the store's, on stored confidence: a record stored at 0.9 and " +
        "decayed to about 0.11 is still ranked, below one stored exactly at the floor that does not decay. Nothing is " +
        "excluded.",
        [
            (Lesson(1, created: Now.AddDays(-90), updated: Now.AddDays(-1), domain: "framework-api", confidence: 0.9), 1),
            (Lesson(2, created: Now.AddDays(-90), updated: Now.AddDays(-1), confidence: 0.5), 1),
        ],
        Now,
        Default(),
        Decay(("framework-api", TimeSpan.FromDays(30))),
        NoPreference);

    private static RetrievalPolicyCase MinimumConfidenceAfterDecay() => new(
        "A custom confidence floor (0.75) is what the store is asked for (query.minConfidence); every candidate it returned " +
        "is at or above it. A 10-day default half-life then decays them all, and those decayed below the floor are still " +
        "ranked, now by decayed confidence: the younger record stored at 0.75 outranks the older one stored at 0.95.",
        [
            (Lesson(1, created: Now.AddDays(-30), updated: Now.AddDays(-2), confidence: 0.95), 1),
            (Lesson(2, created: Now.AddDays(-5), updated: Now.AddDays(-2), confidence: 0.75), 1),
            (Lesson(3, created: Now.AddDays(-15), updated: Now.AddDays(-2), confidence: 0.85), 1),
        ],
        Now,
        Policy(Default(), p => p["minimumConfidence"] = 0.75),
        WithDefault(Decay(), TimeSpan.FromDays(10)),
        NoPreference);

    private static RetrievalPolicyCase RecencyHalfLife() => new(
        "A 7-day recency half-life: updated a week ago scores 0.5 on recency, two weeks ago 0.25, a day ago about 0.906, " +
        "and an updated_at in the future (clock skew) 1. Confidence does not decay without a decay policy.",
        [
            (Lesson(1, created: Now.AddDays(-30), updated: Now.AddDays(-7)), 1),
            (Lesson(2, created: Now.AddDays(-30), updated: Now.AddDays(-14)), 1),
            (Lesson(3, created: Now.AddDays(-30), updated: Now.AddDays(-1)), 1),
            (Lesson(4, created: Now.AddDays(-30), updated: Now.AddHours(1)), 1),
        ],
        Now,
        Policy(Default(), p => p["recencyHalfLifeTicks"] = TimeSpan.FromDays(7).Ticks),
        ConfidenceDecay: null,
        NoPreference);

    private static RetrievalPolicyCase CandidateLimitTruncates() => new(
        "A candidate limit of 3: the store is asked for 4 (the limit plus one) and returns 4 of the 6 that match. Ranking " +
        "considers the first 3 in the store's order, drops the fourth and reports the result truncated; confidence rises " +
        "against the store's order, so ranking reverses it.",
        [.. Enumerable.Range(1, 6).Select(n => (Lesson(n, created: Now.AddDays(-10), updated: Now.AddDays(-1), confidence: 0.5 + (n * 0.05)), 1.0 - (n * 0.0625)))],
        Now,
        Policy(Default(), p => p["candidateLimit"] = 3),
        ConfidenceDecay: null,
        NoPreference);

    private static RetrievalPolicyCase CandidateLimitExactlyReached() => new(
        "A candidate limit of 3 and exactly 3 matches: the store, asked for 4, returns 3, so every one is ranked and the " +
        "result is not truncated.",
        [.. Enumerable.Range(1, 3).Select(n => (Lesson(n, created: Now.AddDays(-10), updated: Now.AddDays(-n)), 1.0))],
        Now,
        Policy(Default(), p => p["candidateLimit"] = 3),
        ConfidenceDecay: null,
        NoPreference);

    private static RetrievalPolicyCase MaxAgeAndDecay() => new(
        "The cross-language test's policy: MaxAge 180 days, a 14-day recency half-life, and decay by domain (framework-api " +
        "30 days, security 90, math never, 365 for the rest). Expired records are excluded before anything is scored; " +
        "the rest rank on decayed confidence, and a preferred environment grades them.",
        [
            (Lesson(1, created: Now.AddDays(-60), updated: Now.AddDays(-2), domain: "framework-api", confidence: 0.95), 1),
            (Lesson(2, created: Now.AddDays(-60), updated: Now.AddDays(-20), domain: "security", confidence: 0.8, status: ExperienceStatus.Reinforced), 0.6000000238418579),
            (Lesson(3, created: Now.AddDays(-500), updated: Now.AddDays(-179), domain: "math", confidence: 0.7), 1),
            (Lesson(4, created: Now.AddDays(-200), updated: Now.AddDays(-181), domain: "math", confidence: 0.99), 1),
            (Lesson(5, created: Now.AddDays(-30), updated: Now.AddDays(-30), confidence: 0.6, region: "us-east"), 0.75),
        ],
        Now,
        CrossLanguagePolicy(),
        CrossLanguageDecay(),
        new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "us-east" });

    /// <summary>
    /// A lesson for a case: <see cref="SearchContract.Seed"/>'s record with these instants, the default environment's
    /// region (or <paramref name="region"/>) and, when given, a domain or extra metadata.
    /// </summary>
    internal static ExperienceRecord Lesson(
        int n,
        DateTimeOffset created,
        DateTimeOffset updated,
        ExperienceStatus status = ExperienceStatus.Validated,
        double confidence = 2d / 3d,
        string? domain = null,
        Dictionary<string, string>? metadata = null,
        string region = "eu-west",
        int family = F,
        Scope? scope = null,
        bool modelAuthored = false)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = region };
        if (domain is not null)
        {
            tags["domain"] = domain;
        }

        foreach (var (key, value) in metadata ?? [])
        {
            tags[key] = value;
        }

        var record = SearchContract.Seed(family, n, "rotate-keys", "Rotate the signing keys", $"Lesson {n}: verified after 1 attempt.", status, confidence, scope, modelAuthored: modelAuthored, metadata: tags);
        return record with { CreatedAt = created, UpdatedAt = updated };
    }

    /// <summary>The default policy as <c>policy.json</c> writes it.</summary>
    public static JsonObject Default() => new()
    {
        ["timeoutTicks"] = RetrievalPolicy.DefaultTimeout.Ticks,
        ["minimumConfidence"] = RetrievalPolicy.DefaultMinimumConfidence,
        ["maxAgeTicks"] = null,
        ["recencyHalfLifeTicks"] = RetrievalPolicy.DefaultRecencyHalfLife.Ticks,
        ["candidateLimit"] = RetrievalPolicy.DefaultCandidateLimit,
        ["maxAbandonedSearches"] = RetrievalPolicy.DefaultMaxAbandonedSearches,
        ["minimumMatchedTerms"] = ExperienceCandidateQuery.DefaultMinimumMatchedTerms,
        ["minimumVectorRelevance"] = null,
    };

    private static JsonObject Policy(JsonObject policy, Action<JsonObject> change)
    {
        change(policy);
        return policy;
    }

    /// <summary>A decay policy with the default key, these half-lives (null: never decays) and no default half-life.</summary>
    private static JsonObject Decay(params (string Domain, TimeSpan? HalfLife)[] halfLives)
    {
        var map = new JsonObject();
        foreach (var (domain, halfLife) in halfLives.OrderBy(entry => entry.Domain, StringComparer.Ordinal))
        {
            map[domain] = halfLife?.Ticks;
        }

        return new JsonObject
        {
            ["domainKey"] = ConfidenceDecayPolicy.DefaultDomainKey,
            ["halfLivesTicks"] = map,
            ["defaultHalfLifeTicks"] = null,
        };
    }

    private static JsonObject WithDefault(JsonObject decay, TimeSpan halfLife)
    {
        decay["defaultHalfLifeTicks"] = halfLife.Ticks;
        return decay;
    }

    private static JsonObject WithKey(JsonObject decay, string key)
    {
        decay["domainKey"] = key;
        return decay;
    }

    /// <summary>
    /// <paramref name="policy"/> as .NET builds it: the five positional values, then the three that only an initializer
    /// sets. Throws exactly as the .NET constructor and initializers do for an invalid value.
    /// </summary>
    public static RetrievalPolicy ReadPolicy(JsonObject policy) =>
        new(
            TimeSpan.FromTicks(Integer(policy["timeoutTicks"]!)),
            Number(policy["minimumConfidence"]!),
            policy["maxAgeTicks"] is { } maxAge ? TimeSpan.FromTicks(Integer(maxAge)) : null,
            TimeSpan.FromTicks(Integer(policy["recencyHalfLifeTicks"]!)),
            checked((int)Integer(policy["candidateLimit"]!)))
        {
            MaxAbandonedSearches = checked((int)Integer(policy["maxAbandonedSearches"]!)),
            MinimumMatchedTerms = checked((int)Integer(policy["minimumMatchedTerms"]!)),
            MinimumVectorRelevance = policy["minimumVectorRelevance"] is { } floor ? Number(floor) : null,
        };

    /// <summary><paramref name="decay"/> as .NET builds it, or <see langword="null"/> for none. Throws as .NET does for an invalid value.</summary>
    public static ConfidenceDecayPolicy? ReadDecay(JsonNode? decay)
    {
        if (decay is not JsonObject value)
        {
            return null;
        }

        var halfLives = new Dictionary<string, TimeSpan?>(StringComparer.Ordinal);
        foreach (var (domain, halfLife) in value["halfLivesTicks"]!.AsObject())
        {
            halfLives[domain] = halfLife is null ? null : TimeSpan.FromTicks(Integer(halfLife));
        }

        return new ConfidenceDecayPolicy
        {
            DomainKey = value["domainKey"]!.GetValue<string>(),
            HalfLives = halfLives,
            DefaultHalfLife = value["defaultHalfLifeTicks"] is { } fallback ? TimeSpan.FromTicks(Integer(fallback)) : null,
        };
    }

    /// <summary>A number as the contract writes one: a JSON number, or the string <c>"NaN"</c>.</summary>
    private static double Number(JsonNode node) =>
        node.GetValueKind() == System.Text.Json.JsonValueKind.String && node.GetValue<string>() == "NaN"
            ? double.NaN
            : double.Parse(node.ToJsonString(), CultureInfo.InvariantCulture);

    /// <summary>An integer as the contract writes one.</summary>
    private static long Integer(JsonNode node) => long.Parse(node.ToJsonString(), CultureInfo.InvariantCulture);

    /// <summary><c>candidates.json</c>: each stored record with the relevance the store gave it, in the store's order.</summary>
    public static JsonArray CandidatesJson(IReadOnlyList<(ExperienceRecord Record, double Relevance)> candidates) =>
        [.. candidates.Select(entry => (JsonNode)new JsonObject
        {
            ["record"] = ContractJson.ToStored(entry.Record),
            ["relevance"] = entry.Relevance,
        })];

    /// <summary>Reads <c>candidates.json</c> as a port does.</summary>
    public static List<(ExperienceRecord Record, double Relevance)> FromCandidatesJson(string json) =>
        [.. JsonNode.Parse(json)!.AsArray().Select(entry => (ContractJson.FromStored(entry!["record"]!), entry["relevance"]!.GetValue<double>()))];

    /// <summary><c>policy.json</c>.</summary>
    public static JsonObject PolicyJson(RetrievalPolicyCase definition) => new()
    {
        ["description"] = definition.Description,
        ["scope"] = JsonNode.Parse(ContractJson.Write(Fixed.Scope)),
        ["taskText"] = TaskText,
        ["now"] = ContractJson.Timestamp(definition.Now),
        ["preferredEnvironment"] = new JsonObject([.. definition.PreferredEnvironment.Select(entry => KeyValuePair.Create(entry.Key, (JsonNode?)entry.Value))]),
        ["excludeModelAuthored"] = definition.ExcludeModelAuthored,
        ["policy"] = definition.Policy.DeepClone(),
        ["confidenceDecay"] = definition.ConfidenceDecay?.DeepClone(),
    };

    /// <summary>
    /// Runs one case through <see cref="ExperienceRetrievalService"/>, over a store that answers with the candidates (at
    /// most the limit it is asked for, as the statement's <c>LIMIT</c> cuts) on a clock fixed at <paramref name="now"/>.
    /// </summary>
    public static RetrievalPolicyRun Run(
        IReadOnlyList<(ExperienceRecord Record, double Relevance)> candidates,
        DateTimeOffset now,
        RetrievalPolicy policy,
        ConfidenceDecayPolicy? decay,
        IReadOnlyDictionary<string, string> preferred,
        bool excludeModelAuthored,
        IReadOnlyList<(ExperienceRecord Record, double Relevance)>? vectorCandidates = null)
    {
        var source = new FixtureSource(candidates);
        var index = vectorCandidates is null ? null : new FixtureIndex(vectorCandidates);
        var service = new ExperienceRetrievalService(
            source,
            policy,
            RankingWeights.Default,
            new FrozenTimeProvider(now),
            embeddingIndex: index,
            embeddingGenerator: index is null ? null : new FixtureGenerator(),
            environmentScorer: null,
            confidenceDecay: decay);
        var request = new RetrieveExperienceRequest(Fixed.Authorization, Fixed.Scope, TaskText)
        {
            PreferredEnvironmentAttributes = preferred,
            ExcludeModelAuthored = excludeModelAuthored,
        };
        var result = service.RetrieveAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        return new RetrievalPolicyRun(source.Query, result) { VectorQuery = index?.Query };
    }

    /// <summary>
    /// <c>expected.json</c>. A hybrid case also has <c>vectorQuery</c>, what the index was asked for, written after
    /// <c>query</c>; a text-only case has none.
    /// </summary>
    public static JsonObject ExpectedJson(RetrievalPolicyRun run)
    {
        var expected = TextExpectedJson(run);
        if (run.VectorQuery is { } vector)
        {
            var ordered = new JsonObject();
            foreach (var (key, value) in expected.ToList())
            {
                expected.Remove(key);
                ordered[key] = value;
                if (key == "query")
                {
                    ordered["vectorQuery"] = new JsonObject
                    {
                        ["minConfidence"] = vector.MinimumConfidence,
                        ["limit"] = vector.Limit,
                        ["excludeModelAuthored"] = vector.ExcludeModelAuthored,
                    };
                }
            }

            return ordered;
        }

        return expected;
    }

    private static JsonObject TextExpectedJson(RetrievalPolicyRun run) => new()
    {
        ["outcome"] = run.Result.Outcome.ToString(),
        ["query"] = run.Query is { } query
            ? new JsonObject
            {
                ["minConfidence"] = query.MinimumConfidence,
                ["limit"] = query.Limit,
                ["minimumMatchedTerms"] = query.MinimumMatchedTerms,
                ["excludeModelAuthored"] = query.ExcludeModelAuthored,
            }
            : null,
        ["truncated"] = run.Result.Truncated,
        ["ranked"] = new JsonArray([.. run.Result.Records.Select(ranked => (JsonNode)new JsonObject
        {
            ["experienceId"] = ranked.Record.ExperienceId.ToString("D", CultureInfo.InvariantCulture),
            ["score"] = ranked.Score,
            ["components"] = new JsonArray([.. ranked.Components.Select(component => (JsonNode)new JsonObject
            {
                ["kind"] = component.Kind.ToString(),
                ["value"] = component.Value,
                ["weight"] = component.Weight,
                ["undecayedValue"] = component.UndecayedValue,
            })]),
        })]),
        ["excluded"] = new JsonArray([.. run.Result.Excluded.Select(excluded => (JsonNode)new JsonObject
        {
            ["experienceId"] = excluded.ExperienceId.ToString("D", CultureInfo.InvariantCulture),
            ["reason"] = excluded.Reason.ToString(),
        })]),
    };

    /// <summary>
    /// <c>policy-validation.json</c>'s inputs: the default policy, the edges .NET accepts, and one invalid value per
    /// validation rule (each with the decay policy it is paired with, or none).
    /// </summary>
    public static IReadOnlyList<(string Description, JsonObject Policy, JsonObject? Decay)> ValidationInputs()
    {
        var day = TimeSpan.FromDays(1).Ticks;
        var micro = TicksPerMicrosecond;
        JsonObject With(string field, JsonNode? value) => Policy(Default(), p => p[field] = value);
        JsonObject DecayWith(Action<JsonObject> change)
        {
            var decay = WithDefault(Decay(("framework-api", TimeSpan.FromDays(30)), ("math", null)), TimeSpan.FromDays(365));
            change(decay);
            return decay;
        }

        return
        [
            ("The default policy and no decay.", Default(), null),
            ("A timeout of exactly one day, the largest.", With("timeoutTicks", day), null),
            ("A timeout of one microsecond.", With("timeoutTicks", micro), null),
            ("A confidence floor of 0.", With("minimumConfidence", 0), null),
            ("A confidence floor of 1.", With("minimumConfidence", 1), null),
            ("A MaxAge of one microsecond.", With("maxAgeTicks", micro), null),
            ("A candidate limit of 1, the smallest.", With("candidateLimit", 1), null),
            ("A candidate limit of 199, the largest (the store is asked for one more, at most 200).", With("candidateLimit", 199), null),
            ("A minimum vector relevance of 0.", With("minimumVectorRelevance", 0), null),
            ("A minimum vector relevance of 1.", With("minimumVectorRelevance", 1), null),
            ("A decay policy with a default half-life and a domain that never decays.", Default(), DecayWith(_ => { })),
            ("A decay policy with no half-lives at all.", Default(), Decay()),
            ("A zero timeout.", With("timeoutTicks", 0), null),
            ("A negative timeout.", With("timeoutTicks", -micro), null),
            ("A timeout longer than one day.", With("timeoutTicks", day + micro), null),
            ("A confidence floor below 0.", With("minimumConfidence", -0.01), null),
            ("A confidence floor above 1.", With("minimumConfidence", 1.01), null),
            ("A confidence floor that is not a number.", With("minimumConfidence", "NaN"), null),
            ("A zero MaxAge.", With("maxAgeTicks", 0), null),
            ("A negative MaxAge.", With("maxAgeTicks", -micro), null),
            ("A zero recency half-life.", With("recencyHalfLifeTicks", 0), null),
            ("A negative recency half-life.", With("recencyHalfLifeTicks", -TimeSpan.FromDays(30).Ticks), null),
            ("A zero candidate limit.", With("candidateLimit", 0), null),
            ("A negative candidate limit.", With("candidateLimit", -1), null),
            ("A candidate limit of 200, one past the largest.", With("candidateLimit", 200), null),
            ("A zero abandoned-search cap.", With("maxAbandonedSearches", 0), null),
            ("A negative abandoned-search cap.", With("maxAbandonedSearches", -1), null),
            ("A zero minimum of matched terms.", With("minimumMatchedTerms", 0), null),
            ("A negative minimum of matched terms.", With("minimumMatchedTerms", -1), null),
            ("A minimum vector relevance below 0.", With("minimumVectorRelevance", -0.01), null),
            ("A minimum vector relevance above 1.", With("minimumVectorRelevance", 1.01), null),
            ("An empty domain key.", Default(), DecayWith(d => d["domainKey"] = string.Empty)),
            ("A blank domain key.", Default(), DecayWith(d => d["domainKey"] = "  ")),
            ("An empty domain in the half-life map.", Default(), DecayWith(d => d["halfLivesTicks"]!.AsObject()[string.Empty] = day)),
            ("A blank domain in the half-life map.", Default(), DecayWith(d => d["halfLivesTicks"]!.AsObject()[" "] = day)),
            ("A zero half-life for a domain.", Default(), DecayWith(d => d["halfLivesTicks"]!.AsObject()["framework-api"] = 0)),
            ("A negative half-life for a domain.", Default(), DecayWith(d => d["halfLivesTicks"]!.AsObject()["framework-api"] = -micro)),
            ("A zero default half-life.", Default(), DecayWith(d => d["defaultHalfLifeTicks"] = 0)),
            ("A negative default half-life.", Default(), DecayWith(d => d["defaultHalfLifeTicks"] = -micro)),
        ];
    }

    /// <summary><c>policy-validation.json</c>: each input, and whether .NET refuses it, naming the parameter and the exception.</summary>
    public static JsonArray ValidationJson()
    {
        var cases = new JsonArray();
        foreach (var (description, policy, decay) in ValidationInputs())
        {
            JsonObject? refusal = null;
            try
            {
                _ = ReadPolicy(policy);
                _ = ReadDecay(decay);
            }
            catch (ArgumentException ex)
            {
                refusal = new JsonObject
                {
                    ["parameter"] = ex.ParamName,
                    ["exception"] = ex.GetType().Name,
                };
            }

            cases.Add(new JsonObject
            {
                ["description"] = description,
                ["policy"] = policy.DeepClone(),
                ["confidenceDecay"] = decay?.DeepClone(),
                ["refusal"] = refusal,
            });
        }

        return cases;
    }

    /// <summary>An index that answers every search with the case's vector candidates, cut at the limit it was asked for, and keeps the query.</summary>
    /// <summary>
    /// A clock fixed at one instant on which no time passes, so the policy's timeout never fires: a case's result is what
    /// retrieval ranks, never a <c>TimedOut</c> that a slow or busy machine's thread pool could cause.
    /// </summary>
    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NeverFires();

        private sealed class NeverFires : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FixtureIndex(IReadOnlyList<(ExperienceRecord Record, double Relevance)> candidates) : IExperienceEmbeddingIndex
    {
        public ExperienceVectorQuery? Query { get; private set; }

        public Task<ExperienceIndexWriteResult> WriteAsync(AuthorizationContext authorization, ExperienceIndexWrite write, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExperienceIndexScanResult> ScanAsync(AuthorizationContext authorization, ExperienceIndexScan scan, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExperienceIndexRemoveResult> RemoveAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExperienceVectorSearchResult> SearchAsync(AuthorizationContext authorization, ExperienceVectorQuery query, CancellationToken cancellationToken)
        {
            Query = query;
            IReadOnlyList<ExperienceCandidate> answer = [.. candidates.Take(query.Limit).Select(entry => new ExperienceCandidate(entry.Record, entry.Relevance))];
            return Task.FromResult(new ExperienceVectorSearchResult(ExperienceVectorSearchOutcome.Found, answer, []));
        }
    }

    /// <summary>A generator with a fixed model and a fixed two-component vector: the index is a fixture, so the vector only has to be valid.</summary>
    private sealed class FixtureGenerator : IExperienceEmbeddingGenerator
    {
        public string ModelId => "fixture-model";

        public int Dimension => 2;

        public Task<ReadOnlyMemory<float>> GenerateAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult<ReadOnlyMemory<float>>(new float[] { 0.6f, 0.8f });
    }

    /// <summary>A store that answers every search with the case's candidates, cut at the limit it was asked for, and keeps the query.</summary>
    private sealed class FixtureSource(IReadOnlyList<(ExperienceRecord Record, double Relevance)> candidates) : IExperienceCandidateSource
    {
        public ExperienceCandidateQuery? Query { get; private set; }

        public Task<ExperienceCandidateSearchResult> SearchAsync(AuthorizationContext authorization, ExperienceCandidateQuery query, CancellationToken cancellationToken)
        {
            Query = query;
            IReadOnlyList<ExperienceCandidate> answer = [.. candidates.Take(query.Limit).Select(entry => new ExperienceCandidate(entry.Record, entry.Relevance))];
            return Task.FromResult(new ExperienceCandidateSearchResult(ExperienceStoreOutcome.Found, answer, []));
        }
    }
}

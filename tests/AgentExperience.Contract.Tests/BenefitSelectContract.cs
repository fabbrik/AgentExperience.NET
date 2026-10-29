using System.Globalization;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Holdout;
using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.Storage.InMemory;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// <c>options.json</c> of a benefit-select case: the benefit policy (<see cref="Enabled"/>,
/// <see cref="PreferMeasuredHelpful"/>, <see cref="PerModel"/>), the invoking agent's model (<see cref="Model"/>,
/// <see langword="null"/> when it has none), the request's own limit (<see cref="RequestLimit"/>, <see langword="null"/>
/// for none), the injection limits and whether model-authored lessons are excluded. Every other injection option is the
/// default (the compact rendering, failure detail <c>ErrorClass</c>, no argument allowlist).
/// </summary>
internal sealed record BenefitSelectOptions(
    string Description,
    bool Enabled,
    bool PreferMeasuredHelpful,
    bool PerModel,
    string? Model,
    int? RequestLimit,
    int MaxRecords,
    int MaxBytes,
    bool ExcludeModelAuthored);

/// <summary>A benefit-select case: the ranked records (best first), the cached estimate and the options.</summary>
internal sealed record BenefitSelectCase(
    IReadOnlyList<(ExperienceRecord Record, RankingInput Ranking)> Records,
    ExperienceBenefitEstimate Estimate,
    BenefitSelectOptions Options);

/// <summary>
/// Contract 1.10's <c>benefit-select</c> family: acting on measured benefit. The ranked records, the estimate the
/// provider's cache holds and the policy go in; what the context provider selects (<c>SelectCandidates</c>: the policy's
/// reordering and omissions, the model-authored exclusion, the record limit), then what the writer's byte budget keeps,
/// and every omission with its reason and detail, come out. Needs no database.
/// </summary>
internal static class BenefitSelectContract
{
    public const string Family = "benefit-select";

    private const int F = 0xa0;

    private const string ModelA = "model-a";
    private const string ModelB = "model-b";
    private const string ModelC = "model-c";
    private const string ModelD = "model-d";

    // The ranked lessons every case draws from.
    private static readonly ExperienceRecord A = Lesson(1, "Rotate the staging keys before production.");
    private static readonly ExperienceRecord B = Lesson(2, "Pause the deploy queue while the keys rotate.");
    private static readonly ExperienceRecord C = Lesson(3, "Re-sign the cached tokens after a rotation.");
    private static readonly ExperienceRecord D = Lesson(4, "Keep the previous key readable for a day.");
    private static readonly ExperienceRecord H = Lesson(5, "Rotate every key at once.");
    private static readonly ExperienceRecord G = Lesson(6, "Check the key vault's audit log after rotating.");
    private static readonly ExperienceRecord M = Lesson(7, "A model wrote this lesson.", modelAuthored: true);
    private static readonly ExperienceRecord HM = Lesson(8, "A model wrote this harmful lesson.", modelAuthored: true);

    // The per-model fleet's lessons.
    private static readonly ExperienceRecord X = Lesson(0x11, "Restart the signer after rotating.");
    private static readonly ExperienceRecord Y = Lesson(0x12, "Skip the canary for a key rotation.");
    private static readonly ExperienceRecord Z = Lesson(0x13, "Rotate the keys in the documented order.");
    private static readonly ExperienceRecord W = Lesson(0x14, "Delete the old key straight away.");
    private static readonly ExperienceRecord V = Lesson(0x15, "Rotate the keys during peak traffic.");
    private static readonly ExperienceRecord U = Lesson(0x16, "Announce the rotation in the team channel.");

    private static readonly BenefitSelectOptions On = new(
        string.Empty,
        Enabled: true,
        PreferMeasuredHelpful: false,
        PerModel: false,
        Model: null,
        RequestLimit: null,
        MaxRecords: ExperienceInjectionLimits.DefaultMaxRecords,
        MaxBytes: ExperienceInjectionLimits.DefaultMaxBytes,
        ExcludeModelAuthored: false);

    /// <summary>Every benefit-select case, by directory name.</summary>
    public static IReadOnlyList<(string Name, BenefitSelectCase Case)> Cases { get; } = Build();

    private static List<(string Name, BenefitSelectCase Case)> Build()
    {
        var mixed = Mixed();
        var scope = ScopeHurts();
        var none = NoEvidence();
        var fleet = Fleet();
        var budget = InjectContract.BudgetFor(Ranked(A, B), InjectOptions.Default) + 1;
        return
        [
            ("one-lesson-hurts", new(Ranked(A, H, B, C), mixed, On with
            {
                Description = "One lesson measured Hurts is omitted as MeasuredHarmful before the record limit, so it takes no slot and the next record fills it.",
                MaxRecords = 3,
            })),
            ("scope-hurts", new(Ranked(A, H, B), scope, On with
            {
                Description = "The scope's overall verdict is Hurts: every record is omitted as MeasuredHarmful with the scope's detail, and nothing is injected.",
            })),
            ("prefer-helpful", new(Ranked(A, B, C, D, G), mixed, On with
            {
                Description = "PreferMeasuredHelpful with no request limit: the lesson measured Helps moves ahead of the rest, each group keeping its order, before the record limit; the limit's details name the reordered ranks.",
                PreferMeasuredHelpful = true,
                MaxRecords = 3,
            })),
            ("prefer-helpful-within-the-request-limit", new(Ranked(A, B, G, C, D), mixed, On with
            {
                Description = "A request limit of 4 over five ranked records: the helpful lesson moves to the front of the host's window; the fifth record was fetched only as backfill, is not selected, and is not reported.",
                PreferMeasuredHelpful = true,
                RequestLimit = 4,
                MaxRecords = 3,
            })),
            ("prefer-helpful-past-the-request-limit", new(Ranked(A, H, B, G, C), mixed, On with
            {
                Description = "A request limit of 2: the window and the backfill past it are reordered separately, so the helpful lesson past the limit never moves into the window. The harmful lesson in the window frees a slot, which the backfill's first record (now the helpful one) takes; the rest of the backfill is not reported.",
                PreferMeasuredHelpful = true,
                RequestLimit = 2,
            })),
            ("per-model", new(Ranked(X, Y, Z, W, V, U), fleet, On with
            {
                Description = "PerModel for model-a: X hurts model-a's runs (pooled it has no evidence); Y has no evidence for model-a, so its pooled Hurts stands; Z helps model-a and moves first; W and V hurt pooled.",
                PreferMeasuredHelpful = true,
                PerModel = true,
                Model = ModelA,
            })),
            ("per-model-other-model", new(Ranked(X, Y, Z, W, V, U), fleet, On with
            {
                Description = "PerModel for model-b: X helps model-b's runs and moves first, Y hurts them, Z has no model-b runs so its pooled Helps stands, and W and V hurt pooled.",
                PreferMeasuredHelpful = true,
                PerModel = true,
                Model = ModelB,
            })),
            ("per-model-scope-hurts", new(Ranked(X, Y, Z, W, V, U), fleet, On with
            {
                Description = "PerModel for model-c, whose runs together were measured Hurts: every record is omitted with the scope's detail.",
                PreferMeasuredHelpful = true,
                PerModel = true,
                Model = ModelC,
            })),
            ("per-model-no-model", new(Ranked(X, Y, Z, W, V, U), fleet, On with
            {
                Description = "PerModel for an agent that reports no model: the pooled verdicts.",
                PreferMeasuredHelpful = true,
                PerModel = true,
                Model = null,
            })),
            ("per-model-unknown", new(Ranked(X, Y, Z, W, V, U), fleet, On with
            {
                Description = "PerModel for an agent whose model is the literal 'unknown': the estimate's 'unknown' group (runs with no recorded model, which together hurt) is never an agent's own, so the pooled verdicts apply.",
                PreferMeasuredHelpful = true,
                PerModel = true,
                Model = ExperienceBenefitEstimate.UnknownModel,
            })),
            ("per-model-unmeasured", new(Ranked(X, Y, Z, W, V, U), fleet, On with
            {
                Description = "PerModel for model-z, which the estimate never measured: the pooled verdicts.",
                PreferMeasuredHelpful = true,
                PerModel = true,
                Model = "model-z",
            })),
            ("pooled-ignores-the-model", new(Ranked(X, Y, Z, W, V, U), fleet, On with
            {
                Description = "PerModel off: the pooled verdicts, whatever the invoking model.",
                PreferMeasuredHelpful = true,
                Model = ModelA,
            })),
            ("policy-disabled", new(Ranked(A, H, B, G), mixed, On with
            {
                Description = "The policy is off: nothing is omitted or reordered, whatever the estimate says.",
                Enabled = false,
                PreferMeasuredHelpful = true,
                MaxRecords = 3,
            })),
            ("no-evidence", new(Ranked(A, H, B, G), none, On with
            {
                Description = "The policy is on and every verdict is NoEvidence: nothing changes.",
                PreferMeasuredHelpful = true,
                MaxRecords = 3,
            })),
            ("existing-omissions", new(Ranked(M, HM, A, B, C, D), mixed, On with
            {
                Description = "Every omission in .NET's order: in rank order, MeasuredHarmful is decided before the model-authored exclusion (so a harmful model-authored lesson is MeasuredHarmful), then ModelAuthored (no detail), then the record limit; the byte budget, applied by the writer, comes last.",
                MaxRecords = 3,
                MaxBytes = budget,
                ExcludeModelAuthored = true,
            })),
        ];
    }

    private static ExperienceRecord Lesson(int n, string lesson, bool modelAuthored = false) =>
        SearchContract.Seed(F, n, "rotate-keys", "Rotate the signing keys", lesson, modelAuthored: modelAuthored);

    private static List<(ExperienceRecord Record, RankingInput Ranking)> Ranked(params ExperienceRecord[] records) =>
        [.. records.Select((record, index) => (record, InjectContract.Ranking(1.0 - (0.05 * index), 0.9)))];

    /// <summary>H hurts and G helps (HM, the model-authored copy, hurts too); together the runs span zero.</summary>
    private static ExperienceBenefitEstimate Mixed()
    {
        var runs = new RunBuilder(F + 1);
        runs.Add(3, HoldoutArm.Treatment, verified: false, null, null, H.ExperienceId, HM.ExperienceId);
        runs.Add(3, HoldoutArm.Holdout, verified: true, null, null, H.ExperienceId, HM.ExperienceId);
        runs.Add(3, HoldoutArm.Treatment, verified: true, null, null, G.ExperienceId);
        runs.Add(3, HoldoutArm.Holdout, verified: false, null, null, G.ExperienceId);
        return runs.Estimate(splitByModel: false);
    }

    /// <summary>Only H's runs: the scope's lessons together hurt.</summary>
    private static ExperienceBenefitEstimate ScopeHurts()
    {
        var runs = new RunBuilder(F + 2);
        runs.Add(3, HoldoutArm.Treatment, verified: false, null, null, H.ExperienceId);
        runs.Add(3, HoldoutArm.Holdout, verified: true, null, null, H.ExperienceId);
        return runs.Estimate(splitByModel: false);
    }

    /// <summary>One run per arm for H and G: below the minimum of 2, so every verdict is NoEvidence.</summary>
    private static ExperienceBenefitEstimate NoEvidence()
    {
        var runs = new RunBuilder(F + 3);
        runs.Add(1, HoldoutArm.Treatment, verified: false, null, null, H.ExperienceId);
        runs.Add(1, HoldoutArm.Holdout, verified: true, null, null, H.ExperienceId);
        runs.Add(1, HoldoutArm.Treatment, verified: true, null, null, G.ExperienceId);
        runs.Add(1, HoldoutArm.Holdout, verified: false, null, null, G.ExperienceId);
        return runs.Estimate(splitByModel: false);
    }

    /// <summary>
    /// A fleet of models, split by model. Pooled: X has no evidence, Y, W and V hurt, Z and U help, and the scope has no
    /// evidence. model-a: X hurts, Y has too few runs, Z helps. model-b: X helps, Y hurts. model-c's runs (V) hurt
    /// together. Runs with no model (W) hurt together, as the 'unknown' group.
    /// </summary>
    private static ExperienceBenefitEstimate Fleet()
    {
        var runs = new RunBuilder(F + 4);
        runs.Add(3, HoldoutArm.Treatment, verified: false, ModelA, ModelB, X.ExperienceId);
        runs.Add(3, HoldoutArm.Holdout, verified: true, ModelA, ModelB, X.ExperienceId);
        runs.Add(3, HoldoutArm.Treatment, verified: true, ModelB, ModelB, X.ExperienceId);
        runs.Add(3, HoldoutArm.Holdout, verified: false, ModelB, ModelB, X.ExperienceId);
        runs.Add(1, HoldoutArm.Treatment, verified: false, ModelA, null, Y.ExperienceId);
        runs.Add(1, HoldoutArm.Holdout, verified: true, ModelA, null, Y.ExperienceId);
        runs.Add(3, HoldoutArm.Treatment, verified: false, ModelB, null, Y.ExperienceId);
        runs.Add(3, HoldoutArm.Holdout, verified: true, ModelB, null, Y.ExperienceId);
        runs.Add(4, HoldoutArm.Treatment, verified: true, ModelA, ModelA, Z.ExperienceId);
        runs.Add(4, HoldoutArm.Holdout, verified: false, ModelA, ModelA, Z.ExperienceId);
        runs.Add(3, HoldoutArm.Treatment, verified: false, null, null, W.ExperienceId);
        runs.Add(3, HoldoutArm.Holdout, verified: true, null, null, W.ExperienceId);
        runs.Add(3, HoldoutArm.Treatment, verified: false, ModelC, ModelC, V.ExperienceId);
        runs.Add(3, HoldoutArm.Holdout, verified: true, ModelC, ModelC, V.ExperienceId);
        runs.Add(8, HoldoutArm.Treatment, verified: true, ModelD, ModelD, U.ExperienceId);
        runs.Add(8, HoldoutArm.Holdout, verified: false, ModelD, ModelD, U.ExperienceId);
        return runs.Estimate(splitByModel: true);
    }

    /// <summary>
    /// Runs one at a time, each a row per lesson it names, with a verified or failed record of its own, at a minimum of 2
    /// runs per arm.
    /// </summary>
    private sealed class RunBuilder(int family)
    {
        public const int Minimum = 2;

        private readonly List<HoldoutOutcomeRow> _rows = [];
        private int _next;

        public void Add(int count, HoldoutArm arm, bool verified, string? runModel, string? lessonModel, params Guid[] lessons)
        {
            for (var i = 0; i < count; i++)
            {
                var run = Fixed.Id(family, 0x100 + _next++);
                foreach (var lesson in lessons)
                {
                    _rows.Add(new HoldoutOutcomeRow(run, arm, lesson, Fixed.T0.AddMinutes(_next), verified ? TaskVerificationStatus.Verified : TaskVerificationStatus.Failed)
                    {
                        RunModel = runModel,
                        LessonModel = lessonModel,
                    });
                }
            }
        }

        public ExperienceBenefitEstimate Estimate(bool splitByModel) =>
            ExperienceBenefitEstimator.Estimate(_rows, new ExperienceBenefitOptions { MinimumRunsPerArm = Minimum, SplitByModel = splitByModel });
    }

    /// <summary>
    /// What the .NET context provider selects for <paramref name="records"/>, then what its writer keeps: the provider is
    /// built with the case's options, the view is the one its cache would hand an invocation of the case's model, and the
    /// selection is <c>SelectCandidates</c>, the method an invocation runs.
    /// </summary>
    public static (List<Guid> Selected, IReadOnlyList<Guid> Injected, List<OmittedExperience> Omitted) Run(
        IReadOnlyList<(ExperienceRecord Record, RankingInput Ranking)> records,
        ExperienceBenefitEstimate estimate,
        BenefitSelectOptions options)
    {
        var clock = new FixedTimeProvider(Fixed.T0);
        var store = new InMemoryExperienceRecordStore(clock);
        var limits = new ExperienceInjectionLimits(options.MaxRecords, options.MaxBytes);
        var provider = new ExperienceContextProvider(
            new ExperienceRetrievalService(new InMemoryExperienceCandidateSource(store), RetrievalPolicy.Default, RankingWeights.Default, clock),
            store,
            new ExperienceInjectionOptions
            {
                ResolveRequest = _ => null,
                Limits = limits,
                SessionLimits = null,
                ModelAuthoredLessons = options.ExcludeModelAuthored ? ModelAuthoredLessonPolicy.Exclude : ModelAuthoredLessonPolicy.Include,
                Benefit = new ExperienceBenefitPolicy
                {
                    Enabled = options.Enabled,
                    PreferMeasuredHelpful = options.PreferMeasuredHelpful,
                    PerModel = options.PerModel,
                    MinimumRunsPerArm = estimate.MinimumRunsPerArm,
                },
                TimeProvider = clock,
            });

        // As ExperienceBenefitCache.Current answers: no view with the policy off, otherwise the views of the cached
        // estimate, for the invoking model only when PerModel is on.
        var view = options.Enabled ? BenefitViews.From(estimate)?.For(options.PerModel ? options.Model : null) : null;
        var ranked = records
            .Select(entry => new RankedExperience(
                entry.Record,
                entry.Ranking.Score,
                [.. entry.Ranking.Components.Select(component => new RankingComponent(component.Kind, component.Value, component.Weight))]))
            .ToList();
        var omitted = new List<OmittedExperience>();
        var selected = provider.SelectCandidates(ranked, omitted, options.RequestLimit, view);
        var payload = HistoricalReferenceWriter.Write(selected, limits, approachArguments: null, new HistoricalReferenceWriteSettings());
        omitted.AddRange(payload.Omitted);
        return ([.. selected.Select(entry => entry.Record.ExperienceId)], payload.ExperienceIds, omitted);
    }

    /// <summary><c>estimate.json</c>: the cached estimate, in the form of contract 1.9's benefit-estimate <c>expected.json</c>.</summary>
    public static JsonObject EstimateJson(ExperienceBenefitEstimate estimate) => BenefitEstimateContract.EstimateJson(estimate);

    /// <summary>
    /// <c>expected.json</c>: <c>selected</c> (the records the provider selects, in order, before the byte budget),
    /// <c>injected</c> (the records the block holds, in order) and <c>omitted</c> (each omission in the order the provider
    /// reports it: <c>experienceId</c>, <c>reason</c> and <c>detail</c>, <c>null</c> when it carries none).
    /// </summary>
    public static JsonObject ExpectedJson(List<Guid> selected, IReadOnlyList<Guid> injected, List<OmittedExperience> omitted) => new()
    {
        ["selected"] = new JsonArray([.. selected.Select(id => (JsonNode)Id(id))]),
        ["injected"] = new JsonArray([.. injected.Select(id => (JsonNode)Id(id))]),
        ["omitted"] = new JsonArray([.. omitted.Select(entry => (JsonNode)new JsonObject
        {
            ["experienceId"] = Id(entry.ExperienceId),
            ["reason"] = entry.Reason.ToString(),
            ["detail"] = entry.Detail,
        })]),
    };

    private static string Id(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
}

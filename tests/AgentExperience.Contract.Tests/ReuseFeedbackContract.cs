using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Confidence;
using AgentExperience.Core.Feedback;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Core.Reflections;
using AgentExperience.Storage.InMemory;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Contract 1.17's in-memory reuse-feedback families. <c>reuse-feedback</c>: a run's feedback recorded by .NET's
/// <see cref="ExperienceReuseFeedbackService"/> over the .NET in-memory feedback ledger and record store, its attributed
/// evidence applied through the 1.16 <see cref="ExperienceLifecycleService"/>, the production path a port reproduces.
/// <c>finalize-reuse-evidence</c>: finalization's opt-in reuse evidence (<see cref="ExperienceFinalizationOptions.ReuseEvidence"/>,
/// <see cref="ExperienceFinalizationOptions.ContradictOnFailure"/>), run by .NET's <see cref="ExperienceFinalizationService"/>.
/// </summary>
/// <remarks>
/// <para>
/// The lifecycle service runs as contract 1.16 has a port run it: <see cref="IndependenceVerification.TrustHostSuppliedIdentifiers"/>,
/// no provenance signing, no capture or indexing service, with the case's engine and <see cref="HostTrustedEvidenceEffect"/>.
/// Under the opt-out the feedback service verifies no attribution (no run lookup, no assessment token check) and finds no
/// tampered signature (there is no signer), so an attribution degrades only through validation.
/// </para>
/// <para>
/// A step may inject store faults, through decorators over the two stores, for that step only: <c>faults.ledger</c> makes
/// the ledger write throw <see cref="ExperienceStoreException"/> with that message, and <c>faults.commit</c> makes one
/// record's lifecycle commit throw one (<c>throws</c>) or answer <see cref="ExperienceStoreOutcome.StaleRevision"/>
/// (<c>answers</c>). The store's clock is a <see cref="SettableTimeProvider"/> whose timers never fire.
/// </para>
/// </remarks>
internal static class ReuseFeedbackContract
{
    public const string Family = "reuse-feedback";
    public const string FinalizeFamily = "finalize-reuse-evidence";

    /// <summary>The files of each case, in both families.</summary>
    public static readonly string[] CaseFiles = ["input.json", "expected.json"];

    /// <summary>The name a case writes for an enum value no member has, which a reader reads back as an undefined value.</summary>
    public const string Undefined = "Sideways";

    private const int R = 0x1170;
    private const int Z = 0x1171;

    private const string Reviewer = "reviewer-ü";
    private const string Evaluator = "comparative-evaluator/2.0";

    private static readonly Scope S = Fixed.Scope;
    private static readonly Scope OtherAgent = Fixed.Scope with { AgentId = "other-agent" };
    private static readonly DateTimeOffset Clock = Fixed.T0.AddHours(3);
    private static readonly DateTimeOffset Observed = Fixed.T0.AddMinutes(30);

    private static readonly Guid RunA = Fixed.Id(R, 0x5100);
    private static readonly Guid RunB = Fixed.Id(R, 0x5101);
    private static readonly Guid Round = Fixed.Id(R, 0x5200);

    /// <summary>Every <c>reuse-feedback</c> case, by directory name, as its <c>input.json</c>.</summary>
    public static IReadOnlyList<(string Name, JsonObject Input)> Cases { get; } = BuildCases();

    /// <summary>Every <c>finalize-reuse-evidence</c> case, by directory name, as its <c>input.json</c>.</summary>
    public static IReadOnlyList<(string Name, JsonObject Input)> FinalizeCases { get; } = BuildFinalizeCases();

    // ---- reuse-feedback -------------------------------------------------------------------------------------------

    private static List<(string, JsonObject)> BuildCases() =>
    [
        ("exposure-only", ExposureOnly()),
        ("idempotency-and-conflict", IdempotencyAndConflict()),
        ("human-improved", HumanImproved()),
        ("human-harmed-contests", HumanHarmedContests()),
        ("comparative-improved", ComparativeImproved()),
        ("comparative-harmed-recorded-only", ComparativeHarmedRecordedOnly()),
        ("duplicate-key", DuplicateKey()),
        ("ineligible-and-unresolved", IneligibleAndUnresolved()),
        ("refused", RefusedCase()),
        ("invalid-fatal", InvalidFatal()),
        ("degraded-human", DegradedHuman()),
        ("degraded-comparative", DegradedComparative()),
        ("denied", DeniedCase()),
        ("engine-throws", EngineThrows()),
        ("engine-not-a-score", EngineNotAScore()),
        ("store-failure-and-retry", StoreFailureAndRetry()),
        ("store-validation", StoreValidation()),
        ("store-idempotency", StoreIdempotency()),
        ("derived-ids", DerivedIds()),
    ];

    private static JsonObject ExposureOnly()
    {
        var (a, b) = (Rec(0x10), Rec(0x11));
        return Case(
            "Exposure is not attribution: feedback with no human assessment and no comparative result is Recorded with " +
            "benefit Unknown, whatever the caller claims, and every exposure is ExposureOnly. The ledger stores the claim " +
            "verbatim and the exposures in record order, not the caller's; no record moves. A failed run without attribution " +
            "moves nothing either.",
            [a, b],
            [
                Submit(Feedback(1, [b.ExperienceId, a.ExperienceId]) with { ClaimedBenefit = ExperienceReuseBenefit.Improved, TrialLabel = "memory-enabled" }),
                Submit(Feedback(2, [a.ExperienceId]) with { RunOutcome = TaskVerificationStatus.Failed, ClaimedBenefit = ExperienceReuseBenefit.Harmed, Measure = new ReuseMeasure("wall-clock-ms", -0.25) }),
            ]);
    }

    private static JsonObject IdempotencyAndConflict()
    {
        var (a, b) = (Rec(0x20), Rec(0x21));
        var original = Feedback(1, [a.ExperienceId, b.ExperienceId]) with { HumanAssessment = Human(1, [a.ExperienceId]) };
        return Case(
            "The feedback ID is the idempotency key. The same submission again, its exposures listed in another order and its " +
            "instant differing below a microsecond, is AlreadyRecorded: the ledger is unchanged and the attributed record's " +
            "evidence is replayed under the same derived IDs. The same ID with any stored field changed, or from another " +
            "reviewer, is a Conflict reporting the stored submission's exposures as Refused; from a caller with no authority " +
            "over the stored submission's scope, a Conflict that reports none. Nothing is written by any of them.",
            [a, b],
            [
                Submit(original, principal: Reviewer),
                Submit(original with { ExposedExperienceIds = [b.ExperienceId, a.ExperienceId], ObservedAt = Observed.AddTicks(3) }, principal: Reviewer),
                Submit(original with { Measure = new ReuseMeasure("tool-calls", 8) }, principal: Reviewer),
                Submit(original, principal: "reviewer-2"),
                Submit(original with { Scope = OtherAgent, HumanAssessment = null }, authorization: OtherAgent),
            ],
            clocks: [Clock, Clock.AddMinutes(1), Clock.AddMinutes(2), Clock.AddMinutes(3), Clock.AddMinutes(4)]);
    }

    private static JsonObject HumanImproved()
    {
        var (a, b, c) = (Rec(0x30), Rec(0x31), Rec(0x32));
        var exposed = new[] { a.ExperienceId, b.ExperienceId, c.ExperienceId };
        return Case(
            "An authorized human's assessment that reuse helped: each attributed record receives Supporting Human evidence, " +
            "counted under the authorization's principal and the run, through the lifecycle path (its round is stored on the " +
            "ledger for audit, never on the evidence; its token is not checked without verification). An exposed record the " +
            "assessment did not name is ExposureOnly. A second reviewer about the same run is a second key; the same reviewer " +
            "again under another feedback ID is recorded, not counted.",
            [a, b, c],
            [
                Submit(Feedback(1, exposed) with { HumanAssessment = Human(1, [a.ExperienceId, c.ExperienceId]) with { VerificationRoundId = Round } }, principal: Reviewer),
                Submit(Feedback(2, [a.ExperienceId]) with { HumanAssessment = Human(2, [a.ExperienceId]) }, principal: "reviewer-2"),
                Submit(Feedback(3, [a.ExperienceId]) with { HumanAssessment = Human(3, [a.ExperienceId]) }, principal: Reviewer),
            ],
            clocks: [Clock, Clock.AddMinutes(1), Clock.AddMinutes(2)]);
    }

    private static JsonObject HumanHarmedContests()
    {
        var (validated, reinforced) = (Rec(0x40), Rec(0x41, ExperienceStatus.Reinforced, supporting: 3));
        return Case(
            "Attributed harm submits Contradicting evidence, which contests a Validated or Reinforced record in the same " +
            "transaction that records it. Nothing is deleted.",
            [validated, reinforced],
            [
                Submit(Feedback(1, [validated.ExperienceId, reinforced.ExperienceId]) with
                {
                    RunOutcome = TaskVerificationStatus.Failed,
                    HumanAssessment = Human(1, [validated.ExperienceId, reinforced.ExperienceId]) with { Benefit = ExperienceReuseBenefit.Harmed },
                }, principal: Reviewer),
            ]);
    }

    private static JsonObject ComparativeImproved()
    {
        var (a, b) = (Rec(0x50), Rec(0x51, supporting: 2));
        return Case(
            "A comparative evaluator's result that reuse helped: Supporting Machine evidence for each attributed record, " +
            "keyed on the run and the result's round, its producer the evaluator and its detail the summary. The ledger " +
            "stores the evaluator, the round and the IDs of the evidence the result rested on.",
            [a, b],
            [Submit(Feedback(1, [a.ExperienceId, b.ExperienceId]) with { ComparativeEvaluation = Comparative([a.ExperienceId, b.ExperienceId]) })]);
    }

    private static JsonObject ComparativeHarmedRecordedOnly()
    {
        var a = Rec(0x60);
        return Case(
            "HostTrustedEvidence = RecordedOnly, a port's default: attributed harm is recorded on an event that moves only the " +
            "revision (Applied, not counted, the record still Validated), and the exposure says so. Another feedback for the " +
            "same run and round is then a duplicate of that key: recorded, not counted.",
            [a],
            [
                Submit(Feedback(1, [a.ExperienceId]) with { ComparativeEvaluation = Comparative([a.ExperienceId]) with { Benefit = ExperienceReuseBenefit.Harmed } }),
                Submit(Feedback(2, [a.ExperienceId]) with { ComparativeEvaluation = Comparative([a.ExperienceId]) }),
            ],
            hostTrustedEvidence: HostTrustedEvidenceEffect.RecordedOnly,
            clocks: [Clock, Clock.AddMinutes(1)]);
    }

    private static JsonObject DuplicateKey()
    {
        var a = Rec(0x70);
        return Case(
            "Two feedback submissions about the same run and round: the first counts; the second, harm under a fresh feedback " +
            "ID, meets the independence key the first took, so it is recorded, not counted, and does not contest the record.",
            [a],
            [
                Submit(Feedback(1, [a.ExperienceId]) with { ComparativeEvaluation = Comparative([a.ExperienceId]) }),
                Submit(Feedback(2, [a.ExperienceId]) with { ComparativeEvaluation = Comparative([a.ExperienceId]) with { Benefit = ExperienceReuseBenefit.Harmed } }),
            ],
            clocks: [Clock, Clock.AddMinutes(1)]);
    }

    private static JsonObject IneligibleAndUnresolved()
    {
        var revoked = Rec(0x80, ExperienceStatus.Revoked);
        var candidate = Rec(0x81, ExperienceStatus.Candidate) with { Revision = 0, SupportingValidations = 0, ReuseConfidence = 0.5 };
        var quarantined = Rec(0x82, ExperienceStatus.Quarantined);
        var foreign = Rec(0x83, scope: OtherAgent);
        var live = Rec(0x84);
        var missing = Fixed.Id(R, 0x8f);
        var exposed = new[] { revoked.ExperienceId, candidate.ExperienceId, quarantined.ExperienceId, foreign.ExperienceId, live.ExperienceId, missing };
        return Case(
            "Every exposure is recorded, and the attributed ones are submitted one by one: a record whose status refuses " +
            "evidence is Ineligible, with its status and the lifecycle's reason; one that does not exist in the feedback's " +
            "scope, or lies in another scope, is Unresolved; the live one is applied. One record's refusal is not the others'.",
            [revoked, candidate, quarantined, foreign, live],
            [Submit(Feedback(1, exposed) with { ComparativeEvaluation = Comparative(exposed) })]);
    }

    private static JsonObject RefusedCase()
    {
        var own = Rec(0x90);
        var planted = Rec(0x91);
        var saturated = Rec(0x92, supporting: int.MaxValue);
        var conflicting = Feedback(2, [planted.ExperienceId]) with { ComparativeEvaluation = Comparative([planted.ExperienceId]) };
        return Case(
            "What the confidence path refuses outright is Refused, not retryable, with its reason: feedback about a record's " +
            "own source run (Unverified, OwnRun); a derived evidence ID already stored with other content (Conflict); and a " +
            "record whose counter is saturated (Invalid, its errors as the reason).",
            [own, planted, saturated],
            [
                Submit(Feedback(1, [own.ExperienceId]) with { RunId = own.SourceRunId, ComparativeEvaluation = Comparative([own.ExperienceId]) with { RunId = own.SourceRunId } }),
                Step("apply", LifecycleContract.ApplyStep(
                    LifecycleContract.Auth(S),
                    Fixed.Id(R, 0x6001),
                    planted.ExperienceId,
                    ExperienceReuseFeedbackService.EvidenceIdFor(conflicting.FeedbackId, planted.ExperienceId),
                    "Contradicting",
                    "Machine",
                    RunB,
                    Round)),
                Submit(conflicting),
                Submit(Feedback(3, [saturated.ExperienceId]) with { ComparativeEvaluation = Comparative([saturated.ExperienceId]) }),
            ],
            clocks: [Clock, Clock.AddMinutes(1), Clock.AddMinutes(2), Clock.AddMinutes(3)]);
    }

    private static JsonObject InvalidFatal()
    {
        var a = Rec(0xa0);
        var id = a.ExperienceId;
        var other = Fixed.Id(R, 0xaf);
        return Case(
            "A structurally incoherent submission is Invalid, every fatal error in .NET's order, and nothing is written: no " +
            "feedback ID or run, an undefined outcome or claim, an unset instant, a blank trial label, a missing or malformed " +
            "measure, no exposures, too many, an empty or repeated one; both attributions at once (which ends validation); an " +
            "attribution naming a record the run was not exposed to; a comparative result about another run. A degrading " +
            "error beside a fatal one is not reported.",
            [a],
            [
                Submit(new ExperienceReuseFeedback(Guid.Empty, Guid.Empty, S, null!, (TaskVerificationStatus)99, new ReuseMeasure(" ", double.NaN), default, (ExperienceReuseBenefit)99, TrialLabel: " ")),
                Submit(Feedback(2, []) with { Measure = null! }),
                Submit(Feedback(3, [.. Enumerable.Range(1, ExperienceReuseFeedback.MaxExposedRecords + 1).Select(n => Fixed.Id(R, 0xa00 + n))]) with { Measure = new ReuseMeasure("tool-calls", double.PositiveInfinity) }),
                Submit(Feedback(4, [Guid.Empty, id, id])),
                Submit(Feedback(5, [id]) with { HumanAssessment = Human(5, [id]), ComparativeEvaluation = Comparative([id]), Measure = new ReuseMeasure("tool-calls", double.NegativeInfinity) }),
                Submit(Feedback(6, [id]) with { HumanAssessment = Human(6, [other, id]) with { Rationale = " " } }),
                Submit(Feedback(7, [id]) with { ComparativeEvaluation = Comparative([id]) with { RunId = RunB } }),
                Submit(Feedback(8, [id]) with { ComparativeEvaluation = Comparative([other]) }),
            ]);
    }

    private static JsonObject DegradedHuman()
    {
        var (a, b) = (Rec(0xb0), Rec(0xb1));
        var exposed = new[] { a.ExperienceId, b.ExperienceId };
        return Case(
            "A human assessment that fails its evidence requirements is dropped, not fatal: the exposure is Recorded with " +
            "benefit Unknown, no evidence is submitted, and the reason lists every degrading error in .NET's order (no " +
            "records named, a record named twice, an undefined or Unknown benefit, no assessment ID, an empty round, a blank " +
            "rationale, an unset instant, and a blank or padded principal, which is the reviewer).",
            [a, b],
            [
                Submit(Feedback(1, exposed) with
                {
                    HumanAssessment = new HumanReuseAssessment(Guid.Empty, ExperienceReuseBenefit.Unknown, [], " ", default, Guid.Empty, "aexat1.ignored"),
                }, principal: " "),
                Submit(Feedback(2, exposed) with { HumanAssessment = Human(2, [a.ExperienceId, a.ExperienceId]) with { Benefit = (ExperienceReuseBenefit)99 } }, principal: " reviewer-1"),
                Submit(Feedback(3, exposed) with { HumanAssessment = Human(3, [b.ExperienceId]) }, principal: ""),
            ]);
    }

    private static JsonObject DegradedComparative()
    {
        var a = Rec(0xc0);
        var ok = Comparative([a.ExperienceId]);
        return Case(
            "A comparative result that fails its evidence requirements is dropped the same way: no records named, an Unknown " +
            "benefit, a blank evaluator, no round, no evidence, a blank summary, an unset instant; and evidence that is null, " +
            "has no ID, repeats an ID, or belongs to another round.",
            [a],
            [
                Submit(Feedback(1, [a.ExperienceId]) with
                {
                    ComparativeEvaluation = new ComparativeEvaluationResult(" ", RunA, Guid.Empty, ExperienceReuseBenefit.Unknown, null!, [], " ", default),
                }),
                Submit(Feedback(2, [a.ExperienceId]) with
                {
                    ComparativeEvaluation = ok with
                    {
                        Evidence = [null!, Ev(1) with { EvidenceId = Guid.Empty }, Ev(2), Ev(2), Ev(3) with { VerificationRoundId = RunB }],
                    },
                }),
            ]);
    }

    private static JsonObject DeniedCase()
    {
        var a = Rec(0xd0);
        return Case(
            "A scope the authorization does not permit is Denied before any store is touched: another agent, and a blank " +
            "tenant, which permits nothing. Validation comes first: an invalid submission from an unpermitted caller is Invalid.",
            [a],
            [
                Submit(Feedback(1, [a.ExperienceId]) with { ComparativeEvaluation = Comparative([a.ExperienceId]) }, authorization: OtherAgent),
                Submit(Feedback(2, [a.ExperienceId]), authorization: S with { TenantId = " " }),
                Submit(Feedback(3, []), authorization: OtherAgent),
            ]);
    }

    private static JsonObject EngineThrows()
    {
        var (a, b, c) = (Rec(0xe0), Rec(0xe1), Rec(0xe2));
        return Case(
            "A host confidence engine that throws fails each attributed record it was asked about, not the submission: the " +
            "exposure is durable, each such record is Failed and retryable with the engine's message, and the result is " +
            "retryable. An exposure the attribution did not name is untouched.",
            [a, b, c],
            [Submit(Feedback(1, [a.ExperienceId, b.ExperienceId, c.ExperienceId]) with { ComparativeEvaluation = Comparative([a.ExperienceId, b.ExperienceId]) })],
            engine: ConfidenceEngineContract.Engine("broken", "1", "throws"));
    }

    private static JsonObject EngineNotAScore()
    {
        var a = Rec(0xe8);
        return Case(
            "A host engine whose score is not in [0, 1] fails the record the same way, with the lifecycle's refusal as the " +
            "reason: nothing is written for it, and resubmitting retries it.",
            [a],
            [Submit(Feedback(1, [a.ExperienceId]) with { ComparativeEvaluation = Comparative([a.ExperienceId]) })],
            engine: ConfidenceEngineContract.Engine("high", "1", 1.5));
    }

    private static JsonObject StoreFailureAndRetry()
    {
        var (a, b, c) = (Rec(0xf0), Rec(0xf1), Rec(0xf2));
        var attributed = Feedback(1, [a.ExperienceId, b.ExperienceId, c.ExperienceId]) with
        {
            ComparativeEvaluation = Comparative([a.ExperienceId, b.ExperienceId, c.ExperienceId]),
        };
        var exposureOnly = Feedback(2, [a.ExperienceId]);
        return Case(
            "The ledger is written first and each record is submitted on its own: a record whose commit throws a store " +
            "failure is Failed and retryable with the failure's message, one whose commit lost a revision race is Failed and " +
            "retryable, and the other is applied. Resubmitting the same feedback is AlreadyRecorded and converges: the two " +
            "outstanding records land, the applied one replays. A ledger write that throws propagates and writes nothing; " +
            "retried, it records.",
            [a, b, c],
            [
                Submit(attributed, faults: new JsonObject
                {
                    ["ledger"] = null,
                    ["commit"] = new JsonObject
                    {
                        [D(a.ExperienceId)] = new JsonObject { ["throws"] = "connection reset by peer" },
                        [D(b.ExperienceId)] = new JsonObject { ["answers"] = "StaleRevision" },
                    },
                }),
                Submit(attributed),
                Submit(exposureOnly, faults: new JsonObject { ["ledger"] = "the feedback ledger is unreachable", ["commit"] = new JsonObject() }),
                Submit(exposureOnly),
            ],
            clocks: [Clock, Clock.AddMinutes(1), Clock.AddMinutes(2), Clock.AddMinutes(3)]);
    }

    private static JsonObject StoreValidation()
    {
        var a = Fixed.Id(R, 0x120);
        var b = Fixed.Id(R, 0x121);
        var none = Ledger(1, [new(a, false, null)]);
        var human = none with
        {
            FeedbackId = Fixed.Id(R, 0xf102),
            Benefit = ExperienceReuseBenefit.Improved,
            AttributionSource = ReuseAttributionSource.HumanAssessment,
            ReviewerIdentity = Reviewer,
            AssessmentId = Fixed.Id(R, 0xa102),
            Rationale = "The reviewer saw the lesson applied.",
            AttributedAt = Observed,
            Exposures = [new(a, true, ExperienceReuseFeedbackService.EvidenceIdFor(Fixed.Id(R, 0xf102), a))],
        };
        var comparative = none with
        {
            FeedbackId = Fixed.Id(R, 0xf103),
            Benefit = ExperienceReuseBenefit.Improved,
            AttributionSource = ReuseAttributionSource.ComparativeEvaluation,
            EvaluatorId = Evaluator,
            VerificationRoundId = Round,
            Rationale = "Runs with the lesson needed fewer tool calls.",
            EvidenceIds = [Fixed.Id(R, 0xe001)],
            AttributedAt = Observed,
        };
        return Case(
            "The ledger validates what it is asked to write, before authorization, with every error in .NET's order: the " +
            "identifiers, the scope, every enum, the instants, the measure, the trial label, the reserved rationale, the shape " +
            "each attribution source requires, and the exposures (none, too many, null, empty, repeated, and an evidence ID " +
            "present exactly on an attributed exposure). A valid submission outside the authorization is Denied. Through the " +
            "service, a measure kind the service accepts and the ledger refuses (a NUL) is Invalid with the ledger's errors.",
            [],
            [
                Record(new RecordedExperienceReuseFeedback(
                    Guid.Empty, Guid.Empty, S with { TenantId = "", ApplicationId = " ", TeamId = "" }, (TaskVerificationStatus)99, (ExperienceReuseBenefit)99, (ExperienceReuseBenefit)99,
                    (ReuseAttributionSource)99, null, null, null, null, null, [], default(DateTimeOffset), new ReuseMeasure("", double.PositiveInfinity), "  ", default, null!)),
                Record(none with { Scope = null!, Measure = null!, Exposures = [] }),
                Record(none with { Measure = new ReuseMeasure("tool\u0000calls", 7), TrialLabel = "trial\u0000", Rationale = "aexp-sealed:v1:abc" }),
                Record(human with { Rationale = "(sealed)" }),
                Record(human with { Benefit = ExperienceReuseBenefit.Unknown, ReviewerIdentity = " ", EvaluatorId = Evaluator, Rationale = null, AttributedAt = null, EvidenceIds = [Fixed.Id(R, 0xe001)], AssessmentId = null, VerificationRoundId = Guid.Empty }),
                Record(human with { ReviewerIdentity = null, AssessmentId = Guid.Empty }),
                Record(comparative with { EvaluatorId = null, ReviewerIdentity = Reviewer, AssessmentId = Fixed.Id(R, 0xa103), Rationale = " ", AttributedAt = null, VerificationRoundId = null, EvidenceIds = [] }),
                Record(comparative with { VerificationRoundId = Guid.Empty, EvidenceIds = [Guid.Empty] }),
                Record(comparative with { EvidenceIds = [Fixed.Id(R, 0xe001), Fixed.Id(R, 0xe001)] }),
                Record(none with { ReviewerIdentity = Reviewer, EvaluatorId = Evaluator, VerificationRoundId = Round, AssessmentId = Fixed.Id(R, 0xa101), Rationale = "a claim", AttributedAt = Observed, EvidenceIds = [Fixed.Id(R, 0xe001)] }),
                Record(none with { Exposures = [.. Enumerable.Range(1, ExperienceReuseFeedback.MaxExposedRecords + 1).Select(n => new ExperienceReuseExposure(Fixed.Id(R, 0xb00 + n), false, null))] }),
                Record(none with
                {
                    Exposures = [null!, new(Guid.Empty, false, null), new(a, false, null), new(a, false, null), new(b, true, null), new(Fixed.Id(R, 0x122), false, Fixed.Id(R, 0xe002)), new(Fixed.Id(R, 0x123), true, Guid.Empty)],
                }),
                Record(comparative, authorization: OtherAgent),
                Record(comparative with { FeedbackId = Guid.Empty }, authorization: OtherAgent),
                Submit(Feedback(4, [a]) with { Measure = new ReuseMeasure("tool\u0000calls", 7) }),
            ]);
    }

    private static JsonObject StoreIdempotency()
    {
        var (a, b) = (Fixed.Id(R, 0x130), Fixed.Id(R, 0x131));
        var feedbackId = Fixed.Id(R, 0xf201);
        var submitted = Ledger(0x201, [new(b, true, ExperienceReuseFeedbackService.EvidenceIdFor(feedbackId, b)), new(a, false, null)]) with
        {
            FeedbackId = feedbackId,
            Benefit = ExperienceReuseBenefit.Harmed,
            AttributionSource = ReuseAttributionSource.HumanAssessment,
            ReviewerIdentity = Reviewer,
            AssessmentId = Fixed.Id(R, 0xa201),
            VerificationRoundId = Round,
            Rationale = "The lesson sent the run down the wrong path.",
            AttributedAt = Observed.AddTicks(1_234_567),
            ObservedAt = Observed.AddTicks(7_654_321),
            ClaimedBenefit = ExperienceReuseBenefit.Improved,
            TrialLabel = "memory-enabled",
        };
        return Case(
            "The ledger on its own: a submission is stored with its exposures in record order and its instants truncated to " +
            "whole microseconds, and that is what it hands back. The same submission with its exposures in another order, or " +
            "an instant differing below a microsecond, is AlreadyRecorded with the stored one; any stored field changed, or " +
            "another set of exposures, is a Conflict with the stored one; from a caller with no authority over its scope, a " +
            "Conflict with none.",
            [],
            [
                Record(submitted),
                Record(submitted with { Exposures = [.. submitted.Exposures.Reverse()] }),
                Record(submitted with { ObservedAt = submitted.ObservedAt.AddTicks(2), AttributedAt = submitted.AttributedAt!.Value.AddTicks(-3) }),
                Record(submitted with { TrialLabel = "memory-disabled" }),
                Record(submitted with { Exposures = [submitted.Exposures[0]] }),
                Record(submitted with { Scope = OtherAgent }, authorization: OtherAgent),
            ]);
    }

    private static JsonObject DerivedIds()
    {
        var feedback = Fixed.Id(R, 0xf301);
        var record = Fixed.Id(R, 0x140);
        return Case(
            "The evidence and event IDs feedback derives for an attributed record (ExperienceReuseFeedbackService.EvidenceIdFor " +
            "and EventIdFor): SHA-256 over the namespace 3f5a1d62-8c04-4b91-a7e3-6d2f0b48c915, the feedback ID and the record " +
            "ID (each 16 bytes, RFC 4122 order) and a tag (1 for the evidence ID, 2 for the event ID), as UUIDv8. Another " +
            "feedback or another record derives other IDs.",
            [],
            [
                Step("derive", Derive(feedback, record)),
                Step("derive", Derive(feedback, Fixed.Id(R, 0x141))),
                Step("derive", Derive(Fixed.Id(R, 0xf302), record)),
                Step("derive", Derive(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), Guid.Parse("00000000-0000-0000-0000-000000000001"))),
            ]);
    }

    // ---- finalize-reuse-evidence -----------------------------------------------------------------------------------

    private static List<(string, JsonObject)> BuildFinalizeCases()
    {
        var same = Rec(0x10, family: Z);
        var otherTask = Rec(0x11, family: Z) with { TaskId = "other-task" };
        var revoked = Rec(0x12, ExperienceStatus.Revoked, family: Z);
        var reinforced = Rec(0x15, ExperienceStatus.Reinforced, supporting: 3, family: Z);
        var foreign = Rec(0x14, scope: OtherAgent, family: Z);
        var saturated = Rec(0x16, supporting: int.MaxValue, family: Z);
        var missing = Fixed.Id(Z, 0x1f);

        var verifiedRun = Fixed.Id(Z, 0x9001);
        var fromRun = Rec(0x13, family: Z) with { SourceRunId = verifiedRun };

        return
        [
            ("same-task-counted", FinalizeCase(
                "ReuseEvidence = SameTask, a verified run, host-trusted evidence Counted: finalization submits Supporting " +
                "Machine evidence, bound to the round it closed and derived IDs (tags 4 and 5), for every record the run was " +
                "given that is readable in its scope and on its task. A record on another task, one that came from this run, " +
                "and one not readable in the run's scope (missing, or in another scope) are Skipped with the reason; a Revoked " +
                "one is Ineligible and a saturated one Invalid, reported and never retried. Finalizing the run again replays " +
                "the same submissions: the store reports the originals, and nothing counts twice.",
                verifiedRun,
                [same, otherTask, revoked, reinforced, foreign, saturated, fromRun],
                [same.ExperienceId, otherTask.ExperienceId, revoked.ExperienceId, missing, fromRun.ExperienceId, reinforced.ExperienceId, foreign.ExperienceId, saturated.ExperienceId],
                CheckResult.Pass,
                ReuseEvidenceMode.SameTask,
                contradictOnFailure: false,
                HostTrustedEvidenceEffect.Counted,
                finalizations: 2)),
            ("same-task-recorded-only", FinalizeCase(
                "The same under RecordedOnly, a port's default: the evidence is recorded on an event that moves only the " +
                "revision, Applied and not counted, and a replay is not a replay (nothing counted).",
                Fixed.Id(Z, 0x9002),
                [same],
                [same.ExperienceId],
                CheckResult.Pass,
                ReuseEvidenceMode.SameTask,
                contradictOnFailure: false,
                HostTrustedEvidenceEffect.RecordedOnly,
                finalizations: 2)),
            ("contradict-on-failure", FinalizeCase(
                "ContradictOnFailure: a run that failed verification (its record Quarantined, and durable) submits " +
                "Contradicting evidence about the records it was given, which contests them.",
                Fixed.Id(Z, 0x9003),
                [same, reinforced],
                [same.ExperienceId, reinforced.ExperienceId],
                CheckResult.Fail,
                ReuseEvidenceMode.SameTask,
                contradictOnFailure: true,
                HostTrustedEvidenceEffect.Counted,
                finalizations: 2)),
            ("failure-without-contradiction", FinalizeCase(
                "Without ContradictOnFailure a failed run submits nothing: a failure is not necessarily the lesson's fault.",
                Fixed.Id(Z, 0x9004),
                [same],
                [same.ExperienceId],
                CheckResult.Fail,
                ReuseEvidenceMode.SameTask,
                contradictOnFailure: false,
                HostTrustedEvidenceEffect.Counted,
                finalizations: 1)),
            ("off", FinalizeCase(
                "ReuseEvidence = Off, the default: finalization submits nothing about the records a run was given.",
                Fixed.Id(Z, 0x9005),
                [same],
                [same.ExperienceId],
                CheckResult.Pass,
                ReuseEvidenceMode.Off,
                contradictOnFailure: true,
                HostTrustedEvidenceEffect.Counted,
                finalizations: 1)),
            ("unknown-verification", FinalizeCase(
                "A run whose verification is Unknown (no evidence for its required check) neither verified nor failed: nothing " +
                "is submitted, ContradictOnFailure or not.",
                Fixed.Id(Z, 0x9006),
                [same],
                [same.ExperienceId],
                null,
                ReuseEvidenceMode.SameTask,
                contradictOnFailure: true,
                HostTrustedEvidenceEffect.Counted,
                finalizations: 1)),
            ("engine-not-a-score", FinalizeCase(
                "A host engine whose score is not one makes the lifecycle throw: the step reports it (no outcome, the " +
                "exception's type and a reason naming it), goes on to the next record, and never fails the finalization.",
                Fixed.Id(Z, 0x9007),
                [same, otherTask],
                [same.ExperienceId, otherTask.ExperienceId],
                CheckResult.Pass,
                ReuseEvidenceMode.SameTask,
                contradictOnFailure: false,
                HostTrustedEvidenceEffect.Counted,
                finalizations: 1,
                engine: ConfidenceEngineContract.Engine("high", "1", 1.5))),
        ];
    }

    private static JsonObject FinalizeCase(
        string description,
        Guid runId,
        IReadOnlyList<ExperienceRecord> records,
        IReadOnlyList<Guid> exposed,
        CheckResult? check,
        ReuseEvidenceMode mode,
        bool contradictOnFailure,
        HostTrustedEvidenceEffect hostTrustedEvidence,
        int finalizations,
        JsonNode? engine = null)
    {
        var t = Fixed.T0.AddHours(6);
        var run = FinalizeContract.Run(runId, "rotate-keys", "Rotate the signing keys", t,
        [
            FinalizeContract.Attempt(runId, 0, t.AddSeconds(1), [FinalizeContract.Call(runId, 0, 0, "rotate", t.AddSeconds(1), new(), result: "rotated")], result: "Rotated."),
        ]) with
        {
            Provenance = Fixed.Provenance(t) with { ExposedTo = [.. exposed.Select(id => new RunExposure(id, 1))] },
        };
        var verification = check is { } result
            ? FinalizeContract.Verified(runId, t.AddSeconds(30), ("keys-rotated", "integration-test", result))
            : FinalizeContract.Verified(runId, t.AddSeconds(30), ("keys-rotated", "integration-test", CheckResult.Pass)) with { Evidence = [] };
        var finalize = new FinalizeInput(description, new FinalizeOptions(AllowedArgumentNames: null), run, FinalizeContract.SubId(runId, 0xe0), verification);

        return new JsonObject
        {
            ["description"] = description,
            ["service"] = new JsonObject
            {
                ["engine"] = engine?.DeepClone(),
                ["hostTrustedEvidence"] = hostTrustedEvidence.ToString(),
            },
            ["finalization"] = new JsonObject
            {
                ["reuseEvidence"] = mode.ToString(),
                ["contradictOnFailure"] = contradictOnFailure,
            },
            ["records"] = new JsonArray([.. records.Select(record => (JsonNode)ContractJson.ToStored(record))]),
            ["finalize"] = JsonNode.Parse(ContractJson.Write(finalize)),
            ["finalizations"] = finalizations,
        };
    }

    /// <summary>
    /// The expected result of a <c>finalize-reuse-evidence</c> case: the run captured, then finalized as many times as the
    /// case says by .NET's finalization service over a fresh in-memory store holding the case's records.
    /// </summary>
    public static JsonObject FinalizeExpected(JsonObject input)
    {
        var finalizeInput = ContractJson.Read<FinalizeInput>(input["finalize"]!.ToJsonString());
        var (capture, _) = FinalizeContract.CaptureAsync(finalizeInput).GetAwaiter().GetResult();
        var clock = new SettableTimeProvider(finalizeInput.Verification.FinalizedAt);
        var store = new InMemoryExperienceRecordStore(clock);
        var records = Seed(store, input);
        var finalizationOptions = input["finalization"]!.AsObject();
        var finalization = new ExperienceFinalizationService(
            capture,
            new DefaultExperienceReflector(),
            store,
            LifecycleContract.Service(store, input["service"]!.AsObject()),
            indexingService: null,
            indexingTimeout: null,
            provenanceSigning: null,
            reflectionSanitizer: null,
            new ExperienceFinalizationOptions
            {
                ReuseEvidence = Enum.Parse<ReuseEvidenceMode>(finalizationOptions["reuseEvidence"]!.GetValue<string>()),
                ContradictOnFailure = finalizationOptions["contradictOnFailure"]!.GetValue<bool>(),
            });

        var results = new JsonArray();
        for (var i = 0; i < input["finalizations"]!.GetValue<int>(); i++)
        {
            var result = finalization.FinalizeAsync(FinalizeContract.Request(finalizeInput)).GetAwaiter().GetResult();
            results.Add(new JsonObject
            {
                ["outcome"] = result.Outcome.ToString(),
                ["experienceId"] = result.ExperienceId is { } id ? D(id) : null,
                ["reuseEvidence"] = new JsonArray([.. result.ReuseEvidence.Select(entry => (JsonNode)new JsonObject
                {
                    ["experienceId"] = D(entry.ExperienceId),
                    ["kind"] = entry.Kind.ToString(),
                    ["outcome"] = entry.Outcome?.ToString(),
                    ["reason"] = entry.Reason,
                    ["counted"] = entry.Counted,
                    ["skipped"] = entry.Skipped,
                    ["replay"] = entry.Replay,
                    ["refusal"] = entry.Refusal?.ToString(),
                    ["exceptionType"] = entry.ExceptionType,
                })]),
                ["reuseEvidenceTruncated"] = result.ReuseEvidenceTruncated,
            });
        }

        return new JsonObject
        {
            ["results"] = results,
            ["records"] = new JsonArray([.. records.Select(record => (JsonNode)ContractJson.ToStored(LifecycleContract.Reread(store, record)))]),
            ["histories"] = Histories(store, records),
        };
    }

    // ---- case building --------------------------------------------------------------------------------------------

    private static ExperienceRecord Rec(int n, ExperienceStatus status = ExperienceStatus.Validated, int? supporting = null, Scope? scope = null, int family = R) =>
        LifecycleContract.Rec(family, n, status, supporting: supporting, scope: scope);

    private static ExperienceReuseFeedback Feedback(int n, IReadOnlyList<Guid> exposed) => new(
        Fixed.Id(R, 0xf000 + n),
        RunA,
        S,
        exposed,
        TaskVerificationStatus.Verified,
        new ReuseMeasure("tool-calls", 7),
        Observed);

    private static HumanReuseAssessment Human(int n, IReadOnlyList<Guid> attributed) => new(
        Fixed.Id(R, 0xa000 + n),
        ExperienceReuseBenefit.Improved,
        attributed,
        "The reviewer saw the lesson applied.",
        Observed.AddMinutes(5),
        VerificationRoundId: null,
        AssessmentToken: "aexat1.not-checked");

    private static ComparativeEvaluationResult Comparative(IReadOnlyList<Guid> attributed) => new(
        Evaluator,
        RunA,
        Round,
        ExperienceReuseBenefit.Improved,
        attributed,
        [Ev(1), Ev(2)],
        "Runs with the lesson needed fewer tool calls.",
        Observed.AddMinutes(10));

    private static Evidence Ev(int n) => new(
        Fixed.Id(R, 0xe000 + n),
        Round,
        "rev-1",
        "fewer-tool-calls",
        "comparative-run",
        CheckResult.Pass,
        Evaluator,
        null,
        Observed);

    private static RecordedExperienceReuseFeedback Ledger(int n, IReadOnlyList<ExperienceReuseExposure> exposures) => new(
        Fixed.Id(R, 0xf100 + n),
        RunA,
        S,
        TaskVerificationStatus.Verified,
        ExperienceReuseBenefit.Unknown,
        ExperienceReuseBenefit.Unknown,
        ReuseAttributionSource.None,
        ReviewerIdentity: null,
        EvaluatorId: null,
        VerificationRoundId: null,
        AssessmentId: null,
        Rationale: null,
        EvidenceIds: [],
        AttributedAt: null,
        new ReuseMeasure("tool-calls", 7),
        TrialLabel: null,
        Observed,
        exposures);

    private static JsonObject Case(
        string description,
        IReadOnlyList<ExperienceRecord> records,
        IReadOnlyList<JsonObject> steps,
        JsonNode? engine = null,
        HostTrustedEvidenceEffect hostTrustedEvidence = HostTrustedEvidenceEffect.Counted,
        IReadOnlyList<DateTimeOffset>? clocks = null)
    {
        var array = new JsonArray();
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            step["clock"] = ContractJson.Timestamp(clocks is { } given && i < given.Count ? given[i] : Clock);
            array.Add(step);
        }

        return new JsonObject
        {
            ["description"] = description,
            ["service"] = new JsonObject
            {
                ["engine"] = engine?.DeepClone(),
                ["hostTrustedEvidence"] = hostTrustedEvidence.ToString(),
            },
            ["records"] = new JsonArray([.. records.Select(record => (JsonNode)ContractJson.ToStored(record))]),
            ["steps"] = array,
        };
    }

    private static JsonObject Step(string operation, JsonObject body) => new() { [operation] = body };

    private static JsonObject Submit(ExperienceReuseFeedback feedback, Scope? authorization = null, string principal = "contract-principal", JsonObject? faults = null)
    {
        var body = new JsonObject
        {
            ["authorization"] = LifecycleContract.Auth(authorization ?? S, principal),
            ["feedback"] = FeedbackJson(feedback),
        };
        if (faults is not null)
        {
            body["faults"] = faults;
        }

        return Step("submit", body);
    }

    private static JsonObject Record(RecordedExperienceReuseFeedback feedback, Scope? authorization = null) => Step("record", new JsonObject
    {
        ["authorization"] = LifecycleContract.Auth(authorization ?? S),
        ["feedback"] = RecordedJson(feedback),
    });

    private static JsonObject Derive(Guid feedbackId, Guid experienceId) => new()
    {
        ["feedbackId"] = D(feedbackId),
        ["experienceId"] = D(experienceId),
    };

    // ---- the JSON forms -------------------------------------------------------------------------------------------

    /// <summary>A submission as a step names it: every member, an undefined enum value as <see cref="Undefined"/>.</summary>
    internal static JsonObject FeedbackJson(ExperienceReuseFeedback feedback) => new()
    {
        ["feedbackId"] = D(feedback.FeedbackId),
        ["runId"] = D(feedback.RunId),
        ["scope"] = feedback.Scope is null ? null : LifecycleContract.ScopeJson(feedback.Scope),
        ["exposedExperienceIds"] = Ids(feedback.ExposedExperienceIds),
        ["runOutcome"] = Name(feedback.RunOutcome),
        ["measure"] = MeasureJson(feedback.Measure),
        ["observedAt"] = ContractJson.Timestamp(feedback.ObservedAt),
        ["claimedBenefit"] = Name(feedback.ClaimedBenefit),
        ["humanAssessment"] = feedback.HumanAssessment is not { } human ? null : new JsonObject
        {
            ["assessmentId"] = D(human.AssessmentId),
            ["benefit"] = Name(human.Benefit),
            ["attributedExperienceIds"] = Ids(human.AttributedExperienceIds),
            ["rationale"] = human.Rationale,
            ["assessedAt"] = ContractJson.Timestamp(human.AssessedAt),
            ["verificationRoundId"] = human.VerificationRoundId is { } round ? D(round) : null,
            ["assessmentToken"] = human.AssessmentToken,
        },
        ["comparativeEvaluation"] = feedback.ComparativeEvaluation is not { } comparative ? null : new JsonObject
        {
            ["evaluatorId"] = comparative.EvaluatorId,
            ["runId"] = D(comparative.RunId),
            ["verificationRoundId"] = D(comparative.VerificationRoundId),
            ["benefit"] = Name(comparative.Benefit),
            ["attributedExperienceIds"] = Ids(comparative.AttributedExperienceIds),
            ["evidence"] = comparative.Evidence is null
                ? null
                : new JsonArray([.. comparative.Evidence.Select(evidence => evidence is null ? null : JsonSerializer.SerializeToNode(evidence, ContractJson.Options))]),
            ["summary"] = comparative.Summary,
            ["evaluatedAt"] = ContractJson.Timestamp(comparative.EvaluatedAt),
        },
        ["trialLabel"] = feedback.TrialLabel,
    };

    /// <summary>A submission as the ledger stores it (<see cref="RecordedExperienceReuseFeedback"/>), for steps, results and the ledger.</summary>
    internal static JsonObject? RecordedJson(RecordedExperienceReuseFeedback? feedback) => feedback is null ? null : new JsonObject
    {
        ["feedbackId"] = D(feedback.FeedbackId),
        ["runId"] = D(feedback.RunId),
        ["scope"] = feedback.Scope is null ? null : LifecycleContract.ScopeJson(feedback.Scope),
        ["runOutcome"] = Name(feedback.RunOutcome),
        ["claimedBenefit"] = Name(feedback.ClaimedBenefit),
        ["benefit"] = Name(feedback.Benefit),
        ["attributionSource"] = Name(feedback.AttributionSource),
        ["reviewerIdentity"] = feedback.ReviewerIdentity,
        ["evaluatorId"] = feedback.EvaluatorId,
        ["verificationRoundId"] = feedback.VerificationRoundId is { } round ? D(round) : null,
        ["assessmentId"] = feedback.AssessmentId is { } assessment ? D(assessment) : null,
        ["rationale"] = feedback.Rationale,
        ["evidenceIds"] = Ids(feedback.EvidenceIds),
        ["attributedAt"] = feedback.AttributedAt is { } at ? ContractJson.Timestamp(at) : null,
        ["measure"] = MeasureJson(feedback.Measure),
        ["trialLabel"] = feedback.TrialLabel,
        ["observedAt"] = ContractJson.Timestamp(feedback.ObservedAt),
        ["exposures"] = feedback.Exposures is null ? null : new JsonArray([.. feedback.Exposures.Select(exposure => exposure is null ? null : (JsonNode)new JsonObject
        {
            ["experienceId"] = D(exposure.ExperienceId),
            ["attributed"] = exposure.Attributed,
            ["evidenceId"] = exposure.EvidenceId is { } evidence ? D(evidence) : null,
        })]),
    };

    private static JsonObject? MeasureJson(ReuseMeasure? measure) => measure is null ? null : new JsonObject
    {
        ["kind"] = measure.Kind,
        ["value"] = Number(measure.Value),
    };

    private static JsonNode Number(double value) =>
        double.IsNaN(value) ? "NaN"
        : double.IsPositiveInfinity(value) ? "Infinity"
        : double.IsNegativeInfinity(value) ? "-Infinity"
        : JsonValue.Create(value);

    private static JsonArray? Ids(IReadOnlyList<Guid>? ids) => ids is null ? null : new JsonArray([.. ids.Select(id => (JsonNode)D(id))]);

    private static string Name<T>(T value)
        where T : struct, Enum => Enum.IsDefined(value) ? value.ToString() : Undefined;

    internal static ExperienceReuseFeedback ReadFeedback(JsonObject json) => new(
        Guid.Parse(json["feedbackId"]!.GetValue<string>()),
        Guid.Parse(json["runId"]!.GetValue<string>()),
        json["scope"] is { } scope ? LifecycleContract.ReadScope(scope) : null!,
        ReadIds(json["exposedExperienceIds"])!,
        ReadEnum<TaskVerificationStatus>(json["runOutcome"]!),
        ReadMeasure(json["measure"])!,
        LifecycleContract.Instant(json["observedAt"])!.Value,
        ReadEnum<ExperienceReuseBenefit>(json["claimedBenefit"]!),
        json["humanAssessment"] is JsonObject human
            ? new HumanReuseAssessment(
                Guid.Parse(human["assessmentId"]!.GetValue<string>()),
                ReadEnum<ExperienceReuseBenefit>(human["benefit"]!),
                ReadIds(human["attributedExperienceIds"])!,
                human["rationale"]?.GetValue<string>()!,
                LifecycleContract.Instant(human["assessedAt"])!.Value,
                human["verificationRoundId"] is { } round ? Guid.Parse(round.GetValue<string>()) : null,
                human["assessmentToken"]?.GetValue<string>())
            : null,
        json["comparativeEvaluation"] is JsonObject comparative
            ? new ComparativeEvaluationResult(
                comparative["evaluatorId"]?.GetValue<string>()!,
                Guid.Parse(comparative["runId"]!.GetValue<string>()),
                Guid.Parse(comparative["verificationRoundId"]!.GetValue<string>()),
                ReadEnum<ExperienceReuseBenefit>(comparative["benefit"]!),
                ReadIds(comparative["attributedExperienceIds"])!,
                comparative["evidence"] is JsonArray evidence
                    ? [.. evidence.Select(item => item is null ? null! : item.Deserialize<Evidence>(ContractJson.Options)!)]
                    : null!,
                comparative["summary"]?.GetValue<string>()!,
                LifecycleContract.Instant(comparative["evaluatedAt"])!.Value)
            : null,
        json["trialLabel"]?.GetValue<string>());

    internal static RecordedExperienceReuseFeedback ReadRecorded(JsonObject json) => new(
        Guid.Parse(json["feedbackId"]!.GetValue<string>()),
        Guid.Parse(json["runId"]!.GetValue<string>()),
        json["scope"] is { } scope ? LifecycleContract.ReadScope(scope) : null!,
        ReadEnum<TaskVerificationStatus>(json["runOutcome"]!),
        ReadEnum<ExperienceReuseBenefit>(json["claimedBenefit"]!),
        ReadEnum<ExperienceReuseBenefit>(json["benefit"]!),
        ReadEnum<ReuseAttributionSource>(json["attributionSource"]!),
        json["reviewerIdentity"]?.GetValue<string>(),
        json["evaluatorId"]?.GetValue<string>(),
        json["verificationRoundId"] is { } round ? Guid.Parse(round.GetValue<string>()) : null,
        json["assessmentId"] is { } assessment ? Guid.Parse(assessment.GetValue<string>()) : null,
        json["rationale"]?.GetValue<string>(),
        ReadIds(json["evidenceIds"])!,
        LifecycleContract.Instant(json["attributedAt"]),
        ReadMeasure(json["measure"])!,
        json["trialLabel"]?.GetValue<string>(),
        LifecycleContract.Instant(json["observedAt"])!.Value,
        json["exposures"] is JsonArray exposures
            ? [.. exposures.Select(item => item is null
                ? null!
                : new ExperienceReuseExposure(
                    Guid.Parse(item["experienceId"]!.GetValue<string>()),
                    item["attributed"]!.GetValue<bool>(),
                    item["evidenceId"] is { } evidence ? Guid.Parse(evidence.GetValue<string>()) : null))]
            : null!);

    private static ReuseMeasure? ReadMeasure(JsonNode? measure) => measure is null ? null : new ReuseMeasure(
        measure["kind"]!.GetValue<string>(),
        measure["value"] is JsonValue value && value.TryGetValue<double>(out var number)
            ? number
            : measure["value"]!.GetValue<string>() switch
            {
                "NaN" => double.NaN,
                "Infinity" => double.PositiveInfinity,
                "-Infinity" => double.NegativeInfinity,
                var other => throw new InvalidOperationException($"Unknown measure value '{other}'."),
            });

    private static List<Guid>? ReadIds(JsonNode? ids) => ids is JsonArray array ? [.. array.Select(id => Guid.Parse(id!.GetValue<string>()))] : null;

    private static T ReadEnum<T>(JsonNode node)
        where T : struct, Enum
    {
        var text = node.GetValue<string>();
        return Enum.TryParse<T>(text, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed) && parsed.ToString() == text
            ? parsed
            : (T)(object)99;
    }

    // ---- running a case -------------------------------------------------------------------------------------------

    /// <summary>The expected result of a <c>reuse-feedback</c> case: its steps run through the service over fresh in-memory stores.</summary>
    public static JsonObject Expected(JsonObject input)
    {
        var clock = new SettableTimeProvider(LifecycleContract.Instant(input["steps"]!.AsArray().FirstOrDefault()?["clock"]) ?? Fixed.T0);
        var inner = new InMemoryExperienceRecordStore(clock);
        var records = Seed(inner, input);
        var store = new FaultingRecordStore(inner);
        var ledger = new FaultingFeedbackStore(NewInMemoryLedger());
        var lifecycle = LifecycleContract.Service(store, input["service"]!.AsObject());
        var service = new ExperienceReuseFeedbackService(ledger, lifecycle);

        var results = new JsonArray();
        foreach (var step in input["steps"]!.AsArray())
        {
            if (LifecycleContract.Instant(step!["clock"]) is { } at)
            {
                clock.Now = at;
            }

            var (operation, body) = step.AsObject().Where(member => member.Key != "clock").Select(member => (member.Key, member.Value!.AsObject())).Single();
            results.Add(operation switch
            {
                "submit" => SubmitResult(service, ledger, store, body),
                "record" => RecordResult(ledger, body),
                "apply" => LifecycleContract.ApplyResult(lifecycle, body),
                "derive" => DeriveResult(body),
                _ => throw new InvalidOperationException($"Unknown reuse-feedback step '{operation}'."),
            });
        }

        return new JsonObject
        {
            ["results"] = results,
            ["ledger"] = LedgerJson(ledger.Inner),
            ["records"] = new JsonArray([.. records.Select(record => (JsonNode)ContractJson.ToStored(LifecycleContract.Reread(inner, record)))]),
            ["histories"] = Histories(inner, records),
        };
    }

    private static JsonObject SubmitResult(ExperienceReuseFeedbackService service, FaultingFeedbackStore ledger, FaultingRecordStore store, JsonObject body)
    {
        var faults = body["faults"] as JsonObject;
        ledger.Fault = faults?["ledger"]?.GetValue<string>();
        store.Faults = faults?["commit"] is JsonObject commit
            ? commit.ToDictionary(entry => Guid.Parse(entry.Key), entry => entry.Value!.AsObject())
            : [];
        try
        {
            ExperienceReuseFeedbackResult result;
            try
            {
                result = service.RecordAsync(
                    LifecycleContract.ReadAuthorization(body["authorization"]!),
                    ReadFeedback(body["feedback"]!.AsObject()),
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (ExperienceStoreException ex)
            {
                return new JsonObject { ["thrown"] = new JsonObject { ["exception"] = nameof(ExperienceStoreException), ["message"] = ex.Message } };
            }

            return SubmitJson(result);
        }
        finally
        {
            ledger.Fault = null;
            store.Faults = [];
        }
    }

    /// <summary>A submission's result, as a <c>submit</c> step's expected result holds it.</summary>
    internal static JsonObject SubmitJson(ExperienceReuseFeedbackResult result) => new()
    {
        ["thrown"] = null,
        ["outcome"] = result.Outcome.ToString(),
        ["feedbackId"] = D(result.FeedbackId),
        ["benefit"] = result.Benefit.ToString(),
        ["attributionSource"] = result.AttributionSource.ToString(),
        ["exposures"] = new JsonArray([.. result.Exposures.Select(exposure => (JsonNode)new JsonObject
        {
            ["experienceId"] = D(exposure.ExperienceId),
            ["disposition"] = exposure.Disposition.ToString(),
            ["evidenceId"] = exposure.EvidenceId is { } evidence ? D(evidence) : null,
            ["counted"] = exposure.Counted,
            ["reuseConfidence"] = exposure.ReuseConfidence,
            ["status"] = exposure.Status?.ToString(),
            ["retryable"] = exposure.Retryable,
            ["reason"] = exposure.Reason,
        })]),
        ["errors"] = LifecycleContract.Errors(result.Errors),
        ["reason"] = result.Reason,
        ["isRetryable"] = result.IsRetryable,
    };

    private static JsonObject RecordResult(IExperienceReuseFeedbackStore ledger, JsonObject body)
    {
        var result = ledger.RecordAsync(
            LifecycleContract.ReadAuthorization(body["authorization"]!),
            ReadRecorded(body["feedback"]!.AsObject()),
            CancellationToken.None).GetAwaiter().GetResult();
        return new JsonObject
        {
            ["outcome"] = result.Outcome.ToString(),
            ["feedback"] = RecordedJson(result.Feedback),
            ["errors"] = LifecycleContract.Errors(result.Errors),
        };
    }

    private static JsonObject DeriveResult(JsonObject body)
    {
        var (feedbackId, experienceId) = (Guid.Parse(body["feedbackId"]!.GetValue<string>()), Guid.Parse(body["experienceId"]!.GetValue<string>()));
        return new JsonObject
        {
            ["evidenceId"] = D(ExperienceReuseFeedbackService.EvidenceIdFor(feedbackId, experienceId)),
            ["eventId"] = D(ExperienceReuseFeedbackService.EventIdFor(feedbackId, experienceId)),
        };
    }

    private static List<ExperienceRecord> Seed(InMemoryExperienceRecordStore store, JsonObject input)
    {
        var records = input["records"]!.AsArray().Select(stored => ContractJson.FromStored(stored!)).ToList();
        foreach (var record in records)
        {
            var created = store.CreateAsync(LifecycleContract.AuthorizationFor(record.Scope), record, CancellationToken.None).GetAwaiter().GetResult();
            if (created.Outcome != ExperienceStoreOutcome.Created)
            {
                throw new InvalidOperationException($"Seeding {record.ExperienceId} was {created.Outcome}: {string.Join("; ", created.Errors.Select(e => $"{e.Path} {e.Message}"))}");
            }
        }

        return records;
    }

    private static JsonArray Histories(IExperienceRecordStore store, IEnumerable<ExperienceRecord> records) =>
        new([.. records.Select(record => (JsonNode)new JsonObject
        {
            ["experienceId"] = D(record.ExperienceId),
            ["events"] = LifecycleContract.WholeHistory(store, record),
        })]);

    /// <summary>The .NET in-memory ledger, which the storage package keeps internal.</summary>
    private static IExperienceReuseFeedbackStore NewInMemoryLedger() =>
        (IExperienceReuseFeedbackStore)Activator.CreateInstance(LedgerType, nonPublic: true)!;

    private static readonly Type LedgerType = typeof(InMemoryExperienceRecordStore).Assembly
        .GetType("AgentExperience.Storage.InMemory.InMemoryExperienceReuseFeedbackStore", throwOnError: true)!;

    /// <summary>Every submission the in-memory ledger holds, as it stores them, by feedback ID (its text order).</summary>
    private static JsonArray LedgerJson(IExperienceReuseFeedbackStore ledger)
    {
        var field = LedgerType.GetField("_submissions", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("The in-memory ledger has no _submissions.");
        var submissions = (Dictionary<Guid, RecordedExperienceReuseFeedback>)field.GetValue(ledger)!;
        return new JsonArray([.. submissions.Values.OrderBy(stored => D(stored.FeedbackId), StringComparer.Ordinal).Select(stored => (JsonNode)RecordedJson(stored)!)]);
    }

    private static string D(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>The feedback ledger, made to throw <see cref="ExperienceStoreException"/> for one step.</summary>
    private sealed class FaultingFeedbackStore(IExperienceReuseFeedbackStore inner) : IExperienceReuseFeedbackStore
    {
        public IExperienceReuseFeedbackStore Inner => inner;

        public string? Fault { get; set; }

        public Task<ExperienceReuseFeedbackStoreResult> RecordAsync(AuthorizationContext authorization, RecordedExperienceReuseFeedback feedback, CancellationToken cancellationToken) =>
            Fault is { } message
                ? Task.FromException<ExperienceReuseFeedbackStoreResult>(new ExperienceStoreException(message))
                : inner.RecordAsync(authorization, feedback, cancellationToken);
    }

    /// <summary>The record store, made to fail one record's lifecycle commit for one step.</summary>
    private sealed class FaultingRecordStore(IExperienceRecordStore inner) : IExperienceRecordStore
    {
        public Dictionary<Guid, JsonObject> Faults { get; set; } = [];

        public Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(AuthorizationContext authorization, Scope scope, LifecycleEvent lifecycleEvent, CancellationToken cancellationToken)
        {
            if (Faults.TryGetValue(lifecycleEvent.ExperienceRecordId, out var fault))
            {
                if (fault["throws"]?.GetValue<string>() is { } message)
                {
                    throw new ExperienceStoreException(message);
                }

                return Task.FromResult(new ExperienceLifecycleCommitResult(
                    Enum.Parse<ExperienceStoreOutcome>(fault["answers"]!.GetValue<string>()),
                    lifecycleEvent.ExpectedRevision + 1,
                    CurrentStatus: null,
                    Errors: []));
            }

            return inner.CommitLifecycleEventAsync(authorization, scope, lifecycleEvent, cancellationToken);
        }

        public Task<ExperienceRecordCreateResult> CreateAsync(AuthorizationContext authorization, ExperienceRecord record, CancellationToken cancellationToken) =>
            inner.CreateAsync(authorization, record, cancellationToken);

        public Task<ExperienceRecordGetResult> GetAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, CancellationToken cancellationToken) =>
            inner.GetAsync(authorization, scope, experienceId, cancellationToken);

        public Task<ExperienceRecordGetResult> GetAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, ExperienceReadOptions options, CancellationToken cancellationToken) =>
            inner.GetAsync(authorization, scope, experienceId, options, cancellationToken);

        public Task<ExperienceRecordGetManyResult> GetManyAsync(AuthorizationContext authorization, Scope scope, IReadOnlyList<Guid> experienceIds, ExperienceReadOptions options, CancellationToken cancellationToken) =>
            inner.GetManyAsync(authorization, scope, experienceIds, options, cancellationToken);

        public Task<ExperienceRecordQueryResult> QueryAsync(AuthorizationContext authorization, ExperienceRecordQuery query, CancellationToken cancellationToken) =>
            inner.QueryAsync(authorization, query, cancellationToken);

        public Task<ExperienceRecordHistoryResult> GetHistoryAsync(AuthorizationContext authorization, ExperienceRecordHistoryQuery query, CancellationToken cancellationToken) =>
            inner.GetHistoryAsync(authorization, query, cancellationToken);

        public Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, Guid replacementExperienceId, CancellationToken cancellationToken) =>
            inner.CheckSupersessionAsync(authorization, scope, experienceId, replacementExperienceId, cancellationToken);
    }
}

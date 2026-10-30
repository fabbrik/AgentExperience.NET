using System.Buffers.Text;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Confidence;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Contract 1.15's <c>confidence-engine</c> family: how confidence moves, computed by .NET's confidence engine and nothing
/// else. Each case is one <c>input.json</c> naming an <c>operation</c>, and the <c>expected.json</c> .NET's classes give
/// for it:
/// <list type="bullet">
/// <item><c>score</c>: <see cref="ReuseConfidenceHeuristic.Score"/> over two counters.</item>
/// <item><c>rule</c>: a host engine's identity checked as the lifecycle service checks it (<c>ConfidenceEngineRule.For</c>),
/// and the rule string every update records.</item>
/// <item><c>apply</c>: one piece of evidence against a stored record: whether its status accepts evidence, the status it
/// moves to, <see cref="ReuseConfidenceHeuristic.Apply"/>'s counters scored by the engine (the default, or a host engine)
/// exactly as the lifecycle service composes them, and the independence key the update is counted under.</item>
/// <item><c>options</c>, <c>issue</c> and <c>verify</c>: the assessment token's options, <see cref="AssessmentTokenIssuer"/>
/// and the token codec's every refusal path.</item>
/// </list>
/// Nothing is written anywhere: these are pure functions. Needs no database.
/// </summary>
internal static class ConfidenceEngineContract
{
    public const string Family = "confidence-engine";

    /// <summary>The files of each case.</summary>
    public static readonly string[] CaseFiles = ["input.json", "expected.json"];

    /// <summary>What <c>ConfidenceEngineRule.For</c> is told the engine's parameter is: the lifecycle service's.</summary>
    public const string EngineParameter = "confidenceEngine";

    private const int F = 0xf6;

    private const string Key = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";
    private const string OtherKey = "f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff000102030405060708090a0b0c0d0e0f";

    /// <summary>The instant every token case's clock reads unless the case says otherwise: 2026-03-02T09:00:00Z.</summary>
    private static readonly long Now = Fixed.T0.ToUnixTimeMilliseconds();

    private static readonly long Day = (long)TimeSpan.FromDays(1).TotalMilliseconds;
    private static readonly long Skew = (long)AssessmentTokenCodec.AllowedClockSkew.TotalMilliseconds;

    private static readonly Guid Run = Fixed.Id(F, 0x100);
    private static readonly Guid Round = Fixed.Id(F, 0x200);
    private static readonly Guid Evidence = Fixed.Id(F, 0x300);
    private static readonly Guid Assessment = Fixed.Id(F, 0x400);
    private static readonly Guid Record1 = Fixed.Id(F, 1);
    private static readonly Guid Record2 = Fixed.Id(F, 2);

    private static readonly Scope Scope = Fixed.Scope;
    private static readonly Scope FullScope = new("contract-tenant", "contract-app", "contract-project", "team-ü", "refund-agent", "user-7");

    /// <summary>Every case, by directory name, as its <c>input.json</c>.</summary>
    public static IReadOnlyList<(string Name, JsonObject Input)> Cases { get; } = Build();

    private static List<(string Name, JsonObject Input)> Build()
    {
        var cases = new List<(string Name, JsonObject Input)>();
        cases.AddRange(ScoreCases());
        cases.AddRange(RuleCases());
        cases.AddRange(ApplyCases());
        cases.AddRange(OptionsCases());
        cases.AddRange(IssueCases());
        cases.AddRange(VerifyCases());
        return cases;
    }

    // ---- score -------------------------------------------------------------------------------------------------

    private static IEnumerable<(string, JsonObject)> ScoreCases()
    {
        (string Name, string Description, long S, long F)[] cases =
        [
            ("score-prior", "No evidence at all: the rule's prior, 1/2.", 0, 0),
            ("score-initial-validation", "One supporting validation (the one a record is finalized with): 2/3.", 1, 0),
            ("score-first-confirmation", "A first independent confirmation after finalization: 3/4.", 2, 0),
            ("score-contradicted", "A contradiction after a confirmation: 3/5.", 2, 1),
            ("score-only-contradictions", "Contradictions only: 1/(2 + F), towards 0 without reaching it.", 0, 1000),
            ("score-repeated-support", "Repeated independent support: towards 1 without reaching it.", 1000000, 0),
            ("score-conflicting-evidence", "As much support as contradiction: 1/2 again.", 7, 7),
            ("score-largest-counters", "Both counters at int.MaxValue: computed in double, no overflow.", int.MaxValue, int.MaxValue),
            ("score-largest-support", "Supporting at int.MaxValue and nothing against: the largest score below 1.", int.MaxValue, 0),
            ("score-negative-supporting", "A negative supporting count is refused, naming supportingValidations.", -1, 0),
            ("score-negative-contradictions", "A negative contradiction count is refused, naming contradictions.", 0, -1),
            ("score-both-negative", "Both negative: the supporting count is checked first.", -1, -1),
        ];

        foreach (var (name, description, s, f) in cases)
        {
            yield return (name, new JsonObject
            {
                ["description"] = description,
                ["operation"] = "score",
                ["supportingValidations"] = s,
                ["contradictions"] = f,
            });
        }
    }

    private static JsonObject Score(JsonObject input)
    {
        try
        {
            return new JsonObject
            {
                ["score"] = ReuseConfidenceHeuristic.Score(Int(input["supportingValidations"]), Int(input["contradictions"])),
                ["refusal"] = null,
            };
        }
        catch (ArgumentException ex)
        {
            return new JsonObject { ["score"] = null, ["refusal"] = Refusal(ex, libraryMessage: false) };
        }
    }

    // ---- rule --------------------------------------------------------------------------------------------------

    private static IEnumerable<(string, JsonObject)> RuleCases()
    {
        (string Name, string Description, JsonNode? Engine)[] cases =
        [
            ("rule-no-engine", "No engine: the heuristic, recording the plain rule version 1.0.0.", null),
            ("rule-default-engine", "The heuristic engine itself, passed explicitly: the same as none.", "default"),
            ("rule-host-engine", "A host engine records \"{RuleId}/{RuleVersion}\".", Engine("bayes", "2.1")),
            ("rule-host-engine-every-allowed-character", "Letters, digits, '.', '_' and '-' are allowed, 64 characters at most.", Engine("A.b_c-9", new string('v', 64))),
            ("rule-host-engine-claims-the-default-id", "A host engine may not use the heuristic's RuleId, even with the heuristic's version.", Engine("reuse-heuristic", "1.0.0")),
            ("rule-host-engine-default-id-is-ordinal", "The default's RuleId is compared ordinally: another casing is a different rule.", Engine("Reuse-Heuristic", "1.0.0")),
            ("rule-rule-id-empty", "An empty RuleId is refused.", Engine("", "1")),
            ("rule-rule-id-null", "A missing RuleId is refused.", Engine(null, "1")),
            ("rule-rule-id-too-long", "A RuleId of 65 characters is refused.", Engine(new string('r', 65), "1")),
            ("rule-rule-id-slash", "A '/' in the RuleId is refused: it separates the recorded rule's two parts.", Engine("bayes/x", "1")),
            ("rule-rule-id-space", "A space in the RuleId is refused.", Engine("bayes x", "1")),
            ("rule-rule-id-non-ascii", "A non-ASCII letter or digit in the RuleId is refused.", Engine("bayés", "1")),
            ("rule-rule-version-empty", "An empty RuleVersion is refused.", Engine("bayes", "")),
            ("rule-rule-version-null", "A missing RuleVersion is refused.", Engine("bayes", null)),
            ("rule-rule-version-too-long", "A RuleVersion of 65 characters is refused.", Engine("bayes", new string('v', 65))),
            ("rule-rule-version-plus", "A '+' in the RuleVersion is refused.", Engine("bayes", "2.1+build")),
            ("rule-both-malformed", "Both malformed: the RuleId is checked first.", Engine("", "")),
        ];

        foreach (var (name, description, engine) in cases)
        {
            yield return (name, new JsonObject
            {
                ["description"] = description,
                ["operation"] = "rule",
                ["engine"] = engine,
            });
        }
    }

    private static JsonObject Rule(JsonObject input)
    {
        try
        {
            return new JsonObject { ["recorded"] = ConfidenceEngineRule.For(ReadEngine(input["engine"]), EngineParameter).Recorded, ["refusal"] = null };
        }
        catch (ArgumentException ex)
        {
            return new JsonObject { ["recorded"] = null, ["refusal"] = Refusal(ex) };
        }
    }

    // ---- apply -------------------------------------------------------------------------------------------------

    private static IEnumerable<(string, JsonObject)> ApplyCases()
    {
        var machine = EvidenceJson(ConfidenceEvidenceKind.Supporting, ConfidenceEvidenceSource.Machine, Round, null);
        var machineAgainst = EvidenceJson(ConfidenceEvidenceKind.Contradicting, ConfidenceEvidenceSource.Machine, Round, null);
        var human = EvidenceJson(ConfidenceEvidenceKind.Supporting, ConfidenceEvidenceSource.Human, null, "reviewer-1");
        var humanAgainst = EvidenceJson(ConfidenceEvidenceKind.Contradicting, ConfidenceEvidenceSource.Human, null, "reviewer-1");

        var cases = new List<(string Name, string Description, ExperienceRecord Record, JsonObject Evidence, JsonNode? Engine)>
        {
            ("apply-supporting-validated", "Supporting machine evidence against a freshly validated record: S 1 -> 2, 2/3 -> 3/4, the status stays Validated.", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), machine, null),
            ("apply-supporting-reinforced", "Supporting evidence never moves a status: a Reinforced record stays Reinforced.", Record(ExperienceStatus.Reinforced, 0.75, 2, 0), machine, null),
            ("apply-contradicting-validated", "A contradiction contests a Validated record: F 0 -> 1, 3/4 -> 3/5.", Record(ExperienceStatus.Validated, 0.75, 2, 0), machineAgainst, null),
            ("apply-contradicting-reinforced", "A contradiction contests a Reinforced record.", Record(ExperienceStatus.Reinforced, 0.8, 3, 0), machineAgainst, null),
            ("apply-contradicting-contested", "A Contested record accepts further contradictions and stays Contested.", Record(ExperienceStatus.Contested, 0.6, 2, 1), machineAgainst, null),
            ("apply-supporting-contested", "Supporting evidence against a Contested record moves its counters, not its status.", Record(ExperienceStatus.Contested, 0.6, 2, 1), machine, null),
            ("apply-human-supporting", "Human evidence is keyed on the reviewer and the run.", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), human, null),
            ("apply-human-contradicting", "A human contradiction contests the record, keyed on the reviewer and the run.", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), humanAgainst, null),
            ("apply-human-non-ascii-reviewer", "A reviewer identity is carried into the key verbatim, never folded.", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), EvidenceJson(ConfidenceEvidenceKind.Supporting, ConfidenceEvidenceSource.Human, null, "Rêviewer:Ω"), null),
            ("apply-detail-carried", "The evidence's detail is carried onto the update unchanged.", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), EvidenceJson(ConfidenceEvidenceKind.Supporting, ConfidenceEvidenceSource.Machine, Round, null, "the retry-after-lock lesson held"), null),
            ("apply-repeated-evidence", "Repeated independent support far along: S 41 -> 42, still below 1.", Record(ExperienceStatus.Reinforced, 41d / 42d, 40, 0), machine, null),
            ("apply-conflicting-evidence", "Support against a record with as many contradictions: the counters, not the stored score, decide.", Record(ExperienceStatus.Contested, 0.5, 5, 5), machine, null),
            ("apply-stored-score-ignored", "The stored score is only the prior: the new score is computed from the counters alone.", Record(ExperienceStatus.Validated, 0.01, 1, 0), machine, null),
            ("apply-zero-counters", "A record with no evidence at all (written by hand): S 0 -> 1, 1/2 -> 2/3.", Record(ExperienceStatus.Validated, 0.5, 0, 0), machine, null),
            ("apply-supporting-at-the-largest-count", "A supporting count at int.MaxValue cannot take one more: refused, naming record.", Record(ExperienceStatus.Validated, 0.99, int.MaxValue, 0), machine, null),
            ("apply-contradicting-at-the-largest-count", "A contradiction count at int.MaxValue cannot take one more: refused, naming record.", Record(ExperienceStatus.Contested, 0.01, 1, int.MaxValue), machineAgainst, null),
            ("apply-contradicting-below-the-largest-count", "Supporting evidence leaves a saturated contradiction count alone.", Record(ExperienceStatus.Contested, 0.01, 1, int.MaxValue), machine, null),
            ("apply-negative-supporting-counter", "A stored negative supporting count is refused, naming record.SupportingValidations.", Record(ExperienceStatus.Validated, 0.5, -1, 0), machine, null),
            ("apply-negative-contradictions-counter", "A stored negative contradiction count is refused, naming record.Contradictions.", Record(ExperienceStatus.Validated, 0.5, 1, -1), machine, null),
            ("apply-machine-without-round", "Machine evidence without a verification round has no independence key: refused, naming update.", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), EvidenceJson(ConfidenceEvidenceKind.Supporting, ConfidenceEvidenceSource.Machine, null, null), null),
            ("apply-human-without-reviewer", "Human evidence without a reviewer identity has no independence key: refused, naming update.", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), EvidenceJson(ConfidenceEvidenceKind.Supporting, ConfidenceEvidenceSource.Human, null, null), null),
            ("apply-human-blank-reviewer", "A blank reviewer identity is no reviewer: refused, naming update.", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), EvidenceJson(ConfidenceEvidenceKind.Supporting, ConfidenceEvidenceSource.Human, null, " \t"), null),
            ("apply-human-padded-reviewer", "A reviewer identity with leading or trailing whitespace is refused rather than trimmed, naming reviewerIdentity.", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), EvidenceJson(ConfidenceEvidenceKind.Supporting, ConfidenceEvidenceSource.Human, null, " reviewer-1"), null),
            ("apply-machine-with-reviewer", "Machine evidence is keyed on the run and round; a reviewer it carries is not part of the key.", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), EvidenceJson(ConfidenceEvidenceKind.Supporting, ConfidenceEvidenceSource.Machine, Round, "reviewer-1"), null),
            ("apply-human-with-round", "Human evidence is keyed on the reviewer and run; a round it carries is not part of the key.", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), EvidenceJson(ConfidenceEvidenceKind.Supporting, ConfidenceEvidenceSource.Human, Round, "reviewer-1"), null),
            ("apply-host-engine", "A host engine scores the counters after the evidence ((2 + S) / (4 + S + F)) and the update records \"bayes/2.1\".", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), machine, Engine("bayes", "2.1", "bayes")),
            ("apply-host-engine-contradicting", "A host engine also scores a contradiction; the move to Contested stays the library's.", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), machineAgainst, Engine("bayes", "2.1", "bayes")),
            ("apply-host-engine-sees-the-record-as-read", "A host engine is given the record as read: one returning its stored score leaves the score where it was.", Record(ExperienceStatus.Validated, 0.7, 1, 0), machine, Engine("stored", "1", "stored")),
            ("apply-host-engine-zero", "A host engine may return exactly 0.", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), machine, Engine("floor", "1", 0d)),
            ("apply-host-engine-one", "A host engine may return exactly 1.", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), machine, Engine("ceiling", "1", 1d)),
            ("apply-host-engine-below-zero", "A score below 0 is refused, never clamped: InvalidOperationException naming the rule and the value.", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), machine, Engine("low", "1", -0.5)),
            ("apply-host-engine-above-one", "A score above 1 is refused, never clamped.", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), machine, Engine("high", "1", 1.5)),
            ("apply-host-engine-just-above-one", "The next double above 1 is refused.", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), machine, Engine("high", "1", Math.BitIncrement(1d))),
            ("apply-host-engine-just-below-zero", "The smallest negative double is refused, written in exponent form.", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), machine, Engine("low", "1", -double.Epsilon)),
            ("apply-host-engine-large", "A large value is refused, written in exponent form.", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), machine, Engine("high", "1", 1e17)),
            ("apply-host-engine-nan", "NaN is refused.", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), machine, Engine("nan", "1", "NaN")),
            ("apply-host-engine-infinity", "Positive infinity is refused.", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), machine, Engine("inf", "1", "Infinity")),
            ("apply-host-engine-negative-infinity", "Negative infinity is refused.", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), machine, Engine("inf", "1", "-Infinity")),
            ("apply-host-engine-throws", "An exception the engine throws propagates unchanged.", Record(ExperienceStatus.Validated, 2d / 3d, 1, 0), machine, Engine("broken", "1", "throws")),
            ("apply-host-engine-counter-refused-first", "A saturated counter is refused before the engine is asked anything.", Record(ExperienceStatus.Validated, 0.99, int.MaxValue, 0), machine, Engine("broken", "1", "throws")),
        };

        foreach (var status in new[] { ExperienceStatus.Candidate, ExperienceStatus.Quarantined, ExperienceStatus.Stale, ExperienceStatus.Superseded, ExperienceStatus.Revoked })
        {
            cases.Add((
                $"apply-against-{status.ToString().ToLowerInvariant()}",
                $"A {status} record does not accept evidence; the update the heuristic would compute, and the status it would keep, are reported anyway.",
                Record(status, 2d / 3d, 1, 0),
                machineAgainst,
                null));
        }

        foreach (var (name, description, record, evidence, engine) in cases)
        {
            yield return (name, new JsonObject
            {
                ["description"] = description,
                ["operation"] = "apply",
                ["record"] = ContractJson.ToStored(record),
                ["evidence"] = evidence.DeepClone(),
                ["engine"] = engine?.DeepClone(),
            });
        }
    }

    private static JsonObject Apply(JsonObject input)
    {
        var record = ContractJson.FromStored(input["record"]!);
        var evidence = input["evidence"]!.AsObject();
        var kind = Enum.Parse<ConfidenceEvidenceKind>(evidence["kind"]!.GetValue<string>());
        var source = Enum.Parse<ConfidenceEvidenceSource>(evidence["source"]!.GetValue<string>());
        var rule = ConfidenceEngineRule.For(ReadEngine(input["engine"]), EngineParameter);

        var result = new JsonObject
        {
            ["acceptsEvidence"] = ReuseConfidenceHeuristic.AcceptsEvidenceIn(record.Status),
            ["statusAfter"] = ReuseConfidenceHeuristic.StatusAfter(record.Status, kind).ToString(),
        };

        ConfidenceUpdate update;
        try
        {
            update = ReuseConfidenceHeuristic.Apply(
                record,
                Guid.Parse(evidence["evidenceId"]!.GetValue<string>()),
                kind,
                source,
                Guid.Parse(evidence["runId"]!.GetValue<string>()),
                evidence["verificationRoundId"] is { } round ? Guid.Parse(round.GetValue<string>()) : null,
                evidence["reviewerIdentity"]?.GetValue<string>(),
                evidence["detail"]?.GetValue<string>());

            // Exactly as ExperienceLifecycleService composes them: the heuristic settles the counters, the engine scores
            // them, and the rule string is the engine rule's.
            update = update with
            {
                NewReuseConfidence = rule.Score(record, update.NewSupportingValidations, update.NewContradictions),
                RuleVersion = rule.Recorded,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HostEngineException)
        {
            result["update"] = null;
            result["recordedOnly"] = null;
            result["independenceKey"] = null;
            result["refusal"] = Refusal(ex, libraryMessage: ex is InvalidOperationException);
            return result;
        }

        result["update"] = UpdateJson(update);
        var recordedOnly = update.AsRecordedOnly();
        result["recordedOnly"] = new JsonObject
        {
            ["newReuseConfidence"] = recordedOnly.NewReuseConfidence,
            ["newSupportingValidations"] = recordedOnly.NewSupportingValidations,
            ["newContradictions"] = recordedOnly.NewContradictions,
            ["counted"] = recordedOnly.Counted,
        };

        try
        {
            var key = ReuseConfidenceHeuristic.IndependenceKeyFor(update);
            result["independenceKey"] = new JsonObject { ["source"] = key.Source.ToString(), ["value"] = key.Value, ["refusal"] = null };
        }
        catch (ArgumentException ex)
        {
            result["independenceKey"] = new JsonObject { ["source"] = null, ["value"] = null, ["refusal"] = Refusal(ex) };
        }

        result["refusal"] = null;
        return result;
    }

    private static ExperienceRecord Record(ExperienceStatus status, double confidence, int supporting, int contradictions) =>
        SearchContract.Seed(F, 1, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt.", status, confidence) with
        {
            SupportingValidations = supporting,
            Contradictions = contradictions,
        };

    private static JsonObject EvidenceJson(ConfidenceEvidenceKind kind, ConfidenceEvidenceSource source, Guid? round, string? reviewer, string? detail = null) => new()
    {
        ["evidenceId"] = D(Evidence),
        ["kind"] = kind.ToString(),
        ["source"] = source.ToString(),
        ["runId"] = D(Run),
        ["verificationRoundId"] = round is { } r ? D(r) : null,
        ["reviewerIdentity"] = reviewer,
        ["detail"] = detail,
    };

    private static JsonObject UpdateJson(ConfidenceUpdate update) => new()
    {
        ["evidenceId"] = D(update.EvidenceId),
        ["kind"] = update.Kind.ToString(),
        ["source"] = update.Source.ToString(),
        ["runId"] = D(update.RunId),
        ["verificationRoundId"] = update.VerificationRoundId is { } round ? D(round) : null,
        ["reviewerIdentity"] = update.ReviewerIdentity,
        ["ruleVersion"] = update.RuleVersion,
        ["priorReuseConfidence"] = update.PriorReuseConfidence,
        ["newReuseConfidence"] = update.NewReuseConfidence,
        ["priorSupportingValidations"] = update.PriorSupportingValidations,
        ["newSupportingValidations"] = update.NewSupportingValidations,
        ["priorContradictions"] = update.PriorContradictions,
        ["newContradictions"] = update.NewContradictions,
        ["detail"] = update.Detail,
        ["counted"] = update.Counted,
    };

    // ---- options -----------------------------------------------------------------------------------------------

    private static IEnumerable<(string, JsonObject)> OptionsCases()
    {
        var day = TimeSpan.FromDays(1).Ticks;
        var max = ExperienceIndependenceOptions.MaximumAssessmentTokenLifetime.Ticks;
        (string Name, string Description, JsonObject Options)[] cases =
        [
            ("options-defaults", "A 32-byte key and every default (Verified, a one-day lifetime): an issuer can be built.", Options()),
            ("options-no-key", "No key: an issuer cannot be built.", Options(key: null)),
            ("options-short-key", "A 31-byte key is refused.", Options(key: Key[..62])),
            ("options-long-key", "A key longer than 32 bytes is accepted.", Options(key: Key + OtherKey)),
            ("options-zero-lifetime", "A zero lifetime is refused.", Options(lifetimeTicks: 0)),
            ("options-negative-lifetime", "A negative lifetime is refused.", Options(lifetimeTicks: -day)),
            ("options-longest-lifetime", "A lifetime of exactly 30 days is accepted.", Options(lifetimeTicks: max)),
            ("options-lifetime-too-long", "One tick over 30 days is refused.", Options(lifetimeTicks: max + 1)),
            ("options-shortest-lifetime", "One tick is a strictly positive lifetime.", Options(lifetimeTicks: 1)),
            ("options-trusting-mode", "TrustHostSuppliedIdentifiers is a defined mode: the issuer is built all the same.", Options(verification: "TrustHostSuppliedIdentifiers")),
            ("options-undefined-mode", "An undefined verification mode is refused, before anything else.", Options(verification: "Sometimes", key: null, lifetimeTicks: 0)),
            ("options-lifetime-before-key", "The lifetime is checked before the key.", Options(key: Key[..2], lifetimeTicks: 0)),
        ];

        foreach (var (name, description, options) in cases)
        {
            yield return (name, new JsonObject
            {
                ["description"] = description,
                ["operation"] = "options",
                ["options"] = options,
            });
        }
    }

    private static JsonObject OptionsResult(JsonObject input)
    {
        try
        {
            _ = new AssessmentTokenIssuer(ReadOptions(input["options"]!.AsObject()));
            return new JsonObject { ["accepted"] = true, ["refusal"] = null };
        }
        catch (ArgumentException ex)
        {
            return new JsonObject { ["accepted"] = false, ["refusal"] = Refusal(ex) };
        }
    }

    // ---- issue -------------------------------------------------------------------------------------------------

    private static IEnumerable<(string, JsonObject)> IssueCases()
    {
        var reviewer = Reviewer("reviewer-1", Scope.TenantId, Scope.ApplicationId, Scope.ProjectId);
        var sixtyFour = Enumerable.Range(1, ExperienceReuseFeedback.MaxExposedRecords).Select(n => Fixed.Id(F, 0x500 + n)).ToArray();
        var sixtyFive = Enumerable.Range(1, ExperienceReuseFeedback.MaxExposedRecords + 1).Select(n => Fixed.Id(F, 0x500 + n)).ToArray();
        var cases = new List<(string Name, string Description, JsonObject Options, JsonObject Reviewer, Scope Scope, Guid Run, string Kind, Guid[] Ids)>
        {
            ("issue-supporting", "A supporting assessment of one record, under a reviewer bounded to the record's tenant, application and project.", Options(), reviewer, Scope, Run, "Supporting", [Record1]),
            ("issue-contradicting", "A contradicting assessment of two records.", Options(), reviewer, Scope, Run, "Contradicting", [Record1, Record2]),
            ("issue-every-scope-field", "A scope with every field set, one of them non-ASCII, under a reviewer bounded to all of them.", Options(), Reviewer("reviewer-1", FullScope.TenantId, FullScope.ApplicationId, FullScope.ProjectId, FullScope.TeamId, FullScope.AgentId, FullScope.UserId), FullScope, Run, "Supporting", [Record1]),
            ("issue-unbounded-reviewer", "A reviewer bounded to the tenant only may assess any scope in it.", Options(), Reviewer("reviewer-1", Scope.TenantId), Scope, Run, "Supporting", [Record1]),
            ("issue-non-ascii-reviewer", "A non-ASCII reviewer principal, signed as UTF-8.", Options(), Reviewer("Rêviewer:Ω", Scope.TenantId), Scope, Run, "Supporting", [Record1]),
            ("issue-most-records", "An assessment of 64 records, the most a token covers.", Options(), reviewer, Scope, Run, "Supporting", sixtyFour),
            ("issue-sub-millisecond-lifetime", "A lifetime of 1.5 ms expires 1 ms after issue: whole milliseconds, truncated.", Options(lifetimeTicks: 15000), reviewer, Scope, Run, "Supporting", [Record1]),
            ("issue-longest-lifetime", "A 30-day lifetime is signed into the token.", Options(lifetimeTicks: ExperienceIndependenceOptions.MaximumAssessmentTokenLifetime.Ticks), reviewer, Scope, Run, "Supporting", [Record1]),
            ("issue-before-the-epoch", "A clock before 1970: the issue time is a negative number of milliseconds.", Options(nowUnixMs: -1000), reviewer, Scope, Run, "Supporting", [Record1]),
            ("issue-blank-principal", "A blank reviewer principal is refused, naming reviewer.", Options(), Reviewer(" ", Scope.TenantId), Scope, Run, "Supporting", [Record1]),
            ("issue-padded-principal", "A reviewer principal with trailing whitespace is refused rather than trimmed, naming reviewer.", Options(), Reviewer("reviewer-1 ", Scope.TenantId), Scope, Run, "Supporting", [Record1]),
            ("issue-scope-outside-authorization", "A scope outside the reviewer's bounds is refused, naming scope.", Options(), Reviewer("reviewer-1", Scope.TenantId, "other-app"), Scope, Run, "Supporting", [Record1]),
            ("issue-other-tenant", "A scope in another tenant is refused, naming scope.", Options(), Reviewer("reviewer-1", "other-tenant"), Scope, Run, "Supporting", [Record1]),
            ("issue-blank-tenant-authorization", "Bounds with a blank tenant permit nothing, even a scope with the same blank tenant: refused, naming scope.", Options(), Reviewer("reviewer-1", " "), new Scope(" ", "contract-app", "contract-project"), Run, "Supporting", [Record1]),
            ("issue-empty-run", "An empty run is refused, naming runId.", Options(), reviewer, Scope, Guid.Empty, "Supporting", [Record1]),
            ("issue-undefined-kind", "An undefined evidence kind is refused, naming kind.", Options(), reviewer, Scope, Run, "Sideways", [Record1]),
            ("issue-no-records", "No record is refused, naming experienceIds.", Options(), reviewer, Scope, Run, "Supporting", []),
            ("issue-too-many-records", "65 records is refused, naming experienceIds.", Options(), reviewer, Scope, Run, "Supporting", sixtyFive),
            ("issue-empty-record", "An empty record ID is refused, naming experienceIds.", Options(), reviewer, Scope, Run, "Supporting", [Record1, Guid.Empty]),
            ("issue-duplicate-records", "A record named twice is refused, naming experienceIds.", Options(), reviewer, Scope, Run, "Supporting", [Record1, Record2, Record1]),
            ("issue-refusal-order", "Every argument wrong: the reviewer is checked first.", Options(), Reviewer(" ", "other-tenant"), Scope, Guid.Empty, "Sideways", []),
            ("issue-scope-before-run", "Scope and run both wrong: the scope is checked first.", Options(), Reviewer("reviewer-1", "other-tenant"), Scope, Guid.Empty, "Supporting", [Record1]),
            ("issue-run-before-kind", "Run and kind both wrong: the run is checked first.", Options(), reviewer, Scope, Guid.Empty, "Sideways", [Record1]),
            ("issue-kind-before-records", "Kind and records both wrong: the kind is checked first.", Options(), reviewer, Scope, Run, "Sideways", []),
            ("issue-without-a-key", "With no key, no issuer can be built at all.", Options(key: null), reviewer, Scope, Run, "Supporting", [Record1]),
        };

        foreach (var (name, description, options, who, scope, run, kind, ids) in cases)
        {
            yield return (name, new JsonObject
            {
                ["description"] = description,
                ["operation"] = "issue",
                ["options"] = options.DeepClone(),
                ["assessmentId"] = D(Assessment),
                ["reviewer"] = who.DeepClone(),
                ["scope"] = ScopeJson(scope),
                ["runId"] = D(run),
                ["kind"] = kind,
                ["experienceIds"] = new JsonArray([.. ids.Select(id => (JsonNode)D(id))]),
            });
        }
    }

    private static JsonObject Issue(JsonObject input)
    {
        var options = ReadOptions(input["options"]!.AsObject());
        var reviewer = ReadReviewer(input["reviewer"]!.AsObject());
        var scope = ReadScope(input["scope"]!.AsObject());
        var run = Guid.Parse(input["runId"]!.GetValue<string>());
        var kind = ReadKind(input["kind"]!.GetValue<string>());
        var ids = input["experienceIds"]!.AsArray().Select(id => Guid.Parse(id!.GetValue<string>())).ToArray();
        try
        {
            // The issuer refuses exactly as a host sees it; it draws a fresh assessment ID, so the token the fixture pins
            // is the same codec's encoding under the case's assessment ID -- the one line Issue adds to its checks.
            _ = new AssessmentTokenIssuer(options).Issue(reviewer, scope, run, kind, ids);
            var issued = AssessmentTokenCodec.Create(options)!.Encode(
                Guid.Parse(input["assessmentId"]!.GetValue<string>()), scope, run, reviewer.PrincipalId, kind, ids);
            return new JsonObject
            {
                ["assessmentId"] = D(issued.AssessmentId),
                ["token"] = issued.Token,
                ["issuedAtUnixMs"] = issued.IssuedAt.ToUnixTimeMilliseconds(),
                ["expiresAtUnixMs"] = issued.ExpiresAt.ToUnixTimeMilliseconds(),
                ["refusal"] = null,
            };
        }
        catch (ArgumentException ex)
        {
            return new JsonObject
            {
                ["assessmentId"] = null,
                ["token"] = null,
                ["issuedAtUnixMs"] = null,
                ["expiresAtUnixMs"] = null,
                ["refusal"] = Refusal(ex),
            };
        }
    }

    // ---- verify ------------------------------------------------------------------------------------------------

    private sealed record Binding(Scope Scope, Guid Run, string Reviewer, ConfidenceEvidenceKind Kind);

    private static IEnumerable<(string, JsonObject)> VerifyCases()
    {
        var bound = new Binding(Scope, Run, "reviewer-1", ConfidenceEvidenceKind.Supporting);
        var one = new[] { Record1 };
        var genuine = Mint(bound, one);
        var two = Mint(bound, [Record1, Record2]);
        var sixtyFour = Mint(bound, [.. Enumerable.Range(1, ExperienceReuseFeedback.MaxExposedRecords).Select(n => Fixed.Id(F, 0x500 + n))]);
        var full = new Binding(FullScope, Run, "Rêviewer:Ω", ConfidenceEvidenceKind.Contradicting);
        var lone = new Binding(Scope, Run, "rev\uD800iewer", ConfidenceEvidenceKind.Supporting);
        var encoded = genuine[AssessmentTokenCodec.Prefix.Length..];

        var cases = new List<(string Name, string Description, string? Token, Binding Binding, long Now, string Key)>
        {
            ("verify-genuine", "A genuine token, bound to exactly this scope, run, reviewer and kind: its assessment ID and the records it covers.", genuine, bound, Now, Key),
            ("verify-genuine-two-records", "A genuine token covering two records, in the order it was minted with.", two, bound, Now, Key),
            ("verify-genuine-most-records", "A genuine token covering 64 records.", sixtyFour, bound, Now, Key),
            ("verify-genuine-every-scope-field", "A genuine contradicting token over a scope with every field set and a non-ASCII reviewer.", Mint(full, one), full, Now, Key),
            ("verify-genuine-lone-surrogate-reviewer", "Minted for a reviewer holding a lone surrogate, which .NET signs as U+FFFD (and JSON carries as U+FFFD): the reviewer spelled with U+FFFD verifies.", Mint(lone, one), lone with { Reviewer = "rev\uFFFDiewer" }, Now, Key),
            ("verify-missing", "No token: AssessmentTokenMissing.", null, bound, Now, Key),
            ("verify-empty", "An empty token: AssessmentTokenMissing.", "", bound, Now, Key),
            ("verify-prefix-only", "The prefix and nothing else: too short to be a token.", AssessmentTokenCodec.Prefix, bound, Now, Key),
            ("verify-wrong-prefix", "Another version's prefix.", "aexat2." + encoded, bound, Now, Key),
            ("verify-prefix-case", "The prefix is compared ordinally.", "AEXAT1." + encoded, bound, Now, Key),
            ("verify-no-prefix", "The encoding without its prefix.", encoded, bound, Now, Key),
            ("verify-random-guid", "A random GUID, the classic forgery.", Fixed.Id(F, 0x900).ToString("D"), bound, Now, Key),
            ("verify-too-long", "A token longer than 4096 characters is refused before any decoding.", AssessmentTokenCodec.Prefix + new string('A', AssessmentTokenCodec.MaxTokenLength), bound, Now, Key),
            ("verify-padding", "Padding is outside the unpadded base64url alphabet.", genuine + "=", bound, Now, Key),
            ("verify-standard-alphabet", "'+' and '/' are base64, not base64url.", AssessmentTokenCodec.Prefix + encoded.Replace('-', '+').Replace('_', '/') + "+/", bound, Now, Key),
            ("verify-whitespace", "A space inside the encoding.", AssessmentTokenCodec.Prefix + encoded[..10] + " " + encoded[10..], bound, Now, Key),
            ("verify-non-ascii", "A non-ASCII letter inside the encoding.", AssessmentTokenCodec.Prefix + encoded[..10] + "é" + encoded[11..], bound, Now, Key),
            ("verify-undecodable-length", "An encoding whose length leaves one character over decodes to nothing.", genuine + "A", bound, Now, Key),
            ("verify-too-short", "Fewer bytes than a header, one record and a MAC.", AssessmentTokenCodec.Prefix + Base64Url.EncodeToString(Bytes(genuine)[..80]), bound, Now, Key),
            ("verify-wrong-version", "A version byte other than 1.", Rewrite(genuine, bytes => bytes[0] = 2), bound, Now, Key),
            ("verify-zero-records", "A count of zero records.", Mint(bound, []), bound, Now, Key),
            ("verify-too-many-records", "A count of 65 records, one over the most a token covers.", Mint(bound, [.. Enumerable.Range(1, ExperienceReuseFeedback.MaxExposedRecords + 1).Select(n => Fixed.Id(F, 0x500 + n))]), bound, Now, Key),
            ("verify-count-disagrees-with-length", "A count that disagrees with the bytes present.", Rewrite(two, bytes => bytes[34] = 1), bound, Now, Key),
            ("verify-trailing-byte", "One byte more than the count accounts for.", AssessmentTokenCodec.Prefix + Base64Url.EncodeToString([.. Bytes(genuine), 0]), bound, Now, Key),
            ("verify-non-canonical", "The same bytes in a non-canonical encoding (unused low bits set) are not the same token.", NonCanonical(genuine), bound, Now, Key),
            ("verify-tampered-record", "A record ID changed after signing.", Rewrite(genuine, bytes => bytes[40] ^= 1), bound, Now, Key),
            ("verify-tampered-expiry", "An expiry moved after signing.", Rewrite(genuine, bytes => bytes[30] ^= 1), bound, Now, Key),
            ("verify-tampered-mac", "A MAC with one bit flipped.", Rewrite(genuine, bytes => bytes[^1] ^= 1), bound, Now, Key),
            ("verify-other-key", "Minted under another key.", genuine, bound, Now, OtherKey),
            ("verify-other-tenant", "Bound to another tenant.", genuine, bound with { Scope = Scope with { TenantId = "other-tenant" } }, Now, Key),
            ("verify-other-team", "Bound to a scope with a team the token's scope does not have.", genuine, bound with { Scope = Scope with { TeamId = "team" } }, Now, Key),
            ("verify-empty-versus-absent", "An empty optional scope field is not an absent one.", genuine, bound with { Scope = Scope with { UserId = "" } }, Now, Key),
            ("verify-other-run", "Bound to another run.", genuine, bound with { Run = Fixed.Id(F, 0x101) }, Now, Key),
            ("verify-other-reviewer", "Bound to another reviewer.", genuine, bound with { Reviewer = "reviewer-2" }, Now, Key),
            ("verify-reviewer-case", "Reviewers are compared ordinally, never folded.", genuine, bound with { Reviewer = "Reviewer-1" }, Now, Key),
            ("verify-other-kind", "A supporting token presented as contradicting evidence.", genuine, bound with { Kind = ConfidenceEvidenceKind.Contradicting }, Now, Key),
            ("verify-other-kind-even-when-expired", "The kind is checked before the expiry: a token presented in the wrong direction is invalid, not expired.", genuine, bound with { Kind = ConfidenceEvidenceKind.Contradicting }, Now + Day, Key),
            ("verify-just-before-expiry", "One millisecond before the expiry the token is accepted.", genuine, bound, Now + Day - 1, Key),
            ("verify-at-expiry", "At the expiry exactly: AssessmentTokenExpired.", genuine, bound, Now + Day, Key),
            ("verify-long-expired", "Long after the expiry: AssessmentTokenExpired.", genuine, bound, Now + (40 * Day), Key),
            ("verify-at-the-allowed-skew", "Issued exactly five minutes in the verifier's future: accepted, as clock skew.", genuine, bound, Now - Skew, Key),
            ("verify-beyond-the-allowed-skew", "Issued five minutes and a millisecond in the verifier's future: invalid.", genuine, bound, Now - Skew - 1, Key),
            ("verify-longest-lifetime", "A genuine token with a 30-day lifetime, a millisecond before it expires.", Mint(bound, one, lifetime: ExperienceIndependenceOptions.MaximumAssessmentTokenLifetime), bound, Now + (30 * Day) - 1, Key),
            ("verify-lifetime-over-the-maximum", "Signed with a lifetime past 30 days (only a key shared with another minter could): invalid.", Mint(bound, one, lifetime: TimeSpan.FromDays(31)), bound, Now, Key),
            ("verify-expiry-not-after-issue", "Signed with an expiry no later than its issue time: invalid.", Mint(bound, one, lifetime: TimeSpan.Zero), bound, Now, Key),
            ("verify-expiry-before-issue", "Signed with an expiry before its issue time: invalid, not expired.", Mint(bound, one, lifetime: TimeSpan.FromMilliseconds(-5)), bound, Now, Key),
            ("verify-issued-before-the-epoch", "Signed with an issue time before 1970: invalid.", Mint(bound, one, issuedAt: -1000), bound, 0, Key),
        };

        foreach (var (name, description, token, binding, now, key) in cases)
        {
            yield return (name, new JsonObject
            {
                ["description"] = description,
                ["operation"] = "verify",
                ["options"] = Options(key: key, nowUnixMs: now),
                ["token"] = token,
                ["scope"] = ScopeJson(binding.Scope),
                ["runId"] = D(binding.Run),
                ["reviewer"] = binding.Reviewer,
                ["kind"] = binding.Kind.ToString(),
            });
        }
    }

    private static JsonObject Verify(JsonObject input)
    {
        var codec = AssessmentTokenCodec.Create(ReadOptions(input["options"]!.AsObject()))!;
        var (refusal, assessmentId, covered) = codec.Verify(
            input["token"]?.GetValue<string>(),
            ReadScope(input["scope"]!.AsObject()),
            Guid.Parse(input["runId"]!.GetValue<string>()),
            input["reviewer"]!.GetValue<string>(),
            ReadKind(input["kind"]!.GetValue<string>()));
        return new JsonObject
        {
            ["refusal"] = refusal?.ToString(),
            ["assessmentId"] = D(assessmentId),
            ["covered"] = new JsonArray([.. covered.Select(id => (JsonNode)D(id))]),
        };
    }

    /// <summary>
    /// A token through the production codec, under <see cref="Key"/>, the case clock and <see cref="Assessment"/>. A
    /// lifetime the options refuse (the paths only a careless minter sharing the key could reach) builds the codec through
    /// its private constructor, so the bytes are still the codec's own encoding.
    /// </summary>
    private static string Mint(Binding binding, Guid[] ids, TimeSpan? lifetime = null, long? issuedAt = null)
    {
        var span = lifetime ?? ExperienceIndependenceOptions.DefaultAssessmentTokenLifetime;
        var options = ReadOptions(Options(nowUnixMs: issuedAt ?? Now, lifetimeTicks: span.Ticks));
        AssessmentTokenCodec codec;
        if (span > TimeSpan.Zero && span <= ExperienceIndependenceOptions.MaximumAssessmentTokenLifetime)
        {
            codec = AssessmentTokenCodec.Create(options)!;
        }
        else
        {
            var constructor = typeof(AssessmentTokenCodec).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, [typeof(byte[]), typeof(TimeSpan), typeof(TimeProvider)])
                ?? throw new InvalidOperationException("AssessmentTokenCodec's private constructor has changed.");
            codec = (AssessmentTokenCodec)constructor.Invoke([options.AssessmentTokenKey!, span, options.TimeProvider]);
        }

        return codec.Encode(Assessment, binding.Scope, binding.Run, binding.Reviewer, binding.Kind, ids).Token;
    }

    private static byte[] Bytes(string token) => Base64Url.DecodeFromChars(token.AsSpan(AssessmentTokenCodec.Prefix.Length));

    private static string Rewrite(string token, Action<byte[]> change)
    {
        var bytes = Bytes(token);
        change(bytes);
        return AssessmentTokenCodec.Prefix + Base64Url.EncodeToString(bytes);
    }

    /// <summary>The token with its last character's unused low bit set: the same bytes, encoded non-canonically.</summary>
    private static string NonCanonical(string token)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var encoded = token[AssessmentTokenCodec.Prefix.Length..];
        if (encoded.Length % 4 == 0)
        {
            throw new InvalidOperationException("A token whose encoding has no unused bits cannot be made non-canonical.");
        }

        var last = Alphabet.IndexOf(encoded[^1], StringComparison.Ordinal);
        return AssessmentTokenCodec.Prefix + encoded[..^1] + Alphabet[last | 1];
    }

    // ---- shared ------------------------------------------------------------------------------------------------

    /// <summary>The expected result of a case, from its input as checked in.</summary>
    public static JsonObject Expected(JsonObject input) => input["operation"]!.GetValue<string>() switch
    {
        "score" => Score(input),
        "rule" => Rule(input),
        "apply" => Apply(input),
        "options" => OptionsResult(input),
        "issue" => Issue(input),
        "verify" => Verify(input),
        var other => throw new InvalidOperationException($"Unknown confidence-engine operation '{other}'."),
    };

    /// <summary>
    /// A refusal: the .NET exception's type and parameter, and the message when the library wrote it (without the
    /// <c>(Parameter '...')</c> suffix .NET appends), which a port reproduces byte for byte. A guard clause's message is
    /// the runtime's wording, not the library's, so it is <see langword="null"/>, as is a host engine's own exception's.
    /// </summary>
    private static JsonObject Refusal(Exception ex, bool libraryMessage = true)
    {
        var parameter = (ex as ArgumentException)?.ParamName;
        var message = ex.Message;
        var suffix = $" (Parameter '{parameter}')";
        if (parameter is not null && message.EndsWith(suffix, StringComparison.Ordinal))
        {
            message = message[..^suffix.Length];
        }

        return new JsonObject
        {
            ["exception"] = ex.GetType().Name,
            ["parameter"] = parameter,
            ["message"] = libraryMessage && ex is not HostEngineException ? message : null,
        };
    }

    private static JsonObject Engine(string? ruleId, string? ruleVersion, JsonNode? score = null) => new()
    {
        ["ruleId"] = ruleId,
        ["ruleVersion"] = ruleVersion,
        ["score"] = score ?? "bayes",
    };

    private static IExperienceConfidenceEngine? ReadEngine(JsonNode? engine) => engine switch
    {
        null => null,
        JsonValue value when value.GetValue<string>() == "default" => ReuseConfidenceHeuristicEngine.Instance,
        JsonObject host => new HostEngine(host["ruleId"]?.GetValue<string>(), host["ruleVersion"]?.GetValue<string>(), host["score"]!),
        _ => throw new InvalidOperationException($"Unknown engine {engine.ToJsonString()}."),
    };

    private static JsonObject Options(string? key = Key, long? lifetimeTicks = null, string verification = "Verified", long? nowUnixMs = null) => new()
    {
        ["verification"] = verification,
        ["assessmentTokenKey"] = key,
        ["assessmentTokenLifetimeTicks"] = lifetimeTicks ?? ExperienceIndependenceOptions.DefaultAssessmentTokenLifetime.Ticks,
        ["nowUnixMs"] = nowUnixMs ?? Now,
    };

    private static ExperienceIndependenceOptions ReadOptions(JsonObject options) => new()
    {
        Verification = Enum.TryParse<IndependenceVerification>(options["verification"]!.GetValue<string>(), out var mode) && Enum.IsDefined(mode)
            ? mode
            : (IndependenceVerification)7,
        AssessmentTokenKey = options["assessmentTokenKey"]?.GetValue<string>() is { } hex ? Convert.FromHexString(hex) : null,
        AssessmentTokenLifetime = TimeSpan.FromTicks(options["assessmentTokenLifetimeTicks"]!.GetValue<long>()),
        TimeProvider = new FrozenTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(options["nowUnixMs"]!.GetValue<long>())),
    };

    private static ConfidenceEvidenceKind ReadKind(string kind) =>
        Enum.TryParse<ConfidenceEvidenceKind>(kind, out var parsed) && Enum.IsDefined(parsed) && parsed.ToString() == kind
            ? parsed
            : (ConfidenceEvidenceKind)2;

    private static JsonObject Reviewer(string principal, string tenant, string? application = null, string? project = null, string? team = null, string? agent = null, string? user = null) => new()
    {
        ["principalId"] = principal,
        ["tenantId"] = tenant,
        ["applicationId"] = application,
        ["projectId"] = project,
        ["teamId"] = team,
        ["agentId"] = agent,
        ["userId"] = user,
    };

    private static AuthorizationContext ReadReviewer(JsonObject reviewer) => new(
        reviewer["tenantId"]!.GetValue<string>(),
        reviewer["principalId"]!.GetValue<string>(),
        [],
        Fixed.T0,
        reviewer["applicationId"]?.GetValue<string>(),
        reviewer["projectId"]?.GetValue<string>(),
        reviewer["teamId"]?.GetValue<string>(),
        reviewer["agentId"]?.GetValue<string>(),
        reviewer["userId"]?.GetValue<string>());

    private static JsonObject ScopeJson(Scope scope) => new()
    {
        ["tenantId"] = scope.TenantId,
        ["applicationId"] = scope.ApplicationId,
        ["projectId"] = scope.ProjectId,
        ["teamId"] = scope.TeamId,
        ["agentId"] = scope.AgentId,
        ["userId"] = scope.UserId,
    };

    private static Scope ReadScope(JsonObject scope) => new(
        scope["tenantId"]!.GetValue<string>(),
        scope["applicationId"]!.GetValue<string>(),
        scope["projectId"]!.GetValue<string>(),
        scope["teamId"]?.GetValue<string>(),
        scope["agentId"]?.GetValue<string>(),
        scope["userId"]?.GetValue<string>());

    private static int Int(JsonNode? node) => checked((int)node!.GetValue<long>());

    private static string D(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>The exception <see cref="HostEngine"/> throws when its score is <c>"throws"</c>.</summary>
    internal sealed class HostEngineException(string message) : Exception(message);

    /// <summary>
    /// A host engine as a case declares it: <c>score</c> is a constant, <c>"NaN"</c>, <c>"Infinity"</c> or
    /// <c>"-Infinity"</c>; <c>"bayes"</c> for <c>(2 + S) / (4 + S + F)</c>; <c>"stored"</c> for the record's stored score;
    /// or <c>"throws"</c>.
    /// </summary>
    private sealed class HostEngine(string? ruleId, string? ruleVersion, JsonNode score) : IExperienceConfidenceEngine
    {
        public string RuleId => ruleId!;

        public string RuleVersion => ruleVersion!;

        public double Score(ExperienceConfidenceInput input)
        {
            if (score is JsonValue value && value.TryGetValue<double>(out var constant))
            {
                return constant;
            }

            return score.GetValue<string>() switch
            {
                "NaN" => double.NaN,
                "Infinity" => double.PositiveInfinity,
                "-Infinity" => double.NegativeInfinity,
                "bayes" => (2d + input.SupportingValidations) / (4d + input.SupportingValidations + input.Contradictions),
                "stored" => input.Record.ReuseConfidence,
                "throws" => throw new HostEngineException("The host engine failed."),
                var other => throw new InvalidOperationException($"Unknown host engine score '{other}'."),
            };
        }
    }

    /// <summary>A clock fixed at one instant, whose timers never fire: no case depends on time passing.</summary>
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
}

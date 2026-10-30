using System.Globalization;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Confidence;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Storage.InMemory;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Contract 1.16's in-memory lifecycle families, <c>lifecycle-commit</c> and <c>lifecycle-apply</c>: a stored lesson moved
/// through its lifecycle by .NET's <see cref="ExperienceLifecycleService"/> over the .NET
/// <see cref="InMemoryExperienceRecordStore"/>, the production path a port reproduces. Each case is one <c>input.json</c>
/// (the records the store holds, the service's options, and the steps, in order) and the <c>expected.json</c> those steps
/// leave: each step's result, every record afterwards in its stored form, and every record's whole history.
/// </summary>
/// <remarks>
/// <para>
/// The service runs as a contract 1.16 port runs it: <see cref="IndependenceVerification.TrustHostSuppliedIdentifiers"/>
/// (the independence verifier is story 27.7's, so every update is admitted <see cref="ConfidenceEvidenceAdmission.HostTrusted"/>),
/// no provenance signing, no capture service and no indexing service (27.6d), with the case's confidence engine and
/// <see cref="HostTrustedEvidenceEffect"/>.
/// </para>
/// <para>
/// The store's clock is a <see cref="SettableTimeProvider"/> whose timers never fire: a step's <c>clock</c> sets it, and
/// nothing depends on time passing.
/// </para>
/// </remarks>
internal static class LifecycleContract
{
    public const string CommitFamily = "lifecycle-commit";
    public const string ApplyFamily = "lifecycle-apply";

    /// <summary>The files of each case.</summary>
    public static readonly string[] CaseFiles = ["input.json", "expected.json"];

    private const int C = 0x1160;
    private const int A = 0x1161;

    private const string Principal = "contract-principal";
    private const string Producer = "governance-review/1.0";

    private static readonly Scope S = Fixed.Scope;
    private static readonly Scope OtherAgent = Fixed.Scope with { AgentId = "other-agent" };
    private static readonly Scope Full = new("contract-tenant", "contract-app", "contract-project", "team-ü", "refund-agent", "user-7");

    private static readonly DateTimeOffset Clock = Fixed.T0.AddHours(1);

    private static readonly ExperienceStatus[] AllStatuses = Enum.GetValues<ExperienceStatus>();

    private static readonly Guid RunA = Fixed.Id(A, 0x5100);
    private static readonly Guid RunB = Fixed.Id(A, 0x5101);
    private static readonly Guid Round = Fixed.Id(A, 0x5200);

    /// <summary>Every <c>lifecycle-commit</c> case, by directory name, as its <c>input.json</c>.</summary>
    public static IReadOnlyList<(string Name, JsonObject Input)> CommitCases { get; } = BuildCommitCases();

    /// <summary>Every <c>lifecycle-apply</c> case, by directory name, as its <c>input.json</c>.</summary>
    public static IReadOnlyList<(string Name, JsonObject Input)> ApplyCases { get; } = BuildApplyCases();

    // ---- lifecycle-commit ---------------------------------------------------------------------------------------

    private static List<(string, JsonObject)> BuildCommitCases()
    {
        var cases = new List<(string, JsonObject)>();

        // Every transition the table allows, from every status that has one; and every one it refuses, from every status.
        foreach (var from in AllStatuses)
        {
            var allowed = AllStatuses.Where(to => ExperienceLifecycleService.IsTransitionAllowed(from, to)).ToList();
            var refused = AllStatuses.Where(to => !ExperienceLifecycleService.IsTransitionAllowed(from, to)).ToList();
            var name = from.ToString().ToLowerInvariant();

            if (allowed.Count > 0)
            {
                cases.Add(($"allowed-from-{name}", AllowedFrom(from, allowed)));
            }

            cases.Add(($"refused-from-{name}", RefusedFrom(from, refused)));
        }

        cases.Add(("first-event", FirstEvent()));
        cases.Add(("stale-revision", StaleRevision()));
        cases.Add(("status-mismatch", StatusMismatch()));
        cases.Add(("wrong-scope", WrongScope()));
        cases.Add(("denied", Denied()));
        cases.Add(("missing", Missing()));
        cases.Add(("invalid-request", InvalidRequest()));
        cases.Add(("replay-and-conflict", ReplayAndConflict()));
        cases.Add(("actor", ActorCase()));
        cases.Add(("supersession-shape", SupersessionShape()));
        cases.Add(("supersession-in-scope", SupersessionInScope()));
        cases.Add(("supersession-check", SupersessionCheck()));
        cases.Add(("history-paging", HistoryPaging()));
        cases.Add(("derived-ids", DerivedIds()));
        return cases;
    }

    private static JsonObject AllowedFrom(ExperienceStatus from, IReadOnlyList<ExperienceStatus> targets)
    {
        var records = new List<ExperienceRecord>();
        var steps = new List<JsonObject>();
        var replacement = Rec(C, 0x90, ExperienceStatus.Validated);
        records.Add(replacement);
        for (var i = 0; i < targets.Count; i++)
        {
            var record = Rec(C, (int)from * 0x10 + i + 1, from);
            records.Add(record);
            var to = targets[i];
            steps.Add(Step("commit", CommitStep(
                Auth(S),
                Fixed.Id(C, 0x1000 + (int)from * 0x10 + i),
                record.ExperienceId,
                S,
                from.ToString(),
                to.ToString(),
                record.Revision,
                to == ExperienceStatus.Superseded ? replacement.ExperienceId : null,
                reason: $"{from} to {to}, decided by the governance review.")));
        }

        return Case(
            $"Every transition the table allows from {from}, each on a record of its own at its current revision: committed, " +
            "the revision moves by one, the event is appended, and updated_at reads the store's clock.",
            records,
            steps);
    }

    private static JsonObject RefusedFrom(ExperienceStatus from, IReadOnlyList<ExperienceStatus> targets)
    {
        var record = Rec(C, 0x200 + (int)from, from);
        var replacement = Rec(C, 0x91, ExperienceStatus.Validated);
        var steps = new List<JsonObject>();
        for (var i = 0; i < targets.Count; i++)
        {
            var to = targets[i];
            steps.Add(Step("commit", CommitStep(
                Auth(S),
                Fixed.Id(C, 0x2000 + (int)from * 0x10 + i),
                record.ExperienceId,
                S,
                from.ToString(),
                to.ToString(),
                record.Revision,
                to == ExperienceStatus.Superseded ? replacement.ExperienceId : null)));
        }

        return Case(
            $"Every move the table refuses from {from} (a move to the status it is already in included) is TransitionNotAllowed, " +
            "refused before the store is asked: no event, and the record is unchanged.",
            [record, replacement],
            steps);
    }

    private static JsonObject FirstEvent()
    {
        var record = Rec(C, 0x300, ExperienceStatus.Candidate) with { Revision = 0, SupportingValidations = 0, ReuseConfidence = 0.5 };
        return Case(
            "A record's first event has no prior status: it may only record Candidate, which the store matches against the " +
            "record's real status (revision 0 to 1); moving it anywhere else without naming the prior status is refused. Then " +
            "the ordinary transition, Candidate to Validated, from revision 1.",
            [record],
            [
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3001), record.ExperienceId, S, null, "Validated", 0)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3002), record.ExperienceId, S, null, "Candidate", 0)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3003), record.ExperienceId, S, "Candidate", "Validated", 1)),
            ]);
    }

    private static JsonObject StaleRevision()
    {
        var record = Rec(C, 0x310, ExperienceStatus.Validated);
        return Case(
            "Two transitions decided against the same revision: the first commits, the second is StaleRevision with the " +
            "record's current revision and writes nothing. Retried against that revision, it commits.",
            [record],
            [
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3101), record.ExperienceId, S, "Validated", "Reinforced", 1)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3102), record.ExperienceId, S, "Validated", "Stale", 1)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3103), record.ExperienceId, S, "Validated", "Stale", 5)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3104), record.ExperienceId, S, "Reinforced", "Stale", 2)),
            ],
            clocks: [Clock, Clock.AddMinutes(1), Clock.AddMinutes(2), Clock.AddMinutes(3)]);
    }

    private static JsonObject StatusMismatch()
    {
        var record = Rec(C, 0x320, ExperienceStatus.Reinforced, revision: 4);
        return Case(
            "A transition the table allows, asserted from a status the record is not in: the store matches the prior status " +
            "against the record's real one, so it is StatusMismatch, carrying the stored status and revision, and nothing is written.",
            [record],
            [Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3201), record.ExperienceId, S, "Validated", "Reinforced", 4))]);
    }

    private static JsonObject WrongScope()
    {
        var record = Rec(C, 0x330, ExperienceStatus.Validated);
        return Case(
            "A record in another scope is indistinguishable from a missing one: NotFound, whether asked from another agent's " +
            "scope or from a narrower one, and nothing is written.",
            [record],
            [
                Step("commit", CommitStep(Auth(OtherAgent), Fixed.Id(C, 0x3301), record.ExperienceId, OtherAgent, "Validated", "Revoked", 1)),
                Step("commit", CommitStep(Auth(S with { TeamId = "team-1" }), Fixed.Id(C, 0x3302), record.ExperienceId, S with { TeamId = "team-1" }, "Validated", "Revoked", 1)),
            ]);
    }

    private static JsonObject Denied()
    {
        var record = Rec(C, 0x340, ExperienceStatus.Validated);
        return Case(
            "A request scope the authorization does not permit is Denied, before the store reads anything: another tenant, " +
            "another agent than the bound one, and a blank tenant, which permits nothing.",
            [record],
            [
                Step("commit", CommitStep(Auth(S with { TenantId = "other-tenant" }), Fixed.Id(C, 0x3401), record.ExperienceId, S, "Validated", "Revoked", 1)),
                Step("commit", CommitStep(Auth(OtherAgent), Fixed.Id(C, 0x3402), record.ExperienceId, S, "Validated", "Revoked", 1)),
                Step("commit", CommitStep(Auth(S with { TenantId = " " }), Fixed.Id(C, 0x3403), record.ExperienceId, S, "Validated", "Revoked", 1)),
            ]);
    }

    private static JsonObject Missing()
    {
        var record = Rec(C, 0x350, ExperienceStatus.Validated);
        return Case(
            "A record that does not exist is NotFound, and nothing is written.",
            [record],
            [Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3501), Fixed.Id(C, 0x35f), S, "Validated", "Revoked", 1))]);
    }

    private static JsonObject InvalidRequest()
    {
        var record = Rec(C, 0x360, ExperienceStatus.Validated);
        var id = record.ExperienceId;
        return Case(
            "Malformed requests are Invalid, every error listed with its field path, and nothing is written: empty IDs, a blank " +
            "or reserved reason, a blank producer, an unset instant, an undefined status (which the store reports, not the " +
            "transition table), a negative revision or one that leaves no room for the next, and a malformed scope.",
            [record],
            [
                Step("commit", CommitStep(Auth(S), Guid.Empty, Guid.Empty, S, "Validated", "Revoked", 1, reason: " ", producer: "", at: default(DateTimeOffset))),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3602), id, S, "Validated", "Revoked", 1, reason: "aexp-sealed:v1:abc")),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3603), id, S, "Validated", "Archived", 1)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3604), id, S, "Archived", "Revoked", 1)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3605), id, S, "Validated", "Revoked", -1)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3606), id, S, "Validated", "Revoked", long.MaxValue - 1)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3607), id, S with { ApplicationId = " ", TeamId = "" }, "Validated", "Revoked", 1)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3608), id, S, "Validated", "Revoked", 1, reason: "nul\u0000inside", producer: "pro\u0000ducer")),
            ]);
    }

    private static JsonObject ReplayAndConflict()
    {
        var (record, other) = (Rec(C, 0x370, ExperienceStatus.Validated), Rec(C, 0x371, ExperienceStatus.Validated));
        var at = Fixed.T0.AddTicks(1_234_567);
        var eventId = Fixed.Id(C, 0x3701);
        return Case(
            "An event ID is the idempotency key. The identical event again (its instant equal once truncated to whole " +
            "microseconds) is the original commit replayed: Committed with the revision that commit produced, nothing written. " +
            "The same event ID with any field changed, or naming another record, is a Conflict, and nothing is written.",
            [record, other],
            [
                Step("commit", CommitStep(Auth(S), eventId, record.ExperienceId, S, "Validated", "Reinforced", 1, at: at)),
                Step("commit", CommitStep(Auth(S), eventId, record.ExperienceId, S, "Validated", "Reinforced", 1, at: at.AddTicks(2))),
                Step("commit", CommitStep(Auth(S), eventId, record.ExperienceId, S, "Validated", "Reinforced", 1, reason: "a different reason", at: at)),
                Step("commit", CommitStep(Auth(S), eventId, other.ExperienceId, S, "Validated", "Reinforced", 1, at: at)),
                Step("commit", CommitStep(Auth(S), eventId, record.ExperienceId, S, "Validated", "Reinforced", 1, at: at.AddTicks(10))),
            ],
            clocks: [Clock, Clock.AddMinutes(5), Clock.AddMinutes(6), Clock.AddMinutes(7), Clock.AddMinutes(8)]);
    }

    private static JsonObject ActorCase()
    {
        var (a, b) = (Rec(C, 0x380, ExperienceStatus.Validated), Rec(C, 0x381, ExperienceStatus.Validated, scope: Full));
        return Case(
            "Every stored event records the principal the commit ran under as its actor, and no actor when the principal is " +
            "blank; a scope with every field set is stored on the event as it is.",
            [a, b],
            [
                Step("commit", CommitStep(Auth(S, principal: "  "), Fixed.Id(C, 0x3801), a.ExperienceId, S, "Validated", "Stale", 1)),
                Step("commit", CommitStep(Auth(Full, principal: "reviewer-ü"), Fixed.Id(C, 0x3802), b.ExperienceId, Full, "Validated", "Revoked", 1)),
            ]);
    }

    private static JsonObject SupersessionShape()
    {
        var (record, replacement) = (Rec(C, 0x390, ExperienceStatus.Validated), Rec(C, 0x391, ExperienceStatus.Validated));
        var id = record.ExperienceId;
        return Case(
            "Supersession names a replacement, and only supersession does. Refused before the store is asked, as " +
            "ReplacementNotAllowed with a reason: a move to Superseded without one (or with the empty GUID), a replacement on " +
            "any other move, and a record replacing itself.",
            [record, replacement],
            [
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3901), id, S, "Validated", "Superseded", 1)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3902), id, S, "Validated", "Superseded", 1, Guid.Empty)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3903), id, S, "Validated", "Revoked", 1, replacement.ExperienceId)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3904), id, S, "Validated", "Superseded", 1, id)),
            ]);
    }

    private static JsonObject SupersessionInScope()
    {
        var a = Rec(C, 0x3a0, ExperienceStatus.Validated);
        var b = Rec(C, 0x3a1, ExperienceStatus.Validated);
        var contested = Rec(C, 0x3a2, ExperienceStatus.Contested);
        var foreign = Rec(C, 0x3a3, ExperienceStatus.Validated, scope: OtherAgent);
        var c = Rec(C, 0x3a4, ExperienceStatus.Reinforced);
        return Case(
            "What only the store can decide, inside the commit: a replacement that does not exist, or lies in another scope, is " +
            "ReplacementNotAllowed with no status; an ineligible one, with its status; an eligible one in the record's exact scope " +
            "commits, the replacement stored on the event. Once A is superseded by B, superseding B by A is refused, because A is " +
            "no longer eligible (the store's cycle walk would refuse it too). Retrying the committed supersession replays it, " +
            "even once the replacement has moved on.",
            [a, b, contested, foreign, c],
            [
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3a01), a.ExperienceId, S, "Validated", "Superseded", 1, Fixed.Id(C, 0x3af))),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3a02), a.ExperienceId, S, "Validated", "Superseded", 1, foreign.ExperienceId)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3a03), a.ExperienceId, S, "Validated", "Superseded", 1, contested.ExperienceId)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3a04), a.ExperienceId, S, "Validated", "Superseded", 1, b.ExperienceId)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3a05), b.ExperienceId, S, "Validated", "Superseded", 1, a.ExperienceId)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3a06), b.ExperienceId, S, "Validated", "Superseded", 1, c.ExperienceId)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3a04), a.ExperienceId, S, "Validated", "Superseded", 1, b.ExperienceId)),
            ]);
    }

    private static JsonObject SupersessionCheck()
    {
        var a = Rec(C, 0x3b0, ExperienceStatus.Validated);
        var b = Rec(C, 0x3b1, ExperienceStatus.Validated);
        var c = Rec(C, 0x3b2, ExperienceStatus.Validated);
        var revoked = Rec(C, 0x3b3, ExperienceStatus.Revoked);
        var foreign = Rec(C, 0x3b4, ExperienceStatus.Validated, scope: OtherAgent);
        return Case(
            "The read-only supersession check, after A is superseded by B and B by C: superseding C by A would close a cycle " +
            "(Cycle, reporting A's status); a record not in scope is RecordNotFound with no status; a replacement not in scope " +
            "is ReplacementNotFound; an ineligible replacement is Allowed with its status (eligibility is the caller's rule); " +
            "an empty ID is Invalid; an unpermitted scope Denied. Nothing is written.",
            [a, b, c, revoked, foreign],
            [
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3b01), a.ExperienceId, S, "Validated", "Superseded", 1, b.ExperienceId)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3b02), b.ExperienceId, S, "Validated", "Superseded", 1, c.ExperienceId)),
                Step("checkSupersession", Check(Auth(S), S, c.ExperienceId, a.ExperienceId)),
                Step("checkSupersession", Check(Auth(S), S, c.ExperienceId, b.ExperienceId)),
                Step("checkSupersession", Check(Auth(S), S, a.ExperienceId, c.ExperienceId)),
                Step("checkSupersession", Check(Auth(S), S, c.ExperienceId, revoked.ExperienceId)),
                Step("checkSupersession", Check(Auth(S), S, foreign.ExperienceId, c.ExperienceId)),
                Step("checkSupersession", Check(Auth(S), S, c.ExperienceId, foreign.ExperienceId)),
                Step("checkSupersession", Check(Auth(S), S, Guid.Empty, Guid.Empty)),
                Step("checkSupersession", Check(Auth(OtherAgent), S, c.ExperienceId, a.ExperienceId)),
            ]);
    }

    private static JsonObject HistoryPaging()
    {
        var record = Rec(C, 0x3c0, ExperienceStatus.Candidate) with { Revision = 0, SupportingValidations = 0, ReuseConfidence = 0.5 };
        var quiet = Rec(C, 0x3c1, ExperienceStatus.Validated);
        var id = record.ExperienceId;
        return Case(
            "A history is read a page at a time, oldest first, with the applied revision as the keyset cursor: two pages of " +
            "two and a last page of one, then an exhausted cursor, which is still Found with the record's revision and no " +
            "events (and no next cursor). A record with no events is Found and empty. A missing record is NotFound; an " +
            "unpermitted scope Denied; a limit outside 1 to 500 or a negative cursor Invalid.",
            [record, quiet],
            [
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3c01), id, S, null, "Candidate", 0)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3c02), id, S, "Candidate", "Validated", 1)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3c03), id, S, "Validated", "Reinforced", 2)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3c04), id, S, "Reinforced", "Contested", 3)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(C, 0x3c05), id, S, "Contested", "Revoked", 4)),
                Step("history", History(Auth(S), S, id, 2, null)),
                Step("history", History(Auth(S), S, id, 2, 2)),
                Step("history", History(Auth(S), S, id, 2, 4)),
                Step("history", History(Auth(S), S, id, 2, 5)),
                Step("history", History(Auth(S), S, quiet.ExperienceId, 100, null)),
                Step("history", History(Auth(S), S, Fixed.Id(C, 0x3cf), 100, null)),
                Step("history", History(Auth(OtherAgent), S, id, 100, null)),
                Step("history", History(Auth(S), S, id, 0, -1)),
                Step("history", History(Auth(S), S, id, 501, null)),
            ],
            clocks: [Clock, Clock.AddMinutes(1), Clock.AddMinutes(2), Clock.AddMinutes(3), Clock.AddMinutes(4)]);
    }

    private static JsonObject DerivedIds()
    {
        var run = Fixed.Id(C, 0x3d00);
        var record = Fixed.Id(C, 0x3d01);
        return Case(
            "The evidence and event IDs finalization derives for the reuse evidence it submits (tags 4 and 5): SHA-256 over " +
            "the namespace, the run, the tag, the scope's six fields, then the record's 16 bytes (RFC 4122 order) and the " +
            "kind's byte (0 supporting, 1 contradicting), as UUIDv8. A different kind, record, run or scope derives a different ID.",
            [],
            [
                Step("derive", Derive(run, S, record, "Supporting")),
                Step("derive", Derive(run, S, record, "Contradicting")),
                Step("derive", Derive(run, Full, record, "Supporting")),
                Step("derive", Derive(run, S, Fixed.Id(C, 0x3d02), "Supporting")),
                Step("derive", Derive(Fixed.Id(C, 0x3d03), S, record, "Supporting")),
            ]);
    }

    // ---- lifecycle-apply ----------------------------------------------------------------------------------------

    private static List<(string, JsonObject)> BuildApplyCases()
    {
        var cases = new List<(string, JsonObject)>
        {
            ("supporting-machine", SupportingMachine()),
            ("contradicting-contests", ContradictingContests()),
            ("contested-keeps-moving", ContestedKeepsMoving()),
            ("human-evidence", HumanEvidence()),
            ("duplicate-key", DuplicateKey()),
            ("evidence-replay-and-conflict", EvidenceReplayAndConflict()),
            ("own-run", OwnRun()),
            ("invalid-submission", InvalidSubmission()),
            ("saturated-counter", SaturatedCounter()),
            ("missing-wrong-scope-denied", MissingWrongScopeDenied()),
            ("host-engine", HostEngine()),
            ("host-engine-refused", HostEngineRefused()),
            ("recorded-only", RecordedOnly()),
            ("replay-after-revoked", ReplayAfterRevoked()),
            ("read-confidence", ReadConfidence()),
            ("read-confidence-recorded-only", ReadConfidenceRecordedOnly()),
            ("read-confidence-refused", ReadConfidenceRefused()),
        };

        foreach (var status in new[] { ExperienceStatus.Candidate, ExperienceStatus.Quarantined, ExperienceStatus.Stale, ExperienceStatus.Superseded, ExperienceStatus.Revoked })
        {
            var record = Rec(A, 0x100 + (int)status, status);
            cases.Add(($"ineligible-{status.ToString().ToLowerInvariant()}", Case(
                $"A {status} record does not accept evidence: Ineligible, reporting its revision and status, before anything " +
                "is computed or written.",
                [record],
                [Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0x1000 + (int)status), record.ExperienceId, Fixed.Id(A, 0x1100 + (int)status), "Supporting", "Machine", RunA, Round))])));
        }

        return cases;
    }

    private static JsonObject SupportingMachine()
    {
        var record = Rec(A, 0x10, ExperienceStatus.Validated) with { Origin = ExperienceRecordOrigin.Finalized };
        return Case(
            "Supporting machine evidence against a freshly validated record: the service reads it, the heuristic moves S from 1 " +
            "to 2, the engine scores 3/4, and the evidence, the counters, the score and the event (Validated to Validated, " +
            "carrying the update, admitted HostTrusted) commit together at the next revision.",
            [record],
            [Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0x1001), record.ExperienceId, Fixed.Id(A, 0x1101), "Supporting", "Machine", RunA, Round, detail: "the retry-after-lock lesson held"))]);
    }

    private static JsonObject ContradictingContests()
    {
        var (validated, reinforced) = (Rec(A, 0x20, ExperienceStatus.Validated, supporting: 2), Rec(A, 0x21, ExperienceStatus.Reinforced, supporting: 3));
        return Case(
            "A contradiction contests a Validated or Reinforced record in the same transaction that moves its counters: F 0 to 1.",
            [validated, reinforced],
            [
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0x2001), validated.ExperienceId, Fixed.Id(A, 0x2101), "Contradicting", "Machine", RunA, Round)),
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0x2002), reinforced.ExperienceId, Fixed.Id(A, 0x2102), "Contradicting", "Machine", RunA, Round)),
            ]);
    }

    private static JsonObject ContestedKeepsMoving()
    {
        var record = Rec(A, 0x30, ExperienceStatus.Validated);
        return Case(
            "A Contested record stays Contested while its counters keep moving: a contradiction contests it, then supporting " +
            "and contradicting evidence from other runs move S and F without moving the status.",
            [record],
            [
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0x3001), record.ExperienceId, Fixed.Id(A, 0x3101), "Contradicting", "Machine", RunA, Round)),
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0x3002), record.ExperienceId, Fixed.Id(A, 0x3102), "Supporting", "Machine", RunB, Round)),
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0x3003), record.ExperienceId, Fixed.Id(A, 0x3103), "Contradicting", "Machine", Fixed.Id(A, 0x5102), Round)),
            ],
            clocks: [Clock, Clock.AddMinutes(1), Clock.AddMinutes(2)]);
    }

    private static JsonObject HumanEvidence()
    {
        var record = Rec(A, 0x40, ExperienceStatus.Validated);
        return Case(
            "Human evidence is counted under the reviewer the authorization names (never a request field) and the run. Without " +
            "independence verification (story 27.7) an assessment token it presents is not checked and spends nothing: the " +
            "update records no assessment. A second reviewer about the same run is a second key; the same reviewer again is not.",
            [record],
            [
                Step("apply", ApplyStep(Auth(S, principal: "reviewer-ü"), Fixed.Id(A, 0x4001), record.ExperienceId, Fixed.Id(A, 0x4101), "Supporting", "Human", RunA, null, token: "aexat1.not-checked")),
                Step("apply", ApplyStep(Auth(S, principal: "reviewer-2"), Fixed.Id(A, 0x4002), record.ExperienceId, Fixed.Id(A, 0x4102), "Contradicting", "Human", RunA, null)),
                Step("apply", ApplyStep(Auth(S, principal: "reviewer-ü"), Fixed.Id(A, 0x4003), record.ExperienceId, Fixed.Id(A, 0x4103), "Supporting", "Human", RunA, null)),
            ],
            clocks: [Clock, Clock.AddMinutes(1), Clock.AddMinutes(2)]);
    }

    private static JsonObject DuplicateKey()
    {
        var record = Rec(A, 0x50, ExperienceStatus.Validated);
        return Case(
            "The same observation under a new evidence ID (the same run and round): Applied but not counted. It is recorded and " +
            "nothing else moves: no event, no counters, no status, no revision, no updated_at; the result reports the record " +
            "as the store found it. A contradiction duplicating a counted support does not contest the record either.",
            [record],
            [
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0x5001), record.ExperienceId, Fixed.Id(A, 0x5101), "Supporting", "Machine", RunA, Round)),
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0x5002), record.ExperienceId, Fixed.Id(A, 0x5102), "Supporting", "Machine", RunA, Round)),
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0x5003), record.ExperienceId, Fixed.Id(A, 0x5103), "Contradicting", "Machine", RunA, Round)),
            ],
            clocks: [Clock, Clock.AddMinutes(1), Clock.AddMinutes(2)]);
    }

    private static JsonObject EvidenceReplayAndConflict()
    {
        var record = Rec(A, 0x60, ExperienceStatus.Validated);
        var evidence = Fixed.Id(A, 0x6101);
        return Case(
            "An evidence ID is a second idempotency key. Resubmitting it with the same content, after other evidence moved the " +
            "record, is the original replayed: Applied with the numbers and the revision it settled at. Under a fresh event ID " +
            "it is a Conflict (its stored row produced an event that this one is not); with any content changed, a Conflict. " +
            "Nothing is written by any of them.",
            [record],
            [
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0x6001), record.ExperienceId, evidence, "Supporting", "Machine", RunA, Round)),
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0x6002), record.ExperienceId, Fixed.Id(A, 0x6102), "Supporting", "Machine", RunB, Round)),
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0x6001), record.ExperienceId, evidence, "Supporting", "Machine", RunA, Round)),
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0x6003), record.ExperienceId, evidence, "Supporting", "Machine", RunA, Round)),
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0x6001), record.ExperienceId, evidence, "Supporting", "Machine", RunA, Round, detail: "changed")),
            ],
            clocks: [Clock, Clock.AddMinutes(1), Clock.AddMinutes(2), Clock.AddMinutes(3), Clock.AddMinutes(4)]);
    }

    private static JsonObject OwnRun()
    {
        var record = Rec(A, 0x70, ExperienceStatus.Validated);
        return Case(
            "Evidence naming the record's own source run is refused in every verification mode: Unverified, OwnRun, nothing written.",
            [record],
            [Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0x7001), record.ExperienceId, Fixed.Id(A, 0x7101), "Supporting", "Machine", record.SourceRunId, Round))]);
    }

    private static JsonObject InvalidSubmission()
    {
        var record = Rec(A, 0x80, ExperienceStatus.Validated);
        var id = record.ExperienceId;
        return Case(
            "A submission's own shape is checked before the record is read, every error listed: empty IDs, a missing run, an " +
            "undefined kind or source, machine evidence without a round or with a token, human evidence with a round or under a " +
            "blank or padded principal, a blank reason or producer, and an unset instant. Nothing is written.",
            [record],
            [
                Step("apply", ApplyStep(Auth(S), Guid.Empty, Guid.Empty, Guid.Empty, "Sideways", "Oracle", Guid.Empty, null, reason: "", producer: " ", at: default(DateTimeOffset))),
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0x8002), id, Fixed.Id(A, 0x8102), "Supporting", "Machine", RunA, null, token: "aexat1.x")),
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0x8003), id, Fixed.Id(A, 0x8103), "Supporting", "Machine", RunA, Guid.Empty)),
                Step("apply", ApplyStep(Auth(S, principal: " "), Fixed.Id(A, 0x8004), id, Fixed.Id(A, 0x8104), "Supporting", "Human", RunA, Round)),
                Step("apply", ApplyStep(Auth(S, principal: " reviewer-1"), Fixed.Id(A, 0x8005), id, Fixed.Id(A, 0x8105), "Supporting", "Human", RunA, null)),
                Step("apply", ApplyStep(Auth(S, principal: ""), Fixed.Id(A, 0x8006), id, Fixed.Id(A, 0x8106), "Supporting", "Machine", RunA, Round)),
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0x8007), id, Fixed.Id(A, 0x8107), "Supporting", "Machine", RunA, Round, reason: "aexp-sealed:v1:x", detail: "aexp-sealed:v1:y")),
            ]);
    }

    private static JsonObject SaturatedCounter()
    {
        var supporting = Rec(A, 0x90, ExperienceStatus.Validated, supporting: int.MaxValue);
        var contradicting = Rec(A, 0x91, ExperienceStatus.Contested, supporting: 1, contradictions: int.MaxValue);
        return Case(
            "A stored counter already at int.MaxValue refuses evidence: the request is Invalid, naming the counter, before the " +
            "engine is asked. The service checks both counters, so support against a saturated contradiction count is refused " +
            "too, although the heuristic alone (contract 1.15's apply) could count it.",
            [supporting, contradicting],
            [
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0x9001), supporting.ExperienceId, Fixed.Id(A, 0x9101), "Supporting", "Machine", RunA, Round)),
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0x9002), contradicting.ExperienceId, Fixed.Id(A, 0x9102), "Contradicting", "Machine", RunA, Round)),
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0x9003), contradicting.ExperienceId, Fixed.Id(A, 0x9103), "Supporting", "Machine", RunA, Round)),
            ]);
    }

    private static JsonObject MissingWrongScopeDenied()
    {
        var record = Rec(A, 0xa0, ExperienceStatus.Validated);
        return Case(
            "The read refuses before anything is computed: a missing record and one in another scope are NotFound; an " +
            "unpermitted scope is Denied; a malformed scope is Invalid, with the read's errors.",
            [record],
            [
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0xa001), Fixed.Id(A, 0xaf), Fixed.Id(A, 0xa101), "Supporting", "Machine", RunA, Round)),
                Step("apply", ApplyStep(Auth(OtherAgent), Fixed.Id(A, 0xa002), record.ExperienceId, Fixed.Id(A, 0xa102), "Supporting", "Machine", RunA, Round, scope: OtherAgent)),
                Step("apply", ApplyStep(Auth(OtherAgent), Fixed.Id(A, 0xa003), record.ExperienceId, Fixed.Id(A, 0xa103), "Supporting", "Machine", RunA, Round)),
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0xa004), record.ExperienceId, Fixed.Id(A, 0xa104), "Supporting", "Machine", RunA, Round, scope: S with { ProjectId = "" })),
            ]);
    }

    private static JsonObject HostEngine()
    {
        var record = Rec(A, 0xb0, ExperienceStatus.Validated);
        return Case(
            "A host engine scores the counters after the evidence ((2 + S) / (4 + S + F)) and every update records its rule, " +
            "\"bayes/2.1\"; the move to Contested stays the library's.",
            [record],
            [
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0xb001), record.ExperienceId, Fixed.Id(A, 0xb101), "Supporting", "Machine", RunA, Round)),
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0xb002), record.ExperienceId, Fixed.Id(A, 0xb102), "Contradicting", "Machine", RunB, Round)),
            ],
            engine: ConfidenceEngineContract.Engine("bayes", "2.1", "bayes"),
            clocks: [Clock, Clock.AddMinutes(1)]);
    }

    private static JsonObject HostEngineRefused()
    {
        var record = Rec(A, 0xc0, ExperienceStatus.Validated);
        return Case(
            "A score that is not in [0, 1] is refused, never clamped: the call throws InvalidOperationException naming the rule " +
            "and the value, and nothing is written. Before that, the read and the eligibility gate still answer: an Ineligible " +
            "record never reaches the engine.",
            [record, Rec(A, 0xc1, ExperienceStatus.Revoked)],
            [
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0xc001), record.ExperienceId, Fixed.Id(A, 0xc101), "Supporting", "Machine", RunA, Round)),
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0xc002), Fixed.Id(A, 0xc1), Fixed.Id(A, 0xc102), "Supporting", "Machine", RunA, Round)),
            ],
            engine: ConfidenceEngineContract.Engine("high", "1", 1.5));
    }

    private static JsonObject RecordedOnly()
    {
        var record = Rec(A, 0xd0, ExperienceStatus.Validated);
        return Case(
            "HostTrustedEvidence = RecordedOnly: host-trusted evidence is committed on an event whose counters, score and status " +
            "equal the prior ones (Applied, not counted, the engine not asked): the revision moves, nothing else, not even " +
            "updated_at. A contradiction does not contest. Another submission for a key an event already recorded is a duplicate.",
            [record],
            [
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0xd001), record.ExperienceId, Fixed.Id(A, 0xd101), "Contradicting", "Machine", RunA, Round)),
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0xd002), record.ExperienceId, Fixed.Id(A, 0xd102), "Supporting", "Machine", RunA, Round)),
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0xd003), record.ExperienceId, Fixed.Id(A, 0xd103), "Supporting", "Machine", RunB, Round)),
            ],
            hostTrustedEvidence: HostTrustedEvidenceEffect.RecordedOnly,
            engine: ConfidenceEngineContract.Engine("broken", "1", "throws"),
            clocks: [Clock, Clock.AddMinutes(1), Clock.AddMinutes(2)]);
    }

    private static JsonObject ReplayAfterRevoked()
    {
        var record = Rec(A, 0xe0, ExperienceStatus.Validated);
        return Case(
            "The ordering wart, stated: evidence is applied, the record is revoked, and the identical evidence resubmitted is " +
            "Ineligible rather than the original replayed, because the eligibility gate runs on the record the service reads.",
            [record],
            [
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0xe001), record.ExperienceId, Fixed.Id(A, 0xe101), "Supporting", "Machine", RunA, Round)),
                Step("commit", CommitStep(Auth(S), Fixed.Id(A, 0xe002), record.ExperienceId, S, "Validated", "Revoked", 2)),
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0xe001), record.ExperienceId, Fixed.Id(A, 0xe101), "Supporting", "Machine", RunA, Round)),
            ],
            clocks: [Clock, Clock.AddMinutes(1), Clock.AddMinutes(2)]);
    }

    private static JsonObject ReadConfidence()
    {
        var finalized = Rec(A, 0xf0, ExperienceStatus.Validated) with { Origin = ExperienceRecordOrigin.Finalized };
        var written = Rec(A, 0xf1, ExperienceStatus.Validated);
        Guid Run(int n) => Fixed.Id(A, 0x5110 + n);
        return Case(
            "Reading confidence pages the history and counts what each admission moved. Everything here was admitted HostTrusted, " +
            "so All reports the stored score; ExcludeHostTrusted and VerifiedOnly leave the evidence out and recompute with the " +
            "engine. VerifiedOnly also leaves out a hand-written record's initial counters (a finalized one's are finalization's).",
            [finalized, written],
            [
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0xf001), finalized.ExperienceId, Fixed.Id(A, 0xf101), "Supporting", "Machine", Run(1), Round)),
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0xf002), finalized.ExperienceId, Fixed.Id(A, 0xf102), "Contradicting", "Machine", Run(2), Round)),
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0xf003), written.ExperienceId, Fixed.Id(A, 0xf103), "Supporting", "Machine", Run(1), Round)),
                Step("readConfidence", Read(Auth(S), S, finalized.ExperienceId, "All")),
                Step("readConfidence", Read(Auth(S), S, finalized.ExperienceId, "ExcludeHostTrusted")),
                Step("readConfidence", Read(Auth(S), S, finalized.ExperienceId, "VerifiedOnly")),
                Step("readConfidence", Read(Auth(S), S, written.ExperienceId, "All")),
                Step("readConfidence", Read(Auth(S), S, written.ExperienceId, "VerifiedOnly")),
            ],
            clocks: [Clock, Clock.AddMinutes(1), Clock.AddMinutes(2)]);
    }

    private static JsonObject ReadConfidenceRecordedOnly()
    {
        var record = Rec(A, 0xf8, ExperienceStatus.Validated) with { Origin = ExperienceRecordOrigin.Finalized };
        return Case(
            "Under RecordedOnly, All adds the recorded-only evidence back, once per independence key, and scores it with the " +
            "engine: what the default (Counted) would have stored. ExcludeHostTrusted reports the stored score unchanged.",
            [record],
            [
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0xf801), record.ExperienceId, Fixed.Id(A, 0xf811), "Supporting", "Machine", RunA, Round)),
                Step("apply", ApplyStep(Auth(S), Fixed.Id(A, 0xf802), record.ExperienceId, Fixed.Id(A, 0xf812), "Contradicting", "Machine", RunB, Round)),
                Step("readConfidence", Read(Auth(S), S, record.ExperienceId, "All")),
                Step("readConfidence", Read(Auth(S), S, record.ExperienceId, "ExcludeHostTrusted")),
                Step("readConfidence", Read(Auth(S), S, record.ExperienceId, "VerifiedOnly")),
            ],
            hostTrustedEvidence: HostTrustedEvidenceEffect.RecordedOnly,
            clocks: [Clock, Clock.AddMinutes(1)]);
    }

    private static JsonObject ReadConfidenceRefused()
    {
        var record = Rec(A, 0xfa, ExperienceStatus.Validated);
        return Case(
            "A confidence read refuses like the record read it starts with: an empty ID or an undefined filter is Invalid; a " +
            "missing record or one in another scope NotFound; an unpermitted scope Denied. A record with no history reports " +
            "its stored numbers.",
            [record],
            [
                Step("readConfidence", Read(Auth(S), S, Guid.Empty, "Everything")),
                Step("readConfidence", Read(Auth(S), S, Fixed.Id(A, 0xff), "All")),
                Step("readConfidence", Read(Auth(OtherAgent), OtherAgent, record.ExperienceId, "All")),
                Step("readConfidence", Read(Auth(OtherAgent), S, record.ExperienceId, "All")),
                Step("readConfidence", Read(Auth(S), S, record.ExperienceId, "VerifiedOnly")),
            ]);
    }

    // ---- case building ------------------------------------------------------------------------------------------

    internal static ExperienceRecord Rec(int family, int n, ExperienceStatus status, long revision = 1, int? supporting = null, int contradictions = 0, Scope? scope = null)
    {
        var record = SearchContract.Seed(family, n, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt.", status, scope: scope);
        var s = supporting ?? record.SupportingValidations;
        return record with
        {
            Revision = revision,
            SupportingValidations = s,
            Contradictions = contradictions,
            ReuseConfidence = status == ExperienceStatus.Quarantined && s == 0 && contradictions == 0
                ? 0d
                : s == 0 && contradictions == 0 ? record.ReuseConfidence : ReuseConfidenceHeuristic.Score(s, contradictions),
        };
    }

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

    internal static JsonObject Auth(Scope bounds, string principal = Principal) => new()
    {
        ["principalId"] = principal,
        ["tenantId"] = bounds.TenantId,
        ["applicationId"] = bounds.ApplicationId,
        ["projectId"] = bounds.ProjectId,
        ["teamId"] = bounds.TeamId,
        ["agentId"] = bounds.AgentId,
        ["userId"] = bounds.UserId,
    };

    internal static JsonObject ScopeJson(Scope scope) => new()
    {
        ["tenantId"] = scope.TenantId,
        ["applicationId"] = scope.ApplicationId,
        ["projectId"] = scope.ProjectId,
        ["teamId"] = scope.TeamId,
        ["agentId"] = scope.AgentId,
        ["userId"] = scope.UserId,
    };

    private static JsonObject CommitStep(
        JsonObject authorization,
        Guid eventId,
        Guid experienceId,
        Scope scope,
        string? prior,
        string current,
        long expectedRevision,
        Guid? replacement = null,
        string? reason = null,
        string producer = Producer,
        DateTimeOffset? at = null) => new()
    {
        ["authorization"] = authorization,
        ["request"] = new JsonObject
        {
            ["eventId"] = D(eventId),
            ["experienceId"] = D(experienceId),
            ["scope"] = ScopeJson(scope),
            ["priorStatus"] = prior,
            ["currentStatus"] = current,
            ["reason"] = reason ?? $"{prior ?? "nothing"} to {current}.",
            ["producer"] = producer,
            ["occurredAt"] = ContractJson.Timestamp(at ?? Fixed.T0),
            ["expectedRevision"] = expectedRevision,
            ["replacementExperienceId"] = replacement is { } r ? D(r) : null,
        },
    };

    internal static JsonObject ApplyStep(
        JsonObject authorization,
        Guid eventId,
        Guid experienceId,
        Guid evidenceId,
        string kind,
        string source,
        Guid runId,
        Guid? round,
        string reason = "the lesson was reused and the checks passed",
        string producer = "verification-aggregator/1.0.0",
        DateTimeOffset? at = null,
        string? detail = null,
        string? token = null,
        Scope? scope = null) => new()
    {
        ["authorization"] = authorization,
        ["request"] = new JsonObject
        {
            ["eventId"] = D(eventId),
            ["experienceId"] = D(experienceId),
            ["scope"] = ScopeJson(scope ?? S),
            ["evidenceId"] = D(evidenceId),
            ["kind"] = kind,
            ["source"] = source,
            ["runId"] = D(runId),
            ["verificationRoundId"] = round is { } r ? D(r) : null,
            ["reason"] = reason,
            ["producer"] = producer,
            ["occurredAt"] = ContractJson.Timestamp(at ?? Fixed.T0),
            ["detail"] = detail,
            ["assessmentToken"] = token,
        },
    };

    private static JsonObject Check(JsonObject authorization, Scope scope, Guid experienceId, Guid replacementId) => new()
    {
        ["authorization"] = authorization,
        ["scope"] = ScopeJson(scope),
        ["experienceId"] = D(experienceId),
        ["replacementExperienceId"] = D(replacementId),
    };

    private static JsonObject History(JsonObject authorization, Scope scope, Guid experienceId, int limit, long? startAfter) => new()
    {
        ["authorization"] = authorization,
        ["query"] = new JsonObject
        {
            ["scope"] = ScopeJson(scope),
            ["experienceId"] = D(experienceId),
            ["limit"] = limit,
            ["startAfterRevision"] = startAfter,
        },
    };

    private static JsonObject Read(JsonObject authorization, Scope scope, Guid experienceId, string filter) => new()
    {
        ["authorization"] = authorization,
        ["scope"] = ScopeJson(scope),
        ["experienceId"] = D(experienceId),
        ["filter"] = filter,
    };

    private static JsonObject Derive(Guid runId, Scope scope, Guid experienceId, string kind) => new()
    {
        ["runId"] = D(runId),
        ["scope"] = ScopeJson(scope),
        ["experienceId"] = D(experienceId),
        ["kind"] = kind,
    };

    // ---- running a case -----------------------------------------------------------------------------------------

    /// <summary>The expected result of a case: its steps run through the service over a fresh in-memory store.</summary>
    public static JsonObject Expected(JsonObject input)
    {
        var clock = new SettableTimeProvider(Instant(input["steps"]!.AsArray().FirstOrDefault()?["clock"]) ?? Fixed.T0);
        var store = new InMemoryExperienceRecordStore(clock);
        var records = input["records"]!.AsArray().Select(stored => ContractJson.FromStored(stored!)).ToList();
        foreach (var record in records)
        {
            var created = store.CreateAsync(AuthorizationFor(record.Scope), record, CancellationToken.None).GetAwaiter().GetResult();
            if (created.Outcome != ExperienceStoreOutcome.Created)
            {
                throw new InvalidOperationException($"Seeding {record.ExperienceId} was {created.Outcome}: {string.Join("; ", created.Errors.Select(e => $"{e.Path} {e.Message}"))}");
            }
        }

        var service = Service(store, input["service"]!.AsObject());
        var results = new JsonArray();
        foreach (var step in input["steps"]!.AsArray())
        {
            if (Instant(step!["clock"]) is { } at)
            {
                clock.Now = at;
            }

            results.Add(RunStep(service, store, step.AsObject()));
        }

        return new JsonObject
        {
            ["results"] = results,
            ["records"] = new JsonArray([.. records.Select(record => (JsonNode)ContractJson.ToStored(Reread(store, record)))]),
            ["histories"] = new JsonArray([.. records.Select(record => (JsonNode)new JsonObject
            {
                ["experienceId"] = D(record.ExperienceId),
                ["events"] = WholeHistory(store, record),
            })]),
        };
    }

    /// <summary>The service a port runs: no independence verification, no signing, no capture or indexing service.</summary>
    internal static ExperienceLifecycleService Service(IExperienceRecordStore store, JsonObject options) => new(
        store,
        indexingService: null,
        new ExperienceIndependenceOptions
        {
            Verification = IndependenceVerification.TrustHostSuppliedIdentifiers,
            HostTrustedEvidence = Enum.Parse<HostTrustedEvidenceEffect>(options["hostTrustedEvidence"]!.GetValue<string>()),
        },
        captureService: null,
        deindexingTimeout: null,
        ConfidenceEngineContract.ReadEngine(options["engine"]));

    private static JsonObject RunStep(ExperienceLifecycleService service, IExperienceRecordStore store, JsonObject step)
    {
        var (operation, body) = step.Where(member => member.Key != "clock").Select(member => (member.Key, member.Value!.AsObject())).Single();
        return operation switch
        {
            "commit" => CommitResult(
                service.CommitAsync(ReadAuthorization(body["authorization"]!), ReadCommit(body["request"]!.AsObject()), CancellationToken.None).GetAwaiter().GetResult(),
                body["request"]!.AsObject()),
            "apply" => ApplyResult(service, body),
            "checkSupersession" => CheckResult(store.CheckSupersessionAsync(
                ReadAuthorization(body["authorization"]!),
                ReadScope(body["scope"]!),
                Guid.Parse(body["experienceId"]!.GetValue<string>()),
                Guid.Parse(body["replacementExperienceId"]!.GetValue<string>()),
                CancellationToken.None).GetAwaiter().GetResult()),
            "history" => HistoryResult(store, body),
            "readConfidence" => ReadResult(service, body),
            "derive" => DeriveResult(body),
            _ => throw new InvalidOperationException($"Unknown lifecycle step '{operation}'."),
        };
    }

    internal static JsonObject ApplyResult(ExperienceLifecycleService service, JsonObject body)
    {
        ApplyConfidenceEvidenceResult result;
        try
        {
            result = service.ApplyEvidenceAsync(ReadAuthorization(body["authorization"]!), ReadApply(body["request"]!.AsObject()), CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ConfidenceEngineContract.HostEngineException)
        {
            return new JsonObject { ["thrown"] = ConfidenceEngineContract.Refusal(ex, libraryMessage: ex is InvalidOperationException) };
        }

        return new JsonObject
        {
            ["thrown"] = null,
            ["outcome"] = result.Outcome.ToString(),
            ["event"] = EventJson(result.Event),
            ["update"] = UpdateJson(result.Update),
            ["revision"] = result.Revision,
            ["status"] = result.Status?.ToString(),
            ["errors"] = Errors(result.Errors),
            ["reason"] = result.Reason,
            ["refusal"] = result.Refusal?.ToString(),
            ["counted"] = result.Counted,
        };
    }

    private static JsonObject ReadResult(ExperienceLifecycleService service, JsonObject body)
    {
        var filter = body["filter"]!.GetValue<string>();
        var result = service.ReadConfidenceAsync(
            ReadAuthorization(body["authorization"]!),
            ReadScope(body["scope"]!),
            Guid.Parse(body["experienceId"]!.GetValue<string>()),
            Enum.TryParse<ConfidenceEvidenceFilter>(filter, out var parsed) && Enum.IsDefined(parsed) && parsed.ToString() == filter ? parsed : (ConfidenceEvidenceFilter)9,
            CancellationToken.None).GetAwaiter().GetResult();

        static JsonObject Counts(ConfidenceAdmissionCounts counts) => new() { ["supporting"] = counts.Supporting, ["contradicting"] = counts.Contradicting };

        return new JsonObject
        {
            ["outcome"] = result.Outcome.ToString(),
            ["report"] = result.Report is not { } report ? null : new JsonObject
            {
                ["experienceId"] = D(report.ExperienceId),
                ["revision"] = report.Revision,
                ["status"] = report.Status.ToString(),
                ["filter"] = report.Filter.ToString(),
                ["reuseConfidence"] = report.ReuseConfidence,
                ["supportingValidations"] = report.SupportingValidations,
                ["contradictions"] = report.Contradictions,
                ["storedReuseConfidence"] = report.StoredReuseConfidence,
                ["storedSupportingValidations"] = report.StoredSupportingValidations,
                ["storedContradictions"] = report.StoredContradictions,
                ["verified"] = Counts(report.Verified),
                ["hostTrusted"] = Counts(report.HostTrusted),
                ["unrecorded"] = Counts(report.Unrecorded),
                ["origin"] = report.Origin.ToString(),
                ["initial"] = Counts(report.Initial),
                ["hostTrustedRecordedOnly"] = Counts(report.HostTrustedRecordedOnly),
            },
            ["errors"] = Errors(result.Errors),
        };
    }

    private static JsonObject HistoryResult(IExperienceRecordStore store, JsonObject body)
    {
        var query = body["query"]!.AsObject();
        var scope = ReadScope(query["scope"]!);
        var result = store.GetHistoryAsync(
            ReadAuthorization(body["authorization"]!),
            new ExperienceRecordHistoryQuery(
                scope,
                Guid.Parse(query["experienceId"]!.GetValue<string>()),
                query["limit"]!.GetValue<int>(),
                query["startAfterRevision"]?.GetValue<long>()),
            CancellationToken.None).GetAwaiter().GetResult();

        return new JsonObject
        {
            ["outcome"] = result.Outcome.ToString(),
            ["revision"] = result.Revision,
            ["events"] = new JsonArray([.. result.Events.Select(stored => (JsonNode)EventRow(stored, scope))]),
            ["errors"] = Errors(result.Errors),
            ["nextStartAfterRevision"] = result.NextStartAfterRevision,
        };
    }

    private static JsonObject DeriveResult(JsonObject body)
    {
        var (run, scope, record) = (Guid.Parse(body["runId"]!.GetValue<string>()), ReadScope(body["scope"]!), Guid.Parse(body["experienceId"]!.GetValue<string>()));
        var kind = Enum.Parse<ConfidenceEvidenceKind>(body["kind"]!.GetValue<string>());
        return new JsonObject
        {
            ["evidenceId"] = D(ExperienceFinalizationService.ReuseEvidenceIdFor(run, scope, record, kind)),
            ["eventId"] = D(ExperienceFinalizationService.ReuseEventIdFor(run, scope, record, kind)),
        };
    }

    /// <summary>
    /// A commit's result. The event's two statuses are the request's, as written: a commit stamps them from the request
    /// unchanged, and a status no member names (which the store refuses as Invalid) has no other spelling.
    /// </summary>
    internal static JsonObject CommitResult(CommitLifecycleTransitionResult result, JsonObject request) => new()
    {
        ["outcome"] = result.Outcome.ToString(),
        ["event"] = EventJson(result.Event) is { } stamped
            ? WithStatuses(stamped, request["priorStatus"]?.GetValue<string>(), request["currentStatus"]!.GetValue<string>())
            : null,
        ["revision"] = result.Revision,
        ["currentStatus"] = result.CurrentStatus?.ToString(),
        ["errors"] = Errors(result.Errors),
        ["reason"] = result.Reason,
    };

    private static JsonObject WithStatuses(JsonObject stamped, string? prior, string current)
    {
        stamped["priorStatus"] = prior;
        stamped["currentStatus"] = current;
        return stamped;
    }

    private static JsonObject CheckResult(ExperienceSupersessionCheckResult result) => new()
    {
        ["outcome"] = result.Outcome.ToString(),
        ["replacementStatus"] = result.ReplacementStatus?.ToString(),
        ["errors"] = Errors(result.Errors),
    };

    internal static JsonArray Errors(IReadOnlyList<StoreValidationError> errors) =>
        [.. errors.Select(error => (JsonNode)new JsonObject { ["path"] = error.Path, ["message"] = error.Message })];

    /// <summary>A <see cref="LifecycleEvent"/> as a result reports it: exactly as the service stamped it.</summary>
    internal static JsonObject? EventJson(LifecycleEvent? lifecycleEvent) => lifecycleEvent is not { } e ? null : new JsonObject
    {
        ["eventId"] = D(e.EventId),
        ["experienceRecordId"] = D(e.ExperienceRecordId),
        ["priorStatus"] = e.PriorStatus?.ToString(),
        ["currentStatus"] = e.CurrentStatus.ToString(),
        ["reason"] = e.Reason,
        ["producer"] = e.Producer,
        ["occurredAt"] = ContractJson.Timestamp(e.OccurredAt),
        ["expectedRevision"] = e.ExpectedRevision,
        ["replacementExperienceId"] = e.ReplacementExperienceId is { } r ? D(r) : null,
        ["confidence"] = UpdateJson(e.Confidence),
    };

    internal static JsonObject? UpdateJson(ConfidenceUpdate? update) => update is not { } u ? null : new JsonObject
    {
        ["evidenceId"] = D(u.EvidenceId),
        ["kind"] = u.Kind.ToString(),
        ["source"] = u.Source.ToString(),
        ["runId"] = D(u.RunId),
        ["verificationRoundId"] = u.VerificationRoundId is { } round ? D(round) : null,
        ["reviewerIdentity"] = u.ReviewerIdentity,
        ["ruleVersion"] = u.RuleVersion,
        ["priorReuseConfidence"] = u.PriorReuseConfidence,
        ["newReuseConfidence"] = u.NewReuseConfidence,
        ["priorSupportingValidations"] = u.PriorSupportingValidations,
        ["newSupportingValidations"] = u.NewSupportingValidations,
        ["priorContradictions"] = u.PriorContradictions,
        ["newContradictions"] = u.NewContradictions,
        ["detail"] = u.Detail,
        ["assessmentId"] = u.AssessmentId is { } assessment ? D(assessment) : null,
        ["admission"] = u.Admission?.ToString(),
        ["counted"] = u.Counted,
    };

    /// <summary>One stored event as a row of <c>agent_experience.lifecycle_events</c>, its values as JSON values.</summary>
    internal static JsonObject EventRow(StoredLifecycleEvent stored, Scope scope)
    {
        var e = stored.Event;
        var c = e.Confidence;
        return new JsonObject
        {
            ["event_id"] = D(e.EventId),
            ["experience_id"] = D(e.ExperienceRecordId),
            ["tenant_id"] = scope.TenantId,
            ["application_id"] = scope.ApplicationId,
            ["project_id"] = scope.ProjectId,
            ["team_id"] = scope.TeamId,
            ["agent_id"] = scope.AgentId,
            ["user_id"] = scope.UserId,
            ["prior_status"] = e.PriorStatus?.ToString(),
            ["current_status"] = e.CurrentStatus.ToString(),
            ["reason"] = e.Reason,
            ["producer"] = e.Producer,
            ["occurred_at"] = ContractJson.Timestamp(e.OccurredAt),
            ["recorded_at"] = ContractJson.Timestamp(stored.RecordedAt),
            ["expected_revision"] = e.ExpectedRevision,
            ["applied_revision"] = stored.AppliedRevision,
            ["replacement_experience_id"] = e.ReplacementExperienceId is { } r ? D(r) : null,
            ["actor"] = stored.Actor,
            ["confidence_evidence_id"] = c is null ? null : D(c.EvidenceId),
            ["confidence_kind"] = c?.Kind.ToString(),
            ["confidence_source"] = c?.Source.ToString(),
            ["confidence_run_id"] = c is null ? null : D(c.RunId),
            ["confidence_verification_round_id"] = c?.VerificationRoundId is { } round ? D(round) : null,
            ["confidence_reviewer_identity"] = c?.ReviewerIdentity,
            ["confidence_rule_version"] = c?.RuleVersion,
            ["confidence_detail"] = c?.Detail,
            ["prior_reuse_confidence"] = c?.PriorReuseConfidence,
            ["new_reuse_confidence"] = c?.NewReuseConfidence,
            ["prior_supporting_validations"] = c?.PriorSupportingValidations,
            ["new_supporting_validations"] = c?.NewSupportingValidations,
            ["prior_contradictions"] = c?.PriorContradictions,
            ["new_contradictions"] = c?.NewContradictions,
            ["confidence_assessment_id"] = c?.AssessmentId is { } assessment ? D(assessment) : null,
            ["confidence_admission"] = c?.Admission?.ToString(),
        };
    }

    internal static ExperienceRecord Reread(IExperienceRecordStore store, ExperienceRecord seeded)
    {
        var read = store.GetAsync(AuthorizationFor(seeded.Scope), seeded.Scope, seeded.ExperienceId, CancellationToken.None).GetAwaiter().GetResult();
        return read.Record ?? throw new InvalidOperationException($"{seeded.ExperienceId} is no longer readable: {read.Outcome}.");
    }

    internal static JsonArray WholeHistory(IExperienceRecordStore store, ExperienceRecord record)
    {
        var events = new JsonArray();
        long? after = null;
        while (true)
        {
            var page = store.GetHistoryAsync(
                AuthorizationFor(record.Scope),
                new ExperienceRecordHistoryQuery(record.Scope, record.ExperienceId, ExperienceRecordHistoryQuery.MaxLimit, after),
                CancellationToken.None).GetAwaiter().GetResult();
            foreach (var stored in page.Events)
            {
                events.Add(EventRow(stored, record.Scope));
            }

            if (page.NextStartAfterRevision is not { } next)
            {
                return events;
            }

            after = next;
        }
    }

    // ---- reading inputs -----------------------------------------------------------------------------------------

    internal static AuthorizationContext AuthorizationFor(Scope scope) => new(
        scope.TenantId, Principal, [], Fixed.T0, scope.ApplicationId, scope.ProjectId, scope.TeamId, scope.AgentId, scope.UserId);

    internal static AuthorizationContext ReadAuthorization(JsonNode authorization) => new(
        authorization["tenantId"]!.GetValue<string>(),
        authorization["principalId"]!.GetValue<string>(),
        [],
        Fixed.T0,
        authorization["applicationId"]?.GetValue<string>(),
        authorization["projectId"]?.GetValue<string>(),
        authorization["teamId"]?.GetValue<string>(),
        authorization["agentId"]?.GetValue<string>(),
        authorization["userId"]?.GetValue<string>());

    internal static Scope ReadScope(JsonNode scope) => new(
        scope["tenantId"]!.GetValue<string>(),
        scope["applicationId"]!.GetValue<string>(),
        scope["projectId"]!.GetValue<string>(),
        scope["teamId"]?.GetValue<string>(),
        scope["agentId"]?.GetValue<string>(),
        scope["userId"]?.GetValue<string>());

    private static CommitLifecycleTransitionRequest ReadCommit(JsonObject request) => new(
        Guid.Parse(request["eventId"]!.GetValue<string>()),
        Guid.Parse(request["experienceId"]!.GetValue<string>()),
        ReadScope(request["scope"]!),
        request["priorStatus"] is { } prior ? ReadStatus(prior.GetValue<string>()) : null,
        ReadStatus(request["currentStatus"]!.GetValue<string>()),
        request["reason"]!.GetValue<string>(),
        request["producer"]!.GetValue<string>(),
        Instant(request["occurredAt"])!.Value,
        request["expectedRevision"]!.GetValue<long>(),
        request["replacementExperienceId"] is { } replacement ? Guid.Parse(replacement.GetValue<string>()) : null);

    private static ApplyConfidenceEvidenceRequest ReadApply(JsonObject request) => new(
        Guid.Parse(request["eventId"]!.GetValue<string>()),
        Guid.Parse(request["experienceId"]!.GetValue<string>()),
        ReadScope(request["scope"]!),
        Guid.Parse(request["evidenceId"]!.GetValue<string>()),
        ReadEnum<ConfidenceEvidenceKind>(request["kind"]!.GetValue<string>()),
        ReadEnum<ConfidenceEvidenceSource>(request["source"]!.GetValue<string>()),
        Guid.Parse(request["runId"]!.GetValue<string>()),
        request["verificationRoundId"] is { } round ? Guid.Parse(round.GetValue<string>()) : null,
        request["reason"]!.GetValue<string>(),
        request["producer"]!.GetValue<string>(),
        Instant(request["occurredAt"])!.Value,
        request["detail"]?.GetValue<string>(),
        request["assessmentToken"]?.GetValue<string>());

    /// <summary>A status by its exact member name, or a value no member has (an undefined status the store refuses).</summary>
    internal static ExperienceStatus ReadStatus(string text) => ReadEnum<ExperienceStatus>(text);

    private static T ReadEnum<T>(string text)
        where T : struct, Enum =>
        Enum.TryParse<T>(text, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed) && parsed.ToString() == text
            ? parsed
            : (T)(object)99;

    internal static DateTimeOffset? Instant(JsonNode? value) =>
        value is null ? null : DateTimeOffset.Parse(value.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    internal static string D(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
}

/// <summary>A clock set by hand, whose timers never fire: no case depends on time passing.</summary>
internal sealed class SettableTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

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

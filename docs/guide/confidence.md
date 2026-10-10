# Confidence and independence

**In short.** Every record carries a reuse confidence: a score that starts at 2/3 when the record is finalized and
moves as evidence about reuse arrives. Supporting evidence raises it; contradicting evidence lowers it and contests
the record, which takes it out of reuse. The same observation can only count once: each piece of evidence has an
*independence key* (one per run and verification round, or one per reviewer and run), and the database enforces it.
By default the library also checks that the key's inputs are real: the run must be one it knows, the round must be
the one that run was finalized with, a human assessment must carry a token your review flow minted, and the run must
actually have been given the record. The score is a heuristic for ranking, not a probability.

Packages: `AgentExperience.Core` computes and verifies; `AgentExperience.Storage.Postgres` stores and enforces the
key. Read the KL-11 boundary in [Known limits and documented boundaries](../known-limits.md#documented-boundaries)
before relying on independence.

## Applying evidence

`ExperienceLifecycleService.ApplyEvidenceAsync` is how the number moves after finalization: submit what happened when
the lesson was reused, and the evidence, the counters, the score, any status change, and the audit entry are
committed in one transaction. Finalization can also submit it for you, when a run that was given a lesson verifies on
the same task (see [Letting reuse move confidence](#letting-reuse-move-confidence)).

```csharp
var result = await lifecycle.ApplyEvidenceAsync(
    hostAuthorization,
    new ApplyConfidenceEvidenceRequest(
        EventId: Guid.NewGuid(),              // the commit's idempotency key
        ExperienceId: experienceId,
        Scope: recordScope,
        EvidenceId: Guid.NewGuid(),           // the evidence's own; reuse it verbatim on a retry
        Kind: ConfidenceEvidenceKind.Supporting,      // or Contradicting
        Source: ConfidenceEvidenceSource.Machine,     // or Human
        RunId: runId,                         // the run the *reuse* happened in -- finalized, or captured, here
        VerificationRoundId: roundId,         // machine only: the round that run was finalized with
        Reason: "the retry-after-lock lesson was applied and the checks passed",
        Producer: "verification-aggregator/1.0.0",
        OccurredAt: DateTimeOffset.UtcNow),
    cancellationToken);

if (result.Outcome == ConfidenceUpdateOutcome.Applied)
{
    logger.LogInformation(
        "Experience {Id} is now {Confidence:F3} ({S} supporting, {F} contradicting){Counted}",
        experienceId, result.ReuseConfidence, result.SupportingValidations, result.Contradictions,
        result.Counted ? "" : " — already counted, recorded only");
}
```

**By default, the score is `(1 + S) / (2 + S + F)`** (a host can [replace the engine](#replacing-the-engine)). `S` counts independent accepted supporting validations, including the one
the record was finalized with; `F` counts independent accepted contradictions. So a fresh validated record is
`2/3`, a first independent confirmation takes it to `3/4`, and a contradiction after that takes it to `3/5`.

**It is a heuristic, not a probability.** Laplace's rule of succession is a monotone, bounded summary of how often
reuse held up — useful for ranking and for a floor. It is not calibrated against anything, and nothing here claims
it is the probability that the next reuse will succeed. The rule is versioned: every accepted update records the
`RuleVersion` that produced it, so a later rule change stays auditable against scores computed under an earlier one.
It is also only the default: a host can supply its own scoring rule (see [Replacing the engine](#replacing-the-engine)).

**It never changes eligibility by itself.** Confidence is independent of the completion score and of status; a number
cannot make an ineligible record eligible. What takes a record out of reuse is the *status*: a contradiction moves a
`Validated` or `Reinforced` record to `Contested` in the same transaction, and a record already `Contested` stays
there while its counters keep moving. Supporting evidence never changes a status by itself — which is how a record
keeps being reinforced through its counters even though `Validated → Reinforced` happens only once (see
[Lifecycle](lifecycle.md#the-transition-table)). Retrieval does apply a confidence floor
(`RetrievalPolicy.MinimumConfidence`, 0.5 by default), so a low score stops a record being *returned*.

## Letting reuse move confidence

Without help, every record stays at 2/3: the score moves only when a host submits evidence itself. Finalization can
do it for you. It is opt-in:

```csharp
services.AddSingleton(new ExperienceFinalizationOptions
{
    ReuseEvidence = ReuseEvidenceMode.SameTask,   // default: Off
    ContradictOnFailure = false,                  // the default: a failed run contradicts nothing
});
// Under the one-call setup: options.ReuseEvidence = ReuseEvidenceMode.SameTask; (and options.ContradictOnFailure).
```

**What counts.** When a run finalizes into a durable record, finalization looks at the records that run was given
(its `Provenance.ExposedTo`, which the MAF adapter records for every lesson it injects). For each one that is readable
in the run's own scope, is on the **same task** (the same `TaskId`, compared ordinally), and did not come from this
run, it submits machine evidence through `ApplyEvidenceAsync`:

- **supporting**, when the run verified;
- **contradicting**, when the run failed verification and `ContradictOnFailure` is set;
- nothing, when the run failed without `ContradictOnFailure`, when its verification was inconclusive, or when its
  finalization closed no round.

What decides the kind is the run's verification, not its own record's status: a verified run whose own record was
quarantined because its reflection failed or was screened out still supports the lessons it was given. The run
verified; its own lesson being refused says nothing about the lessons it used.

The evidence names the run and the round its finalization closed, so it is one independence key per run and record,
and everything above applies unchanged: the run must be known, the round must be its own, and the run must have been
exposed to the record at or before its current revision. Its evidence and event IDs are derived from the run, its
scope, the record and the kind (`ExperienceFinalizationService.ReuseEvidenceIdFor` and `ReuseEventIdFor`; the scope is
mixed in as for the initial event ID, so a writer in another scope cannot take them first), its `Producer` is
`ExperienceFinalizationService.ReuseEvidenceProducer`, and its time is the run's finalization time. Repeated
exposures, empty IDs and the run's own record are dropped first, and then at most `RunExposure.MaxPerRun` records are
considered.

**Why the same task.** A lesson given to a run on another task says little about whether that lesson holds: the run's
verdict is about its own task. Records on another task get no evidence.

**Why not a lesson shared by a grant.** A record this scope reads only through a sharing grant belongs to the lending
scope, and so does its confidence: a grant confers reading, never writing, so the borrower's runs never move it.
Such records are skipped.

**Why contradiction is opt-in.** A run can fail for reasons that have nothing to do with the lesson it was given: a
flaky check, a changed environment, a harder variant of the task. Counting every such failure against every lesson the
run saw would contest good lessons, and a contested record leaves reuse at once. Turn it on when a failed run on the
same task really is evidence against what it was told.

**It never changes finalization.** The step runs after the record is durable and after indexing, within the caller's
cancellation token and bounded by `ExperienceFinalizationOptions.ReuseEvidenceTimeout` (10 seconds by default). Every
record the run's provenance names gets one `ReuseEvidenceResult` on `FinalizeExperienceResult.ReuseEvidence`:

- **Submitted:** the `ConfidenceUpdateOutcome`, whether it `Counted`, the `IndependenceRefusal` when there is one, and
  the reason. A refusal (`Unverified`, a duplicate, `StaleRevision`, `Ineligible`, ...) is reported and never retried.
- **`Skipped`:** not submitted, with the reason: the record could not be read in the run's scope (whatever the read
  answered, `NotFound` included, is named), it is shared by a grant, it is on another task, or it came from this run.
- **Failed:** reading or applying threw. `Outcome` is `null` and `ExceptionType` names the exception, never its
  message.

A cancellation or the timeout stops the step: the last entry says so ("Cancelled before ..." or "Timed out ..."), the
records after it are not reported, and `ReuseEvidenceTruncated` is `true`. The outcome, the record and `IsDurable` stay
what they were.

**Recovering lost evidence.** Evidence a refusal, an exception, a cancellation or the timeout lost is recovered by
finalizing the run again, while the capture service still holds it. The replay (`AlreadyFinalized`) resubmits the same
evidence under the same IDs: what already landed is reported with `Replay = true` and `Counted = false`, so a sum of
`Counted` over every call counts each piece once, and what had not landed is applied now. The same call backfills a run
finalized while the option was off. A replay reports `Ineligible` for a lesson that has since been revoked, quarantined
or superseded, even when the original evidence did land (see the ordering note under [Outcomes](#outcomes)).

**Telemetry.** Each submission is an ordinary `confidence.apply` operation, nested in `finalize`, and a durable
`finalize` carries `agentexperience.reuse_evidence.submitted`, how many records it submitted for, while the option is
on.

**What it does not prove.** Exactly what [verification does not prove](#what-verification-does-not-prove): a verified
run that was given a lesson is one supporting key, whether or not the lesson helped it.

## Replacing the engine

The score comes from an `IExperienceConfidenceEngine` (in `AgentExperience.Core.Confidence`). With none configured,
`ReuseConfidenceHeuristicEngine` computes `(1 + S) / (2 + S + F)`, and every stored value, `RuleVersion`, outcome and
event is exactly what it was before the engine was replaceable. A host that wants a different evidence model supplies
its own:

```csharp
public sealed class BayesianConfidenceEngine : IExperienceConfidenceEngine
{
    public string RuleId => "bayes";
    public string RuleVersion => "2.1";

    // input.Record is the record as read; the counters are the ones *after* this evidence.
    public double Score(ExperienceConfidenceInput input) =>
        (2d + input.SupportingValidations) / (4d + input.SupportingValidations + input.Contradictions);
}

services.AddSingleton<IExperienceConfidenceEngine, BayesianConfidenceEngine>(); // before or after AddAgentExperienceCore
```

Or pass it to the `ExperienceLifecycleService` constructor overload that takes `confidenceEngine` as its last
parameter (`null` means the default). `AddAgentExperienceCore` resolves it once, from the root provider, so register
it as a singleton. One instance serves concurrent calls, so it must be thread-safe; it must also be deterministic,
and it runs synchronously, so it must not block.

What the engine is given, and what it is not:

- **The counters include finalization's validation.** `S` counts the supporting validation a record is finalized
  with, so a finalized record's first supporting evidence scores `S = 2`, and its first contradiction `S = 1, F = 1`.
- **Only counters and the record.** The input carries the record and the two counters, not the evidence's kind or
  source, the run, or the reviewer. A rule that needs to weigh human evidence differently cannot do it here.
- **On a confidence read, the record is unfiltered.** When `ReadConfidenceAsync` recomputes, `input.Record` is the
  stored record, with the stored (unfiltered) counters and score; the input's counters are the filtered ones. Score
  from the input's counters.
- **The retrieval floor uses your scale.** `RetrievalPolicy.MinimumConfidence` (0.5 by default) is compared against
  the stored score, which is your engine's, so choose the floor for your engine's distribution.

- **The engine owns only the score.** Which statuses accept evidence, the move to `Contested`, the counter
  increments, independence keys, verification, exposure and idempotent replay stay in the library, and no engine can
  change them. It scores every accepted piece of evidence, and `ReadConfidenceAsync` scores its recomputation through
  the same engine.
- **The rule is recorded without a migration.** The default engine records the plain `"1.0.0"`, as before. A host
  engine records `"{RuleId}/{RuleVersion}"` (`bayes/2.1` above) in the existing `ConfidenceUpdate.RuleVersion` field,
  and so on the lifecycle event and in the evidence ledger. `RuleId` and `RuleVersion` must each be 1 to 64 characters
  from `[A-Za-z0-9._-]`, and a host engine may not use the default's `RuleId`, `reuse-heuristic`. They are checked
  when the lifecycle service is constructed, and a violation throws `ArgumentException`. Bump `RuleVersion` whenever
  the arithmetic changes. Keep one engine per deployment: a retry of stored evidence under a different rule string is
  a `Conflict`, because the replay check compares the rule version.
- **A wrong score is refused, never repaired.** A score that is NaN, infinite or outside [0, 1] throws
  `InvalidOperationException` naming the rule, with nothing written. An exception the engine throws propagates
  unchanged, like a store failure, and nothing is written either. `ReadConfidenceAsync` throws the same way when it
  recomputes. The reuse-feedback service reports an engine failure as that record's retryable failure and carries
  on with the next record.
- **It applies from the first evidence onward.** Finalization still stamps a freshly validated record with the
  heuristic's 2/3 (`ExperienceFinalizationService.InitialValidatedReuseConfidence`); that stamp is not the engine's,
  and the engine is not asked for it. The engine's score replaces it
  when the record's first evidence is applied. Scores stored before the engine changed keep the rule they were
  recorded under until new evidence arrives.

## Independence is keyed, and the database owns the key

Machine evidence counts once per `(record, run, verification round)`; human evidence once per `(record, reviewer,
run)`. The key is a *generated* column in `confidence_evidence` with a partial unique index over it, so no caller
picks the key **string**: two submissions describing the same observation collide however they are phrased.

## The key's inputs are verified

A key is only worth anything if its inputs cannot be invented, so before anything is computed or written the
submission is checked, and refused with `ConfidenceUpdateOutcome.Unverified` and an `IndependenceRefusal` if it
fails:

- **`RunId`** must be a run the library knows **in the evidence's scope**: one finalized into a record there (the
  record finalization derives for that run and scope, read through the ordinary scoped `GetAsync` — not through a
  grant, not a tombstone), or one the capture service wired into the lifecycle service holds there (`UnknownRun`
  otherwise). Retrieval is exact-scope and a grant never confers writing, so no run in another scope could have been
  exposed to a record that accepts evidence. It is never the record's own `SourceRunId` (`OwnRun`, refused in every
  mode). A record under the derived ID counts only if finalization wrote it (`ExperienceRecordOrigin.Finalized`); one
  written by hand through `CreateAsync` vouches for nothing (`HostWrittenRun`). With
  [provenance signing](#signing-provenance) configured, a record marked `Finalized` also needs a valid signature.
- **Exposure.** The run must have been *given* the record: its provenance (`Provenance.ExposedTo`, on the finalized
  record or on the run the capture service holds) must name the record at a revision at or before the one the
  evidence is computed against, or the submission is refused as `NotExposed`. The MAF adapter records this for you —
  its context provider records, on the captured run, every record it injects, at the revision it rendered — and
  finalization copies it onto the run's record. What a reused session's history carries from an *earlier* run's turns
  is deliberately not credited to a later run: the session account lives in host session storage, unauthenticated,
  so a later run in the same session is exposed only to what it is given itself. An exposure recorded at a *later*
  revision than the record's current one cannot have happened, and is refused. A host that delivers records some
  other way calls `IExperienceCaptureService.RecordExposure(runId, exposures)` from the code that delivers them; it
  keeps one entry per record at the earliest revision, is refused once the run is completed, and stops at
  `RunExposure.MaxPerRun` (256). `StartRun` refuses a provenance that already carries exposures. The exposure check
  runs last, after the run, round and token, and it applies to machine and human evidence alike, because every
  submission is a claim that reusing the record helped or hurt the named run. The machine evidence that is about a
  record's *own* quality — its source run's evaluation, which seeds its first supporting validation — is bound by
  finalization and never comes through this path.
- **`VerificationRoundId`** (machine) must be the round that run was finalized with: finalization stamps
  `ExperienceRecord.ClosedRoundId` from the evaluation it computed. A run only the capture service holds, or one
  finalized with no closed round, has no round to vouch for (`UnknownRound`). So one run yields at most one machine
  key per record.
- **Human evidence** must carry an `AssessmentToken` from `AssessmentTokenIssuer.Issue(reviewer, scope, runId, kind,
  experienceIds)`, which your review flow calls when a person records a decision. It is an HMAC-SHA256 over the
  assessment's ID, issue time, direction and records and over the scope, run and reviewer, under
  `ExperienceIndependenceOptions.AssessmentTokenKey` (at least 32 bytes from your secret store). It is compared in
  constant time before anything it claims is read, expires after `AssessmentTokenLifetime` (a day by default), must
  cover the record, and is spent once per record by the store, in the same transaction as the evidence: another
  evidence ID presenting it is refused (`AssessmentTokenReplayed`), while resubmitting the same evidence still
  replays. A random GUID, a tampered token, or one minted for another scope, run, reviewer, direction or record is
  `AssessmentTokenInvalid` or `AssessmentTokenNotForRecord`; an expired one is `AssessmentTokenExpired`; with no key
  configured, human evidence is refused.

```csharp
services.AddSingleton(new ExperienceIndependenceOptions { AssessmentTokenKey = secrets.AssessmentTokenKey });

// In your review flow, where a person decided -- never in code an agent drives. The issuer is deliberately not
// registered in DI: anything that can resolve it can mint.
var issuer = new AssessmentTokenIssuer(independenceOptions);
var assessment = issuer.Issue(reviewerAuthorization, recordScope, runId, ConfidenceEvidenceKind.Supporting, [experienceId]);
// ...then submit Human evidence with AssessmentToken: assessment.Token, under the same reviewer's authorization.
```

`ExperienceIndependenceOptions` carries the assessment token key, the token lifetime, the clock, and the opt-out.
`ReviewerIdentity` is enforced for you — it is taken from `AuthorizationContext.PrincipalId` and the request has no
field for it, because the number of distinct human reviewers is exactly what this rule protects. Principals are
compared ordinally, like every other identity here, and one with leading or trailing whitespace is refused rather
than trimmed.

## What verification does not prove

Read this before relying on it. Verification proves a run is *real, in scope, and was given the record* — not that
the record mattered to it: every run the library delivered a lesson into is one key, whatever the lesson did there.
Exposure is what the library recorded delivering, and the library believes a host that calls `RecordExposure`
itself. Without [provenance signing](#signing-provenance) it also believes a host that writes a record through
`CreateAsync` marked `ExperienceRecordOrigin.Finalized`, because the store port cannot tell the library's writes from
the host's; with signing on, it believes whoever holds a signing key. The round is the one the host closed at finalization. Real runs
are easy to name: every record in the scope carries its `SourceRunId` and `ClosedRoundId`, a run's round vouches
whatever its own verification concluded (a failed or quarantined run's round vouches too), and a run the capture
service holds stays known while it is held. The token is only as secret as the key and as guarded as the code that
can call the issuer, and single use is the store's guarantee (the PostgreSQL store makes it; an
`IExperienceRecordStore` that ignores `ConfidenceUpdate.AssessmentId` does not).

Verification runs before the store's replay check, so retry a lost acknowledgement within the token's lifetime:
after it (or after its key rotated, or its run stopped being known), the retry is refused (`AssessmentTokenExpired`)
although the original is durable, and a feedback retry then degrades and conflicts with its own stored row. Keep
taking `RunId` from your own run bookkeeping (the adapter's session state), never from agent output.

**The opt-out**, `IndependenceVerification.TrustHostSuppliedIdentifiers`, is the older behaviour for a host that
cannot adopt verification yet — one that captures and retrieves in different scopes, finalizes nothing, or must
accept evidence about runs finalized before verification existed: every identifier is trusted as given (only the
own-run rule stays, and no exposure is checked), and a host that lets agent output populate them hands the agent a
fresh key per call. That is part of the KL-11 boundary. With provenance signing configured, the opt-out still
accepts, as host-trusted, a run whose record carries no signature or one under a key that is not in the ring. The one
thing it refuses is a record marked `Finalized` whose signature is present, under a key in the ring, and does not
verify: a record the library signed that someone then changed. Evidence about that run is refused as `HostWrittenRun`,
and attributed feedback about it is degraded before the ledger. By default the opt-out's evidence counts in the
score retrieval ranks on; `HostTrustedEvidence = RecordedOnly` records it without moving the record (see
[Keeping host-trusted evidence out of the ranked score](#keeping-host-trusted-evidence-out-of-the-ranked-score)).

## Signing provenance

Verification relies on what a run's finalized record says about itself: that finalization wrote it (`Origin`), the
round it closed (`ClosedRoundId`), and what the run was given (`Provenance.ExposedTo`). By default those are plain
fields, so a host that writes a record through `CreateAsync` with the right values, or an application role holding
`AllowSealing` that replaces a payload, is believed. Signing, which is opt-in, closes that gap:

```csharp
services.AddSingleton(new ExperienceProvenanceSigningOptions(
    new Dictionary<string, byte[]> { ["prov-2026-09"] = secrets.ProvenanceSigningKey },   // 32+ random bytes
    currentKeyId: "prov-2026-09")
{
    // Optional: the records finalized before signing was switched on (see "Turning it on" below).
    TrustUnsignedRecordIds = config.LegacyUnsignedRecordIds,
});
```

**Registering it.** `AddAgentExperienceCore` picks the options up in either registration order, registered directly
(`services.AddSingleton(options)`, as above) or as an explicitly registered `IOptions<ExperienceProvenanceSigningOptions>`
(`services.AddSingleton(Options.Create(options))`). A direct registration wins when both exist. `services.Configure<…>`
does not apply, because the options have no parameterless constructor: they are validated when they are built. The
lifecycle service checks every run's record against them, and finalization signs with the same ring. Hosts that
construct services by hand pass the options to the `ExperienceLifecycleService` constructor that takes
`provenanceSigning`. An `ExperienceFinalizationService` built over that lifecycle service signs with the same options.
Given options of its own, it signs with those instead, and its constructor refuses them unless the lifecycle service's
ring holds their current key under the same ID, because a record signed under a key the checker lacks would vouch
for nothing.

- **What is signed.** Finalization signs claims version 3 with HMAC-SHA256 under `CurrentKeyId`, and
  stores the algorithm as `HMAC-SHA256.aexp-prov.v3` (`ExperienceProvenanceSignature.HmacSha256ClaimsV3`), so a
  verifier never guesses the version. The claims are a canonical encoding, tagged `aexp-prov:v3`, of:
  - its `ExperienceId`;
  - all six `Scope` fields, as strict UTF-8 (a null field and an empty one encode differently, and a lone surrogate
    is refused, never replaced);
  - its `SourceRunId`, `ClosedRoundId` and `Origin`;
  - its `Provenance.ExposedTo`, sorted by the record ID's big-endian (RFC 4122) bytes compared as unsigned bytes, then
    by revision ascending;
  - then the SHA-256 digest of its content: everything injection can render from the record, encoded in this order:
    `TaskId`; `TaskSummary`; the outcome's `Status` and evidence count; the `Environment` (host, runtime, operating
    system, application version, then the metadata sorted by key); the `Attempts` (each attempt's sequence number and
    its `Error`, and each tool call's sequence number, tool name, arguments and `Error`, keys sorted, each value through
    its JSON form as the PostgreSQL store writes it (enums by name, object members camel-cased, a repeated member's last
    value), with every number canonicalized by its exact decimal value, so `1e17` and `100000000000000000`, or `1.5`
    and `1.50`, encode alike and either store reads back the same bytes); and the reflection (absent when there is none): `Lesson`, `SuccessfulApproaches`, `FailedApproaches`,
    `ReuseGuidance`, `Preconditions`, `Warnings`, its evidence count, `Authorship` and `Producer`. Every string is
    length-prefixed strict UTF-8 with a presence byte, and every list a presence byte and a count, so moving text
    between fields changes the digest. A run whose content has no such encoding (a lone surrogate, including one in an
    attempt's or tool call's error text that a custom `ISanitizer` let through, or an argument value with no JSON form)
    ends `Failed` at the create stage, with nothing stored.

  So the error classes a `Tried:` line shows, and each call's `[returned]`/`[failed: …]` marker, which injection
  derives from each call's `Error`, are signed: flipping which call failed, or rewriting an error's class or text,
  breaks the signature. The version 3 content encoding is the version 2 one with the errors added where the attempts
  are encoded: each attempt's byte saying whether it failed becomes its `Error` as a string (that byte is the string's
  presence byte), and each tool call's `Error` follows its arguments as a string. Every other byte is unchanged.

  **Version 2 records.** Signatures made by `0.1.0-preview.7` through `0.1.0-preview.9`, or during a
  `SignClaimsVersion = 2` rollout, carry `HMAC-SHA256.aexp-prov.v2` (`ExperienceProvenanceSignature.HmacSha256ClaimsV2`):
  claims version 2, the same encoding tagged `aexp-prov:v2`, whose content digest encodes only *whether* each attempt
  failed, never an attempt's or a tool call's error. They still verify, and still confirm their record's content, so
  upgrading fences nothing; but the error text of a version 2 record is unsigned: a party that can write the store
  can change its error classes and per-call markers without breaking the signature. Nothing re-signs them.

  The signature is stored with the record as `ExperienceRecord.ProvenanceSignature` (key ID, algorithm and value) in
  the same create. Status, counters and timestamps change through the lifecycle and are not signed. Both stores refuse
  a scope field that is not well-formed UTF-16. Signatures made by `0.1.0-preview.6` and earlier carry `HMAC-SHA256`
  (`ExperienceProvenanceSignature.HmacSha256`): claims version 1, the same encoding tagged `aexp-prov:v1` with no
  content digest. They still verify for those claims.
- **Content decides authorship.** With signing configured, a record's content is *confirmed* only when it carries a
  version 3 or version 2 signature, under a key in the ring, that verifies, or when it is unsigned and its ID is in
  `TrustUnsignedRecordIds`. Otherwise (a version 1 signature, none, an unknown key, or one that does not verify) its
  content is unconfirmed, and it counts as model-authored whatever its reflection declares: `ExperienceRetrievalService`
  leaves it out under `ExcludeModelAuthored` (`RetrievalExclusionReason.UnconfirmedContent` when that is the only
  reason, `ModelAuthored` when its reflection is model-authored anyway), and injection omits it under
  `ModelAuthoredLessons = Exclude` (`InjectionOmissionReason.UnconfirmedContent`), or fences and labels it, with its
  task ID (a `Task:` line), its `Recorded:`, `Environment:`, `Verification:` and `Evidence:` lines and its `Tried:` and
  `Worked:` lines inside the fence together with its lesson; only the record header and the confidence and ranking lines the
  library computes stay above it. A stored record whose content cannot be encoded at all is unconfirmed too, and never
  fails a retrieval. So a party that can write
  the store can no longer change a lesson or flip its authorship to `Deterministic` unnoticed (KL-18). The retrieval
  service decides this (`ExperienceRetrievalService.IsModelAuthored` and `IsContentConfirmed`) and the injection provider asks it about the
  record it re-read, so both decide on what is rendered. `AddAgentExperienceRetrieval` passes the registered options to
  the service; a host constructing it by hand passes them to the constructor that takes `provenanceSigning`.
  **Records signed before this release are fenced**, at injection, and excluded under `Exclude`: their version 1
  signature confirms their claims, not their content. Nothing re-signs them, because signing existing content would
  vouch for text nobody checked. A record a host writes through `CreateAsync` is unsigned, so it is fenced too unless
  it is in the cutover set, and a listed unsigned record is trusted as it stands, so its content stays editable.
  Without signing configured, nothing changes.
- **Rolling deploys.** A node refuses a signature under a claims version it does not know as one it cannot check, so
  its records would vouch for nothing there: a node on `0.1.0-preview.7` through `0.1.0-preview.9` refuses version 3,
  and one on `0.1.0-preview.6` or earlier refuses versions 2 and 3. `SignClaimsVersion` (default 3; only 1, 2 or 3 is
  accepted) sets what finalization signs. Sign 2 until every node runs this build, then 3: version 2 records keep
  confirming their content afterwards, with their error text unsigned. Set it to 1 only while nodes on
  `0.1.0-preview.6` or earlier still verify: records signed version 1 meanwhile have unconfirmed content, as any version
  1 record does. Verification always accepts all three versions. **Rolling back** to `0.1.0-preview.7` through
  `0.1.0-preview.9` after signing version 3: those builds refuse a version 3 signature, so there every record signed
  version 3 has unconfirmed content (fenced, or excluded under `Exclude`, as model-authored) and its run vouches for
  nothing in independence verification. Records signed version 2 are unaffected; sign 2 for as long as a rollback must
  stay open.
- **Transition for existing lessons.** `ConfirmV1Content = true` lets a version 1 signature that verifies confirm its
  record's content as well, so lessons signed before this release render by the authorship they declare instead of
  being fenced. It accepts, for those records, the exposure version 1 left open (their text or authorship may have
  been changed by a party that can write the store), so use it while those lessons are reviewed or replaced, then
  turn it off. It never confirms a version 1 signature that does not verify.
- **Per-record confirmation.** `ConfirmContentRecordIds` is the narrower alternative: the IDs of records whose content
  you have reviewed (signed version 1 before this release or during a `SignClaimsVersion = 1` rollout, or unsigned),
  which then count as confirmed. A listed record whose signature is present but does not verify stays unconfirmed.
  Their content stays editable by a party that can write the store, so prefer replacing them with records finalized
  under version 3.
- **What is checked.** Wherever verification relies on a run's finalized record, the signature must verify under a key
  in the ring, compared in constant time. A record whose signature is missing, names a key that is not in the ring,
  or does not verify is treated as written outside finalization, and the evidence is refused as `HostWrittenRun`.
  The reason is the same sentence in all three cases ("its provenance signature does not vouch for it"), so a caller
  learns nothing about which check failed, and it names no key material. A run the capture service still holds stays
  known through the capture service, as it would for any hand-written record, so it has no round to vouch for.
- **Counters are not claims.** `ConfidenceEvidenceFilter.VerifiedOnly` leaves out the initial counters of a record
  whose signature does not vouch for it. For one that does, it counts as verified at most what finalization itself
  sets (one supporting validation, no contradiction); anything above that is its writer's statement.
- **Turning it on.** Records finalized before signing was configured carry no signature, so their runs stop vouching
  for evidence. `TrustUnsignedRecordIds` is the explicit cutover: the IDs of those records, which verification accepts
  unsigned. Capture it once, when you switch signing on, with
  `ExperienceProvenanceSigningCutover.ListUnsignedFinalizedRecordIdsAsync(store, authorization, scopes)`. It lists every
  record marked `Finalized` with no signature in each scope, and names any scope with a status too large for one query
  in `IncompleteScopes`. Review the list, store it in your configuration, and pass it in. A record created afterwards
  can never join the set: its ID would have to be one that already exists, and a store refuses to create over an
  existing ID or a tombstone. So a record forged later is not believed, however far back its `CreatedAt` claims to
  go. A listed record whose signature is present but invalid is refused anyway.
- **Content edits break a version 3 or version 2 signature.** Both cover the content, so a record whose lesson, task
  text, authorship or producer changed after it was signed no longer verifies, and its run stops vouching as well;
  under version 3, so does a changed attempt or tool-call error. A version 1 signature never covered content, so
  independence verification treats such a record exactly as it did before.
- **A retry does not replay a record that does not vouch.** If finalizing a run collides with a stored record at its
  derived ID whose signature does not vouch for it, `FinalizeAsync` reports `Failed` at the create stage, saying the
  stored record does not carry a valid provenance signature, instead of `AlreadyFinalized`.
- **Keys.** Each key must follow these rules:
  - Generate it as at least 32 bytes from a cryptographic random source, and hold it in your secret store.
  - Never store it in the database, in configuration an agent can read, or next to agent tooling.
  - Never reuse the assessment token key: a ring that holds it is refused.
  - Never share a ring between environments. Staging and production each have their own keys, so a record signed in
    one never vouches in the other.

  Key IDs are 1 to 64 characters from `[A-Za-z0-9._-]`. The options are validated and the keys copied when they are
  constructed, and copied again when a service is built. Nothing public returns a key (`KeyIds` lists the IDs), and
  `ToString` redacts them. Only the key ID is stored with a record. The signature never appears in telemetry, logs,
  errors or the Historical Reference block, and its own `ToString` prints its length, not its bytes.
- **Rotation.** Add the new key to the ring and switch `CurrentKeyId` to it. Records signed under the old key keep
  verifying while it stays in the ring. Removing a key makes every record it signed vouch for nothing, as an unsigned
  record does. The cutover set does not bring them back, because it covers unsigned records only.
- **What it does not change.** Anyone who holds a signing key can sign any claims and any content, so the key is
  exactly as trusted as the code that can read it. `RecordExposure` is still the host's statement: finalization signs the exposures the
  capture service holds, whoever recorded them. A store persists the signature and never checks it, because it holds
  no key. Both shipped stores keep it in the record's payload. The PostgreSQL store seals it with the rest in
  crypto-shredding mode and needs no migration for it.

## What the opt-out admitted is kept visible

Every update Core submits carries `ConfidenceUpdate.Admission` — `Verified` when the checks above ran, `HostTrusted`
when the host opted out — and the PostgreSQL store keeps it on the evidence ledger and on the counted event (`0018`),
append-only like the rest of the row; evidence stored before that reads back with no admission. `confidence.apply`
spans carry it as `agentexperience.confidence.admission`, and a refusal as `agentexperience.independence.refusal`, so
an operator can see the opt-out in use without reading the ledger. To read a score without it:

```csharp
var read = await lifecycle.ReadConfidenceAsync(authorization, scope, experienceId, ConfidenceEvidenceFilter.ExcludeHostTrusted, ct);
// read.Report.ReuseConfidence: the confidence engine's score (the heuristic by default) over the counters less the
// host-trusted evidence.
// read.Report.HostTrusted / .Verified / .Unrecorded: what each admission counted. VerifiedOnly also drops Unrecorded
// (no admission recorded: stored before 0018, or written by something other than Core) and, for a record written by
// hand, the initial counters its writer chose (read.Report.Initial, read.Report.Origin).
```

It pages the record's history (a counted update is always an event), counts what each admission moved, and recomputes
the score from the stored counters less the excluded ones; it writes nothing. Under the default, the stored score —
the one retrieval ranks on and injection shows — still counts everything; the exclusion is a read, not a rewrite. It
is only as good as the store's history: an `IExperienceRecordStore` that does not persist `ConfidenceUpdate.Admission`
reads everything back as unrecorded, which `ExcludeHostTrusted` keeps.

### Keeping host-trusted evidence out of the ranked score

A host that wants the opt-out's convenience without letting unverified evidence steer what agents are given sets
`HostTrustedEvidence`:

```csharp
var independence = new ExperienceIndependenceOptions
{
    Verification = IndependenceVerification.TrustHostSuppliedIdentifiers,
    HostTrustedEvidence = HostTrustedEvidenceEffect.RecordedOnly, // default: Counted
};
```

- **`Counted`** (the default, and the earlier behaviour): host-trusted evidence moves the record's counters, its
  stored score (which retrieval ranks and filters on) and, for a contradiction, its status to `Contested`.
- **`RecordedOnly`**: host-trusted evidence is committed exactly as before — a lifecycle event and a ledger row, with
  the same event and evidence IDs, idempotency, assessment spending and `Admission = HostTrusted` — but the event's
  new counters, new score and status equal its prior ones. The record's counters, score, status and `UpdatedAt` stay
  where they were (only its revision moves, as every event's does), so ranking, `MinimumConfidence`, `MaxAge` and
  eligibility are unchanged. The result is `Applied` with `Counted: false`, and the confidence engine is not asked to
  score it. A second submission for an independence key that counted evidence or an earlier recorded-only event
  already holds is a duplicate (a ledger row, no event), as under the default; a recorded-only row claims no key, so
  verified evidence about the same observation still counts.
- **The revision still moves.** A recorded-only event advances the record's revision and appends to its history
  exactly as a counted one does. So a reused session that tracks revisions is given the record again, concurrent
  writers contend on the revision (`StaleRevision`) and the history grows, all as under `Counted`.
- **Verified evidence always counts**, under either setting, and with verification on the setting has no effect.
- **Reading it back.** `ReadConfidenceAsync` with `ExcludeHostTrusted` (or `VerifiedOnly`) reports the stored score.
  With `All` it adds the recorded-only evidence to the stored counters, once per independence key no counted update
  holds, and scores that with the confidence engine — which is the counts and score `Counted` would have stored for
  the same evidence. It reproduces those numbers only: not the `Contested` status or the de-indexing a counted
  contradiction would have applied. `report.HostTrustedRecordedOnly` says how much it added.
- **No backfill.** Switching to `RecordedOnly` does not uncount evidence already counted: what was counted under
  `Counted` stays in the counters, and `ExcludeHostTrusted` is the way to read a score without it.
- **Stores.** A store accepts an event whose evidence moves no counter only when its admission is `HostTrusted` and
  its score and status do not move either; anything else is `Invalid`. PostgreSQL needs migration `0023` (see
  [0023: recorded-only evidence](postgres-schema.md#0023-recorded-only-evidence)): **migrate before setting
  `RecordedOnly`**, because against a database without it every host-trusted submission fails with an
  infrastructure error (`ExperienceStoreException`). An out-of-tree store must accept
  such an event, leave its counters, score and `UpdatedAt` alone, and treat a later recorded-only submission for a key
  already counted or recorded as a duplicate.

## Outcomes

| Submission | Outcome |
| --- | --- |
| First for its independence key | `Applied`, `Counted: true` — counters and score move |
| Same run and round (or reviewer and run) under a **new** evidence ID | `Applied`, `Counted: false` — a ledger row is written and *nothing else* moves: no counters, no status, no revision, no `UpdatedAt`, and no lifecycle event |
| …and the record moved between the read and the commit | `StaleRevision`, `StatusMismatch` or `NotFound`, with nothing stored at all — a duplicate is still committed against the record it describes |
| Host-trusted evidence under `HostTrustedEvidence = RecordedOnly`, first for its key | `Applied`, `Counted: false` — a ledger row and a lifecycle event; the revision moves, and the counters, score, status and `UpdatedAt` do not |
| Same evidence ID, identical content | `Applied` — the original outcome, reported again; nothing is written twice |
| Same evidence ID, different content | `Conflict` — nothing written |
| Two submissions computed from one revision | Exactly one `Applied`; the other `StaleRevision` with the revision to retry against |
| Against a `Candidate`, `Quarantined`, `Stale`, `Superseded`, or `Revoked` record | `Ineligible` — refused before anything is written |
| An unknown or own run, a round finalization did not close, or a missing, invalid, expired, other-record or spent assessment token | `Unverified`, with `Refusal` naming which — nothing written (checked after `Ineligible`) |
| A real run that was never given the record, or given it only at a later revision; or a run known only through a hand-written record (with signing on, also one whose record's signature is missing, under an unknown key, or invalid) | `Unverified`, `Refusal: NotExposed` or `HostWrittenRun` — nothing written, and a token it presented is not spent |
| Against an erased record | `Deleted`, with no ledger row: an erased record's ID must not go back into a table the erasure emptied |

**A record cannot be created claiming evidence it does not have.** `CreateAsync` refuses a record whose
`ReuseConfidence` is not the one its own counters explain — creation is the single moment the two arrive
independently, and after it every change goes through the guarded path above. A record created with *no* counters
may carry any confidence its host wants to seed it with; the first accepted evidence recomputes from those counters,
so a seeded number never survives contact with evidence.

**Core owns the arithmetic; the store owns independence.** Core reads the record, computes the new counters and the
new score from what it read, and submits them with *that* revision, so the arithmetic and the concurrency guard are
about the same version of the record. The store writes those numbers and derives none: what it decides is whether
the independence key was free, and whether the revision still holds. Everything else is a fact it was given.

**Why a duplicate must move nothing.** The two obvious exceptions are the harmful ones. Refreshing `UpdatedAt`
would let one observation, replayed under fresh evidence IDs, keep a record permanently recent for ranking and
permanently un-expired — retrieval reads recency and expiry off that column. Writing the status would contest a
record on the strength of an observation the independence rule had just declared already counted, leaving an event
that says nothing moved beside a ledger with zero counted contradictions.

**One ordering wart, stated rather than hidden.** Core's eligibility gate runs on the record it read, before the
store is asked anything, so it takes precedence over the store's idempotency check: resubmitting evidence that was
already accepted, *after* the record has since been revoked or quarantined, reports `Ineligible` rather than
replaying `Applied`. Nothing is lost — the original update is durable and in the history — but reconcile retries
against the history rather than reading that as "it never landed".

**History makes an update reconstructable.** Each *counted* update's event carries the prior and new score, the
prior and new counters, the evidence ID, the rule version, and the `Actor` — the principal the commit ran under,
recorded by the store from the host's authorization and never from anything the caller put in the event. Read it
through `GetHistoryAsync` like any other transition; `stored.Event.Confidence` is `null` for the events that carried
none. An *uncounted* duplicate has no event, by construction — the ledger row is its audit trail; recorded-only
host-trusted evidence is the one uncounted submission that rides an event. Listing that ledger is not a port
operation; its retention is covered by a record's erasure, which removes every evidence row that named it.

## How the PostgreSQL store enforces it

A `LifecycleEvent` may carry an optional `ConfidenceUpdate`. When it does, the same transaction that appends the
event and updates the projection also writes a row to `confidence_evidence` and sets the record's
`reuse_confidence`, `supporting_validations`, and `contradictions`. Every number in it was computed by Core from the
record Core read (the counters by `ReuseConfidenceHeuristic`, the score by the confidence engine); the store writes
them and derives none. The `RuleVersion` that produced the score travels on the row.

- **Independence is a unique index.** `confidence_evidence.independence_key` is a **generated** column:
  `'machine:' || run_id || ':' || verification_round_id` for machine evidence, `'human:' || reviewer_identity || ':'
  || run_id` for human evidence. A partial unique index on `(experience_id, independence_key) WHERE counted` admits
  the first submission for a key and no other. Generating it here means no writer picks the key *string*; the index
  itself does **not** stop a writer inventing the key's inputs, and there is no foreign key behind `run_id` or
  `verification_round_id` because nothing in this schema knows what a run or a closed round is. Core checks the
  inputs before anything reaches the store (above); a writer that bypasses Core is unchecked here. Core computes the
  same string in `ConfidenceIndependenceKey`, and an integration test pins the two against each other.
- **Exposure and origin travel in the payload.** The run's record must carry the record in its provenance's
  `exposedTo` (record IDs and revisions only, written by finalization from what the capture service recorded) at a
  revision no later than the record's current one, and it must carry `"origin": "Finalized"`. A record written
  without finalization reads back as `ExperienceRecordOrigin.HostWritten` and vouches for no run. Like
  `closedRoundId`, both are optional version-1 payload fields, written only when set, needing no column (so is
  `provenanceSignature`, the [provenance signature](#signing-provenance), with its value in base64); in
  crypto-shredding mode they are sealed with the rest of the payload, and the application role cannot rewrite them
  either way, because it has no `UPDATE` on `payload` — except through `0016`'s sealing function while
  `AllowSealing` is granted, which replaces a plaintext payload with whatever sealed envelope the application
  produces (take `AllowSealing` away once the upgrade is done). The store refuses a record whose
  `Provenance.ExposedTo` names an empty ID, a negative revision, a record twice, or more than `RunExposure.MaxPerRun`
  records.
- **The admission is recorded, append-only.** `confidence_evidence.admission` and
  `lifecycle_events.confidence_admission` (from `0018`) keep `ConfidenceUpdate.Admission` — `Verified` or
  `HostTrusted`, `NULL` on rows written before `0018` — exactly as Core gave it, and read it back on a replay and in
  history. It is not part of a replay's content comparison: resubmitting evidence reports the admission the original
  was stored with. Neither ledger grants `UPDATE`, and `0007`'s triggers refuse one, so host-trusted evidence cannot
  be relabelled after the fact.
- **An assessment is spent once per record.** `confidence_evidence.assessment_id` (from `0015`) records the
  assessment token a human submission presented, and a unique index on `(experience_id, assessment_id) WHERE
  assessment_id IS NOT NULL` — not partial on `counted` — lets one assessment land one piece of evidence per record.
  Another evidence ID presenting it is refused with nothing written: `Conflict`, with an error on
  `ConfidenceUpdate.AssessmentIdPath`, which Core reports as `Unverified`/`AssessmentTokenReplayed`. A resubmission
  of the *same* evidence ID is still a replay: because PostgreSQL does not promise which unique index a statement
  that violates several reports first, the store answers an assessment violation by looking for the evidence ID and
  compares the stored row exactly as the primary-key path does. The counted human event carries the same ID in
  `lifecycle_events.confidence_assessment_id`, surfaced as `ConfidenceUpdate.AssessmentId`.
- **A duplicate is recorded, and changes nothing else.** The first insert claims the key with `counted = true`,
  under a savepoint, because losing that race is an expected outcome the commit has to survive — a unique violation
  would otherwise abort the transaction that is supposed to record the duplicate. On the violation the statement is
  undone, the record is re-read `FOR UPDATE` (so the usual `NotFound`/`StaleRevision`/`StatusMismatch` refusals
  still apply), and the same submission is written again with `counted = false`. That ledger row is *all* the call
  writes. An event is impossible as well as unwanted, since it must claim `expected_revision + 1`.
  `result.AppliedConfidence` reports what was stored and its `Counted` says which happened, while `result.Revision`
  and `result.CurrentStatus` report the record the call left untouched.
- **The counters are guarded like the rest of the projection.** Migration `0007` extends the `experience_records`
  trigger so `reuse_confidence`, `supporting_validations`, and `contradictions` move only together with the revision
  of the lifecycle event that recorded the evidence for them — and only to the values that event recorded, so
  `UPDATE … SET reuse_confidence = 1, revision = revision + 1` is refused too, with SQLSTATE `42501`. See
  [Lifecycle](lifecycle.md#append-only-enforced-by-the-database) for what such a guard does and does not bind.
  `confidence_evidence` is append-only for the same reason the event logs are: a row that could be edited or
  removed would free an independence key, and the same observation could then be counted twice.

`EvidenceId` is a second idempotency key alongside `EventId`. Resubmitting it with identical content — the same
record, kind, source, run and round or reviewer, assessment, rule version, and detail — reports the original outcome
and the revision that commit produced, and writes nothing. Resubmitting it with different content is `Conflict` with
nothing written. The counters are deliberately *not* compared: they are derived from whatever the record held when
the submission was first made, so comparing them would report a genuine replay as a conflict for agreeing with
itself. The event ID *is* compared for a stored row that produced one, so a retry under a fresh event ID is a
`Conflict` rather than a `Committed` carrying a lifecycle event that was never written. The lookup joins
`experience_records` and applies the exact scope predicate, so a guessed evidence ID from another scope reads back as
no row at all — the primary key is global, and this is the one statement that finds a row by it alone. Every number
a replay reports comes from that ledger row, so the answer describes one moment rather than a stored revision beside
a freshly read status.

Every commit also records `lifecycle_events.actor` — the host's `AuthorizationContext.PrincipalId`, taken from the
authorization context and never from anything on the event, and surfaced as `StoredLifecycleEvent.Actor`. For human
evidence the same principal is the reviewer identity, which is what makes "one reviewer, one vote per run"
enforceable at all.

Ordering inside the transaction is not incidental: the evidence goes in **before** the event, because whether its
key was free decides which numbers the event must record, and an event is append-only the moment it is written.

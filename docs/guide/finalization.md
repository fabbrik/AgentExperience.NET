# Finalization: turning a run into a record

**In short.** A captured run becomes a durable Experience Record through one call,
`ExperienceFinalizationService.FinalizeAsync`. It checks the run against the checks you declared, using only evidence
from the verification round you closed; asks whether the caller may store it and whether your storage policy allows
it; draws a lesson; and stores the record. A verified run becomes `Validated` and reusable. A run that failed
verification is stored as `Quarantined`, never reusable. The call never throws for an expected outcome, and retrying
it is always safe: the same run can never produce two records.

Package: `AgentExperience.Core`. The MAF adapter can call it for you after each invocation
([below](#finalizing-from-the-maf-adapter)).

## The call

`FinalizeAsync` runs six stages in order — load the captured snapshot, evaluate it, check authorization and the
host's storage decision, reflect on it, create the record, commit its initial lifecycle event — and stops at the
first stage that ends the call, always returning a structured result rather than throwing. The two gates precede
reflection on purpose: the reflector is the seam a host would plug a model into, so a run that is about to be refused
is never handed to it.

```csharp
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Verification;

var result = await finalization.FinalizeAsync(
    new FinalizeExperienceRequest(
        RunId: runId,
        Authorization: authorization,                         // host-established; the run's scope must lie inside it
        ClosedRound: new ClosedVerificationRound(roundId, "rev-7"),
        RequiredChecks: [new RequiredCheck("unit-tests-pass", ExpectedKind: "TestResult")],
        Evidence: evidence,                                   // finalization filters and aggregates it itself
        CurrentArtifactRevision: "rev-7",
        StorageDecision: StorageDecision.Permit,              // or StorageDecision.Deny("retention policy")
        FinalizedAt: DateTimeOffset.UtcNow),
    cancellationToken);

if (result.IsDurable)
{
    logger.LogInformation("Experience {Id} is {Status} at revision {Revision}",
        result.ExperienceId, result.Status, result.Revision);
}
else
{
    logger.LogWarning("Finalization ended at {Stage}: {Outcome} — {Reason}",
        result.Stage, result.Outcome, result.Failure?.Reason);
}
```

| Outcome | When | What was written |
| --- | --- | --- |
| `Validated` | Verified, reflection succeeded, storage permitted | The record (reuse confidence 2/3, one supporting validation, no contradictions), created as `Candidate`, plus the initial event that moved it to `Validated` |
| `Quarantined` | Storage permitted, but verification did not pass, or the reflector threw, returned nothing, returned a reflection that does not match its request, or returned one [screening](#screening-reflections) refused | The record, with **no** reflection, created as `Candidate`, plus the initial event that moved it to `Quarantined`. `Failure` names the stage that decided it |
| `AlreadyFinalized` | This run's record already exists *and* is already confirmed | Nothing. The result reports the stored record, status, and revision. (A record left unconfirmed by an earlier call is resumed instead: the retry commits its initial event and returns `Validated`/`Quarantined`.) |
| `StorageDenied` | The host's `StorageDecision` denied | Nothing at all, and no record ID is issued |
| `NotAuthorized` | The run's scope lies outside the authorization | Nothing; denied before any store call |
| `RunNotFound` / `RunNotFinished` | No such captured run, or it has no execution status | Nothing |
| `Failed` | A stage failed (for example the database was unavailable) | Never reported as durable. Any record already created stays a `Candidate`, which is never reusable, and the captured run stays available for a retry |

## Why retrying is safe

Three properties make retrying safe. The record is *created* as a `Candidate` and its initial lifecycle event
performs the real transition, so a commit that never lands leaves nothing reusable behind. The record ID, the
reflection ID, and the initial event ID are all derived from the run ID (and the record ID from the scope too), so a
second call cannot create a second record or a second initial confirmation. And the initial event's fields are a pure
function of the stored record, so a retry re-derives exactly the event the store already deduplicates on.

Finalization never re-sanitizes captured content — capture already rejected anything unsafe (see
[Sanitization happens at capture](capture.md#sanitization-happens-at-capture)) — but it does screen the new text a
reflector wrote ([below](#screening-reflections)). It never decides storage or risk policy on the host's behalf:
`StorageDecision` travels in the request and Core simply obeys it.

A record whose run was later erased can never be finalized again: the derived ID collides with the tombstone (see
[Deletion and retention](deletion-and-retention.md#a-record-whose-run-was-erased-can-never-be-finalized-again)).

If an indexing hook is registered, one more thing happens *after* those six stages: the committed record is embedded
and its vector stored. That step is outside the canonical write and can never change the outcome above — see
[Indexing](indexing.md).

## Verifying a run, and binding its evaluation

Every required check names the evidence kind that satisfies it, and matching is default-deny: evidence of any other
kind is ignored, so a check it was not meant for stays `Unknown` rather than passing. A check that really should
accept any producer says so explicitly with `RequiredCheck.AnyKind` (`"*"`); a null or blank kind is an
`ArgumentException` from `VerificationAggregator.Aggregate` (and so from finalization), never an implied wildcard,
and `RequiredCheck.Accepts` returns `false` for it. The kinds the built-in
`TaskCheckEvaluators` produce are `ToolExitCode`, `TestResult`, `WorkflowCompletion`, `HumanApproval` and
`HumanCorrection`.

```csharp
RequiredChecks: [
    new RequiredCheck("unit-tests-pass", ExpectedKind: "TestResult"),
    new RequiredCheck("reviewed", ExpectedKind: RequiredCheck.AnyKind),   // explicit, visible opt-in
]
```

Verification is deterministic and uses no model. Aggregation resolves each required check independently, against only
the evidence carrying its `CheckId` and belonging to the closed round and the current artifact revision. An empty set
of required checks can never verify, and no closed round evaluates to `Unknown`.

An evaluation is bound to the run it was computed for. `VerificationAggregator.Aggregate(runId, ...)` is the only
way to get a `VerificationResult` (it has no public constructor, so it cannot be deserialized), and its `Basis`
records the run ID, the closed round, the artifact revision and a copy of the required checks. A `ReflectionRequest`
refuses an evaluation whose basis names another run, or a run whose own recorded outcome disagrees with it, with a
`ReflectionBindingException` (an `ArgumentException`); its `Run` and `Evaluation` are get-only. So every
`IExperienceReflector`, the default one or a host's, only ever receives a run together with its own evaluation. On
the way back, `ReflectionRequest.EnsureMatches` checks that a reflection carries its request's identity and copies its
evaluation's verdict, score, rule version and evidence IDs; finalization applies it to every reflection and
quarantines a record whose reflection fails it, exactly as if the reflector had thrown. A host that calls a reflector
directly can call it too.

What the binding cannot do: it is exactly as strong as the run ID. Evidence carries no run ID, so the library cannot
tell whether the round and evidence a host aggregated under a run ID really belong to that run, and a host that
re-stamps another run with `run with { RunId = ... }` is refused only when that run's own recorded outcome
contradicts the evaluation. That remains the host's statement (the KL-11 boundary in
[Known limits and documented boundaries](../known-limits.md#documented-boundaries)): finalization binds the capture
service's own run and records the round it closed, which is what confidence evidence is later checked against, but a
direct caller of `Aggregate` names its own run ID. A host that calls a reflector and then writes records without
finalization is writing records itself, and nothing but its own call to `EnsureMatches` checks that path.

That path does not reach confidence independence by default. Finalization marks the records it writes
`ExperienceRecordOrigin.Finalized`; a record written any other way is `HostWritten` (the default), and a run known
only through one is refused as `IndependenceRefusal.HostWrittenRun`. So a direct aggregator result stored by hand
vouches for no run, round or exposure unless the host marks it `Finalized` itself — which is then the host's
statement, and part of what the KL-11 boundary says. See [Confidence and independence](confidence.md). With
[provenance signing](#signing-provenance) on, marking it is no longer enough.

## Screening reflections

Captured content is sanitized at capture, but a reflection is new text: a host reflector, or a future model-backed
one, can write anything into a lesson, the approaches, the preconditions, the warnings and the reuse guidance, and
that text later reaches other agents through injection. So after a reflection passes the binding check, and before
the record is created, finalization screens those six free-text fields, and `Producer`, in two layers. Both run for
every reflector, the default one included.

1. **Built-in hygiene.**
   - Limits, from `ReflectionLimits`: 4,000 characters for the lesson (and for the reuse guidance), 1,000 for each
     list item, 32 items per list, and 200 for `Producer`. Lengths are UTF-16 code units, counted after invisible
     characters are removed; list counts are counted as the reflector returned them. Over-limit text is refused,
     never truncated: cutting a lesson short silently changes what it says.
   - Invisible characters are removed: control, format, private-use and unassigned code points, lone surrogates,
     variation selectors, the combining grapheme joiner, Hangul fillers and the blank braille pattern. A whitespace
     one, and the line and paragraph separators, become a space. A run of more than four combining marks is cut to
     four. It is the same rule the Historical Reference writer applies to tool names, and a test holds the two to it
     code point by code point.
   - A field that is empty or blank afterwards counts as absent: the list item is dropped, the reuse guidance becomes
     `null`, and a reflection with no lesson or no producer left is refused.
2. **Your sanitizer.** The six fields go through the registered `ISanitizer` as one payload of kind
   `ExperienceReflection` (`ReflectionScreening.PayloadKind`): `Lesson` and `ReuseGuidance` as strings, the four
   lists as lists of strings, within `ReflectionLimits.SanitizerTimeout` (5 s by default). A rejection, a timeout, a
   throw, a cancellation of its own, a result that is neither an allowed payload of text nor a rejection, or an
   omitted field or item (listed in `OmittedFieldPaths`, or missing from `Fields`) refuses the reflection: nothing is
   ever stored silently empty. What it returns goes through the hygiene layer again, so a redaction cannot push a
   field over its limit. Extra keys it adds are ignored.

A refused reflection quarantines the record with a `FinalizationFailure` at stage `Reflect`, exactly as a binding
mismatch does, and `FinalizationFailure.ScreeningRefusal` says why as a closed set (`ReflectionScreeningRefusal`:
`OverLimit`, `MissingLesson`, `MissingProducer`, `MissingField`, `Unreadable`, `SanitizerRejected`,
`SanitizerFailed`, `SanitizerTimedOut`, `FieldOmitted`), which the `finalize` span also carries as
`agentexperience.reflection.screening_refusal`. The reason names the field, the reflector's own index and the limit,
never the text, and never repeats your sanitizer's reason (which may quote what it rejected). A sanitizer or a
reflector list that throws is recorded by type only, in `FinalizationFailure.ExceptionType`; the exception itself is
withheld, since its message may quote the reflection. The caller's own cancellation still propagates.

Redactions are kept, and `FinalizeExperienceResult.ReflectionRedactedFieldPaths` lists the redacted fields' paths as
indexes into the stored lists (`Lesson`, `Warnings[1]`), never their values. A redacted item that was then blank and
dropped is not listed, and a path your sanitizer reports that does not name a screened field is dropped rather than
reported, since it could carry the value. The bound fields — the identifiers, the verdict, the score, the rule
version, the evidence IDs and `CreatedAt` — are never rewritten. No reflection text reaches telemetry.

**Adding the kind to your sanitizer.** A host `ISanitizer` must allow the `ExperienceReflection` kind, or every
reflection is refused (`SanitizerRejected`) and every record quarantined. With `DefaultSanitizer`, nothing is needed:
an unconfigured `ExperienceReflection` kind gets `ReflectionScreening.DefaultSanitizationPolicy`, which allows exactly
the six fields, bounded by the default limits, and changes nothing. To redact, configure a policy for the kind; with
your own sanitizer, handle the kind and return the fields you were given:

```csharp
// DefaultSanitizer: redact the whole reuse guidance, starting from the built-in policy.
policies[ReflectionScreening.PayloadKind] = ReflectionScreening.DefaultSanitizationPolicy with
{
    SecretFieldNames = new HashSet<string>(StringComparer.Ordinal) { "ReuseGuidance" },
};

// Your own ISanitizer: allow the kind, applying your pattern rules to its strings.
if (payload.Kind == ReflectionScreening.PayloadKind)
{
    return Task.FromResult(RedactPatternsIn(payload)); // same keys back; list fields as lists of strings
}

// Different limits, anywhere before the finalization service is first resolved.
services.AddSingleton(new ExperienceFinalizationOptions
{
    ReflectionLimits = ReflectionLimits.Default with { MaxListItems = 48, SanitizerTimeout = TimeSpan.FromSeconds(2) },
});
```

Pitfalls:

- **Raised limits need a matching policy.** The built-in policy is bounded by the *default* limits. If you raise
  `ReflectionLimits` and screen through a `DefaultSanitizer`, configure
  `ReflectionScreening.SanitizationPolicyFor(yourLimits)` for the kind, or reflections the raised limits allow are
  rejected. (A finalization service built without a sanitizer does this for you.)
- **`MaxDepth` below 2 rejects every reflection**, because the lists are one level down.
- **A whole-value redaction of `Lesson` by the default `ErasingRedactor` quarantines everything**: it leaves an empty
  lesson, which counts as absent. Redact the lesson with a `Redactor` that leaves a marker, or with pattern rules. A
  list redacted whole comes back as one string, which is stored as a one-item list (or none, if it is empty).
- **Your pattern rules see text that is not Unicode-normalized.** Screening removes invisible characters before your
  sanitizer runs, but full-width letters, compatibility forms and confusables are left as written. Fold inside your
  sanitizer (for example NFKC plus confusable folding) before matching secrets or markers.

`AddAgentExperienceCore` passes the registered `ISanitizer` and any registered `ExperienceFinalizationOptions`; a
service built by hand takes both through the constructor overload with `reflectionSanitizer` and `options`, and every
other overload screens through a `DefaultSanitizer` bounded by the service's own limits. A retry reflects and screens
again before the store's conflict shows the record already exists; the redacted paths are reported, never persisted,
so a replay reports none.

The default reflector bounds its own output to `ReflectionLimits.Default` so a large run still validates: it quotes
at most 500 characters of any captured text, cuts the lesson, the reuse guidance and each list item to their limits,
and keeps a list to 32 items with the last saying how many more are not listed. Each cut ends in an ellipsis and never
falls inside a surrogate pair. A host that lowers the limits below the defaults can see its output refused. Its output
otherwise passes unchanged, except that an invisible character captured into a quoted error or an environment value
is removed like any other.

What screening cannot do: it detects neither prompt injection nor an arbitrary secret in free text. The hygiene layer
removes what a reader cannot see and bounds what it can; anything beyond that is your sanitizer's policy.

## Signing provenance

Signing is opt-in. With an `ExperienceProvenanceSigningOptions` registered (or passed to the lifecycle service
finalization is built over), every record `FinalizeAsync` creates carries
`ExperienceRecord.ProvenanceSignature`: an HMAC-SHA256, under the ring's `CurrentKeyId`, over the record's
finalization claims (its ID, scope, source run, closed round, origin and exposures), written in the same create as the
record. Confidence verification then refuses a run whose record is unsigned, signed under a key that is not in the
ring, or changed in any signed claim after it was signed. So a record written through `CreateAsync`, or a payload
edited outside the library, cannot pass as finalized. Without the options, nothing is signed and nothing changes.

Finalization signs with the lifecycle service's ring by default. Options passed to finalization's own constructor take
precedence, and are refused (`ArgumentException`) unless the lifecycle service it is built over checks their current
key under the same ID: a record signed under a key the checker lacks would vouch for nothing. With signing on, a retry
whose run already has a stored record that does not carry a valid signature ends `Failed` at the create stage,
saying so, instead of replaying it as `AlreadyFinalized`. A run whose scope has no strict UTF-8 encoding (a lone
surrogate) cannot be signed and also ends `Failed`, with nothing stored.

```csharp
services.AddSingleton(new ExperienceProvenanceSigningOptions(
    new Dictionary<string, byte[]> { ["prov-2026-09"] = secrets.ProvenanceSigningKey },
    currentKeyId: "prov-2026-09"));
```

Key management:

- **Generate** each key as at least 32 random bytes (for example `RandomNumberGenerator.GetBytes(32)`), once, and keep
  it in your secret store. Never store it in the database the records live in, never in configuration an agent can
  read, and never where agent tooling runs. The record stores only the key ID.
- **One purpose, one environment.** A provenance key is never the assessment token key (a ring holding it is refused),
  and staging and production never share a ring.
- **Rotate** by adding the new key to the ring and switching `CurrentKeyId` to it. Keep the old key in the ring for as
  long as records it signed should keep vouching. Removing a key makes those records read as signed by an unknown
  key, so they vouch for nothing.
- **Switch on** by listing, once, the records finalized before signing with
  `ExperienceProvenanceSigningCutover.ListUnsignedFinalizedRecordIdsAsync`, storing the IDs in your configuration, and
  passing them as `TrustUnsignedRecordIds`, if those records must keep vouching. The set accepts only records with no
  signature, and no record created later can join it, whatever creation time it claims.
- **Know what it proves.** A valid signature proves the claims are the ones finalization signed under your key. It
  does not prove the host's own bookkeeping (the round it closed, the exposures it recorded) was honest, and anyone
  holding a key can sign. See [Signing provenance](confidence.md#signing-provenance) for the verification side and
  the KL-11 boundary in [Known limits and documented boundaries](../known-limits.md#documented-boundaries).

## Finalizing from the MAF adapter

Capture alone keeps the run in memory. To turn each invocation into a durable Experience Record, give the adapter
Core's finalization service and a resolver that supplies what only the host knows — the required checks, the
verification evidence and the round it was closed in, the authorization context, and the storage decision:

```csharp
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Verification;

var options = new ExperienceCaptureOptions
{
    ResolveRun = context => new ExperienceRunDescriptor("triage-ticket", hostScope),

    FinalizationService = finalization,          // AgentExperience.Core.Finalization.ExperienceFinalizationService
    ResolveFinalization = context => new FinalizeExperienceRequest(
        RunId: context.Run.RunId,
        Authorization: hostAuthorization,
        ClosedRound: new ClosedVerificationRound(roundId, artifactRevision),
        RequiredChecks: [new RequiredCheck("unit-tests-pass", ExpectedKind: "TestResult")],
        Evidence: evidenceFor(context.Run),
        CurrentArtifactRevision: artifactRevision,
        StorageDecision: StorageDecision.Permit,
        FinalizedAt: DateTimeOffset.UtcNow),

    OnRunFinalized = result => logger.LogInformation(
        "Experience {Id} is {Status}", result.ExperienceId, result.Status),
};
```

- **When it runs.** Immediately after the run's attempt and completion were both recorded, inside the same once-only
  step and the same `FinalizationTimeout`. A run whose capture reported a problem is never finalized, so a
  half-captured run is never persisted as if it were whole. A run kept open for a further attempt is finalized only
  when it completes.
- **Opting out per run.** `ResolveFinalization` returning `null` skips that run, and is not a failure.
- **Failures.** A throwing resolver, a throwing `FinalizeAsync`, or a non-durable outcome (`StorageDenied`,
  `NotAuthorized`, `Failed`, …) is reported through `OnCaptureFailure` with stage `Finalization` and never thrown.
  The captured run is left untouched, so the host can retry finalization itself from the capture service.
- **Latency.** Finalization is a database round trip and is awaited inside `FinalizationTimeout` (5 s by default), so
  it adds caller-visible latency. Leave `FinalizationService` unset and finalize out of band if that is not
  acceptable.
- **Setting `FinalizationService` without `ResolveFinalization` throws** at `UseExperienceCapture`, rather than
  silently doing nothing.

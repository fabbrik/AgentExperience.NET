# Limits history

This page records how the known limits of earlier previews were resolved or narrowed. It is history: the current
state is in [Known limits and documented boundaries](known-limits.md), and the per-release detail, including every
breaking change, is in the [changelog](../CHANGELOG.md).

A narrowed limit keeps its row and its number; it now lives in the Documented boundaries table.

## Resolved or narrowed in `0.1.0-preview.2`

`0.1.0-preview.1` shipped with sixteen known limits. `0.1.0-preview.2` resolved thirteen of them and narrowed the
other three (KL-2, KL-11 and KL-12) as far as they go. What is left of those three is inherent, so they moved to the
Documented boundaries table. The Known limits table is now empty.

- **KL-1** (serial round trips on the re-index path and on the invocation's critical path) is resolved.
  `IExperienceEmbeddingGenerator` gained `GenerateBatchAsync`, and a re-index pass embeds the records that need a
  vector `ReindexExperienceRequest.EmbeddingBatchSize` (default 16, at most 128) per provider call, with each record
  still written on its own, conditionally on its own revision. `IExperienceRecordStore` gained `GetManyAsync`, which
  the PostgreSQL store answers with one statement applying exactly `GetAsync`'s scope, grant, disclosure and tombstone
  rules and one audit append; injection's final eligibility check is now that one call. Measured in the tests: eight
  candidates cost one read and one access-row append where they cost twelve commands, and forty records cost three
  provider calls where they cost forty. Both methods have a default implementation that does what the library did
  before, one call per item, so an out-of-tree store or generator keeps compiling and keeps its behaviour; it simply
  saves no round trips until it overrides them. See
  [the final eligibility check](guide/injection.md#limits-and-the-final-eligibility-check) and
  [Indexing](guide/indexing.md).
- **KL-2** (erasure reaching only this database's live rows) is **narrowed, not closed**, by crypto-shredding. With
  an `ExperienceEncryption` over an `IExperienceKeyStore`, every free-text column erasure removes — the record
  payload and task ID, lifecycle reasons and evidence detail, grant reasons, and reuse-feedback rationale — is
  stored as AES-256-GCM ciphertext under a per-record data key held outside the database, bound by its associated
  data to its record, row, column and scope; erasure destroys the key inside the erasure's transaction, so a
  `pg_dump`, a base backup, a replica, the WAL and the dead heap tuple all hold only ciphertext nothing can open. A
  record never looks erased while its key survives, and never looks live once its key is gone. Core ships an
  envelope key store (`EnvelopeExperienceKeyStore`, with KEK rotation and re-wrap) so a host plugs its KMS in
  through two small ports; the library takes no new dependency. Existing records are sealed by a bounded, resumable,
  authorized upgrade job, `SealPlaintextRecordsAsync`. What its row in Documented boundaries says is exactly what
  remains. See [Crypto-shredding](guide/crypto-shredding.md).
- **KL-3** (a retention sweep matching one scope exactly) is resolved by `ScopeMatch.Subtree`.
  `SweepExpiredAsync(auth, scope, age, batch, ScopeMatch.Subtree, ct)` sweeps the scope and every scope beneath it,
  bounded, one record per transaction, with `MoreRemain` true across the whole subtree. "Beneath" means the same
  tenant, application and project, and each team, agent or user field either left null on the root or equal to it,
  which is the reading `AuthorizationContext` already gives a null bound, so authorizing the root authorizes the
  subtree. The five-argument overload is unchanged and still exact. A subtree never spans projects: a host with
  several sweeps each project root, a list it configures rather than one it has to discover. See
  [A sweep reaches one scope, or everything beneath it](guide/deletion-and-retention.md#a-sweep-reaches-one-scope-or-everything-beneath-it-and-you-choose-which).
- **KL-4** (the purge path being auditability, not a privilege boundary) is resolved by the two-role deployment,
  which is now the documented and supported one. An owner role owns the schema and runs the migrators; the
  application role is given exactly what the stores need by
  `ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync`, run on every deploy: no ownership, so no
  `ALTER TABLE`, `DISABLE TRIGGER` or replaced guard function; no `DELETE`, `TRUNCATE` or `UPDATE` on any ledger, so a
  purge marker it sets by hand admits nothing; `UPDATE` on `experience_records` and `experience_grants` only on the
  columns the store moves, so it cannot write a tombstone by hand either; and `EXECUTE` on the purge functions only
  when the host opts in. The call refuses a superuser, the owner itself, and any member of an owning role, and
  verifies the role's effective privileges before it commits. The tests prove each refusal from the application
  role's own connection, and every store test now runs as that role. **What remains is inherent in PostgreSQL:** the
  owner role and superusers are not bound by any of it — they can disable a trigger, replace a function, or bypass
  privileges altogether — so keep the owner's credentials out of the application. A deployment that runs the
  application as the owner (the single-role shape, still fine for local development) gets none of this. `0013` also
  pins `search_path` with `pg_temp` last on every purge and guard function. See
  [Deploying with two roles](guide/deployment.md#deploying-with-two-roles).
- **KL-5** (default-deny on evidence kind opt-in per check) is resolved. `RequiredCheck`'s `ExpectedKind` is now
  required; accepting any kind is spelled `RequiredCheck.AnyKind` (`"*"`), and a null or blank kind is refused by the
  aggregator rather than read as "any". **Breaking:** `new RequiredCheck("id")` no longer compiles. See
  [Verifying a run](guide/finalization.md#verifying-a-run-and-binding-its-evaluation).
- **KL-6** (a reflection pairable with the wrong evaluation by a direct caller) is resolved. An evaluation now
  records the run, round, revision and checks it was computed from, only `VerificationAggregator.Aggregate` can make
  one, and a `ReflectionRequest` cannot be constructed from an evaluation computed for another run
  (`ReflectionBindingException`). Finalization also checks every reflection a reflector returns against its request
  and quarantines one that does not match. **Breaking:** `Aggregate` takes the run ID first, `VerificationResult` has
  no public constructor (so it can no longer be deserialized), a request's `Run` and `Evaluation` cannot be replaced
  with `with`, and a run whose own outcome disagrees with the evaluation now fails at request construction rather
  than inside the default reflector. The binding is only as strong as the run ID a host supplies, which is part of
  KL-11; see [Verifying a run](guide/finalization.md#verifying-a-run-and-binding-its-evaluation).
- **KL-7** (an invocation that opens a run and never returns holding it with no bound) is resolved by arming the
  open-run duration bound when the run is opened, for every run, with the timer disposed when the invocation
  releases the run. An invocation that never returns now holds its run for at most twice `MaxOpenRunDuration`, and
  the close is reported. See [Retries as attempts of one run](guide/capture.md#retries-as-attempts-of-one-run).
- **KL-8** (an approach being its tool names only) is resolved in two steps. First, a host could allowlist, per
  tool, the argument keys whose sanitized scalar values the `Approach:` line shows
  (`ExperienceInjectionOptions.ApproachArguments`). The second step closed what that left — values only for
  top-level scalars, and never for a borrowed record. An allowlisted key may now be a dotted path — `options.mode`,
  or `targets.0` for an array element — and only the scalar it ends on is shown, under every bound the first step
  set (sanitized stored values only, the 64-character clamp, quote and `->` neutralization, the invisible-character
  strip, the 512-character line cap and the byte budget); a path that ends on an object or an array still shows the
  marker, and a container is never shown whole. A borrowed record shows argument values through a new, immutable
  third disclosure level, `ExperienceGrantDisclosure.LessonApproachAndArguments`: the owner names on the grant the
  keys it consents to show (`ExperienceGrantRequest.ApproachArguments`, stored on the grant by `0017`), and the
  recipient's model is shown a key only when the recipient's own `ApproachArguments` names it for the same tool as
  well — the intersection, so neither side can widen what the other allowed. `LessonOnly` still withholds the whole
  `Approach:` line and `LessonAndApproach` is still tool names only. Existing grants keep their level; a borrowed
  record's arguments appear only after the owner revokes and reissues at the new level. See
  [Showing selected argument values](guide/injection.md#showing-selected-argument-values).
- **KL-9** (a borrowed lesson disclosing the lending scope's tool names) is resolved by the grant disclosure level;
  see [Sharing and grants](guide/sharing.md).
- **KL-10** (no retention path for the grant access log) is resolved by `0012` and
  `PostgresExperienceGrantAccessLog.PurgeOlderThanAsync`: a bounded, administrator-authorized purge of access rows
  the database recorded before a host-given cutoff, within an owner scope or its subtree, through a
  `SECURITY DEFINER` function whose `EXECUTE` is revoked from `PUBLIC`. It never removes a row younger than 30 days,
  by the database's clock: a later cutoff is refused rather than clamped, and the append-only guard re-checks every
  row. Erasing a record still keeps its access rows. It is the `grant.access.purge` telemetry operation. See
  [Retention for the grant access log](guide/deletion-and-retention.md#retention-for-the-grant-access-log).
- **KL-11** (confidence independence trusting host-supplied identifiers) is **narrowed, not closed**, in two steps.
  The Documented boundaries row states only what remains.
  - *Verified identifiers.* By default `ApplyEvidenceAsync`, and every attributed feedback submission, refuses an
    independence key whose inputs the library cannot vouch for, with `ConfidenceUpdateOutcome.Unverified` and an
    `IndependenceRefusal`: a run that is not finalized into a record in the evidence's scope nor held by the capture
    service (`UnknownRun`), the record's own run (`OwnRun`, in every mode), a machine round other than the one
    finalization closed for that run (`UnknownRound`; finalization now stamps `ExperienceRecord.ClosedRoundId`), and a
    human assessment without a valid assessment token. `AssessmentTokenIssuer` mints the token under a host-held key
    (`ExperienceIndependenceOptions.AssessmentTokenKey`); it is HMAC-SHA256, bound to the scope, run, reviewer,
    direction and records, compared in constant time, expires (a day by default), and is spent once per record by
    `0015`'s unique index, atomically with the evidence. A forged run, round or token, an expired or replayed token,
    and a token for another scope or record are all refused with nothing written. **Breaking:** evidence the library
    cannot verify is refused by default, including machine evidence about runs finalized before this version (their
    records carry no closed round); evidence naming the record's own run is refused in every mode; a human
    assessment without a token is recorded with benefit `Unknown`; `ConfidenceUpdateOutcome` gains `Unverified`;
    three records gain a trailing optional parameter; and a store implementation must persist `ClosedRoundId` and
    spend `AssessmentId` once per record. `IndependenceVerification.TrustHostSuppliedIdentifiers` restores the
    previous behaviour for a host that cannot adopt the new flow.
  - *Exposure-bound evidence.* The MAF adapter's context provider now records, on the captured run, which records it
    injected into the invocation and at which revision, as `Provenance.ExposedTo` (identifiers and revisions only);
    finalization copies that onto the run's record and marks the record `ExperienceRecordOrigin.Finalized`.
    Confidence evidence and attributed feedback are then refused unless the run was exposed to the record at or
    before the revision the evidence is computed against (`IndependenceRefusal.NotExposed`), so a caller choosing
    among real runs gets one key per run that was given the lesson rather than one per real run. A run known only
    through a record written by hand is refused (`HostWrittenRun`). Every piece of evidence now records which mode
    admitted it (`ConfidenceUpdate.Admission`: `Verified` or `HostTrusted`, migration `0018`), confidence spans carry
    it (`agentexperience.confidence.admission`, and `agentexperience.independence.refusal` on a refusal), and
    `ExperienceLifecycleService.ReadConfidenceAsync` reads a score without host-trusted evidence, or with only
    verified evidence. **Breaking:** evidence about a run with no recorded exposure is refused by default — including
    every run finalized before this version and every run captured without the adapter's context provider (call
    `RecordExposure` from the code that delivers records, or opt out); records stored before this version read back
    as `HostWritten` and vouch for no run; `StartRun` refuses a provenance that already carries exposures;
    `IndependenceRefusal` and `ExperienceCaptureFailureStage` gain members; and `IExperienceCaptureService` gains
    `RecordExposure` (with a default implementation that records nothing).

  See [Confidence and independence](guide/confidence.md).
- **KL-12** (injected blocks accumulating in a reused session, and a delivered block that cannot be retracted) is
  **narrowed, not closed**, by session tracking, on by default; the Documented boundaries row states what is left.
  With a session supplied, `ExperienceContextProvider` keeps an account in the session's `StateBag` (record IDs,
  revisions and counters, never content), omits a revision the session already holds (`AlreadyDelivered`), bounds a
  session to 32 record deliveries and 64 KB of Historical Reference across its invocations
  (`ExperienceInjectionOptions.SessionLimits`), and re-checks every record the session holds on every invocation. A
  record that has been erased, revoked, superseded, quarantined or contested, has lost its grant, fallen below the
  confidence floor or past `MaxAge`, or is now read through a grant that withholds what the session was shown, gets
  one fixed withdrawal notice, ahead of any new record, carrying no reason and no content. A host whose chat history
  drops injected blocks sets `SessionLimits = null`, which restores the previous behaviour exactly. See
  [Reused sessions](guide/injection.md#reused-sessions-a-budget-no-repeats-and-withdrawal-notices).
- **KL-13** (the supported matrix stopping short of a newer MAF, `net8.0` and PostgreSQL 14) is resolved, with one
  boundary left on purpose. **MAF** is no longer pinned exactly: the adapter declares `Microsoft.Agents.AI`
  `[1.22.0, 2.0.0)`, so a host needing a newer 1.x resolves it with no warning. CI tests the floor and the newest 1.x
  on every change, and both gate pushes and the weekly schedule (reporting only on pull requests), so a MAF minor
  that breaks the adapter fails `main` on the next push or weekly run. The bound sits at 2.0 because MAF states no
  SemVer promise and changed caller-visible behaviour five times between 1.15 and 1.22 (one on a hook the adapter
  uses, which its tests caught); a host on a MAF 2.x, which does not exist yet, gets NU1608 about this adapter.
  **`net8.0`** is a third target framework of all five packages, tested like the other two: on `net8.0` only, Core
  and the store take `System.Text.Json` 10.0.12+ and Core `Microsoft.Bcl.Memory` 10.0.12+, the .NET 10 train's
  packages for three of the four .NET 9 APIs the library uses (the fourth is a one-line `#if`), so every framework
  runs the same decoder and token codec. **PostgreSQL 14 stays out**, and that is the boundary: DbUp journals scripts
  by name, `0005` is PostgreSQL 15 syntax, and no path to 14 leaves `0005` untouched without a second schema lineage
  (a PostgreSQL 14 database upgraded in place would keep it for life); PostgreSQL 14 itself reaches end of life on
  12 November 2026. The row left the table because what remains is not a limit of this library but an upstream
  end-of-life date, and a major bound on a dependency that has no next major yet; neither restricts a host on a
  supported PostgreSQL or any MAF release that exists. `net8.0` and `net9.0` also leave support on 10 November 2026,
  and the first preview published after that date drops them. See
  [Compatibility evidence](compatibility-evidence.md#supported-matrix).
- **KL-14** (exact pins blocking a newer MAF) is resolved by moving the supported pin to `Microsoft.Agents.AI`
  1.22.0, with `Microsoft.Extensions.DependencyInjection.Abstractions` `[10.0.12]` in Core and both stores and
  `Microsoft.Extensions.AI.Abstractions` `[10.10.0]` in the vectors package. The general hazard that remained (a
  later MAF needing newer shared pins) was part of KL-13, which the support-matrix work resolved: every reference but
  MAF is now a floor, and MAF a range to its next major. See
  [Compatibility evidence](compatibility-evidence.md#the-maf-compatibility-matrix).
- **KL-15** (Core's redaction dependency a floor) was resolved by exact-pinning
  `Microsoft.Extensions.Compliance.Redaction` at `[10.10.0]`, when every shipping `PackageReference` was exact. Later
  in the same preview that policy was replaced: every reference but MAF is now a floor CI tests at both ends of its
  major, and MAF a range to its next major, tested the same way (see KL-13).
- **KL-16** (erasure emitting no library telemetry) is resolved. Deletion, the retention sweep and the grant purge
  are the `delete`, `retention.sweep` and `grant.purge` operations on the `AgentExperience.Storage.Postgres` source
  and meter, and they carry nothing that was erased; see [the telemetry contract](telemetry.md#operations).

### A script comment that predates its fix

`0006`'s header still tells an operator to purge events by disabling a trigger "until the library ships a purge
path"; `0010` is that purge path and says so in its own header, and the runbook in `0006` must not be used. Journaled
scripts are never edited, so this and the similar forward references in `0007`–`0009` are corrected in
[the schema guide](guide/postgres-schema.md#script-comments-that-were-written-before-the-work-they-point-at-shipped)
instead.

## How the project got here

The work was planned in four epics, all implemented and tested before `0.1.0-preview.1`:

1. **Capture and explain agent experience:** contracts, sanitization, capture, verification, reflection, and the MAF
   adapter.
2. **Reuse relevant experience:** PostgreSQL persistence, atomic audited lifecycle commits, one-call finalization of
   captured runs, bounded text retrieval with explainable ranking, revision-safe embedding ingestion with hybrid
   retrieval, and Historical Reference injection into MAF.
3. **Govern experience safely:** explicit sharing grants, the full audited lifecycle transition table with
   supersession and database-enforced append-only logs, evidence-based confidence updates, and reuse feedback.
4. **Operate and measure the learning loop:** OpenTelemetry-compatible instrumentation, the end-to-end sample, a
   controlled reuse baseline with a negative control, deletion and expiry of library-owned data, learning from
   failure through the adapter, and release hardening (versioning, package verification, the public API baseline,
   pin evidence, the security suite, and the MAF compatibility matrix).

Everything since then has gone into the known limits above. Full requirements and acceptance criteria are in
[`_sdlc/planning-artifacts/epics.md`](../_sdlc/planning-artifacts/epics.md).

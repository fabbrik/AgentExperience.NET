# Changelog

AgentExperience.NET is a **preview**. It is not production ready, and public APIs may change between previews. The
[Known limits](docs/known-limits.md#known-limits) table lists every limit that is still unresolved, and the
[Documented boundaries](docs/known-limits.md#documented-boundaries) table states what no code change can remove.

## Unreleased

### The in-memory capture service bounds the finished runs it keeps (story 16.1)

- **The problem it fixes.** `InMemoryExperienceCaptureService`, the default `IExperienceCaptureService`, kept every
  run it had ever captured, so a long-lived host grew without bound until it ran out of memory.
- **Retention bounds.** Completed runs are dropped after `CaptureLimits.CompletedRunRetention` (new, default 24 hours)
  or once more than `CaptureLimits.MaxRetainedCompletedRuns` (new, default 10,000) are held, earliest completed first.
  Both are optional `init` properties; the positional constructor is unchanged. Open runs are never dropped. The
  bounds are applied on the service's own calls (no timer). Age is measured on the `TimeProvider`'s monotonic
  timestamp, so wall-clock changes do not move it; the service gains a constructor overload taking a `TimeProvider`,
  and `AddAgentExperienceCore` passes the registered one.
- **Behaviour change.** A dropped run answers exactly as an unknown run: finalizing it (or retrying finalization)
  returns `RunNotFound`, a continuation naming it opens a new run, and evidence naming a run that was never finalized
  must arrive while the run is held. While a run is held, nothing changes: a finalization retry still returns
  `AlreadyFinalized`. A reused ID of a dropped run finalizes as `AlreadyFinalized` for the earlier record, so use a
  fresh run ID per run. See [How long runs are kept](docs/guide/capture.md#how-long-runs-are-kept).

### The in-memory capture service bounds the runs it holds open (story 16.4)

- **The problem it fixes.** Story 16.1 bounded completed runs only, so a caller that used
  `InMemoryExperienceCaptureService` directly (without the MAF adapter's `MaxOpenRunDuration`) and never completed
  its runs still grew without bound.
- **Open-run count bound, on by default.** `CaptureLimits.MaxOpenRuns` (new, default 10,000). Past it, a `StartRun`
  call that would open a new run returns the new `StartRunOutcome.CapacityExceeded` (appended last, so existing
  values are unchanged) and stores nothing; continuing an open run is never refused. The MAF adapter reports the
  refusal through `OnCaptureFailure` and runs the invocation uncaptured.
- **Open-run age bound, off by default.** `CaptureLimits.MaxOpenRunAge` (new, `null` by default). When set, the
  service completes an open run older than it as `Cancelled`, on its next call, and the run is then held and dropped
  like any completed run. A later host completion under its own event ID returns `Conflict`.
- **Behaviour change.** A host with more than 10,000 runs open at once in one service now has new runs refused; raise
  `MaxOpenRuns` if that is intended. The service can hold up to `MaxOpenRuns + MaxRetainedCompletedRuns` runs (20,000
  at the defaults, so about twice the completed-only worst case), so size the two together. A run the age bound
  cancels is never finalized or reported by the service. See [How long runs are kept](docs/guide/capture.md#how-long-runs-are-kept).

### The whole pre-model path is bounded, not only retrieval (story 16.2)

- **The problem it fixes.** The injection provider's final eligibility re-read was bounded only cooperatively: a
  store that ignored its cancellation token, or was slow to cancel, held the model call well past
  `EligibilityCheckTimeout`, and the per-record fallback after a failed batch could make it worse.
- **Hard bound.** The batch re-read, each per-record fallback read, and the session's withdrawal re-check read are
  each awaited for what is left of one `EligibilityCheckTimeout` budget, as retrieval bounds its search. A read still
  running at expiry is abandoned (cancelled in the background) and the check reports its usual timeout; nothing is
  injected.
- **Behaviour change.** `ExperienceInjectionLimits.DefaultEligibilityCheckTimeout` drops from 2 s to **500 ms**, so
  the worst case before the model call is about 1 s at the defaults (retrieval's 500 ms plus the check's 500 ms,
  which includes `DecideInjection` time, plus the host's `ResolveRequest`). A host whose store needs longer should set
  `Limits.EligibilityCheckTimeout`. Hosts most at risk use a store that keeps the port's default `GetManyAsync`, which
  reads one record at a time; the symptom is `Failed` injection results whose reason says the final eligibility check
  exceeded its bound, with nothing injected.
- **Abandoned reads keep running.** A read abandoned at the bound keeps running against the store in the background
  until it observes its cancellation, so the store must tolerate concurrent use (not a scoped, non-thread-safe
  context), it may hold a pooled connection until the cancel lands, and a grant's access rows may be written after the
  timeout is reported. The bound is released by a `TimeProvider` timer, so a starved thread pool can still release it
  late. See [Pre-model latency budget](docs/guide/injection.md#pre-model-latency-budget).

### Abandoned reads against a hung store are capped (story 16.5)

- **The problem it fixes.** Retrieval and the injection provider's eligibility re-read abandon a timed-out store read
  and let it keep running. Against a store that hangs on every call, each invocation added another running read, so
  they piled up without limit and could exhaust the connection or thread pool. An abandoned read also kept a
  registration on the caller's cancellation token until it ended, which for a hung read was never.
- **Running-count cap.** `RetrievalPolicy.MaxAbandonedSearches` and `ExperienceInjectionLimits.MaxAbandonedReads`
  (new `init` properties, default 16 each, must be positive; `DefaultMaxAbandonedSearches` and
  `DefaultMaxAbandonedReads` are new). Each retrieval service and each provider counts its own abandoned reads still
  running, whether they were abandoned on a timeout or because the caller cancelled. At the cap no new read is
  started: retrieval returns `TimedOut` at once, the same empty result as a timeout, but with a `Failure` whose reason
  names the cap (a real timeout still carries no `Failure`; injection reports it as `RetrievalTimedOut` with that
  reason), and the eligibility check is not started and reports a `Failed` injection result whose reason names
  `MaxAbandonedReads`. Neither touches the store. The count drops as the abandoned reads end. There is no
  circuit-breaker state, probe or timer.
- **Shared instances only.** The counts are per `ExperienceRetrievalService` and per `ExperienceContextProvider`
  instance, so the caps engage only when those are shared and long-lived (for example DI singletons); one built per
  request never reaches them.
- **Detached at once.** An abandoned read is now unregistered from the caller's token the moment it is abandoned; its
  own token source is still cancelled off the caller's thread and disposed only once the read ends. Reads that finish
  in time, and caller cancellation, behave as before. See
  [Pre-model latency budget](docs/guide/injection.md#pre-model-latency-budget).

### Retrieval unwraps record keys in one batch (story 16.3)

- **The problem it fixes.** With crypto-shredding on, every sealed row a read returned cost one key-store call, made
  in turn while the data reader held its connection. At 10 ms per KMS unwrap, a 50-candidate text search took about
  half a second, the whole default retrieval budget, and injection silently delivered nothing.
- **Batch key lookup.** `IExperienceKeyStore.GetKeysAsync` (new) looks up several references in one call. It is a
  default interface method that calls `GetKeyAsync` for each reference in order, so existing key stores keep working
  unchanged; on a failure it disposes every key it already obtained. `EnvelopeExperienceKeyStore` overrides it to
  look up and unwrap concurrently, at most 16 at a time by default; a new constructor overload takes
  `maxConcurrentKeyLookups`, and `MaxConcurrentKeyLookups` and `DefaultMaxConcurrentKeyLookups` are new. Nothing is
  cached: a destroyed key is never served by a later call.
- **Retrieval reads.** The text search, the vector search and `GetManyAsync` (injection's eligibility re-read) now
  read their rows into memory, close the reader, commit their read-only transaction and return the connection to the
  pool, then make one `GetKeysAsync` call for every sealed row and decode. A slow key store no longer holds a reader,
  a transaction or a pooled connection on these paths. The buffered rows are bounded by the candidate limit (the
  searches) or the ID list given (`GetManyAsync`). Results, ordering, error types and access-audit rows are
  unchanged; plaintext rows never reach the key store. Other reads still make one call per sealed record with their
  reader open.
- **Concurrency to plan for.** `maxConcurrentKeyLookups` bounds one `GetKeysAsync` call, not the process: one
  injection can run the text search, the vector search and the re-read together, and hosts run retrievals in
  parallel, so the unwraps in flight can reach the bound times the concurrent calls. With the envelope store, your
  `IExperienceWrappedKeyRepository` and `IExperienceKeyEncryptionKey` implementations are now called concurrently and
  must be thread-safe. One injection can unwrap the same record's key more than once (text, vector, re-read).
- **Action for a custom key store over a remote KMS:** override `GetKeysAsync` to unwrap concurrently or through the
  KMS's batch API; the default is still one call after another. A wrapper or decorator around a key store must
  forward `GetKeysAsync`, or it falls back to sequential lookups. The new `EncryptedRetrievalBenchmarks` measures it at
  10 ms per unwrap: 62.7 ms batched against 610.4 ms with concurrency limited to one, which exceeds the 500 ms
  default retrieval timeout. With no unwrap latency, batching adds a small overhead (5.1 ms against 4.3 ms, within
  the run's error). See [Key custody](docs/guide/crypto-shredding.md#key-custody-the-property-is-only-as-true-as-this).

### Unknown authorship fails closed, and the library's own reflector is recognised (story 17.1)

- **The problem it fixes.** Two clauses of the KL-18 boundary were removable by code. Under
  `ModelAuthoredLessons = Exclude`, a PostgreSQL row sealed without its authorship flag (before migration `0021`, by
  an instance on an earlier build during a rolling deploy, or by any writer that left the flag out) was still returned
  by both channels and took a place in the candidate window before retrieval dropped it. And records the library's own
  `ChatClientExperienceReflector` wrote before it declared authorship read as `Deterministic`.
- **Behaviour change: fail closed in SQL.** Under exclusion, the text candidate source, the vector search and its
  compatibility probe keep only rows whose `reflection_model_authored` is `false` (`IS FALSE`, was `IS NOT TRUE`).
  A sealed row with an unknown flag is left out of an excluding search, so a deterministic record among them is not
  injected under `Exclude` until its flag is written. Without the exclusion nothing changes.
- **One authorship rule.** Core (finalization's content guard, retrieval's exclusion check), the MAF adapter
  (injection labelling, fencing and exclusion) and both stores now decide authorship by one rule: model-authored when
  `Authorship` is anything but `Deterministic`, **or** when `Producer` starts with
  `AgentExperience.ChatClientExperienceReflector/` (ordinal), now public as
  `ReflectionAuthorshipConventions.LibraryModelReflectorProducerPrefix` in Abstractions. No other producer is read.
  The reflector shipped in `0.1.0-preview.5`, the release that also made it declare `Model`, so no published release
  wrote one of its records as `Deterministic`; the rule covers unreleased builds between stories 14.2 and 14.3.
- **Action for a custom `IExperienceCandidateSource` or `IExperienceEmbeddingIndex`:** `ExcludeModelAuthored` now also
  covers a reflection whose producer starts with `ReflectionAuthorshipConventions.LibraryModelReflectorProducerPrefix`,
  whatever authorship it declares, so an implementation must apply that prefix check too. The store conformance suite
  checks it.
- **Migration `0022_library_reflector_authorship`** (`PostgresExperienceRecordSchema.LibraryReflectorAuthorshipScriptName`)
  replaces `payload_reflection_model_authored` with the same rule and recomputes the flag on every live plaintext row
  that differs; a reflection whose producer is missing or not a string, which the reader refuses, is `true` (fail
  closed). It adds no table, column, index or function signature, takes row locks only on the rows it rewrites, and is
  idempotent. Its `UPDATE` reads every live plaintext payload inside the migrator's transaction and command timeout:
  on a large plaintext table, migrate in a maintenance window or with a raised command timeout. See [0022](docs/guide/postgres-schema.md#0022-library-reflector-authorship).
- **Owner-run backfill.** `PostgresExperienceRecordStore.BackfillSealedAuthorshipAsync` (new, with an optional
  `startAfter` cursor, returning the new `ExperienceAuthorshipBackfillResult` with `SetCount`, `SkippedCount`,
  `MoreRemain` and `ResumeAfter`) opens each live sealed row whose flag is unknown with its record key and writes the
  flag, in bounded, authorized batches that resume past the last examined ID. It fetches a batch's keys in one
  key-store call before locking any row. It must run over the owner's data source with the record keys (the
  application role holds no `UPDATE` on the flag), skips and counts a row whose key was destroyed or whose payload
  cannot be opened, and never changes a flag already written; a call that throws may already have set some flags, and
  rerunning it is safe. It is the new `record.authorship.backfill` telemetry operation. **Action with crypto-shredding
  on:** run it after upgrading, passing each `ResumeAfter` on until `MoreRemain` is `false` for every project; see
  [Backfilling authorship flags](docs/guide/crypto-shredding.md#backfilling-authorship-flags-after-upgrading).
- **KL-18** drops both clauses; see [Limits history](docs/limits-history.md#narrowed-after-010-preview6).

### The reflection is signed too (story 17.2)

- **The problem it fixes.** Provenance signing (story 13.1) covered only a record's finalization claims, so a party
  that could write the store could change a signed record's lesson, its approach, or flip its `Reflection.Authorship`
  to `Deterministic`, and the result was then injected unfenced (KL-18).
- **Claims version 2.** With `ExperienceProvenanceSigningOptions` configured, finalization signs `aexp-prov:v2`: the
  version 1 claims, then a SHA-256 digest of everything injection renders from the record: `TaskId`, `TaskSummary`,
  the outcome's status and evidence count, the `Environment`, the `Attempts` (tool names and argument values, through
  their JSON form as the PostgreSQL store writes it, numbers by exact decimal value, so both stores read back the same
  bytes), and the reflection's free text, evidence count,
  `Authorship` and `Producer`. The signature's `Algorithm` is the new `ExperienceProvenanceSignature.HmacSha256ClaimsV2`
  (`HMAC-SHA256.aexp-prov.v2`, within the characters every store accepts), so a verifier never guesses the version. A
  version 1 signature (`HmacSha256`) still verifies for the finalization claims, and independence verification treats
  it exactly as before. A version 2 record whose content changed after signing no longer verifies, so its run stops
  vouching too. A run whose content has no canonical encoding (a lone surrogate, or a tool argument value with no JSON
  form) now ends `Failed` at the create stage with nothing stored, and the failure says the claims or content could
  not be encoded. No stored schema changes, and nothing re-signs existing records.
- **Behaviour change: unconfirmed content is model-authored.** With signing configured, a record's content is
  confirmed only by a version 2 signature that verifies under a key in the ring, or by `TrustUnsignedRecordIds` for a
  listed unsigned record. Any other record counts as model-authored: `ExperienceRetrievalService` excludes it under
  `ExcludeModelAuthored`, and injection omits it under `ModelAuthoredLessons = Exclude`, both with the new reason
  `UnconfirmedContent` (`RetrievalExclusionReason.UnconfirmedContent`, `InjectionOmissionReason.UnconfirmedContent`)
  when that is the only reason. Otherwise injection fences and labels it, and every line drawn from it moves inside
  the fence: its task ID (a `Task:` line, the `Source:` line ending with `HistoricalReferenceWriter.UnconfirmedTaskNotice`),
  its `Recorded:`, `Environment:`, `Verification:` and `Evidence:` lines and its `Approach:` line. A stored record whose
  content cannot be encoded is unconfirmed and never fails a retrieval. **Records signed before this release are fenced as model-authored at injection and excluded under
  `Exclude`**, as are records a host writes through `CreateAsync` without a signature. A store's candidate window is
  not aware of signatures, so under `Exclude` such records still take candidate slots before retrieval drops them.
  Without signing, nothing changes.
- **Upgrade settings.** `ExperienceProvenanceSigningOptions.SignClaimsVersion` (new, default 2, only 1 or 2): set it
  to 1 during a rolling deploy in which nodes on an earlier build still verify, since they refuse a version 2
  signature; switch it back afterwards. `ExperienceProvenanceSigningOptions.ConfirmV1Content` (new, default `false`):
  a transition setting that lets a valid version 1 signature confirm its record's content, accepting for those records
  the exposure version 1 left open, while existing lessons are reviewed or replaced.
  `ExperienceProvenanceSigningOptions.ConfirmContentRecordIds` (new, empty by default) is the per-record alternative:
  listed records (signed version 1, including during a `SignClaimsVersion = 1` rollout, or unsigned) count as
  confirmed once reviewed, unless their signature is present and does not verify.
- **New API.** `ExperienceRetrievalService` gains a constructor that takes `provenanceSigning`, and public
  `IsModelAuthored(ExperienceRecord)` and `IsContentConfirmed(ExperienceRecord)`, which the MAF provider uses on the
  record it re-read. `AddAgentExperienceRetrieval` passes registered signing options (directly or as `IOptions<T>`) to
  it. `ExperienceInjectionDecisionContext.ModelAuthored` (new, `true` unless set, which the provider always does) carries
  the provider's verdict to `DecideInjection`; a
  host should decide on it rather than on the declared authorship. `HistoricalReferenceWriter.Write` gains an overload
  taking a content-confirmation function; the existing overloads decide on the reflection alone.
  **Action for a host that constructs `ExperienceRetrievalService` by hand with signing on:** pass the same options
  to the new constructor, or records are judged without the content check.
- **KL-18** drops the clause that signing covers the finalization claims only; KL-11 now says a content edit is
  caught only for version 2 records. See [Limits history](docs/limits-history.md#narrowed-after-010-preview6) and
  [Signing provenance](docs/guide/confidence.md#signing-provenance).

### Host-trusted evidence can be kept out of the ranked score (story 17.3)

- **The problem it fixes.** Under the verification opt-out (`IndependenceVerification.TrustHostSuppliedIdentifiers`),
  evidence stored as `HostTrusted` still moved the record's counters, its stored `ReuseConfidence` (which retrieval
  ranks and filters on) and, for a contradiction, its status. Only `ReadConfidenceAsync` could leave it out (KL-11).
- **New option.** `ExperienceIndependenceOptions.HostTrustedEvidence` of the new enum `HostTrustedEvidenceEffect`
  (`Counted = 0`, the default and the earlier behaviour; `RecordedOnly = 1`). An undefined value is refused when a
  service is constructed. It only matters under the opt-out; verified evidence always counts.
- **`RecordedOnly`.** Host-trusted evidence is committed on a lifecycle event and an evidence row exactly as before
  (same IDs, idempotency, assessment spending, access audit, `Admission = HostTrusted`), but the event's new counters,
  new score and status equal the prior ones, and the record's `UpdatedAt` is not refreshed: only its revision moves.
  `ApplyEvidenceAsync` reports `Applied` with `Counted: false`, a contradiction does not contest the record, and the
  confidence engine is not asked to score it. A later recorded-only submission for a key already held by counted
  evidence or by an earlier recorded-only event is a duplicate (a ledger row, no event). The revision still advances
  and the event is appended to history exactly as a counted one is, so reused-session re-delivery, `StaleRevision`
  contention and history growth are as under `Counted`.
- **Reading it.** `ReadConfidenceAsync` with `ConfidenceEvidenceFilter.All` now adds recorded-only host-trusted
  evidence to the stored counters (once per independence key no counted update holds) and scores that, which gives
  the counts and score `Counted` would have stored (not the `Contested` status or de-indexing it would have applied);
  `ExcludeHostTrusted` and `VerifiedOnly` report without it. `ConfidenceReport` gains
  `HostTrustedRecordedOnly`. With no such evidence, `All` still reports the stored score unchanged.
- **Stores.** Both shipped stores, through the shared validator, accept a confidence event whose evidence moves no
  counter only when its admission is `HostTrusted` and its score and status do not move; any other such event is
  `Invalid` (it was refused before too: as an infrastructure failure in PostgreSQL). **Migration
  `0023_recorded_only_evidence`** (`PostgresExperienceRecordSchema.RecordedOnlyEvidenceScriptName`) replaces `0007`'s
  `confidence_evidence_event_only_when_counted` CHECK with one that also admits such a row, and adds the matching
  CHECK on `lifecycle_events` (both `NOT VALID`), plus a partial index for the duplicate check; no new table, column
  or grant. **Migrate before setting `RecordedOnly`:** against a database without `0023` every host-trusted
  submission fails as an infrastructure error. **Action for an out-of-tree
  store:** accept such an event, leave counters, score and `UpdatedAt` alone, and treat a later recorded-only
  submission for a key already counted or recorded as a duplicate.
- **No backfill.** Evidence counted before `RecordedOnly` was set stays counted; read it out with `ExcludeHostTrusted`.
- **KL-11** drops the clause that the opt-out's evidence is excluded only on read; see
  [Limits history](docs/limits-history.md#narrowed-after-010-preview6) and
  [Keeping host-trusted evidence out of the ranked score](docs/guide/confidence.md#keeping-host-trusted-evidence-out-of-the-ranked-score).

### Session tracking is serialized within a process (story 17.4)

- **The problem it fixes.** Session tracking (story 6.5) loaded a session's account, awaited retrieval, then staged one
  pending delivery and saved it. Two invocations running concurrently on one session both read the same account, so
  they could deliver the same record revision twice, and the second save overwrote the first one's stage: that
  delivery was never charged, never tracked, and never withdrawn later (KL-12).
- **A lock per account.** `ExperienceContextProvider` now holds a `SemaphoreSlim` per `AgentSession` instance and
  state key (held in a `ConditionalWeakTable`, so it goes with the session, and shared by every provider in the
  process) from loading the account to saving it, through retrieval and the final eligibility check, and around
  settling in `InvokedCoreAsync`. It is never held across the model call. Waiting to inject honours the invocation's
  cancellation token, and a cancelled wait propagates like any cancellation of the invocation; settling waits
  regardless, so a cancelled invocation's stage is still discarded. Concurrent invocations on one session now queue
  their pre-model latency (retrieval and the eligibility re-check) behind one another.
- **Action for a host:** the lock is held while `DecideInjection` runs, so a `DecideInjection` callback must not run
  any invocation that injects with the same session and the same state key. That invocation would wait for the lock
  until its token is cancelled, and with a token that cannot be cancelled it waits indefinitely.
- **One pending stage per in-flight invocation.** Each provider settles the stages its own account holds among the
  blocks a context provider injected into the invocation; a block the request carries from the chat history, or one
  the host sends again as input, settles nothing. Until settled, every decision counts a stage as delivered: it is
  charged to the budget, its records are not delivered again and are re-checked for withdrawal, and its withdrawal
  notices stay owed. At most eight stages stay pending; staging a ninth first commits the oldest as unsure, as a
  single leftover stage was before. With one invocation at a time, the block, the charges and the notices are
  unchanged.
- **New option: `ExperienceInjectionSessionLimits.InFlightStageWindow`** (default `DefaultInFlightStageWindow`,
  5 minutes, the capture adapter's default `MaxOpenRunDuration`; must be strictly positive). Each stage records when
  it was staged, on the provider's `TimeProvider`. A pending stage is held as in flight only within the window; an
  older one (or one dated further ahead than the window) is taken as abandoned and committed as unsure at the
  session's next invocation, exactly as a leftover stage was before: charged, its records eligible again, its
  notices still owed. MAF gives no signal for an abandoned stream, so its age is the only discriminator. Set the
  window above your longest invocation: a stage that outlives it is committed as unsure while still running, so a
  concurrent invocation can deliver its records again (charged and tracked, never lost). A successful invocation
  whose settling is skipped (settling threw, say) is likewise committed as unsure after the window.
- **Behaviour change: an abandoned stream's records are held for at most the window before becoming eligible
  again.** A stream abandoned before MAF settled it was committed as unsure at the session's next invocation, so its
  records were delivered again at once. Nothing tells it from an invocation still running, so an invocation within
  `InFlightStageWindow` of the abandoned one now counts its records as delivered; one after the window commits it as
  unsure and delivers them again, as before.
- **State format version 2.** The account's `pending` member is now a list of stages (empty when none is pending),
  each carrying `staged`, when it was staged. Version 1 still loads: its single stage recorded no staging time, so it
  is committed as unsure on load, as before, and the state is saved back as version 2 at the session's next
  invocation. **Rollback:** a build before this change reads version 2 as an unreadable state, so a session saved by
  this build injects nothing there until the host removes its state key, which resets the session's account.
- **KL-12** drops the clause that concurrent invocations on one session race on the tracking; what remains is two
  processes resuming the same serialized session, the host deleting or replacing the state key, and a model that
  cannot unread a block. See [Limits history](docs/limits-history.md#narrowed-after-010-preview6) and
  [Reused sessions](docs/guide/injection.md#reused-sessions-a-budget-no-repeats-and-withdrawal-notices).

## 0.1.0-preview.6

Excluding model-authored lessons now happens inside retrieval (story 14.4), so
`ModelAuthoredLessons = Exclude` returns the top deterministic lessons instead of
fewer. The release also fixes a flaky test.

Upgrading from `0.1.0-preview.5` applies migration `0021`. Run the migrator before deploying the new build. **It holds
an `ACCESS EXCLUSIVE` lock on `experience_records`, blocking every read and write of the table, until its backfill
commits, and it rewrites every row it backfills (recomputing its stored `search_vector`)**: every live record in a
crypto-shredding deployment, usually few in a plaintext one. It runs under the command timeout (30 seconds by
default). Above about 20,000 rows to rewrite, take the two-step route in
[0021: reflection authorship](docs/guide/postgres-schema.md#0021-reflection-authorship): the script by hand without its
backfill, then the backfill in batches. During a rolling deploy, instances still on `0.1.0-preview.5` seal new records
without the flag; see the residual below.

### Exclusion of model-authored lessons moves into retrieval (story 14.4)

- **The problem it fixes.** With `ModelAuthoredLessons = Exclude`, story 14.3 dropped model-authored records only after
  retrieval had applied its limit, so when model-authored records filled the candidate window
  (`RetrieveExperienceRequest.Limit`, or the policy's candidate limit), deterministic records below it were never
  found and fewer lessons were injected than existed. No model-written text leaked.
- **Port.** `ExperienceCandidateQuery` and `ExperienceVectorQuery` gain an init property `ExcludeModelAuthored`
  (default `false`). When set, a source leaves out every record whose reflection authorship is anything but
  `Deterministic` (fail closed on undefined values; a record with no reflection is kept), **before** its limit, like
  the status and confidence filters. A source that cannot honour it must answer `Invalid` rather than ignore it. The
  store conformance suite has a new rule: with model-authored records ranked above deterministic ones and
  `Limit = N`, an excluding search returns the top N deterministic records; a non-excluding search is unchanged. The
  in-memory and PostgreSQL stores pass it, and the end-to-end sample's in-memory double honours it too.
- **Retrieval.** `RetrieveExperienceRequest` gains an init property `ExcludeModelAuthored` (default `false`), which
  `ExperienceRetrievalService` passes to the text channel and, when hybrid, to the vector channel. It then excludes
  any model-authored record a source still returned (fail closed; a record with no reflection is kept), listing it in
  `Excluded` under the new `RetrievalExclusionReason.ModelAuthored`. With the property unset, every query and every
  result is exactly what it was.
- **Injection.** `ExperienceContextProvider` sets it on the resolved request when `ModelAuthoredLessons = Exclude`
  (and never clears a host's own `true`). It keeps its own check before the record limit and after the re-read, and
  `InjectionOmissionReason.ModelAuthored` for anything it still drops; since retrieval now excludes such records
  first, they appear in the injection result's `Excluded` instead of `Omitted`.
- **Migration `0021_reflection_authorship`** (`PostgresExperienceRecordSchema.ReflectionAuthorshipScriptName`): the
  nullable `experience_records.reflection_model_authored` flag, added with a catalog-only `false` default that is then
  dropped; `agent_experience.payload_reflection_model_authored(payload, payload_version)`, the one rule (`NULL` for a
  sealed payload; otherwise `false` when the reflection is null, has no `authorship` member, or one that is null or
  `Deterministic` in any ASCII case, and `true` for anything else, an unreadable payload of an unknown version
  included); a backfill of only the live rows that differ from `false` (sealed rows to `NULL`, model-authored
  plaintext rows to `true`); a `BEFORE INSERT OR UPDATE` trigger that derives it for every live unsealed row on every
  write and gives every tombstone the fixed `false`; and a `NOT VALID` check that a tombstone carries nothing else. The
  store writes it from the reflection for a sealed record, and the application role cannot update it. The PostgreSQL
  text search and the vectors package's search and compatibility probes filter `reflection_model_authored IS NOT TRUE`
  only when asked. No index, no privilege change; the row-level security policies cover it as they cover every
  column. Under the out-of-band HNSW index the vector channel applies it after the index walk, as it does its other
  filters, so it can return fewer than its limit; `hnsw.iterative_scan` (pgvector 0.8+) is the host's lever.
- **Residual: a sealed row stored without its flag.** A row sealed before `0021` (a migration cannot open it), one an
  instance still on `0.1.0-preview.5` seals during a rolling deploy, or one any writer inserts without the flag keeps
  `NULL`, and an excluding search still returns it, so it takes a place in its source's candidate window; the
  retrieval service opens it and excludes it. Find them with the query in
  [0021: reflection authorship](docs/guide/postgres-schema.md#0021-reflection-authorship), read each through the
  store, and revoke, supersede or erase the model-authored ones. In crypto-shredding mode the flag is one bit of
  plaintext metadata about a sealed lesson, like the status.
- **Tests.** The conformance rule; Core tests that both channels receive the flag and that Core excludes what a
  source returns anyway; injection tests over a fake source, the in-memory store, the sample's double and a real
  PostgreSQL (five model-authored records ranked above three deterministic ones, retrieval limit three); PostgreSQL
  tests of the write, the trigger (unknown payload versions included), the application role's refusal, sealing,
  erasure, grant-shared records, the exact-scope fallback, the backfill on a pre-`0021` database (null, case, number,
  object and unknown values) and the sealed residual; vector-channel and hybrid tests, grant-shared included; and an
  upgrade-suite check that a test-written plaintext payload saying `Model` in a `0.1.0-preview.2` database is flagged
  and left out, while every sealed row stays unknown and every tombstone `false`.

## 0.1.0-preview.5

The hardening release. It adds:
- tests that upgrade databases the published previews created, plus benchmarks (Epic 12);
- opt-in HMAC signing of finalization provenance (Epic 13);
- screening of every reflection, an optional model-backed reflector, and labelling and an exclude option for the
  lessons it writes (Epic 14);
- opt-in PostgreSQL row-level security behind the application role (Epic 15).

Upgrading from `0.1.0-preview.4` applies migration `0019` and, with the vectors package, `0020`. Everything new is off
by default apart from reflection screening. Read the story sections below before you upgrade.

### Model-authored lessons are marked and guarded (story 14.3)

- **Authorship, declared by the reflector.** `Reflection` gains `Authorship` (`ReflectionAuthorship.Deterministic =
  0`, the default, or `Model = 1`), an init property. It is self-declared: `ChatClientExperienceReflector` always sets
  `Model`, and **a host's own model-backed `IExperienceReflector` must set it too** (the contract now says so). Nothing
  infers it from `Producer`. A deterministic reflection serializes as before (System.Text.Json omits the default). The
  PostgreSQL store writes an optional `authorship` member in the version-1 reflection payload only when it is not
  `Deterministic` (no migration; absent reads back as `Deterministic`; sealed with the rest in crypto-shredding mode);
  the in-memory store keeps it. An undefined value is `Invalid` in both stores and refused by screening as the new
  `ReflectionScreeningRefusal.UndefinedAuthorship`. Neither authorship nor the reflection's text is covered by
  provenance signing (new documented boundary KL-18).
- **A best-effort content guard for model-authored text.** After hygiene and the host sanitizer, screening refuses a
  reflection whose authorship is anything but `Deterministic` with the new `ReflectionScreeningRefusal.UnsafeContent`
  when a free-text field holds credential-shaped text (private keys, AWS, OpenAI/Anthropic `sk-`, GitHub, GitLab,
  Google, Slack, JWT, Azure `AccountKey=`/`SharedAccessSignature=`, `password=`/`pwd=`); a word mixing Latin with
  Cyrillic or Greek letters; listed instruction-override phrasing (normalized, with look-alikes folded, letters run
  together, and across all fields joined); a `data:`, `javascript:`, `vbscript:` or `file:` link or a UNC path; or a
  whole URL, hostname (last label a listed TLD, an `xn--` or a non-ASCII label) or IP address that does not equal a
  whole token in the run content the reflector was given, after undoing `[.]`, `(dot)`, ` dot ` and full-width dots.
  The new `IReflectionRunContent` lets a reflector declare exactly what it sent; `ChatClientExperienceReflector`
  implements it with the same clipping as its prompt. With no run content, every link is refused. The reason names
  the field and the rule, never the matched text.
- **Injection labels every model-written field and can exclude them.** A model-authored entry carries its `Approach:`
  line first, then `HistoricalReferenceWriter.ModelAuthoredLine`, the lesson, reuse guidance, preconditions and
  warnings, then `HistoricalReferenceWriter.ModelAuthoredEndLine`. Any authorship other than `Deterministic` counts.
  The new `ExperienceInjectionOptions.ModelAuthoredLessons` (`ModelAuthoredLessonPolicy.Include`, the default, or
  `Exclude`) omits such records as the new `InjectionOmissionReason.ModelAuthored`, with no detail, **before** the
  record limit (so they cannot crowd deterministic records out) and again after the re-read.
- **Behaviour change for deterministic records:** none to their rendering, except that a line of a record's own text
  that starts with `Authored:` or `End authored:` is now neutralized like any other field label. A test pins a
  deterministic entry byte for byte against the pre-14.3 rendering.
- **Behaviour change in `ChatClientExperienceReflector`'s `Producer`:** a model ID longer than 128 characters
  (`MaxProducerModelLength`), or one containing `:` or `/`, or one shaped like a URL, hostname or IP address, is now
  dropped (falling back to `ModelName`, then `unknown`) instead of being cut and named. The reflection is not refused.
- **Upgrade note.** Records the story 14.2 `ChatClientExperienceReflector` wrote before this release read back as
  `Deterministic`, are not relabelled, and `Exclude` does not cover them. Find them by `Producer` (it starts with
  `AgentExperience.ChatClientExperienceReflector/`) and revoke or erase them; see
  [Limits of model-authored lessons](docs/guide/finalization.md#limits-of-model-authored-lessons).
- **The guard is a filter, not a boundary.** Content echoed from the run (a poisoned tool result included) passes by
  design; paraphrased instructions, whitespace-split dots, encoded secrets and mid-line contradictions pass too; and a
  legitimate lesson can be refused (`ASP.NET`, `1.0.0.0`). The label, `Exclude` and the approval boundary are the
  controls to rely on. This closes the deferred-work item story 14.2 opened.

### Row-level security (story 15.1)

- **An opt-in second isolation layer.** `ExperienceApplicationRoleOptions.EnableRowLevelSecurity` (default `false`)
  makes `ApplyApplicationRolePrivilegesAsync` re-create the canonical helper functions and policies, enable PostgreSQL row-level security on
  every table the application role reads or writes by scope, and verify it -- policy names, commands, roles and
  expressions included -- in the same transaction as the privileges. It is never forced; `false` disables it again,
  so the setting is declarative. The call refuses missing or extra policies, an unreadable `experience_grants`, a role
  or database default for the declared settings, and an application role that can reach `BYPASSRLS`. See
  [Enabling row-level security](docs/guide/deployment.md#enabling-row-level-security).
- **Migration `0019_row_level_security`**, and the vectors package's **`0020_embeddings_row_level_security`**: the
  `rls_*` policies and five helper functions (none `SECURITY DEFINER`). They switch nothing on. A row is admitted only
  inside the authorization bounds the current operation declared, plus, for reading, the records and embeddings a live
  grant shares with those bounds; a recipient reads only live grants; an access row must describe a live grant that
  names it exactly; nothing is admitted when nothing was declared. `0019` also redefines the four `SECURITY DEFINER`
  functions (erasure, grant purge, access purge, sealing), bodies unchanged, with a guard that refuses a scope outside
  declared bounds and, while row-level security is enabled, an undeclared caller that is neither a member of the
  tables' owner nor `BYPASSRLS`. Run both migrators before enabling. `PostgresExperienceRecordSchema.RowLevelSecurityScriptName` and
  `ExperienceVectorSchema.EmbeddingsRowLevelSecurityScriptName` name them.
- **Every store operation now runs in a transaction and declares its bounds first**, whether or not row-level
  security is on: the host `AuthorizationContext`'s tenant, application, project, team, agent and user bounds, all
  seven settings every transaction, transaction-locally, as parameters. Operations that were one autocommitted
  statement are wrapped in a transaction; the SQL they send is otherwise unchanged. An audited read's access rows are
  appended under the reader's authorization; `IExperienceGrantAccessLog.RecordAsync` called directly declares the
  recipient scope its rows name.
- **Behaviour change, with row-level security on only.** Every role but the owner sees nothing until it declares
  bounds, so a reporting role or a hand-run script as the application role must declare them too. Presetting the
  settings (connection-string `-c` options, session-level `SET` on pooled connections) is not supported.
- **Cost.** About 0.7 ms per operation for the transaction and the declaration, and about 1 ms more per text search
  with row-level security on, on the benchmark machine. **Text search does not use its GIN index beneath the
  policies** (`@@` is not leakproof): it scans the declared tenant's live records through the scope index. The vector
  channel keeps its HNSW index. See [Row-level security](docs/benchmarks.md#row-level-security-story-151).
- **What it does not do**: bind a compromised application role, which can declare any bounds itself; enforce grant
  disclosure levels; hide that a colliding ID exists in another tenant; check the record a feedback exposure names.
  That is [KL-17](docs/known-limits.md#documented-boundaries), a new documented boundary.
- **Tests.** CI runs the store, vector (with the conformance suites), sample and upgrade suites again on every
  PostgreSQL major with `AGENTEXPERIENCE_TEST_RLS=on`, and the store and vector suites with both
  `AGENTEXPERIENCE_TEST_RLS=on` and `AGENTEXPERIENCE_TEST_ENCRYPTION=on`; `RELEASING.md` step 3 does the same. In RLS
  mode the suites' own hand-written SQL runs through a fixture role that inherits the application role's privileges
  with `BYPASSRLS`, while every store runs as the application role behind the policies. `PostgresRowLevelSecurityTests`
  proves the layer in every mode, table by table, including the grant rules, the owner's functions, policy tampering,
  preset defaults, access-log forgery and transaction locality. The upgrade suite's schema comparison now covers each
  table's row-level security and every policy.

### Model-backed reflector (story 14.2)

- **An optional `ChatClientExperienceReflector` in `AgentExperience.MicrosoftAgentFramework`.** It implements
  `IExperienceReflector` over a host-supplied `IChatClient` and asks the model, through structured output, only for the
  free text: `lesson`, `successfulApproaches`, `failedApproaches`, `preconditions`, `warnings` and `reuseGuidance`.
  Every bound field is copied from the `ReflectionRequest`, so story 5.5's binding check and story 14.1's screening
  apply unchanged; extra members the model returns, and any reasoning content, are ignored and never stored. The
  answer is parsed strictly (no duplicate properties, comments or trailing commas; a code fence only as the whole
  answer). `Producer` is `AgentExperience.ChatClientExperienceReflector/1.0.0 (<model>)`, the model restricted to
  `[A-Za-z0-9._:/@+-]` and cut to fit 200 characters.
- **Off by default, and never evicts silently.** `AddAgentExperienceChatClientReflector(configure,
  chatClientServiceKey, replaceExisting)` replaces the default reflector; any other registered reflector needs
  `replaceExisting: true`. It resolves a registered, optionally keyed (no fallback) `IChatClient`, and refuses a scoped
  one. `DefaultExperienceReflector` stays the default and is unchanged.
- **Privacy: it sends sanitized captured run content to the host's model provider**: the task text (or task ID),
  each attempt's sequence number and ordered tool names, each tool call's and attempt's result and error clipped to
  `MaxQuotedLength` (500), and the verification status, check IDs and evidence IDs. Every captured string, tool
  names included, is escaped before it is clipped, so none can create structure. The message is capped at
  `MaxPromptLength` (16,000) by bounding the header's lists and dropping the oldest attempts first; when not even the
  smallest header fits, nothing is sent. The system prompt is the public constant
  `ChatClientExperienceReflector.SystemPrompt`, and `BuildPrompt` returns the data message.
- **No tools, bounded output.** Every call offers no tools, re-asserted after `ConfigureChatOptions`; a client whose
  pipeline contains a `FunctionInvokingChatClient` is refused at construction; a response carrying a function call
  fails. `MaxOutputTokens` (2,048) is set on every call, and a text answer over `MaxAnswerBytes` (64 KB) is refused
  unread.
- **Failure quarantines.** The new `ReflectionFailedException` (`ReflectionFailureKind`: `ModelCallFailed`,
  `TimedOut`, `Unparseable`, `EmptyLesson`, `ToolCallAttempted`, `OutputTooLarge`, `PromptTooLarge`,
  `ChatOptionsCallbackFailed`) carries no model text and no inner exception, only the cause's type name. The timeout
  is enforced even on a client that ignores its token; the caller's cancellation still propagates.
- **Options:** `ChatClientExperienceReflectorOptions` with `ModelName`, `Timeout`, `MaxQuotedLength`,
  `MaxPromptLength`, `MaxOutputTokens`, `Temperature` (0 by default) and `ConfigureChatOptions`, all validated and
  copied when the reflector is built.
- Documented in the finalization guide's new "Model-backed reflection" section (with its limits: captured tool
  output can steer a model-authored lesson, which the new open deferred-work item records) and the MAF README.

### Reflection screening (story 14.1)

- **Finalization screens what a reflector wrote before the record is created.** After the binding check, the six
  free-text fields (`Lesson`, `SuccessfulApproaches`, `FailedApproaches`, `Preconditions`, `Warnings`,
  `ReuseGuidance`) and `Producer` pass two layers, for every reflector, the default one included:
  - **built-in hygiene:** the new `ReflectionLimits` (4,000 characters of lesson and of reuse guidance, 1,000 per list
    item, 32 items per list, 200 characters of `Producer`, and a 5-second `SanitizerTimeout`); invisible characters
    removed; a field empty afterwards counts as absent, and a missing lesson or producer refuses the reflection;
  - **the host's `ISanitizer`,** as a payload of the new kind `ExperienceReflection`
    (`ReflectionScreening.PayloadKind`), bounded by `SanitizerTimeout`.
- **Over-limit text is refused, never truncated.** A refused reflection quarantines the record with a
  `FinalizationFailure` at stage `Reflect` and a content-free reason, exactly as a binding mismatch does. The new
  `FinalizationFailure.ScreeningRefusal` (`ReflectionScreeningRefusal`: `OverLimit`, `MissingLesson`,
  `MissingProducer`, `MissingField`, `Unreadable`, `SanitizerRejected`, `SanitizerFailed`, `SanitizerTimedOut`,
  `FieldOmitted`) says why, and the `finalize` span carries it as `agentexperience.reflection.screening_refusal`.
  A sanitizer that times out, throws, cancels on its own, omits a field or item, or returns something other than an
  allowed payload of text or a rejection quarantines too; the caller's cancellation still propagates. An exception
  behind a refusal is withheld; only its type is kept, in the new `FinalizationFailure.ExceptionType`.
- **Redactions are kept and reported by path.** The new `FinalizeExperienceResult.ReflectionRedactedFieldPaths` lists
  the redacted fields' paths as indexes into the stored lists, never their values. No reflection text reaches
  telemetry, logs or failure reasons.
- **One invisible-character rule, shared with the Historical Reference writer.** Story 8.2's rule now also removes
  variation selectors, the combining grapheme joiner, Hangul fillers and the blank braille pattern, turns the line
  and paragraph separators into spaces, and cuts a run of combining marks to four, in the writer and in screening
  alike; a cross-check test holds the two to it code point by code point.
- **`DefaultSanitizer` has a built-in policy for the one new kind.** `ReflectionScreening.DefaultSanitizationPolicy`
  (`SanitizationPolicyFor(ReflectionLimits.Default)`) applies when the options configure none for
  `ExperienceReflection`; it allows exactly the six fields, bounded by the default limits, and changes nothing. A
  configured policy replaces it. Every other unconfigured kind is still rejected.
- **The default reflector bounds its own output** to `ReflectionLimits.Default`: at most 500 characters of any quoted
  captured text, each field and item cut to its limit with an ellipsis, and a list kept to 32 items, the last saying
  how many more are not listed. Its output for the sample is unchanged, pinned byte for byte, and the golden
  transcript is unchanged. A captured invisible character it quotes is now removed from the stored reflection.
- **Wiring.** `AddAgentExperienceCore` passes the registered `ISanitizer` and an optional registered
  `ExperienceFinalizationOptions` (new, holding `ReflectionLimits`). Hosts that build services by hand use the new
  `ExperienceFinalizationService` constructor overload with `reflectionSanitizer` and `options`; the existing
  overloads screen through a `DefaultSanitizer` bounded by the service's limits. **A host `ISanitizer` must allow the
  `ExperienceReflection` kind**, or every record is quarantined as `SanitizerRejected`.
- Documented in [Screening reflections](docs/guide/finalization.md#screening-reflections), in the telemetry reference,
  and in the `IExperienceReflector` remarks.

### Signed provenance (story 13.1)

- **Finalization can sign what it vouches for** (KL-11, narrowed). Register an `ExperienceProvenanceSigningOptions`
  and `ExperienceFinalizationService` signs every record it creates. The options hold a key ring of keys of at least
  32 bytes (only `KeyIds` is public; keys are copied and never returned), a `CurrentKeyId`, and an optional
  `TrustUnsignedRecordIds` cutover set. The signature is HMAC-SHA256 over a canonical encoding (`aexp-prov:v1`) of the
  record's finalization claims:
  - the record ID;
  - all six scope fields, as strict UTF-8;
  - the source run, the closed round and the origin;
  - the exposures, sorted by big-endian record ID then revision.

  It is stored with the record, in the same create, as the new `ExperienceRecord.ProvenanceSignature`
  (`ExperienceProvenanceSignature`: key ID, algorithm, value). Content fields and counters are not signed.
- **Verification refuses a record finalization did not sign.** With the options registered, a run known through a
  record whose signature is missing, names a key not in the ring, or does not verify (constant-time comparison) is
  refused as `IndependenceRefusal.HostWrittenRun`. The reason is one generic sentence for all three cases and names
  no key material. So a record written through `CreateAsync` claiming `Finalized`, or a payload edited outside the
  library, can no longer pass as finalized.
- **Initial counters are capped.** `ConfidenceEvidenceFilter.VerifiedOnly` leaves out a refused record's initial
  counters. For a record that vouches, it counts at most finalization's own initial validation as verified.
- **The cutover is a list of record IDs, not a date.** `ExperienceProvenanceSigningCutover.ListUnsignedFinalizedRecordIdsAsync`
  lists the unsigned finalized records in given scopes. Run it once when switching signing on and configure the
  result as `TrustUnsignedRecordIds`. A record created later can never join the set, so backdating a forged record
  gains nothing.
- **Under the `TrustHostSuppliedIdentifiers` opt-out**, a record with no signature or under an unknown key stays
  host-trusted. Only a finalized record whose signature is present, under a key in the ring, and invalid is refused,
  in the confidence path and in feedback attribution alike.
- **Misconfiguration fails at construction.** A ring holding the assessment token key is refused. Finalization's own
  options are refused unless the lifecycle service it is built over checks their current key.
- **A retry does not replay a record that does not vouch.** With signing on, finalizing a run whose stored record
  lacks a valid signature ends `Failed` at the create stage, saying so, instead of `AlreadyFinalized`. A run whose
  scope cannot be encoded (a lone surrogate) ends `Failed` with nothing stored.
- **Rotation:** add a key, switch `CurrentKeyId`. Older signatures verify while their key stays in the ring.
- **No key ring, no change.** Without the options nothing is signed or checked, and every existing test passes
  unmodified. `AddAgentExperienceCore` picks the options up in either registration order, directly or as an explicitly
  registered `IOptions<ExperienceProvenanceSigningOptions>`. Hosts that build services by hand use the new
  `ExperienceLifecycleService` and `ExperienceFinalizationService` constructor overloads that take
  `provenanceSigning`; a finalization service built over a lifecycle service with signing options signs with them by
  default.
- **Storage.** Both stores persist the signature. The PostgreSQL store writes it as an optional version-1 payload
  field, `provenanceSignature`, only when set, so it needs no migration and is sealed with the rest of the payload in
  crypto-shredding mode. Both stores refuse, as `Invalid`:
  - a malformed signature: a key ID or algorithm outside `[A-Za-z0-9._-]{1,64}`, or a value that is empty or longer
    than 512 bytes;
  - a scope field that is not well-formed UTF-16.

  The store conformance suite checks the round trip and each malformation.
- **Store implementers:** an `IExperienceRecordStore` must persist `ExperienceRecord.ProvenanceSignature` as given.
  One that drops it makes every signed record read back unsigned, and, with signing on, their runs vouch for nothing.
- Documented in [Signing provenance](docs/guide/confidence.md#signing-provenance), in
  [Finalization](docs/guide/finalization.md#signing-provenance), in the deployment wiring, in the
  [security suite](docs/security-suite.md), and in the narrowed KL-11 row of
  [Known limits and documented boundaries](docs/known-limits.md#documented-boundaries).

### Benchmarks

- **The hot paths are measured** (story 12.2). `benchmarks/AgentExperience.Benchmarks` is a BenchmarkDotNet console
  project, in the solution so every build compiles it, never packed and never run by CI (a release test checks that no
  workflow names it). It times retrieval over 1k and 10k records (text-only, with a preferred environment, and with a
  confidence decay policy), the MAF context provider producing an 8-record Historical Reference block with and without
  session tracking, finalization of a captured run with tool calls, and applying confidence evidence, each on the
  in-memory store and on PostgreSQL, with allocations. PostgreSQL runs in one Testcontainers pgvector container,
  started and seeded once; without Docker those cases are skipped with a message. No model is ever called.
- [docs/benchmarks.md](docs/benchmarks.md) says how to run it and holds one committed baseline with the machine, OS,
  runtime and PostgreSQL version it ran on. The numbers compare only with another run on the same machine.
- `BenchmarkDotNet` 0.15.8 and `Testcontainers.PostgreSql` 4.15.0 are exact-pinned in that project only, and recorded
  in the [compatibility evidence](docs/compatibility-evidence.md#test-infrastructure-not-shipped). No shipping package
  or `src/` file changed.

### Tests: upgrades from published previews

- **A database a published preview created now upgrades under test** (story 12.1). The new
  `tests/AgentExperience.Upgrade.Tests` suite starts a PostgreSQL container per preview, lets that preview's own
  packages, restored from nuget.org, create and fill a database, and upgrades it with today's migrators following the
  [runbook](#upgrade-in-this-order). As the application role it then reads every record, lifecycle event, grant,
  grant access row, reuse feedback submission, tombstone and embedding back through today's stores (queries, text
  and vector searches and batch reads included), carries the lifecycle on (a transition, verified and opt-out
  confidence evidence, an expiring grant's purge, a new grant, an erasure, new feedback, a new embedding), and
  compares the upgraded schema with a fresh install of today's, catalog object by catalog object. It covers
  `0.1.0-preview.1` (schema `0001` to `0010`, one role) and `0.1.0-preview.2` (`0001` to `0018`, two roles), in
  plaintext and in crypto-shredding mode; `0.1.0-preview.2`'s schema is also that of `0.1.0-preview.3` and
  `0.1.0-preview.4`. It runs in CI's `postgres` matrix on every supported major. Until now every upgrade test built
  its old database from a prefix of today's scripts, with today's store writing the rows.
- The databases come from one seeder program per preview, under `tests/AgentExperience.Upgrade.Seeders`, each
  exact-pinning that preview's packages. They are not in the solution and never packed, and
  `CompatibilityPinAgreementTests` exempts only that directory from its floor rule, because the pins are deliberately
  old, and holds it to its own: each seeder resolves exactly the preview it names, and the set matches the suite's.
  The suite's [README](tests/AgentExperience.Upgrade.Tests/README.md) lists what it checks, the items whose meaning
  the upgrade changes by design (a `0.1.0-preview.1` grant becomes `LessonOnly`, for example), and how to add a
  preview.
- No migration script, store or package changed: both previews upgrade with their data intact.

## 0.1.0-preview.4

The .NET 10 release: every package now targets `net10.0` only. Nothing else changes for a host already on .NET 10;
a host on .NET 8 or 9 stays on `0.1.0-preview.3`.

### Breaking: .NET 10 only

- **All six packages now target `net10.0` only.** `net8.0` and `net9.0` are gone from every package, and every test
  project, the sample and the experiments build and run on `net10.0` alone. This lands ahead of the date announced
  with `0.1.0-preview.3` (the first preview after .NET 8 and 9 leave support on 10 November 2026), by the
  maintainer's decision. **A host on .NET 8 or 9 stays on `0.1.0-preview.3`**, the last release that targets them.
- **The `net8.0`-only dependencies are gone.** Core no longer references `System.Text.Json` or `Microsoft.Bcl.Memory`,
  and `AgentExperience.Storage.Postgres` no longer references `System.Text.Json`; the `net10.0` shared framework
  supplies every API they did. Every other dependency and floor is unchanged, and the public API is identical.
- **Code that existed only for older runtimes is removed:** the `#if NET9_0_OR_GREATER` fallback in
  `ExperienceIndex.ComputeContentHash` (the hash is unchanged), and the retry in the MAF adapter's open-run registry
  that worked around a `ConcurrentDictionary` bug in the .NET 8.0.0–8.0.10 runtimes (the fix listed under
  `0.1.0-preview.3`), which a `net10.0` assembly can never run on. See
  [Target frameworks](docs/compatibility-evidence.md#target-frameworks).

## 0.1.0-preview.3

Everything since `0.1.0-preview.2`: stories 8.2, 9.1, 9.2, 10.1–10.4, 11.1 and 11.2. It adds a sixth package,
`AgentExperience.Storage.InMemory` (development and tests only, guarded against production use), graded environment
compatibility, a replaceable confidence engine, read-time confidence decay by domain, a capability gate on injection,
a store conformance suite that both stores pass, and the first live-model results: a confirmatory run against Gemini
and an exploratory one against Claude, both concluding that the injected content reduces failed attempts. There are
no new migrations; every new behaviour is opt-in and leaves default results unchanged.

### Fixed

- **An invocation can no longer spin forever on a forgotten run on .NET 8.0.10 and earlier.** On .NET 8.0.0 through
  8.0.10, `ConcurrentDictionary.TryRemove` can report an entry another thread has only just added as absent
  ([dotnet/runtime#107525](https://github.com/dotnet/runtime/issues/107525), fixed in 8.0.11). When that happened as
  the Microsoft Agent Framework adapter forgot a run, the forgotten entry stayed in its open-run ledger, and every
  later invocation naming that run spun at full CPU looking it up again, never returning. The adapter now retries the
  removal for as long as that very entry is still there.

### Store contract clarifications

- **Reordered feedback exposures now converge.** `PostgresExperienceReuseFeedbackStore` and
  `InMemoryExperienceReuseFeedbackStore` sort a submission's exposures by `ExperienceId` before they store it or
  compare a resubmission with it, so the same exposures listed in another order are `AlreadyRecorded` rather than
  `Conflict`, and the submission handed back lists them in ID order, as `RecordedExperienceReuseFeedback.Exposures`
  always documented. Naming one record twice is still `Invalid`. Callers going through Core saw no difference: Core
  already ordered them.
- **`ExperienceLifecycleCommitResult.CurrentStatus` doc corrected.** On `Committed` it is set only for a confidence
  submission that did not move the record (an independence key already taken, or a replayed evidence ID); an
  identical replay of an event that carried no confidence payload reports `null`, as both stores already did.
- **`ExperienceSupersessionCheckResult.ReplacementStatus` doc corrected.** It is `null` on `RecordNotFound`, even when
  the replacement exists in the scope, as both stores already did.
- The store conformance suite now asserts all three instead of leaving them open.

### In-memory storage for development (story 11.2)

- **A sixth package, `AgentExperience.Storage.InMemory`, for development and tests only.** It holds
  `InMemoryExperienceRecordStore` (`IExperienceRecordStore`), `InMemoryExperienceCandidateSource`
  (`IExperienceCandidateSource`, searching that same record store) and `InMemoryExperienceReuseFeedbackStore`
  (`IExperienceReuseFeedbackStore`), all sealed and thread-safe, and copying everything on write into read-only
  snapshots (timestamps in UTC, truncated to whole microseconds, as PostgreSQL stores them), so capture, finalization, retrieval, injection and
  feedback run without PostgreSQL. Data is lost when the process ends, and none of the PostgreSQL guarantees apply
  (append-only enforcement, erasure reach, backups, the two database roles, crypto-shredding); every public type says
  so. This reverses the earlier "never publish an in-memory store" policy, by the maintainer's decision, on the
  condition that the package is guarded and conformance-tested.
- **It passes the whole store conformance suite of story 11.1** on `net8.0`, `net9.0` and `net10.0`
  (`tests/AgentExperience.Storage.InMemory.Tests`), with the suite unchanged, and keeps the confidence ledger's
  evidence-ID idempotency, independence key and single-use assessment.
- **One registration, `AddAgentExperienceInMemoryStorageForDevelopment(configure)`,** registers the three ports as
  singletons, sharing one record store. It runs only when the environment is `Development`, `Test` or `Testing`
  (case-insensitive): the registered `IHostEnvironment`'s name, or with none registered `DOTNET_ENVIRONMENT`, else
  `ASPNETCORE_ENVIRONMENT` (neither set: nothing to check). Anywhere else, `Staging` and blank names included, a hosted
  service it registers stops a Generic Host from starting and resolving a store throws `InvalidOperationException`,
  unless `InMemoryStorageOptions.AllowProductionEnvironment` is set. It throws at registration beside a different
  `IExperienceRecordStore`, and on a second call that passes options. Constructing the stores directly is not guarded.
- **Search matches every query word, without stemming.** Text is normalized (Unicode form KC, case-folded) and split
  into words; a query drops the common English stopwords PostgreSQL's `english` configuration drops and terms shorter
  than two characters, and a record matches only when its task ID, task summary or lesson (first 100,000 characters)
  contains every remaining term. Relevance is the fraction of the terms it contains, weighted towards the task
  summary, in (0, 1]; its values still differ from PostgreSQL's. Candidates come strongest first, then by ID, after
  the scope, status and confidence filters and before the limit.
- **Same validation as PostgreSQL.** The PostgreSQL store's rules for the core ports moved into
  `src/Shared/ExperienceRecordValidator.Shared.cs`, which both packages link, so both stores refuse exactly the same
  requests. The only wording change: a reserved sealed-value prefix or placeholder is now "reserved for sealed values"
  rather than "which the store reserves". No new public API in Abstractions or the PostgreSQL package.
- **The feedback conformance suite also varies a human assessment's reviewer and assessment** (a new
  `HumanAssessedFeedback` fixture), for both stores.
- **Not provided:** sharing grants and the grant access log (every read behaves as if no grant exists), the embedding
  index, deletion, retention and encryption.
- **Dependencies:** Abstractions, `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.12+ and
  `Microsoft.Extensions.Hosting.Abstractions` 10.0.3+ (new, the version the repository already resolves; evidence row
  in `docs/compatibility-evidence.md`). No Core, Npgsql or MAF; `eng/verify-packages.cs` holds it to that.
- **Release checks count six packages:** package verification, the public API baselines (a new
  `AgentExperience.Storage.InMemory.verified.txt`), `release.yml`'s artifact count, and `RELEASING.md`, which also
  documents checking the first push of the new package ID under the Trusted Publishing policy, and the one-time
  API-key push if nuget.org refuses it. Nothing was published.
- The end-to-end sample keeps its own in-memory doubles; the comment that stated the old policy now points at this
  package.

### Anthropic provider for the live experiment

- **An exploratory run against `claude-haiku-4-5` concluded `ReuseBenefitAttributableToContent`**
  ([report](experiments/AgentExperience.LiveReuse/results/anthropic-claude-haiku-4-5-2026-09-28.md)): mean failed attempts 0.83 with memory, 2.75 without it, 2.42
  with the strategy withheld and 2.67 with stale experience (sign tests p = 0.0020 against both no memory and the
  placebo; the stale control p = 0.81). It is exploratory because no Anthropic model is registered, and it cost about
  USD 0.93.
- **`experiments/AgentExperience.LiveReuse` can run against Anthropic (Claude).** Set `ANTHROPIC_API_KEY` (and
  optionally `ANTHROPIC_MODEL`, default `claude-haiku-4-5`); `AGENTEXPERIENCE_LIVE_PROVIDER=anthropic` chooses it when
  another provider's variables are set too, and any two or three configured providers without a choice are refused.
- **Anthropic's own C# SDK**, `Anthropic` exact-pinned `[12.50.0]` in the experiment only, through its
  `AsIChatClient` adapter; not an OpenAI-compatible endpoint. It keeps `Microsoft.Extensions.AI.Abstractions` at
  10.10.0. The key and base URL are set explicitly, the SDK's default retries (two) are kept, and API errors are
  recorded by exception type and HTTP status only.
- **Request differences, recorded in the report's Settings row:** no seed (the API has none), a 4096-token
  `max_tokens` cap per call (the API requires one; other providers are sent none), no thinking. Built-in price for
  `claude-haiku-4-5`: USD 1.00 / 5.00 per million input / output tokens.
- **Exploratory runs are labelled.** The harness now reads `registeredModels` from `preregistration.json` (which is
  unchanged). A run of a provider or model it does not register -- any Anthropic run, or a non-default Gemini model --
  says **EXPLORATORY RUN** in its first lines and its *Run* table, carries `"exploratory": true` in its raw results,
  and is never called a candidate for the confirmatory result. The seed wording no longer claims every seedless
  provider rejects the field.
- **A redundant call after success no longer stops the report.** Claude sometimes made a second `apply_migration`
  call in the response that got the migration live; the stored record and the `Approach:` line keep both, in order.
  The harness took the last strategy as stored and the first on the line as shown, and refused to report. It now
  compares the full ordered sequences (`storedStrategies`, `blockStrategies` in the raw results; joined with ` > ` in
  the report), still refuses a block that differs from its store, and keeps `StoredStrategy`, `BlockStrategy` and
  `Followed` on the first strategy.
- No change to `src/`, to any shipping package, or to the registered design.

### Capability gate for injection (story 10.4)

- **`ExperienceInjectionOptions.ReceivingAgent`**, an optional `ReceivingAgentCapabilities` declaring what the agent
  receiving the Historical Reference can and may do: `AvailableTools` (tool names), `MaxRiskClass`, and
  `ToolRiskClasses` (each tool's host-declared `ToolRiskClass`: `Low`, `Medium`, `High`, `Critical`). With it set, a
  record whose verified approach calls a tool the agent lacks is omitted as the new
  `InjectionOmissionReason.ToolUnavailable`, and one whose approach calls a tool riskier than `MaxRiskClass` as the new
  `RiskClassExceeded`. The tool check runs first, and the first failure decides the reason. Either check is skipped
  when its property is `null`.
- **Risk classes are declared, never inferred.** A tool missing from `ToolRiskClasses` counts as `Critical`, even one
  listed in `AvailableTools`. `ToolRiskClasses` has no effect without `MaxRiskClass`, and `MaxRiskClass = Critical`
  disables the risk check. A recorded call with a null or blank tool name is always unavailable, and `Critical`.
- **Only the tools the `Approach:` line would name** are checked: the verified final attempt's calls, up to
  `MaxApproachToolNames`. Tool names the lesson text mentions are not. A record with no approach passes, and so does a
  borrowed record whose grant withholds the line, so the gate cannot be used to probe a lender's tool names. Tool
  names compare ordinally, whatever comparer the host's collections use.
- **Between the final eligibility re-read and `DecideInjection`**, after `AlreadyDelivered`. It runs after the
  `MaxRecords` cut, so a gated record still takes a record slot and the agent may get fewer records than the limit. A
  gated record is never shown to the host's decision, rendered, charged to a session budget, or recorded as a run
  exposure. Its omission has no detail, so no tool name reaches results or telemetry. A gated borrowed record still
  writes a grant access row, because the store disclosed it on the re-read, as one the host denies does.
- **Passing grants nothing.** The approval boundary still decides every tool call.
- **Validated and snapshotted at construction.** A null, empty or whitespace tool name in either collection, or an
  undefined risk class, throws `ArgumentException`. With `ReceivingAgent` unset, the block, omissions and outcomes are
  unchanged. The new enum members are appended, so existing numeric values are stable.

### Confidence decay by domain (story 10.3)

- **`ConfidenceDecayPolicy`**, an optional retrieval setting. With one configured, each eligible record's `Confidence`
  ranking component is its stored `ReuseConfidence` times `2^(-age / halfLife)`. The half-life is chosen by the
  record's domain: the `EnvironmentFingerprint.Metadata` entry named `DomainKey` (default `"domain"`), matched
  ordinally against `HalfLives` (stored as a read-only copy). The factor is in [0, 1] and reaches 0 only for an age
  vastly beyond the half-life. A domain mapped to `null` never decays. A missing or unlisted domain uses
  `DefaultHalfLife`, whose default of `null` means no decay.
- **Age is the lesson's, not its last activity's.** It is measured from `CreatedAt`, because `UpdatedAt` already drives
  the separate Recency component. A `CreatedAt` in the future (clock skew) decays nothing.
- **Ranking only.** Eligibility, including the `MinimumConfidence` floor, uses the stored value, so decay never
  excludes a record, and it only reorders candidates already fetched under the stored-confidence floor and the
  candidate bound. A read never writes. Decay can change the order of injected records, and the MAF Historical
  Reference block's `Confidence:` line still shows the stored confidence, so it can differ from the decayed value on
  the components line.
- **`RankingComponent.UndecayedValue`**, an optional init property. On the Confidence component it holds the stored,
  normalized confidence whenever a half-life applied, and it is `null` everywhere else. With no policy, scores,
  components, ordering and the golden ranking fixture are unchanged.
- **Validated at construction and on `with`.** A zero or negative half-life throws `ArgumentOutOfRangeException`. A
  blank `DomainKey`, or a null or blank key in `HalfLives`, throws `ArgumentException`.
- **A new `ExperienceRetrievalService` constructor overload** takes an optional policy (`null` means no decay). The
  existing constructors are unchanged, and `AddAgentExperienceRetrieval` resolves a registered
  `ConfidenceDecayPolicy` in any registration order.

### First live result (story 9.1)

- **The first confirmatory run, against `gemini-3.1-flash-lite`, concluded `ReuseBenefitAttributableToContent`**
  ([report](experiments/AgentExperience.LiveReuse/results/gemini-gemini-3.1-flash-lite-2026-09-27.md)). Mean failed attempts were 0.00 with memory, 2.17 without it,
  2.42 with the strategy withheld (placebo) and 2.75 with stale experience. Memory beat both no memory (sign test
  p = 0.0010) and the placebo (p = 0.0005), and the stale control showed no benefit (p = 0.94). Every condition
  verified all 12 tasks. The run cost about USD 0.08 (288 model calls). It is one model, one run, 12 instances and a
  synthetic task; the report lists its limitations.
- **Pre-registration amendment 1**, recorded before any trial completed: the seed is sent only to a provider that
  accepts it. Gemini's OpenAI-compatible endpoint rejects a `seed` field with HTTP 400, which errored the first
  attempt's first two calls; that attempt was stopped and stays in `results/ledger.tsv`, with its partial record.
  `RunDescriptor.SeedSent` carries the choice, and the report says whether the seed was sent.

### Replaceable confidence engine (story 10.2)

- **`IExperienceConfidenceEngine`**, a Core port with `RuleId`, `RuleVersion` and `Score(ExperienceConfidenceInput)`.
  The input is the record as read and the evidence counters after this evidence is applied. The engine supplies only
  the score. Status changes, accepted statuses, counter increments, independence and exposure admission, and replay
  stay in the library.
- **`ReuseConfidenceHeuristicEngine`**, the default (`Instance`, rule ID `reuse-heuristic`, version `1.0.0`), wraps
  `ReuseConfidenceHeuristic.Score`. With no host engine, every stored value, rule version, outcome and event is
  unchanged.
- **The rule is recorded with no migration.** The default engine still records `"1.0.0"` in
  `ConfidenceUpdate.RuleVersion`; a host engine records `"{RuleId}/{RuleVersion}"`, on the lifecycle event and in the
  evidence ledger. Both parts must be 1 to 64 characters from `[A-Za-z0-9._-]`, and a host engine may not use
  `reuse-heuristic`. A violation throws `ArgumentException` when the lifecycle service is constructed.
- **A score is validated, never clamped.** NaN, an infinity, or a value outside [0, 1] throws
  `InvalidOperationException` naming the rule, with nothing written. An engine exception propagates unchanged.
- **`ReadConfidenceAsync`** recomputes a filtered score through the same engine, so with a host engine it can now
  throw when it recomputes: `InvalidOperationException` for a value that is not a score, or the engine's own
  exception.
- **Reuse feedback keeps going.** An engine failure while `ExperienceReuseFeedbackService` submits one exposed
  record fails that record only, reported as retryable like a storage failure; the other records are still
  submitted.
- **Replay compares the rule string.** Retrying evidence stored under a different rule string (for example after
  installing or changing an engine) is reported as `Conflict`. Keep one engine per deployment, and roll a change out
  after in-flight retries drain.
- **A new `ExperienceLifecycleService` constructor overload** takes `confidenceEngine` as its last parameter, after
  `deindexingTimeout` (`null` means the default for either), and
  `AddAgentExperienceCore` resolves a registered `IExperienceConfidenceEngine` once, from the root provider. The
  existing constructors are unchanged. Finalization's initial 2/3 is still the
  heuristic's; a host engine applies from the first evidence onward.

### Graded environment compatibility (story 10.1)

- **`RetrieveExperienceRequest.PreferredEnvironmentAttributes`**, an optional init property. Preferred attributes only
  grade the `EnvironmentCompatibility` ranking component; they never exclude a record. Required attributes still
  exclude a mismatch as `EnvironmentMismatch`, and are applied before anything is scored.
- **`IEnvironmentCompatibilityScorer`**, a Core port called for every eligible record with its `EnvironmentFingerprint`
  and the preferred attributes (an empty dictionary when none are set). Its value is clamped to [0, 1], and NaN counts
  as 0. A scorer that throws fails the retrieval closed: `RetrievalOutcome.Failed` and no records. The
  `RetrievalFailure`'s reason is fixed and content-free; its exception is the scorer's own, passed through as is. An
  `OperationCanceledException` while the caller's token is cancelled propagates unwrapped.
- **`AttributeMatchEnvironmentScorer`**, the default: the fraction of preferred attributes the record's
  `Metadata` carries with an ordinally equal value, and 1.0 when nothing is preferred. With no preferences and the
  default scorer, scores, components, ordering and exclusions are unchanged, and the golden ranking fixture still
  passes as it was.
- **A new `ExperienceRetrievalService` constructor overload** takes an optional scorer (`null` means the default), and
  `AddAgentExperienceRetrieval` resolves a host-registered `IEnvironmentCompatibilityScorer` once, from the root
  provider, so register it as a singleton. It must be thread-safe, and it is not bounded by `RetrievalPolicy.Timeout`:
  the caller's token is checked between records, not during a call. The
  existing constructors are unchanged. `CompatibleEnvironmentScore` stays, now documented as the default scorer's value
  when nothing is preferred.
- MAF hosts set preferred attributes in `ExperienceInjectionOptions.ResolveRequest`, as they already do for required
  ones. The Historical Reference block format is unchanged.

### Live-model reuse experiment (story 9.1)

- **`experiments/AgentExperience.LiveReuse`**, an opt-in console app that runs story 4.4's reuse methodology against a
  real model: Gemini (default `gemini-3.1-flash-lite`) or Azure OpenAI, both through `IChatClient`. The model works a
  migration-rollout task whose working strategy is a hidden property of each service's database; the library
  captures, verifies, reflects on and stores its verified learning runs; then each service's different evaluation
  ticket runs memory-disabled, memory-enabled, as a placebo (the same block with the working strategy withheld), and
  as a negative control with genuine but stale experience injected. Success is decided by the simulated database's
  state, never by a model.
- **Pre-registered** in `preregistration.json` before any live run: 12 instances, the metrics, and the verdict rule --
  a strictly lower mean of failed attempts, an exact one-sided sign test at 0.05, no loss of verified success and no
  rise in refused bypass requests, with a benefit credited to the injected content only if memory-enabled also beats
  the placebo and the negative control shows none. The harness reads every value it executes from the file, which is
  embedded in the build, and a test pins the file's blob id so any later change must be recorded as an amendment. The
  registered model and a first-complete-run rule, enforced by an append-only `results/ledger.tsv`, fix which run is the
  confirmatory one.
- **Configuration from environment variables only**, a hard budget cap on model calls and tokens (default 600 and
  2,000,000) that stops the run cleanly, and a Markdown report plus raw per-trial JSON under `results/` with no key,
  no prompt, and only the endpoint's host (for Azure, without the resource name). With no provider variable set it skips with a message and spends nothing.
  It never runs in CI or in `dotnet test`.
- **One new package, in the experiment only**: `Microsoft.Extensions.AI.OpenAI` `[10.10.0]`, which reaches Gemini's
  OpenAI-compatible endpoint and the Azure OpenAI v1 endpoint alike, and resolves `Microsoft.Extensions.AI.Abstractions`
  to exactly the shipping floor. Nothing under `src/` changes. The comparison with `Google.GenAI` and `Azure.AI.OpenAI`
  is in the experiment's README.
- **`experiments/AgentExperience.LiveReuse.Tests`**, part of the normal suite, proves the harness offline with scripted
  models: a model that follows the block benefits and gains nothing from stale experience or the placebo, a model that
  ignores the block shows identical numbers in every condition, the four conditions' first requests are byte-identical
  once the block is removed, no request ever replays a function call (batched calls, unknown tools and unbindable
  arguments included), the bypass guardrail can fail, the budget cap stops the run, errors are recorded by type only,
  and a fake key appears in no output.
- **`CompatibilityPinAgreementTests`** now also checks the lock files under `experiments/`, so the experiment's graph
  must resolve every floored package to its floor, as the test projects' do.
- The first live result is recorded separately, below the heading "First live result (story 9.1)".

### Release criteria: known limits and documented boundaries

The README's Known limits table is split in two, and the `1.0` gate is redefined. No code changes, and this does
not claim `1.0` or production readiness; it makes the gate reachable. The decision is reversible, and its rationale
is recorded in [`RELEASING.md`](RELEASING.md#decision-known-limits-and-documented-boundaries).

- **Known limits** are unresolved problems a code change could fix, and they block `1.0`. A row still leaves only by
  fixing the limit. The table is now empty.
- **Documented boundaries** are properties the library cannot remove by code, inherent to PostgreSQL, to what
  independence can prove, or to how models work. They do not block `1.0`. Each row states the boundary exactly, why
  no code change can remove it, and what the library does about it. KL-2, KL-11 and KL-12 move here with their
  numbers and their wording unchanged. A row may move from limits to boundaries only with a written reason why no
  code change can remove it, and it returns to the limits table if that reason stops holding.
- **The gate** (`RELEASING.md` step 9, and the same step in `release.yml`) now counts only the rows of the Known
  limits table. Dropping the preview suffix requires that table to be empty and the maintainers' deferred-work ledger
  to be closed; a non-empty table still requires a preview version. The GitHub release notes carry both tables.

### Hygiene: tool names, the session state key, deferred tests (story 8.2)

- **Invisible characters in a tool name no longer reach the `Approach:` line.** Story 6.2 turned every control,
  format, private-use and unassigned code point in an argument *value* into a space, classified per Unicode scalar so
  TAG characters are caught, but left tool *names* alone. Names now go through the same routine, before whitespace is
  collapsed and markers are neutralized: a bidirectional override or isolate, a zero-width character or joiner, any
  other format character (a soft hyphen, a byte-order mark), a TAG-character payload, a C0 or C1 control, a
  private-use or unassigned code point, or a lone surrogate in a name becomes a space, so a name can neither use a
  bidirectional control to reorder the rest of the line when it is displayed nor carry text in those code points, and
  a name made only of them reads `(none recorded)`. A marker split by one of them, even inside a word, is still
  neutralized. Variation selectors, the combining grapheme joiner and Hangul fillers are letters or marks and pass
  through, as they do in argument values; which code points are unassigned follows the running .NET's Unicode data. **Behaviour change, for such names only:** until now TAG characters, controls and private-use
  code points reached the block as they were, and format characters in the Basic Multilingual Plane were removed
  rather than turned into spaces (`read`, a zero-width space and `ledger` rendered `readledger` and now renders
  `read ledger`; an emoji ZWJ sequence now renders with spaces). A name
  that holds none of these characters renders byte for byte as before; the sample's and the reuse baseline's golden
  reports are unchanged.
- **`ExperienceInjectionOptions.SessionStateKey`** (new, additive) sets the `StateBag` key session tracking keeps its
  account under. It defaults to `ExperienceContextProvider.SessionStateKey` (`"AgentExperience.InjectionSession"`),
  so nothing changes unless it is set. Two providers on one agent can now each have their own account (budget,
  deduplication and withdrawals); with the same key, `ChatClientAgent` refuses two of its own providers when it is
  built. The key is
  validated when the provider is constructed: not null or blank, at most `MaxSessionStateKeyLength` (128)
  characters, no whitespace or invisible code point, and not capture's `"AgentExperience.RunId"`. Changing the key of
  a deployed provider starts existing sessions afresh. `ExperienceContextProvider.StateKeys` now returns the
  configured key.
- **Tests for paths that had none:** the sealing function's `Deleted` (plaintext and sealed tombstones) and
  `AlreadySealed` outcomes, and a feedback replay after every sealed rationale copy has become unopenable (story
  6.4); `ReadConfidenceAsync`'s revision cut-off under a race with a later commit, and its refusal of a history cursor
  that stays put or goes back (story 7.3). Each was checked by breaking the code it covers.

### Planned: `net8.0` and `net9.0` leave the matrix

.NET 8 and .NET 9 leave support on 10 November 2026. The first preview published after that date removes the
`net8.0` and `net9.0` targets from all six packages, with the `net8.0`-only `System.Text.Json` and
`Microsoft.Bcl.Memory` references; a host still on either must stay on an earlier preview or move to .NET 10. Nothing
is removed in this release. PostgreSQL 14 (end of life 12 November 2026) stays outside the supported matrix, as it
is now. See [Target frameworks](docs/compatibility-evidence.md#target-frameworks).

## 0.1.0-preview.2

Everything since `0.1.0-preview.1`: stories 3.6, 5.1–5.6, 6.1–6.6 and 7.1–7.3. It adds migrations `0011` to `0018`
(there is no `0014`), a supported two-role deployment, opt-in crypto-shredding, verified and exposure-bound confidence
evidence, session tracking for reused MAF sessions, argument values on the `Approach:` line, and a wider supported
matrix (`net8.0`, `net9.0` and `net10.0`; PostgreSQL 15 to 18; MAF `[1.22.0, 2.0.0)`).

### Known limits resolved or narrowed

`0.1.0-preview.1` shipped sixteen known limits. This preview resolves thirteen and narrows the other three, which stay
in the table (see [Known limits](docs/known-limits.md#known-limits)).

- **Resolved:** KL-1 (story 5.6), KL-3 and KL-10 (5.4), KL-4 (6.1), KL-5 and KL-6 (5.5), KL-7 (5.3), KL-8 (6.2, then
  7.1), KL-9 (3.6), KL-13 (6.3, then 7.2), KL-14 and KL-15 (5.1), and KL-16 (5.2).
- **Narrowed, not closed:**
  - **KL-2** (story 6.4). In encrypted mode a `pg_dump`, a base backup, a replica, the WAL and the dead heap tuple
    hold only ciphertext that nothing opens once the record is erased. What remains in every copy is the derived
    search data PostgreSQL reads in the clear: the full-text vector (task ID, summary and lesson as lexemes with
    positions) and the embedding with its content hash. So do the identifiers and metadata that are never sealed,
    and anything written before the switch. Plaintext mode is unchanged, and the property is only as good as a key
    store kept outside the database's backups, whose own backup retention bounds the erasure.
  - **KL-11** (stories 6.6 and 7.3). What remains: exposure means the library *delivered* a record into a run, not
    that the run used it, so each run given a lesson is one key; a host that calls `RecordExposure` for records it
    did not deliver, or marks a hand-written record `Finalized`, is believed; the round is the `ClosedRound` the host
    passed to finalization, and a direct aggregator caller's run ID is still its own statement (its result counts
    only through finalization or such a marked record); whoever holds the assessment token key, or can call the
    issuer, can mint; a retry made after its token expired is refused although the original landed; and the opt-out
    still trusts everything — its evidence is labelled and excludable on read, but the stored score retrieval ranks
    on still counts it.
  - **KL-12** (story 6.5). What remains: the earlier block stays in the session's history, verbatim, and a model that
    already read it cannot be made to forget it, so a withdrawal notice is advisory. The provider cannot strip its
    own earlier blocks, and the tracking is only as trustworthy as the host's session storage (removing the key
    resets it; concurrent invocations on one session race on it).

### Upgrade, in this order

From `0.1.0-preview.1`, whose schema ends at `0010`. Steps 1 to 5 are one maintenance window: once `0011` is applied
an old build can no longer write grant events or access rows, and once ownership moves (step 2) the application's
role cannot read or write any table until step 4 grants it the manifest. Step 6 is optional and comes later.

0. **Optional, before the window, on a large database:** build the new indexes out of band, as the role that owns
   the tables at the time. The headers of `0012` (`ix_experience_grant_access_retention`), `0015` (the
   `assessment_id` column, then `ux_confidence_evidence_assessment`) and `0016` (the `search_vector_sealed` column,
   then `ix_experience_records_search_sealed` and `ix_experience_records_unsealed`) have the `CREATE INDEX
   CONCURRENTLY` statements. Check each with `pg_index.indisvalid` and drop and retry an invalid one: `IF NOT EXISTS`
   would otherwise accept it.
1. **Stop every process running `0.1.0-preview.1`**, and keep it stopped until step 5.
2. **Create an owner role and move ownership to it, as a superuser** (story 6.1), using the SQL in
   [Store: upgrading an existing single-role database](docs/guide/deployment.md#upgrading-an-existing-single-role-database).
   It creates the owner, grants it the one superuser-only privilege a non-superuser migrating role needs —
   `GRANT SET ON PARAMETER agent_experience.purge_authorized, agent_experience.access_purge_authorized` — and moves
   ownership of the database, the `agent_experience` schema and every table, sequence and function in it. The role
   your application uses today stays the application role, with the same credentials. The purge functions stay
   `SECURITY DEFINER` and now run as the new owner; the transfer takes a brief `ACCESS EXCLUSIVE` lock on each table.
3. **Run the schema migrator as the owner** (`ExperienceSchemaMigrator.MigrateAsync`), then
   `ExperienceVectorSchemaMigrator.MigrateAsync` if you use the vector channel. It applies `0011` to `0018`; no
   journaled script is edited and none rewrites a row. Most new checks on existing tables are added `NOT VALID`, so
   nothing large is scanned; each such header has the `VALIDATE CONSTRAINT` statement to run later, at a time of your
   choosing. On a busy database, give the owner a `lock_timeout` (for example
   `ALTER ROLE <owner> SET lock_timeout = '5s'`) and retry on timeout, rather than let an `ACCESS EXCLUSIVE` request
   queue behind a long reader. **Every existing sharing grant becomes `LessonOnly` the moment `0011` applies** (see
   Behaviour changes). From here on the migrator never runs with the application's credentials.
4. **Call `ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync` as the owner**, after both migrators,
   naming the application role, and again on every deploy. Set `AllowErasure` if the application deletes records,
   sweeps retention or purges expired grants, and `AllowAccessLogPurge` if it purges the access log; without them
   those calls fail with a permission error. Leave `AllowSealing` off.
5. **Deploy `0.1.0-preview.2`, then start it.** Never before step 3: this build reads `0011`'s `disclosure`, `0016`'s
   `search_vector_sealed` (in both modes) and `0017`'s `approach_arguments`, so against an older schema every
   grant-joined read and every text search fails with `42703`. Before starting it, decide:
   - **Exposure.** An agent built with both `ExperienceContextProvider` and `UseExperienceCapture` records what it
     delivers with no change. A host that injects records some other way calls
     `IExperienceCaptureService.RecordExposure(runId, exposures)` from the code that delivers them, before the run
     completes. Without either, confidence evidence and attributed feedback about reuse in that run are refused.
   - **Human assessments.** A host that records them registers an assessment token key,
     `services.AddSingleton(new ExperienceIndependenceOptions { AssessmentTokenKey = ... })` (at least 32 random
     bytes from your secret store), and calls `AssessmentTokenIssuer.Issue` from its review flow.
   - **Or opt out** with `ExperienceIndependenceOptions.Verification = IndependenceVerification.TrustHostSuppliedIdentifiers`
     if you capture and retrieve in different scopes, finalize nothing, or must keep accepting evidence about runs
     finalized before this version. Its evidence is labelled `HostTrusted`.
   - **Session tracking** is on by default. A host whose chat history drops injected blocks sets
     `ExperienceInjectionOptions.SessionLimits = null`, and `Limits.MaxBytes` must be at least
     `HistoricalReferenceWriter.RetractionBlockBytes` while it is on.
   - **Grants.** To restore a borrowed record's `Approach:` line, the owner revokes the grant and issues it again as
     `LessonAndApproach`; to show selected argument values too, as `LessonApproachAndArguments`, naming the keys,
     which the recipient allowlists as well. Review any dotted key already in an `ApproachArguments` allowlist.
6. **Optional: turn on crypto-shredding** (story 6.4). Plaintext mode stays the default.
   1. Stand up a key store outside the database's backup domain: `EnvelopeExperienceKeyStore` over your KMS
      (`IExperienceKeyEncryptionKey`) and a durable repository for wrapped keys (`IExperienceWrappedKeyRepository`)
      that does not share the database's backups.
   2. Give every PostgreSQL component the same `ExperienceEncryption`
      (`services.AddAgentExperiencePostgresEncryption(keyStore)`, or the new constructor parameter) and deploy. New
      records and ledger rows are sealed from here on.
   3. Re-apply the application role's privileges with the **same options plus `AllowSealing = true`** (the call is
      declarative: leaving out `AllowErasure` takes erasure away), and seal the existing records with
      `SealPlaintextRecordsAsync`, batch by batch, until `MoreRemain` is `false` for every project root.
   4. Take `AllowSealing` away again on the next deploy: it is a content-rewrite power over plaintext records.
   5. Run `VACUUM` on `agent_experience.experience_records` and age out pre-upgrade backups. Copies made before the
      records were sealed are plaintext; ledger rows and grant reasons written before step 6.2 stay plaintext until
      their record is erased.

A host that skips steps 2 and 4 keeps working as a single-role deployment. That is fine for local development and
tests, but it gets none of the two-role guarantees and is not a supported production deployment.

### Breaking changes

- **`RequiredCheck` must name an evidence kind** (KL-5). `new RequiredCheck("id")` no longer compiles. To accept any
  kind, pass `RequiredCheck.AnyKind` (`"*"`).
- **An evaluation is bound to its run** (KL-6).
  - `VerificationAggregator.Aggregate` now takes `runId` as its first argument.
  - `VerificationResult` can only be produced by the aggregator: it has no public constructor (so it can no longer be
    deserialized), and its properties are get-only.
  - `ReflectionRequest.Run` and `ReflectionRequest.Evaluation` are get-only. A mismatched pair throws
    `ReflectionBindingException`, so a run whose own outcome disagrees with the evaluation fails at request
    construction rather than inside the default reflector.
  - Finalization quarantines a reflection that does not match its request.
- **Confidence evidence the library cannot verify is refused by default** (stories 6.6 and 7.3, KL-11) with
  `ConfidenceUpdateOutcome.Unverified` and an `IndependenceRefusal`; an attributed feedback submission records the
  failing attribution with benefit `Unknown`.
  - Machine evidence about a run finalized before this version is refused (`UnknownRound`: its record carries no
    closed round), and so is evidence about a run captured and retrieved in different scopes (`UnknownRun`).
  - Evidence about a run with no recorded exposure is refused (`NotExposed`). That includes every run finalized
    before this version (no record carries exposures yet) and every run captured without something calling
    `RecordExposure`.
  - A record read back without an origin is `HostWritten`, so a record stored before this version, or written by hand
    through `CreateAsync`, vouches for no run (`HostWrittenRun`). A host that writes records itself and wants them to
    count sets `Origin = ExperienceRecordOrigin.Finalized` — and is then making that statement.
  - **Evidence naming the record's own source run is refused in every mode** (`OwnRun`). It was a documented rule that
    nothing enforced.
  - **A human assessment needs an assessment token.** Without one (or with an invalid one, or an `AssessmentId` that
    is not the token's) the feedback is recorded with benefit `Unknown`; a direct `Human` submission is `Unverified`.
    A token spent on a record is refused for any other evidence ID, including a second feedback submission. `Machine`
    evidence carrying an `AssessmentToken` is `Invalid`.
  - `IndependenceVerification.TrustHostSuppliedIdentifiers` opts out of all of this except `OwnRun`.
- **New enum members; an exhaustive `switch` needs the arms:** `ConfidenceUpdateOutcome.Unverified` (9),
  `IndependenceRefusal.NotExposed` (9) and `.HostWrittenRun` (10), `ExperienceCaptureFailureStage.RecordExposure` (5),
  `InjectionOutcome.Retracted` and `.SessionBudgetExhausted`, `InjectionOmissionReason.AlreadyDelivered` and
  `.OverSessionBudget`, and `ExperienceGrantDisclosure.LessonApproachAndArguments`.
- **Binary break for code compiled against the previous preview: recompile.** These gain a trailing optional
  positional parameter, which changes their constructor and `Deconstruct` signatures (source that names its arguments,
  or omits the new one, compiles unchanged):
  - `ApplyConfidenceEvidenceRequest` (`AssessmentToken`), `ApplyConfidenceEvidenceResult` (`Refusal`) and
    `HumanReuseAssessment` (`AssessmentToken`);
  - `ExperienceGrant`, `ExperienceGrantRequest` (`ApproachArguments`), `ExperienceRecordGetResult`,
    `RankedExperience` and `ExperienceInjectionDecisionContext` (`GrantApproachArguments`);
  - the PostgreSQL record store, candidate source, grant store, reuse-feedback store and embedding index
    (`ExperienceEncryption? encryption`).
- **Other signature and shape changes.**
  - `ExperienceLifecycleService` gains a constructor taking `ExperienceIndependenceOptions` and an optional
    `IExperienceCaptureService`; the existing constructors verify, with no key and no capture service.
  - `IExperienceCaptureService` gains `RecordExposure`, with a default interface implementation that records nothing
    and returns `NotSupported`: an existing implementation compiles, and its runs are exposed to nothing.
  - `StartRun` throws `ArgumentException` for a provenance whose `ExposedTo` is not empty: exposure is recorded, not
    claimed up front.
  - `Provenance` gains the init property `ExposedTo` and value equality that compares it element by element;
    `ExperienceRecord` gains `ClosedRoundId` and `Origin`; `ConfidenceUpdate` gains `AssessmentId` and `Admission`.
  - **Record equality:** `ApproachArguments` is a dictionary, compared by reference in the generated `Equals`, so two
    reads of the same `LessonApproachAndArguments` grant are no longer equal as whole records. Compare its fields
    instead.
- **Stricter validation.** A `Limits.MaxBytes` below `HistoricalReferenceWriter.RetractionBlockBytes` is refused while
  session tracking is on (the provider's constructor throws `ArgumentException`). The PostgreSQL store refuses
  `ExperienceRecord.ClosedRoundId == Guid.Empty`, an `AssessmentId` on machine evidence, and an `ExposedTo` with an
  empty ID, a negative revision, a duplicate record or more than 256 entries (`Invalid`), and text in the sealed
  format (see Behaviour changes).
- **For `IExperienceRecordStore` implementers:**
  - persist `ExperienceRecord.ClosedRoundId` (a store that drops it makes every machine submission `UnknownRound`),
    `Provenance.ExposedTo`, `ExperienceRecord.Origin` (a store that drops it makes every run `HostWrittenRun`) and
    `ConfidenceUpdate.Admission`;
  - refuse a second piece of evidence presenting the same `ConfidenceUpdate.AssessmentId` for a record with `Conflict`
    and an error on `ConfidenceUpdate.AssessmentIdPath` (a store that ignores it leaves tokens replayable, though each
    replay still meets the independence key);
  - a store (or test double) that reports `LessonApproachAndArguments` must also report `GrantApproachArguments`, and
    a `PermittingGrantId`, or no borrowed value is shown.
- **Two-role deployments:** without `AllowErasure` or `AllowAccessLogPurge`, the calls those options cover fail with a
  permission error.

### Behaviour changes

- **Existing sharing grants become `LessonOnly`** (story 3.6, KL-9). A borrowed record loses its `Approach:` line
  until its owner revokes the grant and issues it again as `LessonAndApproach`. The level is immutable, and the
  recipient has no access between the two calls.
- **Every run is bounded from the moment it opens** (KL-7). This includes the default single-invocation path.
  - An invocation still running after one `MaxOpenRunDuration` period (5 minutes by default) is handed the bound.
  - At twice that duration, its run is completed as `Cancelled` and the closure is reported. The invocation's answer
    is untouched, but its attempt is refused.
- **Retention sweeps count only what they erase.** `DeletedCount` no longer includes records that another caller
  erased first.
- **The injection eligibility check re-checks its time limit before each record and after the last decision**, from
  elapsed time as well as its timer. A slow host decision can no longer slip a record in after the limit.
- **Session tracking is on by default** (story 6.5, KL-12): a reused `AgentSession` no longer gets the same record
  twice, and its injection is bounded. With a session supplied, `ExperienceContextProvider` keeps an account in the
  session's `StateBag` under `ExperienceContextProvider.SessionStateKey` (`"AgentExperience.InjectionSession"`):
  record IDs, revisions and counters, never content. It is on by default because KL-12 is a safety limit and MAF's
  `ChatClientAgent` keeps every injected block in the session's history. A host that uses a fresh session per task
  sees no difference in what is injected; it still gets the state key written, and the `Limits.MaxBytes` floor.
  - A record revision the session already holds is omitted as `AlreadyDelivered` and takes no slot; a strictly newer
    revision is injected again.
  - A session is given at most 32 record deliveries and 64 KB of Historical Reference across its invocations
    (`ExperienceInjectionOptions.SessionLimits`). When that cannot take another record, retrieval is not run and the
    outcome is `SessionBudgetExhausted`; a record the byte budget drops is `OverSessionBudget`.
  - A delivery is charged only when MAF reports the invocation succeeded. A failed invocation's records are delivered
    again. A stage nothing settled (an abandoned stream) is charged at the next invocation, but its records may be
    delivered again and its notices stay owed, because MAF kept no history for it.
  - Every record a session holds is re-checked on every invocation, and one no longer valid gets a withdrawal notice
    (see Added). A session whose resolver changes scope withdraws what the new scope cannot read: held records are
    re-checked in the current request's authorization and scope, and the account keeps no scope.
  - A session state that does not validate injects nothing: the invocation reports `Failed` and leaves the value as
    it is, until the host removes the key. So does a withdrawal re-check that throws or times out, until it succeeds.
  - **Who should turn it off:** a host whose chat history drops injected blocks (for example a `ChatHistoryProvider`
    written to follow the old advice for KL-12, or a chat reducer that trims old messages) should set
    `SessionLimits = null`, or deduplication hides a record the model no longer sees. `null` writes no state and
    restores the previous blocks, omissions and outcomes exactly.
- **Marker and label matching is looser for every field** (all record text, not only notices). A marker now matches
  across any run of whitespace or line break and with dash look-alikes, invisible format characters (zero-width
  spaces, bidirectional controls) are removed from stored text, every Unicode line separator is a line break, and a
  label matches after leading whitespace. Record text without such characters renders byte for byte as before.
- **An existing `ApproachArguments` key that contains a `.` can now show more** (story 7.1). Such a key was already
  valid, and matched only an argument literally named that way; a call without one showed nothing. It is now also
  read as a path, so `["options.mode"]` shows the nested `options.mode` of a record the reader owns. Review any dotted
  key you allowlisted.
- **A session withdraws borrowed argument values more eagerly.** A delivery whose line showed a borrowed record's
  values is withdrawn when the record is next read through any other grant — even a reissued one at the same level —
  or at a level that shows no values. Session state records the grant (`grantArgs`) only on a delivery that actually
  showed a borrowed value, so a state that never did is byte for byte what an older build writes; a state that did is
  refused by an older build (it rejects unknown members), which then injects nothing and says so rather than
  forgetting the delivery.
- **A borrowed value needs a named grant.** A store that reports `LessonApproachAndArguments` without a
  `PermittingGrantId` shows no borrowed value, because the session could not tell a later switch of grants.
- **Text in the sealed format is refused on write, in both modes** (story 6.4). A lifecycle reason, confidence
  detail, grant or revocation reason, or feedback rationale beginning with `aexp-sealed:v1:` is `Invalid`, and so is a
  rationale that is exactly `(sealed)`: the store opens a value with that prefix as ciphertext.
- **A sealed record cannot be erased without its key.** `0016`'s guard refuses to tombstone a `payload_version = 2`
  row unless the erasing transaction declares that it destroys the key, so a process left without an
  `ExperienceEncryption` fails its delete (`42501`) instead of reporting `Deleted` while the key survives.
- **`ExperienceReuseFeedbackService` checks an attribution's run** (known, and not an attributed record's own), round,
  token and exposure before writing its ledger, and records a failing one with benefit `Unknown` and the reason, as it
  does every other attribution failure.

### Added

**The two-role deployment** (story 6.1, KL-4) is now the documented and supported one. An owner role owns the schema
and runs the migrators. A separate application role, which the stores connect as, owns nothing and holds exactly what
the stores need: no `ALTER TABLE`, so it cannot disable a trigger or replace a guard function; no `DELETE`,
`TRUNCATE` or `UPDATE` on any ledger, so a purge marker it sets by hand admits nothing; `UPDATE` only on the columns
the store moves, so it cannot write a tombstone by hand; and `EXECUTE` on the purge functions only when the host opts
in. The owner role and superusers remain unbound, which is inherent in PostgreSQL. See
[Store: deploying with two roles](docs/guide/deployment.md#deploying-with-two-roles).

- `ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync` and `ExperienceApplicationRoleOptions`
  (`AllowErasure`, `AllowAccessLogPurge`, `AllowSealing`). In one transaction, the call:
  - refuses a missing role, an unmigrated schema, a superuser, the caller itself, any role that is or is a member of
    an owner of the database, the schema or an object in it, and any role that reaches a superuser, a server-file
    role, `pg_maintain` (PostgreSQL 17 and later) or `SET` on `session_replication_role`;
  - revokes everything the application role holds in the schema;
  - grants the stores' manifest. Every `UPDATE` is column-level, including on `experience_embeddings`;
  - verifies the role's effective privileges, including `CREATE` on the schema and grant options, across every role
    the application role can `SET ROLE` to, and rolls back on any difference. On PostgreSQL 17 and later that
    includes `MAINTAIN` on any table in the schema.
- `0013_role_separation_hardening`: `search_path = pg_catalog, agent_experience, pg_temp` on the three purge
  functions and on every guard trigger function, and `EXECUTE` on the purge functions revoked from `PUBLIC` again.

**Crypto-shredding** (story 6.4, KL-2), an opt-in mode in which erasure reaches every copy of a record's text. See
[Store: crypto-shredding](docs/guide/crypto-shredding.md).

- **`ExperienceEncryption`** (Storage.Postgres): with it, every free-text column erasure removes is stored as
  AES-256-GCM ciphertext under a per-record data key. That covers the record payload together with the task ID,
  lifecycle reasons and confidence detail, evidence detail, grant reasons, revocation reasons and grant event reasons,
  and the reuse-feedback rationale, sealed once per exposed record. The associated data binds every value to its
  column, row, record and all six scope fields; a value that fails its tag throws and nothing decrypted is returned.
  `DeleteAsync` and the sweep destroy the key inside the erasure's transaction, after every database-side check and
  before the commit: a record never looks erased while its key survives, and never looks live once its key is gone.
  A sealed row read without a key it ever had is a configuration error, never "erased".
- The record store, candidate source, grant store, reuse-feedback store and embedding index each gain a trailing
  optional `ExperienceEncryption? encryption` constructor parameter (`null` is plaintext mode), and the DI extensions
  pick up a registered `ExperienceEncryption`; `AddAgentExperiencePostgresEncryption(keyStore)` registers one. The
  grant-store and feedback-store overloads taking a data source now register a factory rather than an instance, so
  the encryption is picked up however the registrations are ordered.
- **The key-custody port** (Abstractions): `IExperienceKeyStore` (`CreateKeyAsync` get-or-create, `GetKeyAsync`,
  `DestroyKeyAsync`, scoped to one record's `ExperienceKeyReference`; a destroyed reference is destroyed for ever),
  `ExperienceKeyLookup`, `ExperienceKeyStatus`, `ExperienceDataKey`, and for envelope encryption
  `IExperienceKeyEncryptionKey`, `IExperienceWrappedKeyRepository`, `ExperienceWrappedKey` and
  `ExperienceWrappedKeyEntry`.
- **`EnvelopeExperienceKeyStore`** (Core), with `RewrapAsync` for KEK rotation (bounded, resumable, and never writing
  back a key destroyed meanwhile), and the development-only reference implementations
  `LocalExperienceKeyEncryptionKey` and `InMemoryExperienceWrappedKeyRepository`. No new package reference anywhere:
  BCL `AesGcm` only.
- **`PostgresExperienceRecordStore.SealPlaintextRecordsAsync`** and `ExperienceSealingResult`: the upgrade job. It
  seals existing plaintext records oldest first, in bounded batches, `Exact` or `Subtree`, authorized like a sweep.
  Each record is sealed in its own transaction, and the seal is opened and compared before it is written. Nothing the
  record answers changes: its revision, ranking, embedding and history stay as they were.
  `ExperienceApplicationRoleOptions.AllowSealing` grants `EXECUTE` on `0016`'s `seal_experience_record`, and the
  privilege manifest and its verification cover it. It is the `record.seal` telemetry operation, carrying
  `agentexperience.sealed_count` and `scope_match` only.
- `0016_crypto_shredding`: `search_vector_sealed` (the full-text vector of a sealed record, from exactly `0003`'s
  expression, so a sealed record ranks as its plaintext twin), `reuse_feedback_exposures.rationale_sealed`, two partial
  indexes, three `NOT VALID` sealed-shape checks, a trigger clearing the sealed vector on erasure, and the sealing
  function. It touches no row.

**Verified, exposure-bound confidence evidence** (stories 6.6 and 7.3, KL-11). See
[Updating confidence from evidence](docs/guide/confidence.md).

- **Verified independence keys.** `ExperienceLifecycleService.ApplyEvidenceAsync`, and so every attributed feedback
  submission, refuses with `ConfidenceUpdateOutcome.Unverified` and an `IndependenceRefusal` an independence key it
  cannot vouch for, checking the run, then the round or token, then exposure, for machine and human evidence alike:
  - a `RunId` that is not finalized into a record in the evidence's scope nor held by the capture service
    (`UnknownRun`), or is the record's own source run (`OwnRun`);
  - a machine `VerificationRoundId` other than the round finalization closed for that run (`UnknownRound`);
  - human evidence without a valid assessment token: missing, forged, for another scope, run, reviewer, direction or
    record, expired, or already spent on this record (`AssessmentToken*`), or with no key configured;
  - a run whose recorded exposure does not include the record at or before its current revision (`NotExposed`), and
    a run known only through a hand-written record (`HostWrittenRun`).
- `ExperienceIndependenceOptions` (`Verification`, `AssessmentTokenKey`, `AssessmentTokenLifetime`, `TimeProvider`),
  `IndependenceVerification`, `IndependenceRefusal`, `AssessmentTokenIssuer` and `IssuedAssessmentToken`.
  `AddAgentExperienceCore` wires the registered capture service and options into the lifecycle service. It
  deliberately does not register the issuer: anything that can resolve it can mint, so the review flow constructs it.
- An assessment token is HMAC-SHA256 under the host's key, over its ID, issue time, direction and records and over
  the scope, run and reviewer. It is compared in constant time, carries its signed expiry (a day by default, at most
  30), and is spent once per record in the same transaction as the evidence it lands. It never appears in
  `ToString()`.
- `ExperienceRecord.ClosedRoundId`, stamped by finalization from the evaluation's `VerificationBasis.ClosedRound` and
  stored in the payload (`closedRoundId`, omitted when null). `ConfidenceUpdate.AssessmentId`, stored in
  `confidence_evidence.assessment_id` and `lifecycle_events.confidence_assessment_id` and read back with history.
- **Exposure is recorded on the captured run.** `Provenance.ExposedTo`, a list of `RunExposure` (record ID and
  revision, never content), one entry per record at the earliest revision it was delivered at, at most
  `RunExposure.MaxPerRun` (256). `IExperienceCaptureService.RecordExposure` adds to it on a still-open run
  (`RecordExposureOutcome`: `Recorded`, `DuplicateNoOp`, `Conflict` once the run is completed, `RunNotFound`,
  `CapacityExceeded`, `NotSupported`). The MAF context provider records every record it injects, at the revision it
  rendered, on the run `UseExperienceCapture` captures the invocation as — only when that run is its own agent's, so
  an uncaptured agent nested inside a captured one exposes nothing to the outer run; a failure, including a capture
  service that returns `NotSupported`, is reported through `OnCaptureFailure` at the new stage `RecordExposure`. What
  a reused session's history carries from an earlier run is not credited to a later run: the session account is host
  storage, unauthenticated.
- **Finalization carries it onto the record** and marks the record `ExperienceRecord.Origin = Finalized`
  (`ExperienceRecordOrigin`: `HostWritten`, the default, and `Finalized`). The PostgreSQL store keeps both in the
  payload as optional version-1 fields (`provenance.exposedTo`, `origin`), sealed in crypto-shredding mode.
- **Which mode admitted each piece of evidence is recorded.** `ConfidenceUpdate.Admission`
  (`ConfidenceEvidenceAdmission`: `Verified`, `HostTrusted`; `null` when none was recorded — evidence stored before
  this version, or an update written by something other than Core), stored in `confidence_evidence.admission` and
  `lifecycle_events.confidence_admission`, read back on replay and in history, and never part of a replay's content
  comparison.
- **`ExperienceLifecycleService.ReadConfidenceAsync(authorization, scope, experienceId, filter, ct)`**, with
  `ConfidenceEvidenceFilter` (`All`, `ExcludeHostTrusted`, `VerifiedOnly`), `ConfidenceReadResult`, `ConfidenceReport`
  and `ConfidenceAdmissionCounts`: a score recomputed from the stored counters less the excluded evidence, read from
  the record's history. `VerifiedOnly` also leaves out a hand-written record's initial counters (the report carries
  `Origin` and `Initial`). It writes nothing.
- **Telemetry:** `confidence.apply` spans carry `agentexperience.confidence.admission` on `Applied` (the admission the
  stored evidence carries), and `agentexperience.independence.refusal` on `Unverified`. Both are span attributes only;
  the metric dimensions are unchanged.
- `0015_verified_independence`: the two `assessment_id` columns, human-only `CHECK`s (`NOT VALID`), and the unique
  index `ux_confidence_evidence_assessment` on `(experience_id, assessment_id)`. `0018_evidence_admission`: the two
  admission columns and two `NOT VALID` checks. Neither adds a table, function or grant, so the application role's
  manifest is unchanged.

**Injection into MAF** (stories 6.2, 6.5 and 7.1, KL-8 and KL-12). See
[Adapter: showing selected argument values](docs/guide/injection.md#showing-selected-argument-values)
and [Adapter: reused sessions](docs/guide/injection.md#reused-sessions-a-budget-no-repeats-and-withdrawal-notices).

- **`ExperienceInjectionOptions.ApproachArguments`: selected argument values on the `Approach:` line.** A host can
  allowlist, per tool name, the argument keys whose values the injected `Approach:` line shows, for example
  `ApproachArguments = { ["run_incident_check"] = ["strategy"] }`, which renders
  `run_incident_check(strategy="wait-for-lock")`. It is empty by default, and without it the block is byte for byte
  what it was. `HistoricalReferenceWriter.Write` gains an overload that takes the same allowlist. A malformed
  allowlist is refused when `ExperienceContextProvider` is constructed, and the provider keeps a copy.
  - Only keys named for that exact tool are read, and only the value the capture-time sanitizer stored, so a redacted
    value stays redacted.
  - Only a string, number, boolean, null or enum name is shown. An object or array becomes `(not shown: not a string,
    number or boolean)`.
  - A string has invisible characters (per Unicode scalar) and whitespace collapsed and trimmed, its markers
    neutralized, its double quotes turned into single quotes and `->` broken up, and is cut to 64 characters and
    quoted. A line's arguments are capped at 512 characters in total, and the byte budget still drops whole records.
- **Dotted paths:** an `ApproachArguments` key such as `options.mode` or `targets.0` walks, by lookup only, into an
  object- or array-valued argument and shows the scalar it ends on, under every bound above; a path ending on a
  container shows the not-shown marker. A key that exists literally at the top level is matched first.
- **Borrowed argument values:** `ExperienceGrantDisclosure.LessonApproachAndArguments` (Abstractions), a third,
  immutable grant level; existing member names and values are unchanged. `ExperienceGrantRequest` and
  `ExperienceGrant` gain `ApproachArguments` — per tool, the argument keys the owner consents to show — required at
  the new level and `Invalid` at any other, bounded by `ExperienceGrant.MaxApproachArgumentTools` (32),
  `MaxApproachArgumentKeysPerTool` (16), `MaxApproachArgumentToolNameLength` (256) and `MaxApproachArgumentKeyLength`
  (64), with the same key rules as the injection allowlist. Under the new level the `Approach:` line shows a borrowed
  record's value for a key both the grant and the reader's `ApproachArguments` name for the same tool, and ends with
  `HistoricalReferenceWriter.ApproachGrantArgumentsSuffix`; a malformed or missing owner allowlist shows nothing.
  Under `LessonOnly` and `LessonAndApproach` a borrowed record still shows no argument value.
- `ExperienceRecordGetResult.GrantApproachArguments`, `RankedExperience.GrantApproachArguments` and
  `ExperienceInjectionDecisionContext.GrantApproachArguments`: the owner's keys, read from the same grant row as the
  level, only at the new level and only for a borrowed record.
- `0017_grant_argument_disclosure`: `experience_grants.approach_arguments jsonb NULL`, with checks that it is present
  exactly at the new level and is a non-empty object of non-empty string arrays, the three `*_disclosure_known`
  checks widened, and `approach_arguments` among the monotonicity trigger's pins (restated with its `search_path`
  pin). The owner's keys are names only and are stored in the clear in encrypted mode as well: do not put anything
  secret into a tool name or an argument key.
- **Withdrawal notices.** Every record a session holds is re-checked on every invocation, in one `GetManyAsync` call
  declared `ScopeCheck`, so no access row is written for it. One that is no longer readable in scope (erased, deleted,
  grant revoked or expired), no longer in an eligible status (revoked, superseded, quarantined, contested), below the
  confidence floor, past `MaxAge`, or read through a grant that now withholds the approach the session was shown gets
  a fixed notice, once, ahead of any new record: `Withdrawn: experience <id>, delivered earlier in this conversation,
  is withdrawn and is no longer valid reference material.` inside a `--- WITHDRAWN ---` section. It carries no reason
  and no content. Record text cannot forge one structurally: the section markers and the wording are block markers
  and `Withdrawn:` is a field label. Notices take the block budget first, no record is written while one is still
  owed, and the session budget never refuses one. A re-check that throws injects nothing and withdraws nothing; one
  the store answers with a refusal or no row withdraws.
- `ExperienceInjectionSessionLimits`, `ExperienceInjectionOptions.SessionLimits`, `ExperienceInjectionSessionUsage`,
  `ExperienceInjectionResult.RetractedExperienceIds` and `.Session`, `HistoricalReferencePayload.RetractedExperienceIds`,
  `InjectionOutcome.Retracted` and `.SessionBudgetExhausted`, `InjectionOmissionReason.AlreadyDelivered` and
  `.OverSessionBudget`, `ExperienceContextProvider.SessionStateKey` and its `StateKeys` and `InvokedCoreAsync`
  overrides, `HistoricalReferenceWriter.RetractionBegin`, `.RetractionEnd`, `.WithdrawnNotice` and
  `.RetractionBlockBytes`, and the span attribute `agentexperience.retracted_count` (written only when not zero).

**Grants, retention, batching and telemetry** (stories 3.6, 5.2, 5.4 and 5.6).

- **A grant disclosure level:** `ExperienceGrantDisclosure`, recorded on grants, grant events and every access row
  (KL-9), by `0011_grant_disclosure`.
- **Retention for subtrees and for the access log** (KL-3, KL-10).
  - `ScopeMatch.Subtree` for retention sweeps.
  - `PostgresExperienceGrantAccessLog.PurgeOlderThanAsync`, by `0012_grant_access_retention`, which keeps every access
    row for at least 30 days.
- **Batch reads and embeddings** (KL-1), both as default interface methods, so no implementer breaks.
  - `IExperienceRecordStore.GetManyAsync`.
  - `IExperienceEmbeddingGenerator.GenerateBatchAsync`, with `ReindexExperienceRequest.EmbeddingBatchSize`.
  - One injection re-read now takes 2 database commands instead of 12, for 8 candidates.
- **Telemetry for erasure** (KL-16): `delete`, `retention.sweep`, `grant.purge` and `grant.access.purge`, on the
  `AgentExperience.Storage.Postgres` source and meter.

### Release process

- **Releases are published by `.github/workflows/release.yml`, with NuGet Trusted Publishing.** Pushing a `v*` tag
  re-runs the release checks on the tagged commit, which must be on `main` and match `Directory.Build.props`. After
  a reviewer approves the `nuget-release` environment, the workflow pushes the verified packages with a short-lived
  OIDC-issued key and creates the GitHub release, whose notes are this section. No API key is stored. See
  `RELEASING.md` step 10.

### Fixed

- **The store README said the migrator never needs a superuser.** A non-superuser migrating role has always failed at
  `0010`, with "permission denied to set parameter", until a superuser grants it `SET` on the two marker parameters.
  The README now documents that one grant.
- **A crypto-shredding store test failed about one run in 43** (test only). It used a random task ID and asserted its
  lexeme, but when all eight hex digits were decimal PostgreSQL's parser split the ID into a word and a signed
  integer. It now uses a fixed ID and asserts the lexemes on `search_vector_sealed` itself.
- `RELEASING.md` step 3's PostgreSQL loop now splits its list of majors in zsh as well as bash.

### Dependencies and support matrix

- **Target frameworks: `net8.0`, `net9.0` and `net10.0`** (stories 6.3 and 7.2); `0.1.0-preview.1` targeted
  `net10.0` only. All five packages ship all three builds with the same public API, which one baseline per assembly
  gates; the test projects run on all three.
  - **New `net8.0`-only dependencies:** Core takes `System.Text.Json` 10.0.12+ and `Microsoft.Bcl.Memory` 10.0.12+,
    and the store takes `System.Text.Json` 10.0.12+. They are the .NET 10 train's packages for APIs the .NET 8 shared
    framework lacks: `JsonElement.DeepEquals`, the strict payload decoder's two options, and `Base64Url`. `net9.0` and
    `net10.0` declare nothing new.
  - `ExperienceEmbeddingDescriptor.ComputeContentHash` uses `Convert.ToHexString(...).ToLowerInvariant()` on
    `net8.0`, which gives the same 64 lowercase hex characters.
  - .NET 8 and .NET 9 both leave support on 10 November 2026. The first preview after that date drops both.
- **PostgreSQL 15, 16, 17 and 18** (previously 16 only). CI runs the store, vector, proof and sample suites on each
  major (the sample on `net10.0`), and the store and vector suites again in crypto-shredding mode. **PostgreSQL 14 is
  not supported:** migration `0005` uses `NULLS NOT DISTINCT`, which is PostgreSQL 15 syntax. DbUp journals scripts
  by name, so the only ways to reach 14 are serving different text under the journaled name or keeping a second
  schema lineage, and a PostgreSQL 14 database upgraded in place would keep that lineage for life. PostgreSQL 14
  itself reaches end of life on 12 November 2026.
- **Every dependency except MAF is a floor, not an exact pin** (story 6.3), declared `>=` with no upper bound, so a
  host that needs a newer release of any of them, or a MAF that raises one, gets no restore conflict. CI tests each
  floor itself and the newest release in its major (the same minor for `Pgvector`) on every change; the second is the
  `floating-dependencies` job, `eng/probe-floating-dependencies.sh`, which also floats MAF to `1.*`. It gates pushes
  and the weekly schedule, and reports without blocking on pull requests. A later major restores but is not claimed.
- **`Microsoft.Agents.AI` is the range `[1.22.0, 2.0.0)`** (story 7.2). A host that needs a newer MAF 1.x restores it
  with no NU1608 warning or NU1107 conflict; the lock files still resolve 1.22.0. The bound is at the next major
  because MAF states no SemVer promise: between 1.15 and 1.22 it kept every signature the adapter uses, but changed
  caller-visible behaviour five times, once (1.22's per-run clone of `ChatClientAgentRunOptions`) on a hook the
  adapter uses. A host on a MAF 2.x, once one exists, gets NU1608. The MAF probe's `latest` leg probes the newest
  stable version inside the range and gates pushes and the weekly schedule, reporting without blocking on pull
  requests; the `pinned` leg runs the floor and always gates.

| Package | `0.1.0-preview.1` | `0.1.0-preview.2` |
| --- | --- | --- |
| `Microsoft.Agents.AI` | `[1.20.0]` | `[1.22.0, 2.0.0)` |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | `[10.0.11]` | `10.0.12` |
| `Microsoft.Extensions.Compliance.Redaction` | `10.9.0` | `10.10.0` |
| `Microsoft.Extensions.AI.Abstractions` | `[10.9.0]` | `10.10.0` |
| `Npgsql` | `[10.0.3]` | `10.0.3` |
| `dbup-postgresql` | `[7.0.1]` | `7.0.1` |
| `dbup-core` | `[6.1.1]` | `6.1.1` |
| `Pgvector` | `[0.3.2]` | `0.3.2` |
| `System.Text.Json` (`net8.0` only; Core and the store) | — | `10.0.12` |
| `Microsoft.Bcl.Memory` (`net8.0` only; Core) | — | `10.0.12` |

A bare version is a floor (`>=`). Story 5.1 briefly made every reference exact at the versions above (KL-14, KL-15);
story 6.3 replaced that policy with floors before this preview shipped.

### For contributors

- A full local test run needs the .NET 8 and .NET 9 runtimes beside the pinned SDK; CI installs both in every job.
  `AGENTEXPERIENCE_POSTGRES_MAJOR` selects the PostgreSQL major the container tests start (16 by default; 15 to 18
  are accepted, and anything else fails), and `AGENTEXPERIENCE_TEST_ENCRYPTION=on` runs the store and vector suites,
  unmodified, in crypto-shredding mode.
- `RELEASING.md` step 3 runs the container suites on every supported major, in both modes, and step 6 runs the MAF
  range probes and the floating-dependency probe as release blockers.
- `CompatibilityPinAgreementTests` refuses an exact pin in a shipping project. It allows a bare floor, or, for MAF
  alone, `[x.y.z, (x+1).0.0)`, and checks that a framework-conditioned reference names one target framework and is a
  direct reference only in that framework's lock-file section. `eng/probe-maf-version.sh` reads the range, skips the
  `DeclaredPins` tests for any version but the floor, and says whether the probed version is inside the range.
  `eng/verify-packages.cs` checks each framework's dependency group, including the `net8.0`-only floors, which appear
  there and nowhere else.
- In the store and vector suites, tests whose subject is the stored representation are mode-aware, or pinned to
  plaintext where their subject is a plaintext row (a database from before `0016`, the plaintext decoder); tests that
  purge by hand declare the key destruction `0016`'s guard asks for, and none is skipped. Every store test runs as
  the application role. `PostgresApplicationRoleTests` proves each refusal from the application role's own
  connection. `PostgresCryptoShreddingTests` proves the crypto-shredding property with a real `pg_dump` and a
  `pageinspect` read of the dead tuple, both failure orders of key destruction, tampering, values moved between
  records, rows, columns and scopes, KEK rotation, the ledgers, and the upgrade job; `EnvelopeExperienceKeyStoreTests`
  covers the key store.
- `HistoricalReferenceBorrowedAndNestedArgumentsTests` plants a marker in every place a value must not come from —
  siblings, other array elements, container content, owner-only, reader-only, case variants and other tools — for
  owned and borrowed records, in both the CLR and the JSON shapes, and checks the intersection, the fail-closed owner
  allowlist, `LessonOnly`'s withheld line and every bound on a nested leaf. `InjectedContentAuthorizationTests` adds
  a nested value on a borrowed record that orders a guarded call, which the boundary still denies. The store suite
  covers `0017` on each supported major, in both modes (the upgrade test itself writes plaintext rows, since a
  pre-`0016` schema can hold only those).
- The script-order tests in `OfflineStoreTests` pin each script's absolute position, so appending a script no longer
  shifts every earlier assertion.
- **The reuse baseline** no longer registers a host `WorkingApproachReflector`. The learning phase runs the shipped
  `DefaultExperienceReflector` alone, and the trials allowlist the `strategy` argument instead. The three golden
  reports changed only in prose: the learned-records line now reads the strategy off the record's final attempt, and
  the paragraph explaining what the harness supplies names the allowlist rather than a reflector. No number moved:
  every trial, mean, verdict, experience ID and confidence is identical. A new test shows the allowlist is
  load-bearing: with it off, the agent reads no strategy from the block and the harness refuses to report reuse.
- The sample's golden transcript is unchanged, byte for byte, and so are the reuse baseline's reports apart from the
  prose above: the sample submits unattributed feedback, which checks no exposure, and the reuse baseline drives
  capture directly without the adapter's capture wrapper, so it records no exposure and submits no attribution. The
  synthetic comparative-evaluation tests seed their run's record as finalized and exposed; the Core telemetry loop
  reuses its lesson in a second run, seeded into its store double as finalized, exposed and with a token, rather than
  citing the record's own run, which the own-run rule refuses; its call table is unchanged, and its span-attribute set
  gains the two new attributes.

## 0.1.0-preview.1

The first preview: Epics 1–4. See the
[release](https://github.com/fabbrik/AgentExperience.NET/releases/tag/v0.1.0-preview.1).

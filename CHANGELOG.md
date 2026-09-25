# Changelog

AgentExperience.NET is a **preview**. It is not production ready, and public APIs may change between previews. The
[Known limits](README.md#known-limits) table lists every limit that is still unresolved.

## Unreleased

### Upgrade, in this order

Stop the application first and run steps 1 to 3 in one maintenance window: once ownership moves, the application's
role cannot read or write any table until step 3 grants it the manifest.

1. **Create an owner role and move ownership to it**, as a superuser: the SQL is in
   [Store: upgrading an existing single-role database](src/AgentExperience.Storage.Postgres/README.md#upgrading-an-existing-single-role-database).
   It includes the one superuser-only grant a non-superuser owner needs to migrate: `SET` on the parameters
   `agent_experience.purge_authorized` and `agent_experience.access_purge_authorized`. The role your application
   uses today stays the application role, with the same credentials.
2. **Run the schema migrator as the owner.** It applies `0013_role_separation_hardening`, which only pins
   `search_path` on functions and restates a `REVOKE`. No journaled script is edited.
3. **Call `ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync` as the owner**, after the vectors migrator
   if you use it, and again on every deploy. Set `AllowErasure` if the application deletes records, sweeps
   retention or purges expired grants, and `AllowAccessLogPurge` if it purges the access log. Without them, those
   calls now fail with a permission error.
4. **Start the application again.** From here on the migrator never runs with the application's credentials.

A host that skips all of this keeps working exactly as before, as a single-role deployment.

### Added

- `ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync` and `ExperienceApplicationRoleOptions`: in one
  transaction, the call does four things.
  - It refuses a missing role, an unmigrated schema, a superuser, the caller itself, any role that is or is a
    member of an owner of the database, the schema or an object in it, and any role that reaches a superuser, a
    server-file role, `pg_maintain` (PostgreSQL 17 and later) or `SET` on `session_replication_role`.
  - It revokes everything the application role holds in the schema.
  - It grants the stores' manifest. Every `UPDATE` is column-level, including on `experience_embeddings`.
  - It verifies the role's effective privileges, including `CREATE` on the schema and grant options, across every
    role the application role can `SET ROLE` to, and rolls back on any difference. On PostgreSQL 17 and later
    that includes `MAINTAIN` on any table in the schema.
- `0013_role_separation_hardening`: `search_path = pg_catalog, agent_experience, pg_temp` on the three purge
  functions and on every guard trigger function.
- **`ExperienceInjectionOptions.ApproachArguments`: selected argument values on the `Approach:` line** (story 6.2,
  KL-8). A host can allowlist, per tool name, the argument keys whose values the injected `Approach:` line shows, for
  example `ApproachArguments = { ["run_incident_check"] = ["strategy"] }`, which renders
  `run_incident_check(strategy="wait-for-lock")`. It is empty by default, and without it the block is byte for byte
  what it was. `HistoricalReferenceWriter.Write` gains an overload that takes the same allowlist.
  - Only keys named for that exact tool are read, and only the value the capture-time sanitizer stored, so a redacted
    value stays redacted.
  - Only a string, number, boolean, null or enum name is shown. An object or array becomes `(not shown: not a string, number or
    boolean)`.
  - A string has invisible characters (per Unicode scalar) and whitespace collapsed and trimmed, its markers
    neutralized, its double quotes turned into single quotes and `->` broken up, and is cut to 64 characters and
    quoted. A line's
    arguments are capped at 512 characters in total, and the byte budget still drops whole records.
  - A record borrowed through a sharing grant never shows an argument value, under either disclosure level. No
    migration is needed.
  - A malformed allowlist is refused when `ExperienceContextProvider` is constructed, and the provider keeps a copy.

### Fixed

- **The store README said the migrator never needs a superuser.** A non-superuser migrating role has always failed
  at `0010`, with "permission denied to set parameter", until a superuser grants it `SET` on the two marker
  parameters. The README now documents that one grant.

### Resolved

- **KL-4: the purge path was auditability, not a privilege boundary** (story 6.1). The two-role deployment is now
  the documented and supported one. An owner role owns the schema and runs the migrators. A separate application
  role, which the stores connect as, owns nothing and holds exactly what the stores need:
  - no `ALTER TABLE`, so it cannot disable a trigger or replace a guard function;
  - no `DELETE`, `TRUNCATE` or `UPDATE` on any ledger, so a purge marker it sets by hand admits nothing;
  - `UPDATE` only on the columns the store moves, so it cannot write a tombstone by hand;
  - `EXECUTE` on the purge functions only when the host opts in.

  The owner role and superusers remain unbound, which is inherent in PostgreSQL. A single-role deployment still
  works for development, but gets none of this.

### Known limits

- **KL-8 is narrowed, not closed.** Approaches that differ by an allowlisted scalar argument now render differently.
  What remains: an object- or array-valued argument still renders as a marker, and a borrowed record shows no
  argument value (names only under `LessonAndApproach`, no `Approach:` line under `LessonOnly`).

### Reuse baseline

- The reference experiment no longer registers a host `WorkingApproachReflector`. The learning phase runs the shipped
  `DefaultExperienceReflector` alone, and the trials allowlist the `strategy` argument instead. The three golden
  reports changed only in prose: the learned-records line now reads the strategy off the record's final attempt, and
  the paragraph explaining what the harness supplies names the allowlist rather than a reflector. No number moved:
  every trial, mean, verdict, experience ID and confidence is identical. A new test shows the allowlist is
  load-bearing: with it off, the agent reads no strategy from the block and the harness refuses to report reuse.

**Story 6.3** widens the supported matrix to everything CI can prove, and narrows KL-13 to what it cannot (see the
[supported matrix](docs/compatibility-evidence.md#supported-matrix)).

### Supported matrix

- **Target frameworks: `net9.0` and `net10.0`.** Every package ships both builds and declares the same dependencies
  for each; the public API is identical on both, and one baseline per assembly gates it. `net8.0` is not targeted:
  its `System.Text.Json` lacks `JsonElement.DeepEquals` and the strict payload-decoding options the store relies on.
  .NET 9 leaves support on 10 November 2026, and the first preview after that drops `net9.0`.
- **PostgreSQL 15, 16, 17 and 18.** CI runs the store, vector, proof and sample suites on each major (the store, vector
  and proof suites on both frameworks, the sample on `net10.0`). PostgreSQL 14 is not supported: migration `0005`
  uses `NULLS NOT DISTINCT`, which needs 15.

### Dependency policy

- **Every dependency except MAF is now a floor, not an exact pin.** `Microsoft.Extensions.DependencyInjection.Abstractions`
  10.0.12, `Microsoft.Extensions.Compliance.Redaction` 10.10.0, `Microsoft.Extensions.AI.Abstractions` 10.10.0,
  `Npgsql` 10.0.3, `dbup-postgresql` 7.0.1, `dbup-core` 6.1.1 and `Pgvector` 0.3.2 are declared `>=` with no upper
  bound. A host that needs a newer release of any of them, or a MAF that raises one, no longer gets a restore conflict.
- **What is tested:** each floor itself, and the newest release in its major (the same minor for `Pgvector`), on
  every change. The second is the new `floating-dependencies` CI job, `eng/probe-floating-dependencies.sh`. It
  gates pushes and the weekly schedule, and reports without blocking on pull requests. A later major restores but
  is not claimed.
- **`Microsoft.Agents.AI` stays exact at `[1.22.0]`.** MAF ships a minor every week or two, has changed
  adapter-visible behaviour between minors, and keeps `[Experimental]` surface next to the adapter's hooks. The MAF
  probe still reports the newest version without blocking.

### For contributors

- A full local test run needs the .NET 9 runtime beside the pinned SDK. `AGENTEXPERIENCE_POSTGRES_MAJOR` selects the
  PostgreSQL major the container tests start (16 by default; 15 to 18 are accepted, and anything else fails).
- `RELEASING.md` step 3 runs the container suites on every supported major, and step 6 runs the floating-dependency
  probe as a release blocker.

**Story 6.5** tracks what injection gives a reused MAF session, and narrows KL-12 to what tracking cannot do (see
[Adapter: reused sessions](src/AgentExperience.MicrosoftAgentFramework/README.md#reused-sessions-a-budget-no-repeats-and-withdrawal-notices)).

### Behaviour change: session tracking is on by default

- **A reused `AgentSession` no longer gets the same record twice, and its injection is bounded.** With a session
  supplied, `ExperienceContextProvider` keeps an account in the session's `StateBag` under
  `ExperienceContextProvider.SessionStateKey` (`"AgentExperience.InjectionSession"`): record IDs, revisions and
  counters, never content. On by default, because KL-12 is a safety limit and MAF's `ChatClientAgent` keeps every
  injected block in the session's history: a host that reuses sessions gets fewer duplicate bytes, a bound, and
  withdrawal notices. A host that uses a fresh session per task sees no difference in what is injected; it still
  gets the state key written, and the `Limits.MaxBytes` floor below.
  - A record revision the session already holds is omitted as `AlreadyDelivered` and takes no slot; a strictly newer
    revision is injected again.
  - A session is given at most 32 record deliveries and 64 KB of Historical Reference across its invocations
    (`ExperienceInjectionOptions.SessionLimits`). When that cannot take another record, retrieval is not run and the
    outcome is `SessionBudgetExhausted`; a record the byte budget drops is `OverSessionBudget`.
  - A delivery is charged only when MAF reports the invocation succeeded. A failed invocation's records are
    delivered again. A stage nothing settled (an abandoned stream) is charged at the next invocation, but its
    records may be delivered again and its notices stay owed, because MAF kept no history for it.
- **Who should turn it off:** a host whose chat history drops injected blocks (for example a `ChatHistoryProvider`
  written to follow the old advice for KL-12, or a chat reducer that trims old messages) should set
  `SessionLimits = null`, or deduplication hides a record the model no longer sees. `null` writes no state and
  restores the previous blocks, omissions and outcomes exactly.
- **A session whose resolver changes scope withdraws what the new scope cannot read.** Held records are re-checked in
  the current request's authorization and scope, and the account keeps no scope.
- **A smaller `Limits.MaxBytes` is refused while tracking is on.** It must be at least
  `HistoricalReferenceWriter.RetractionBlockBytes`, so a withdrawal notice always fits; the provider's constructor
  throws `ArgumentException` otherwise.
- **A session state that does not validate injects nothing.** The invocation reports `Failed` and leaves the value
  as it is, until the host removes the key. So does a withdrawal re-check that throws or times out, until it
  succeeds.

### Added

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
- **Marker and label matching is looser for every field** (all record text, not only notices). A marker now matches
  across any run of whitespace or line break and with dash look-alikes, invisible format characters (zero-width
  spaces, bidirectional controls) are removed from stored text, every Unicode line separator is a line break, and a
  label matches after leading whitespace. Record text without such characters renders byte for byte as before.
- `ExperienceInjectionSessionLimits`, `ExperienceInjectionOptions.SessionLimits`, `ExperienceInjectionSessionUsage`,
  `ExperienceInjectionResult.RetractedExperienceIds` and `.Session`, `HistoricalReferencePayload.RetractedExperienceIds`,
  `InjectionOutcome.Retracted` and `.SessionBudgetExhausted`, `InjectionOmissionReason.AlreadyDelivered` and
  `.OverSessionBudget`, `ExperienceContextProvider.SessionStateKey` and its `StateKeys` and `InvokedCoreAsync`
  overrides, `HistoricalReferenceWriter.RetractionBegin`, `.RetractionEnd`, `.WithdrawnNotice` and
  `.RetractionBlockBytes`, and the span attribute `agentexperience.retracted_count` (written only when not zero).

### Known limits

- **KL-12 is narrowed, not closed.** What remains: the earlier block stays in the session's history, verbatim, and a
  model that already read it cannot be made to forget it, so a notice is advisory. The provider cannot strip its own
  earlier blocks, and the tracking is only as trustworthy as the host's session storage (removing the key resets it;
  concurrent invocations on one session race on it).

**Story 6.6** verifies confidence independence by default, and narrows KL-11 to what verification cannot establish
(see [Updating confidence from evidence](README.md#updating-confidence-from-evidence)).

### Upgrade for verified independence

1. **Run the schema migrator** (as the owner role). It applies `0015_verified_independence`: two nullable columns
   and one unique index, and no table, so the application role's manifest is unchanged. Its header describes
   building the index out of band first, for a large evidence ledger. No journaled script is edited.
2. **Give the lifecycle service an assessment token key** if the host records human assessments:
   `services.AddSingleton(new ExperienceIndependenceOptions { AssessmentTokenKey = ... })`, at least 32 random bytes
   from your secret store, and call `AssessmentTokenIssuer.Issue` from your review flow.
3. **Or opt out** with `IndependenceVerification.TrustHostSuppliedIdentifiers` if you capture and retrieve in
   different scopes, finalize nothing, or must keep accepting evidence about runs finalized before this version.

### Added

- **Verified independence keys** (story 6.6, KL-11). `ExperienceLifecycleService.ApplyEvidenceAsync`, and so every
  attributed feedback submission, now refuses with `ConfidenceUpdateOutcome.Unverified` and an `IndependenceRefusal`
  an independence key it cannot vouch for:
  - a `RunId` that is not finalized into a record in the evidence's scope nor held by the capture service
    (`UnknownRun`), or is the record's own source run (`OwnRun`);
  - a machine `VerificationRoundId` other than the round finalization closed for that run (`UnknownRound`);
  - human evidence without a valid assessment token: missing, forged, for another scope, run, reviewer, direction or
    record, expired, or already spent on this record (`AssessmentToken*`), or with no key configured.
- `ExperienceIndependenceOptions` (mode, key, token lifetime, clock), `IndependenceVerification`,
  `IndependenceRefusal`, `AssessmentTokenIssuer` and `IssuedAssessmentToken`. `AddAgentExperienceCore` wires the
  registered capture service and options into the lifecycle service. It deliberately does not register the issuer:
  anything that can resolve it can mint, so the review flow constructs it.
- An assessment token is HMAC-SHA256 under the host's key, over its ID, issue time, direction and records and over
  the scope, run and reviewer. It is compared in constant time, carries its signed expiry (a day by default, at most
  30), and is spent once per record in the same transaction as the evidence it lands. It never appears in
  `ToString()`.
- `ExperienceRecord.ClosedRoundId`, stamped by finalization from the evaluation's `VerificationBasis.ClosedRound` and
  stored in the payload (`closedRoundId`, omitted when null). `ConfidenceUpdate.AssessmentId`, stored in
  `confidence_evidence.assessment_id` and `lifecycle_events.confidence_assessment_id` and read back with history.
- `0015_verified_independence`: those two columns, human-only `CHECK`s (`NOT VALID`), and the unique index
  `ux_confidence_evidence_assessment` on `(experience_id, assessment_id)`.
- `ExperienceReuseFeedbackService` checks an attribution's run (known, and not an attributed record's own), round and
  token before writing its ledger, and records a failing one with benefit `Unknown` and the reason, as it does every
  other attribution failure.

### Breaking

- **Evidence the library cannot verify is refused by default.** Machine evidence about a run finalized before this
  version is refused (`UnknownRound`): its record carries no closed round. Evidence about a run that was captured
  and retrieved in different scopes is refused (`UnknownRun`). Use the opt-out for either.
- **Evidence naming the record's own source run is refused in every mode** (`OwnRun`). It was a documented rule
  that nothing enforced.
- **A human assessment needs an assessment token.** Without one (or with an invalid one, or an `AssessmentId` that
  is not the token's) the feedback is recorded with benefit `Unknown`; a direct `Human` submission is `Unverified`.
  A token spent on a record is refused for any other evidence ID, including a second feedback submission.
- `ConfidenceUpdateOutcome` gains `Unverified` (value 9); an exhaustive `switch` needs the arm. `Machine` evidence
  carrying an `AssessmentToken` is `Invalid`.
- `ApplyConfidenceEvidenceRequest` (`AssessmentToken`), `ApplyConfidenceEvidenceResult` (`Refusal`) and
  `HumanReuseAssessment` (`AssessmentToken`) each gain a trailing optional parameter: a binary break, and a source
  break for positional deconstruction. `ExperienceLifecycleService` gains a constructor taking `ExperienceIndependenceOptions` and an optional
  `IExperienceCaptureService`; the existing constructors verify, with no key and no capture service.
- The store refuses `ExperienceRecord.ClosedRoundId == Guid.Empty` and an `AssessmentId` on machine evidence
  (`Invalid`).
- **For `IExperienceRecordStore` implementers:** persist `ExperienceRecord.ClosedRoundId` (a store that drops it makes
  every machine submission `UnknownRound`), and refuse a second piece of evidence presenting the same
  `ConfidenceUpdate.AssessmentId` for a record with `Conflict` and an error on `ConfidenceUpdate.AssessmentIdPath`
  (a store that ignores it leaves tokens replayable, though each replay still meets the independence key).

### Known limits

- **KL-11 is narrowed, not closed.** What remains by default: a run is proven real and in scope, not exposed to the
  record, so a caller that can choose among real runs gets one key per real run rather than one per call; the round
  is the one the host closed at finalization, whatever that run's own verdict; a record written by hand through
  `CreateAsync` vouches for itself; whoever holds the key, or can call the issuer, can mint; and a retry made after
  its token expired is refused although the original landed. The opt-out is the old trust boundary.

### Tests

- The sample's golden transcript and 4.4's three golden reports are unchanged, byte for byte: neither submits
  attributed feedback. The Core telemetry loop now reuses its lesson in a second run, seeded into its store double as
  finalized, with a token, rather than citing the record's own run, which the own-run rule refuses; its call table
  is unchanged.

**Story 6.4** adds crypto-shredding, an opt-in mode in which erasure reaches every copy of a record's text, and
narrows KL-2 to the derived search data it cannot reach (see
[Store: crypto-shredding](src/AgentExperience.Storage.Postgres/README.md#crypto-shredding-erasure-that-reaches-every-copy)).

### Upgrade for crypto-shredding

Plaintext mode stays the default, and a deployment that does not opt in only has to migrate:

1. **Run the schema migrator as the owner.** It applies `0016_crypto_shredding`: two nullable columns, two partial
   indexes, three `NOT VALID` checks, one trigger and the sealing function. It touches no row, and its header has
   the `CONCURRENTLY` statements for building the indexes out of band on a large table. Migrate before deploying
   this build: its text search reads `search_vector_sealed` in both modes, so against a schema without `0016` every
   search fails with `42703`.

To turn crypto-shredding on, then:

2. **Stand up a key store outside the database's backup domain**: `EnvelopeExperienceKeyStore` over your KMS
   (`IExperienceKeyEncryptionKey`) and a durable repository for wrapped keys (`IExperienceWrappedKeyRepository`) that
   does not share the database's backups.
3. **Give every PostgreSQL component the same `ExperienceEncryption`** (`services.AddAgentExperiencePostgresEncryption(keyStore)`,
   or the new constructor parameter) and deploy. New records and ledger rows are sealed from here on.
4. **Re-apply the application role's privileges with the same options plus `AllowSealing = true`** (the call is
   declarative: leaving out `AllowErasure` takes erasure away), and seal the existing records with
   `SealPlaintextRecordsAsync`, batch by batch, until `MoreRemain` is `false` for every project root. `AllowSealing`
   is a content-rewrite power over plaintext records; take it away again afterwards.
5. **Run `VACUUM` on `agent_experience.experience_records` and age out pre-upgrade backups.** Copies made before
   step 4 are plaintext; ledger rows and grant reasons written before step 3 stay plaintext until their record is
   erased.

### Added

- **`ExperienceEncryption`** (Storage.Postgres): with it, every free-text column erasure removes is stored as
  AES-256-GCM ciphertext under a per-record data key. That covers the record payload together with the task ID,
  lifecycle reasons and confidence detail, evidence detail, grant reasons, revocation reasons and grant event reasons,
  and the reuse-feedback rationale, sealed once per exposed record. The associated data binds every value to its
  column, row, record and all six scope fields; a value that fails its tag throws and nothing decrypted is returned.
  `DeleteAsync` and the sweep destroy the key inside the erasure's transaction, after every database-side check and
  before the commit: a record never looks erased while its key survives, and never looks live once its key is gone.
  A sealed row read without a key it ever had is a configuration error, never "erased".
- The record store, candidate source, grant store, reuse-feedback store and embedding index each gain a trailing
  optional `ExperienceEncryption? encryption` constructor parameter (`null` is plaintext mode), and the DI
  extensions pick up a registered `ExperienceEncryption`; `AddAgentExperiencePostgresEncryption(keyStore)` registers
  one. The grant-store and feedback-store overloads taking a data source now register a factory rather than an
  instance, so the encryption is picked up however the registrations are ordered.
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
  Each record is sealed in its own transaction, and the seal is opened and compared before it is written. Nothing
  the record answers changes: its revision, ranking, embedding and history stay as they were.
  `ExperienceApplicationRoleOptions.AllowSealing` grants `EXECUTE` on `0016`'s `seal_experience_record`, and the
  privilege manifest and its verification cover it. It is the `record.seal` telemetry operation, carrying
  `agentexperience.sealed_count` and `scope_match` only.
- `0016_crypto_shredding`: `search_vector_sealed` (the full-text vector of a sealed record, from exactly `0003`'s
  expression, so a sealed record ranks as its plaintext twin), `reuse_feedback_exposures.rationale_sealed`, the
  sealed-shape checks, a trigger clearing the sealed vector on erasure, and the sealing function.

### Behaviour changes

- **Text in the sealed format is refused on write, in both modes.** A lifecycle reason, confidence detail, grant or
  revocation reason, or feedback rationale beginning with `aexp-sealed:v1:` is `Invalid`, and so is a rationale that
  is exactly `(sealed)`: the store opens a value with that prefix as ciphertext.
- **A sealed record cannot be erased without its key.** `0016`'s guard refuses to tombstone a `payload_version = 2`
  row unless the erasing transaction declares that it destroys the key, so a process left without an
  `ExperienceEncryption` fails its delete (`42501`) instead of reporting `Deleted` while the key survives.

### Known limits

- **KL-2 is narrowed, not closed.** In encrypted mode a `pg_dump`, a base backup, a replica, the WAL and the dead
  heap tuple hold only ciphertext that nothing opens once the record is erased. What remains in every copy is the
  derived search data PostgreSQL reads in the clear: the full-text vector (task ID, summary and lesson as lexemes
  with positions) and the embedding with its content hash. So do the identifiers and metadata that are never
  sealed, and anything written before the switch. Plaintext mode is unchanged, and the property is only as good as
  a key store kept outside the database's backups, whose own backup retention bounds the erasure.

### Tests

- The store and vector suites now also run unmodified in encrypted mode (`AGENTEXPERIENCE_TEST_ENCRYPTION=on`), as a
  new CI leg. Existing tests whose subject is the stored representation were made mode-aware, or pinned to plaintext
  where their subject is a plaintext row (a database from before `0016`, the plaintext decoder), and the tests that
  purge by hand now declare the key destruction `0016`'s guard asks for; none is skipped.
- `PostgresCryptoShreddingTests` proves the property with a real `pg_dump` and a `pageinspect` read of the dead
  tuple, both failure orders of key destruction, tampering, values moved between records, rows, columns and
  scopes, KEK rotation, the ledgers, and the upgrade job. `EnvelopeExperienceKeyStoreTests` covers the key store.

**Story 7.2** closes KL-13: MAF becomes a tested range, `net8.0` is supported, and PostgreSQL 14 stays out for a
recorded reason (see the [supported matrix](docs/compatibility-evidence.md#supported-matrix)). It supersedes the
story 6.3 lines above on MAF's exact pin, on `net8.0`, and on the MAF probe never blocking; those lines are kept as
the record of 6.3.

### Dependency policy

- **`Microsoft.Agents.AI` is `[1.22.0, 2.0.0)`, not `[1.22.0]`.** A host that needs a newer MAF 1.x now restores
  it with no NU1608 warning or NU1107 conflict. The lock files still resolve 1.22.0, so the default run is unchanged.
  The bound is at the next major because MAF states no SemVer promise: between 1.15 and 1.22 it kept every signature
  the adapter uses, but changed caller-visible behaviour five times, once (1.22's per-run clone of
  `ChatClientAgentRunOptions`) on a hook the adapter uses. A host on a MAF 2.x, once one exists, gets NU1608.
- **The MAF probe's `latest` leg now gates** pushes and the weekly schedule, and reports without blocking only on
  pull requests, like the floating-dependency leg. It probes the newest stable version *inside* the range. The
  floating-dependency leg floats MAF to `1.*` too, so the newest 1.x runs the whole suite.
- `CompatibilityPinAgreementTests` now refuses an exact pin in a shipping project. It allows a bare floor, or, for
  MAF alone, `[x.y.z, (x+1).0.0)`. It also checks that a framework-conditioned reference names one target framework
  and is a direct reference only in that framework's lock-file section.

### Supported matrix

- **Target frameworks: `net8.0`, `net9.0` and `net10.0`.** All five packages ship a `net8.0` build, with the same
  public API; the test projects run on all three.
  - **New `net8.0`-only dependencies:** Core takes `System.Text.Json` 10.0.12+ and `Microsoft.Bcl.Memory` 10.0.12+,
    and the store takes `System.Text.Json` 10.0.12+. They are the .NET 10 train's packages for APIs the .NET 8
    shared framework lacks: `JsonElement.DeepEquals`, the strict payload decoder's two options, and `Base64Url`.
    `net9.0` and `net10.0` declare nothing new.
  - `ExperienceIndex.ComputeContentHash` uses `Convert.ToHexString(...).ToLowerInvariant()` on `net8.0`, which
    gives the same 64 lowercase hex characters.
  - `.NET 8` and `.NET 9` both leave support on 10 November 2026. The first preview after that date drops both.
- **PostgreSQL 14 is still not supported.** Migration `0005` is PostgreSQL 15 syntax. DbUp journals scripts by name,
  so the only ways to reach 14 are serving different text under the journaled name or keeping a second schema
  lineage. A PostgreSQL 14 database upgraded in place would keep that lineage for life. PostgreSQL 14 itself reaches
  end of life on 12 November 2026.

### For contributors

- A full local test run needs the .NET 8 and .NET 9 runtimes beside the pinned SDK. CI installs both in every job.
- `eng/probe-maf-version.sh` reads the range, skips the `DeclaredPins` tests for any version but the floor, and says
  whether the probed version is inside the range. `eng/verify-packages.cs` checks the `net8.0` dependency group,
  including the `net8.0`-only floors, which appear there and nowhere else.

## 0.1.0-preview.2

This preview resolves ten known limits: KL-1, KL-3, KL-5, KL-6, KL-7, KL-9, KL-10, KL-14, KL-15 and KL-16. The six
still in the table (KL-2, KL-4, KL-8, KL-11, KL-12, KL-13) are boundaries of the design, and code alone cannot remove
them.

### Upgrade, in this order

1. **Stop every writer running `0.1.0-preview.1`.** Once `0011` is applied, an old build can no longer write grant
   events or access rows.
2. **Run the schema migrator.** It applies `0011_grant_disclosure` and `0012_grant_access_retention`. Neither edits a
   journaled script, and `0012`'s header describes building its index out of band first, for large tables.
3. **Deploy `0.1.0-preview.2`.** Against a schema without `0011`, this build fails every grant-joined read with
   `42703`.

### Breaking changes

- **`RequiredCheck` must name an evidence kind** (KL-5). `new RequiredCheck("id")` no longer compiles. To accept any
  kind, pass `RequiredCheck.AnyKind`.
- **An evaluation is bound to its run** (KL-6).
  - `VerificationAggregator.Aggregate` now takes `runId` as its first argument.
  - `VerificationResult` can only be produced by the aggregator: it has no public constructor, and its properties are
    get-only.
  - `ReflectionRequest.Run` and `ReflectionRequest.Evaluation` are get-only. A mismatched pair throws
    `ReflectionBindingException`.
  - Finalization quarantines a reflection that does not match its request.

### Behaviour changes

- **Existing sharing grants become `LessonOnly`** (story 3.6, KL-9). A borrowed record loses its `Approach:` line
  until its owner revokes the grant and issues it again as `LessonAndApproach`. The recipient has no access between
  the two calls.
- **Every run is bounded from the moment it opens** (KL-7). This includes the default single-invocation path.
  - An invocation still running after one `MaxOpenRunDuration` period (5 minutes by default) is handed the bound.
  - At twice that duration, its run is completed as `Cancelled` and the closure is reported. The invocation's answer
    is untouched, but its attempt is refused.
- **Retention sweeps count only what they erase.** `DeletedCount` no longer includes records that another caller
  erased first.
- **The injection eligibility check re-checks its time limit before each record and after the last decision**, from
  elapsed time as well as its timer. A slow host decision can no longer slip a record in after the limit.

### Added

- **A grant disclosure level:** `ExperienceGrantDisclosure`, recorded on grants, grant events and every access row
  (KL-9).
- **Retention for subtrees and for the access log** (KL-3, KL-10).
  - `ScopeMatch.Subtree` for retention sweeps.
  - `PostgresExperienceGrantAccessLog.PurgeOlderThanAsync`, which keeps every access row for at least 30 days.
- **Batch reads and embeddings** (KL-1), both as default interface methods, so no implementer breaks.
  - `IExperienceRecordStore.GetManyAsync`.
  - `IExperienceEmbeddingGenerator.GenerateBatchAsync`, with `ReindexExperienceRequest.EmbeddingBatchSize`.
  - One injection re-read now takes 2 database commands instead of 12, for 8 candidates.
- **Telemetry for erasure** (KL-16): `delete`, `retention.sweep`, `grant.purge` and `grant.access.purge`, on the
  `AgentExperience.Storage.Postgres` source and meter.

### Dependencies

These moved (KL-14, KL-15), and every `PackageReference` a shipping project declares is now exact:

| Package | From | To |
| --- | --- | --- |
| `Microsoft.Agents.AI` | `1.20.0` | `1.22.0` |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | `10.0.11` | `10.0.12` |
| `Microsoft.Extensions.Compliance.Redaction` | `10.9.0` (floor) | `[10.10.0]` (exact) |
| `Microsoft.Extensions.AI.Abstractions` | `10.9.0` | `10.10.0` |

## 0.1.0-preview.1

The first preview: Epics 1–4. See the
[release](https://github.com/fabbrik/AgentExperience.NET/releases/tag/v0.1.0-preview.1).

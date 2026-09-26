# Changelog

AgentExperience.NET is a **preview**. It is not production ready, and public APIs may change between previews. The
[Known limits](README.md#known-limits) table lists every limit that is still unresolved, and the
[Documented boundaries](README.md#documented-boundaries) table states what no code change can remove.

## Unreleased

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
`net8.0` and `net9.0` targets from all five packages, with the `net8.0`-only `System.Text.Json` and
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
in the table (see [Known limits](README.md#known-limits)).

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
   [Store: upgrading an existing single-role database](src/AgentExperience.Storage.Postgres/README.md#upgrading-an-existing-single-role-database).
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
[Store: deploying with two roles](src/AgentExperience.Storage.Postgres/README.md#deploying-with-two-roles).

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
[Store: crypto-shredding](src/AgentExperience.Storage.Postgres/README.md#crypto-shredding-erasure-that-reaches-every-copy).

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
[Updating confidence from evidence](README.md#updating-confidence-from-evidence).

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
[Adapter: showing selected argument values](src/AgentExperience.MicrosoftAgentFramework/README.md#showing-selected-argument-values)
and [Adapter: reused sessions](src/AgentExperience.MicrosoftAgentFramework/README.md#reused-sessions-a-budget-no-repeats-and-withdrawal-notices).

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

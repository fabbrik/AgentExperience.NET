# Sharing and grants

**In short.** A record is normally readable only from the exact scope that owns it. A **sharing grant** is the one,
audited exception: an administrator lets one named record be *read* from one other scope (another team, agent or
user in the same tenant, application and project) until the grant expires or is revoked. A grant never allows
writing, never crosses tenant, application or project, and cannot last longer than a configured maximum (90 days by
default). It also decides how much of a borrowed lesson a model may see. Every grant change is audited, and an
optional access log records every read a grant made possible.

Packages: `AgentExperience.Abstractions` (`IExperienceGrantStore`, `IExperienceGrantAccessLog`) and
`AgentExperience.Storage.Postgres` (their PostgreSQL implementations).

## Creating a grant

```csharp
using AgentExperience.Storage.Postgres.DependencyInjection;

services.AddAgentExperiencePostgresGrantStore();   // IExperienceGrantStore

// The host decides who may administer sharing. This is a separate, explicit input: it is never
// derived from an AuthorizationContext, from a role string, or from the requesting scope.
var administration = new GrantAdministration(
    AdministratorPrincipalId: currentUser.Id,
    AuthorizedAt: DateTimeOffset.UtcNow);

var result = await grants.CreateAsync(
    hostAuthorization,                                  // the caller's own authority, over the owner scope
    administration,                                     // authority to administer sharing
    new ExperienceGrantRequest(
        GrantId: Guid.NewGuid(),
        ExperienceId: recordId,
        RecordScope: ownerScope,                        // where the record lives: team-a
        RecipientScope: ownerScope with { TeamId = "team-b" },
        Reason: "team-b owns the follow-up work",
        ExpiresAt: DateTimeOffset.UtcNow.AddDays(7)),
    cancellationToken);
// Created — the grant row and its audit event were written in one transaction.
```

**Two authorities, never one.** Every grant-mutating call takes the `AuthorizationContext` *and* a
`GrantAdministration`. A `null` administration, or one whose principal is blank, is `Denied` before any connection
opens — administering sharing is not something a role string or a scope can imply.

Revoking is an append:

```csharp
await grants.RevokeAsync(
    hostAuthorization,
    administration,
    new ExperienceGrantRevocation(grant.GrantId, ownerScope, "the collaboration ended"),
    cancellationToken);
// Revoked — the next read is denied, and the grant's history keeps both events.

var history = await grants.ListAsync(hostAuthorization, ownerScope, recordId, cancellationToken);
// Every grant over the record, revoked and expired ones included. Owner scope only: a recipient
// cannot enumerate the grants over a record it can read.
```

## What a grant permits, and what it never does

**What a grant permits.** Reading one named record, and only reading: `GetAsync` (and `GetManyAsync`, which is
`GetAsync` for several named records in one statement), the text channel, and the vector channel — and therefore
injection, which re-reads through `GetManyAsync`. A granted record comes back exactly as its owner sees it, still
carrying the owner's `Scope`. `CreateAsync`, `CommitLifecycleEventAsync`, `GetHistoryAsync`, `QueryAsync`'s
enumeration, and issuing further grants all keep the exact-scope predicate, so none of them is ever widened by a
grant.

Nothing about sharing weakens eligibility. A shared record still has to be `Validated` or `Reinforced`, still has
to clear the confidence floor, expiry, and environment checks, and is ranked exactly like an owned one.

| Rule | Where it is enforced |
| --- | --- |
| Relaxes only `TeamId`, `AgentId`, `UserId`; tenant, application, and project are always the record's own | Validation with the field path, *and* a `CHECK` constraint, so an unstorable grant is unstorable |
| Confers no write, no lifecycle history, and no enumeration | Every non-read statement keeps the exact-scope predicate |
| Stops permitting reads once `ExpiresAt` passes | The read predicate, against `clock_timestamp()` — the *database's* wall clock, never the caller's, and never the transaction's start time |
| Stops permitting reads the moment it is revoked | The same predicate; revocation appends an event and deletes nothing. At most one grant per (record, recipient scope) may be active at a time, so revoking the grant you know about really is the end of that recipient's access — a second, overlapping one is refused as `Conflict` rather than stacked |
| Cannot be issued to last longer than the configured maximum, and can never be permanent | `Invalid` on `ExpiresAt` with nothing written, *and* a fixed `CHECK` ceiling underneath it |
| Cannot be issued or revoked without administrator authority | `Denied`, before any connection is opened |
| Changes nothing about the record: not its status, confidence, counters, or revision | The grant path never touches `experience_records` |

**Enforcement is a SQL predicate.** Reads compose `(exact scope) OR (an active grant naming this record and
permitting this scope)` in the same statement as everything else, so the database can never return a row the
predicate did not permit, and no application code is in a position to widen one. *Active* means issued, not revoked,
and not expired as of `clock_timestamp()` — the database's own wall clock, so a caller whose clock is wrong cannot
widen anything. It is `clock_timestamp()` rather than `now()` because `now()` is fixed at the start of the
surrounding transaction, and inside a long caller-held transaction that would keep admitting a grant that expired
minutes ago.

**Null optional recipient fields are exact, not "one sibling team".** Scope matching is exact everywhere, so a
recipient of `(tenant, application, project, null, null, null)` permits exactly the requests whose scope has all
three optional fields null — the project-level scope, which is usually broader than intended. Name every optional
field the recipient actually uses.

**The batched read is the single read, widened only in its ID match.** `GetManyAsync` is built from the same select
list, the same lateral join that names the permitting grant and its disclosure level, and the same readability
predicate as `GetAsync`'s statement — the text is shared, not retyped, and a test asserts that the batched statement
is the single one with `experience_id = @experience_id` replaced by `experience_id = ANY(@experience_ids)` and nothing
else. Each row is answered by the same code as a single read: a tombstone is `Deleted` to the scope that owned it and
`NotFound` to anyone who reached it through a grant, a missing ID is `NotFound`, and an empty GUID is `Invalid` at its
own position without reaching the database. The grant fallback narrows it exactly as it narrows `GetAsync`. The batch
is read by one statement, where a per-record loop would read each record at its own instant; grant expiry is still
decided per row by `clock_timestamp()`. A scope outside the authorization refuses the whole request as `Denied`,
before any position is looked at. Under a failing ledger the host's `OnNotRecorded` callback is called once for the
batch, carrying every row it could not write.

## Outcomes

**Atomicity.** `CreateAsync` writes the grant row and its `Issued` event in one transaction, on one connection;
`RevokeAsync` updates the row and appends a `Revoked` event in another. Both or neither, every time. The insert's
source row is the canonical record itself, matched on the exact owner scope, so a grant over a record that is not
there writes nothing and returns `NotFound`, and a stored grant's owner scope is copied from the record rather than
asserted by the caller.

| Outcome | When |
| --- | --- |
| `Created` / `Revoked` | The grant and its audit event were committed together |
| `Found` | `ListAsync` or `GetHistoryAsync` answered; a listing may legitimately have no grants |
| `NotFound` | No such record, or no such grant, in the requested owner scope — including when it exists elsewhere |
| `Denied` | No administrator authority, or a scope outside the host authorization. Nothing was accessed |
| `Invalid` | Malformed request, with the field path. A recipient scope that changes tenant, application, or project, or that equals the owner's, is reported on that field; so is an undated `GrantAdministration` |
| `Conflict` | That `GrantId` is already stored in some scope, **or** an active grant already permits the same recipient over the same record. Nothing was written |
| `AlreadyRevoked` | The grant was already revoked. Nothing was written and its history is unchanged |

`NotFound` says nothing about whether a `GrantId` is free. The insert reads the record row first, so a create naming
a record that is not in the owner scope selects nothing and reports `NotFound` before the primary key is ever
tested — even when that `GrantId` is already stored. Only `Created` and `Conflict` say anything about the ID, so
generate a fresh one per attempt.

**One active grant per recipient.** `ux_experience_grants_active_recipient` allows at most one *unrevoked* grant per
(record, recipient scope) pair, so revoking the grant an administrator knows about genuinely ends that recipient's
access instead of leaving an overlapping one alive. Re-issuing while one is active is `Conflict`; once it is
revoked, the same recipient can be granted access again. Different recipients are independent of each other.

`ListAsync` returns every grant over a record, revoked and expired ones included, oldest first, bounded by its
`limit` (1–500, default 100) — from the **owner** scope only. It is driven from the record, so "this record has no
grants" (`Found`, empty) and "there is no such record here" (`NotFound`) are different answers.

`GetHistoryAsync` reads one grant's audit trail: the grant as it stands now plus every `Issued`/`Revoked` event,
oldest first, each carrying the administrator, when the host established that administrator's authority, both
scopes, the reason, and the expiry at the time. It mirrors `IExperienceRecordStore.GetHistoryAsync` and is likewise
owner-scope only. **It is an administration trail:** it answers "who permitted this?". "Who read it?" is the
separate, optional [access log](#recording-who-read-a-shared-record).

**Privileges, and deployments without the grant table.** Honouring grants needs `SELECT` on
`agent_experience.experience_grants` in addition to `experience_records`; administering them needs `INSERT`/`UPDATE`
on `experience_grants` and `INSERT` on `experience_grant_events`. The read privilege is **optional**: a role without
it, and a database that has not applied `0005` yet, are both supported. The first read that meets an undefined table
(`42P01`) or an insufficient privilege (`42501`) latches that reader into degraded mode, retries with the
exact-scope predicate alone, and reports it once through the optional `onGrantsUnavailable` callback on
`PostgresExperienceRecordStore`, `PostgresExperienceCandidateSource`, and `PostgresExperienceEmbeddingIndex`.
Degrading only ever **narrows** what a read returns, so it is a configuration problem rather than a safety one.

## A borrowed lesson is labelled as one

A read widened by a grant comes back with `SharedByGrant` set — on `ExperienceRecordGetResult` and on every
`ExperienceCandidate` — because the store is the only layer that knows. All three read paths additionally return
`PermittingGrantId`: *which* grant permitted it, produced by the same `LEFT JOIN LATERAL` that decided readability, so
it can never name a grant that did not permit the read. Where two active grants would both admit it, the join orders
by `grant_id` and takes one, so the answer is stable per read rather than whatever the planner returned first. Core
passes both through on `RankedExperience`, and the MAF provider surfaces them to the host's risk policy
(`ExperienceInjectionDecisionContext.SharedByGrant`, `.PermittingGrantId`) and labels the injected block with a
`Shared:` line (with no scope identifier in it). Consumers keep a strict "this is my own record" check for anything
not flagged, so a source that returns a foreign record without declaring a grant is still refused downstream.

## Disclosure levels

A verified record's block includes the `Approach:` line — the ordered tool names the run called — and for a borrowed
record those are the *lending* scope's tool names, which (`hr_salary_lookup`, `stripe_charge_prod`) are themselves
information about its systems. So every grant carries an immutable disclosure level,
`ExperienceGrantRequest.Disclosure`:

```csharp
new ExperienceGrantRequest(grantId, recordId, ownerScope, recipientScope, reason, expiresAt,
    Disclosure: ExperienceGrantDisclosure.LessonAndApproach);   // default: LessonOnly

// Or, to let the recipient's model also see selected argument values, name the keys you consent to show:
new ExperienceGrantRequest(grantId, recordId, ownerScope, recipientScope, reason, expiresAt,
    Disclosure: ExperienceGrantDisclosure.LessonApproachAndArguments,
    ApproachArguments: new Dictionary<string, IReadOnlyList<string>> { ["retry_refund"] = ["delay", "options.mode"] });
```

| Level | What the recipient's model sees on the `Approach:` line |
| --- | --- |
| `LessonOnly` (the default) | Nothing: the line is omitted, and the `Shared:` line says the grant withholds it when the record has one |
| `LessonAndApproach` | The owner's tool names, exactly as the owner would see them, and never an argument value |
| `LessonApproachAndArguments` | The tool names, plus a value only for a key the owner named on the grant **and** the reader's own `ApproachArguments` names for the same tool |

The level governs the `Approach:` line **only**: the lesson, reuse guidance, preconditions and warnings are the
reflector's prose and are rendered unfiltered, so a tool name a reflector wrote into them reaches the model under
any level. Only the *block* is governed: the `ExperienceRecord` a store returns to host code is complete either way.
A store that says a record is shared but reports no level is rendered as `LessonOnly`, and the host decision can deny
a record but never widen its level.

The level is read from the same lateral join that names the permitting grant and comes back on
`ExperienceRecordGetResult.GrantDisclosure` (`null` for the reader's own record); the search channels read it too, but
put it only on the access row, never on an `ExperienceCandidate`. It reaches the host's risk policy as
`ExperienceInjectionDecisionContext.GrantDisclosure`. It is stored on the grant, copied onto its `Issued` and `Revoked`
events and onto every access row, and **cannot be changed**: the database refuses an `UPDATE` of it, so to widen or
narrow it, revoke the grant and issue a new one — the one-active-grant rule means the revoke comes first, so the
recipient has no access in the gap. A level the enum does not define is `Invalid` on `Disclosure`, and nothing is
written. The access row records the level the library applied at delivery, not whether an `Approach:` line actually
reached a model: the host may deny the record, the byte budget may drop it, or it may have no approach.

**The third level carries the owner's consent to argument values.** Under `LessonApproachAndArguments` the request
must name, per tool, the argument keys (or dotted paths such as `options.mode`) the owner consents to show:
`ExperienceGrantRequest.ApproachArguments`. The keys are required at that level and refused at any other; a blank or
control-character tool name, a key a reader could not configure (blank, over 64 characters, whitespace, a control,
format or surrogate character, or one of `= ( ) , " \`), a key listed twice, a tool with no keys, more than
`ExperienceGrant.MaxApproachArgumentKeysPerTool` (16) keys for a tool or `MaxApproachArgumentTools` (32) tools is
`Invalid` on `ApproachArguments`, and nothing is written. They are stored on the grant as one JSON object
(`experience_grants.approach_arguments`, `0017`), come back on `ExperienceGrant.ApproachArguments`, and are read from
the same lateral row as the level onto `ExperienceRecordGetResult.GrantApproachArguments` (single and batched reads
alike; `null` for the reader's own record and at every other level; a stored value that does not parse reads as
`null`, which shows nothing). The host sees them as `ExperienceInjectionDecisionContext.GrantApproachArguments`. They
are **as immutable as the level** — the monotonicity trigger pins them, and the application role has no `UPDATE` on
the column — and the schema itself refuses keys under another level, the level without keys, and any shape but a
non-empty object of non-empty string arrays. They are names only, never a value, so they are stored **in the clear in
encrypted mode too**: do not put anything secret into a tool name or an argument key. Events and access rows record
the level, not the keys; the keys go with the grant when it is purged or its record erased. How the values are
rendered is in [Showing selected argument values](injection.md#showing-selected-argument-values).

### Upgrading the grant schema

**`0011` changes behaviour, and the order matters.** It gives every existing grant `LessonOnly`, so a borrowed
record's `Approach:` line disappears from injected blocks until the owner revokes the grant and issues a
`LessonAndApproach` replacement. Events and access rows written before `0011` read back with a `null` level: it was
never recorded. **Run `0011`, then deploy the new build, and stop older writers first.** A build that knows about
levels, on a pre-`0011` schema, fails every grant-joined read with `42703` (undefined column), and an older build on a
`0011` schema cannot write grant events or access rows, because both now require a level. Both failures are loud by
design; there is no silent fallback.

**`0017` adds the third level** and changes nothing that is shown: every stored grant keeps its level and has no
keys, so a borrowed record's argument values appear only once its owner revokes and reissues at
`LessonApproachAndArguments`. **Run `0017`, then deploy the new build**: it selects the owner's keys in every
grant-joined read and fails with `42703` against a pre-`0017` schema. An older build keeps working on a `0017`
schema, but cannot decode a grant stored at the new level and renders a record read through one as `LessonOnly`.

## Bounding a grant's lifetime

`PostgresExperienceGrantPolicy` is the policy the store administers grants under. Its one rule is `MaxLifetime`, the
longest a *new* grant may be issued for — 90 days by default:

```csharp
IExperienceGrantStore grants = new PostgresExperienceGrantStore(
    dataSource,
    new PostgresExperienceGrantPolicy(TimeSpan.FromDays(30)));
// or services.AddAgentExperiencePostgresGrantStore(new PostgresExperienceGrantPolicy(TimeSpan.FromDays(30)));
```

An expiry further ahead than the maximum is `Invalid` on `ExpiresAt` with nothing written; one exactly at the
maximum is accepted. There is no unbounded option — `TimeSpan.Zero`, `Timeout.InfiniteTimeSpan`, and anything past
3650 days all throw at construction — so `DateTimeOffset.MaxValue` is refused like any other over-long expiry. A
bound that would run off the end of `DateTimeOffset` is treated as *exceeded* rather than saturated, because
saturating would make the comparison vacuously true and admit exactly the value the bound exists to refuse.

**It is checked twice, against two clocks, on purpose.** The client check produces the message naming the bound,
but it measures from the caller's clock. The insert statement carries the same bound as
`expires_at <= now() + @max_lifetime`, measured from the clock that stamps `issued_at`, so a caller whose clock
runs behind cannot buy itself a longer grant; it is reported on the same field, and nothing is written either way.

The bound binds a grant when it is **created**, and never afterwards. Raising the maximum does not extend a grant
already issued; lowering it does not shorten one. End an over-long grant by revoking it. The rule that an expiry may
only ever shrink (`0006`'s `experience_grants_monotonic` trigger) still holds.

Underneath the policy, `0009` adds `experience_grants_lifetime_bounded`:
`CHECK (revoked_at IS NOT NULL OR expires_at <= issued_at + interval '10 years')`. A `CHECK` cannot express
"whatever interval this deployment configured", so the two do different jobs: the policy is the deployment's rule,
the constraint is a fixed, generous floor that binds even a writer bypassing this library. It is added `NOT VALID`;
see the script's header for the confirm-then-`VALIDATE` step and what to do about a grant already issued beyond it.

The **revoked exemption matters**: PostgreSQL re-checks a `CHECK` on every `UPDATE`, so without it, revoking a
grant stored before `0009` with an unbounded expiry — the one remedy the runbook prescribes — would be refused by
the very constraint that made it a problem, leaving it permanent forever. Because the ceiling is relative to
`issued_at`, `0009` also adds a `BEFORE INSERT` trigger refusing a grant dated in the future: "ten years from 2126"
outlives everyone the trail is for, and a `CHECK` cannot call `now()`.

## Recording who read a shared record

The grant trail answers "who permitted this?". `IExperienceGrantAccessLog` answers "who read it?", and it is a
separate, optional ledger — a deployment can keep grants without paying for access rows:

```csharp
services.AddAgentExperiencePostgresGrantAccessLog(
    onNotRecorded: failure => logger.LogError(failure.Failure, "grant access row not written"),
    mode: ExperienceGrantAuditingMode.BestEffort);   // or Required

// Outside a container:
var store = new PostgresExperienceRecordStore(
    dataSource,
    onGrantsUnavailable: null,
    auditing: new ExperienceGrantAuditing(
        new PostgresExperienceGrantAccessLog(dataSource),
        OnNotRecorded: failure => logger.LogError(failure.Failure, "grant access row not written"),
        ExperienceGrantAuditingMode.Required));
```

Each row names the grant, the record **and the revision that was disclosed**, the grant's disclosure level at
delivery (kept on the row after the grant itself is purged; `null` on rows written before `0011`; the level the
library applied, not proof that an `Approach:` line reached a model), the owner scope, the recipient scope, the
reading principal (the host's `AuthorizationContext.PrincipalId`, never anything a caller passed as data), the host's
correlation ID for the work that caused the read, and both `occurred_at` (the reader's clock, which is
`ExperienceGrantAuditing.Clock`) and `recorded_at` (the database's `clock_timestamp()`). The revision and the
correlation ID are on the row rather than joined in later because a record is a mutable projection and the table is
append-only: neither can ever be backfilled.

The read also tells the caller **which** grant permitted it — `ExperienceRecordGetResult.PermittingGrantId` and
`ExperienceCandidate.PermittingGrantId`, carried on to `RankedExperience` and to the host's
`ExperienceInjectionDecisionContext` — so an injected lesson can be tied back to the sharing decision behind it. The
grant ID is for the host: the injected block still names no grant and no scope.

| Read | Recorded? | Why |
| --- | --- | --- |
| `GetAsync` widened by a grant | **Yes** | The record was handed to a caller who could only see it through that grant |
| `GetManyAsync` positions a grant delivered | **Yes** | One row per delivered position, exactly the row a `GetAsync` of that ID would write. The batch's rows are written in **one** statement, so under `Required` they land together or not at all: a failed append drops every grant-delivered position and leaves the owner's own positions as read |
| The MAF provider's pre-injection re-read | **Yes** | It is one `GetManyAsync`, and it is a delivery |
| A text or vector candidate a grant admitted | **Yes** | `ExperienceCandidate.Record` is the record read back *in full*, so returning one across a scope boundary is a disclosure, not a notice that something matched. A search's rows are written in **one** statement, so auditing costs one round trip per search rather than one per row |
| An owner reading its own record | **No** | No grant permitted it, so there is no access to attribute to one |
| A read that found nothing | **No** | Nothing was delivered |
| A read the caller refuses *because* a grant is what made it readable | **No** | Nothing was handed over. Declare it with `ExperienceReadOptions(ExperienceReadPurpose.ScopeCheck)`; Core's confidence path does, because a grant never confers writing |

**The rows are never written inside the read's own statement.** That would take a write lock on every read and stop
reads running on a replica. The append is a separate statement afterwards, which is why what happens when it fails
is a policy rather than an accident:

- `BestEffort` (the default): the records are still returned and the failure goes to `onNotRecorded`, carrying
  every row that did not land. That callback is **required**, not optional — under best effort it is the only place
  a missing row is visible, and an audit that can fail silently is worse than none.
- `Required`: the read returns nothing. A `GetAsync` returns `NotFound`, which is the same answer a record no grant
  permitted would give, so failing closed tells a caller nothing it would not otherwise have; a search returns *no*
  candidates rather than the subset that needed no grant, because a partly-returned page would quietly be a
  different search than the caller asked for. The failure is still reported.
- A blank `AuthorizationContext.PrincipalId` is itself an audit failure: a row that cannot say **who** read the
  record does not answer the question the ledger exists for, so it is reported and, under `Required`, fails closed.
  Establish a principal, or do not require auditing.
- Cancellation arriving during the append is a failure like any other rather than an exception thrown over a
  completed read — the records were already read, so the mode decides whether they may be returned.
- A throwing `onNotRecorded` is swallowed: the host's own logging never changes what a read returns.

**Reading the trail.** `IExperienceGrantAccessLog.QueryAsync` answers the question the ledger exists for, without
hand-written SQL. It is owner-scope only and cursored, mirroring `IExperienceGrantStore.GetHistoryAsync`:

```csharp
var page = await accessLog.QueryAsync(
    authorization,
    new ExperienceGrantAccessQuery(ownerScope),            // or (ownerScope, recordId) for one record
    cancellationToken);
// page.Accesses — oldest first, bounded by Limit (1-500, default 100).
// page.NextCursor — pass as StartAfter for the next page; the cursor is (occurred_at, access_id), so
//                   several deliveries sharing an instant page correctly.
```

A recipient cannot enumerate who else read a record it can read, any more than it can list the grants over one.

**What turning it on costs.** Every read that discloses something across a scope boundary does a second,
synchronous round trip on a pooled connection before it returns — one per `GetAsync`, one per `GetManyAsync` or
search however many rows it disclosed. Under `Required`, read availability becomes a function of *write*
availability: if the ledger is unreachable, grant-widened reads return nothing. That is the mode's promise, not a
bug, but it is a real coupling. The `dataSource` overloads exist largely for this: pointing
`PostgresExperienceGrantAccessLog` at its own `NpgsqlDataSource` keeps the audit writes off the read pool, so a slow
ledger cannot exhaust the connections reads depend on.

`experience_grant_access` is append-only in the database, like every other ledger here, so a delivery cannot be
edited or deleted out of the trail afterwards — except by its one retention path, `PurgeOlderThanAsync`, which never
removes a row younger than 30 days (see
[Retention for the grant access log](deletion-and-retention.md#retention-for-the-grant-access-log)). Erasing a record
keeps its access rows. Wire nothing and auditing is off entirely: no extra write, no extra round trip, no extra
failure mode, `0009`'s table simply stays empty, and reads behave exactly as they do without it.

**Auditing binds the implementation that was registered.** Every registration uses `TryAdd`, so a host that
registers its own `IExperienceRecordStore`, `IExperienceCandidateSource`, or `IExperienceEmbeddingIndex` *before*
calling these extensions keeps its own — and takes on the obligation to honour a configured
`ExperienceGrantAuditing` itself. Registering the access log does not make somebody else's store audit.

## Source compatibility of the grant additions

The grant and audit additions were additive at the source level — defaulted positional parameters, defaulted
constructor arguments, and one default interface method — so code recompiles unchanged. None of it is
binary-compatible, so recompile rather than drop in new assemblies:

| Type | Change |
| --- | --- |
| `ExperienceRecordGetResult` | gained `Guid? PermittingGrantId = null` |
| `ExperienceCandidate` | gained `Guid? PermittingGrantId = null` |
| `ExperienceCandidateQuery`, `ExperienceVectorQuery` | gained `string? CorrelationId = null` |
| `RankedExperience`, `ExperienceInjectionDecisionContext` | gained `Guid? PermittingGrantId = null` |
| `IExperienceRecordStore` | gained a defaulted `GetAsync(..., ExperienceReadOptions, ...)` overload that forwards to the existing one |
| `PostgresExperienceRecordStore`, `PostgresExperienceCandidateSource`, `PostgresExperienceEmbeddingIndex` | constructors gained `ExperienceGrantAuditing? auditing = null` |
| `PostgresExperienceGrantStore` | constructor gained `PostgresExperienceGrantPolicy? policy = null, TimeProvider? timeProvider = null` |

The one *behavioural* break was deliberate: a grant issued with an expiry more than 90 days out — including
`DateTimeOffset.MaxValue` — is `Invalid`. Configure `PostgresExperienceGrantPolicy` if your deployment needs a
different window.

The table layout is in [PostgreSQL schema](postgres-schema.md#0005-sharing-grants).

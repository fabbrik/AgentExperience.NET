# Store conformance suite

What any implementation of the core storage ports must do, written as tests. The library holds three abstract xUnit
classes. A test project that implements the ports subclasses each one and supplies its store through a factory
method; its own runner then discovers and runs the inherited tests. The library itself is not a test project, is
never packed, and depends only on `AgentExperience.Abstractions` and xUnit's assertion and core packages.

| Class | Port | Factory |
|---|---|---|
| `RecordStoreConformanceTests` | `IExperienceRecordStore` | `CreateStore()` |
| `CandidateSourceConformanceTests` | `IExperienceCandidateSource` (seeded through an `IExperienceRecordStore`) | `CreateRecordStore()`, `CreateCandidateSource()` |
| `ReuseFeedbackStoreConformanceTests` | `IExperienceReuseFeedbackStore` | `CreateStore()` |

Every test observes the ports only, with no SQL and no knowledge of how a store is built. Each test works in a
tenant of its own, so a store shared by all tests (one database, one container) still keeps them independent.
The PostgreSQL adapter passes the suite through the subclasses in
[`AgentExperience.Storage.Postgres.Tests/Conformance`](../AgentExperience.Storage.Postgres.Tests/Conformance), which
run in the `postgres` CI job on every supported major, in plaintext and crypto-shredding mode.

## The contract

**Throughout**

- **Read your writes.** A write the port acknowledged (a create, a committed lifecycle event, a recorded
  submission) is visible to the very next read, query, history page or search, on the same store or on a candidate
  source over the same data. Every test relies on this.
- **Refusals.** A request scope outside the host-established authorization is `Denied` and writes nothing. A
  request the port documents as malformed is `Invalid`, carries errors, and writes nothing.
- **Cancellation.** An already-cancelled token surfaces as an `OperationCanceledException`, never wrapped in an
  `ExperienceStoreException`, and writes nothing.

**Record store**

- A record reads back exactly as it was created, every nested part included (attempts, tool-call arguments,
  evidence, reflection, environment metadata, provenance and its exposures, origin, closed round, provenance
  signature, counters). A store persists the provenance signature as given and never checks it, but refuses a
  malformed one (an empty or over-long value, a key ID or algorithm outside `[A-Za-z0-9._-]{1,64}`) as `Invalid`.
- `CreateAsync` only creates. An ID that already exists, in any scope, is `Conflict`, the stored record does not
  change, and the result is the same whichever scope holds the existing record. Concurrent creates of one ID have
  exactly one winner. Malformed records (empty ID, blank task ID or required scope field, confidence outside
  [0, 1], negative counters or revision) are `Invalid`.
- Reads are scope-exact. A record in another scope, whether it differs by tenant, project, team or letter case,
  reads exactly like a missing one (`NotFound`), so the result does not show that the record exists. An empty ID
  is `Invalid`.
- `QueryAsync` returns only the exact scope and the requested statuses, newest first, within its limit, in the same
  order every time for records created at the same instant. Limits outside their bounds and an empty status list
  are `Invalid`.
- `CommitLifecycleEventAsync`:
  - moves the status, sets the revision to expected + 1, moves `UpdatedAt` forward, and changes nothing else;
  - is idempotent on `EventId`: a replay returns the revision the original commit produced (even after later
    commits), a `null` `CurrentStatus` for an event that carried no confidence payload, and writes nothing, and the same ID with any different field (reason, producer, statuses, revision,
    time, record, scope, replacement, confidence payload), in any scope, is `Conflict` and writes nothing;
  - checks `ExpectedRevision` optimistically: anything but the current revision is `StaleRevision` with the current
    revision and writes nothing, and of many concurrent commits from one revision exactly one wins;
  - applies the prior-status guard: a mismatch is `StatusMismatch`, reports the stored status and writes nothing,
    and a `null` prior status is checked against the event's current status, so it cannot skip the guard;
  - applies the supersession guard: a replacement that is missing, in another scope, ineligible or on a chain back
    to the record is `ReplacementNotAllowed`, reporting the replacement's status (`null` when it is not in scope);
    a record naming itself is refused (`Invalid` or `ReplacementNotAllowed`); replaying a committed supersession
    still reports `Committed` after the replacement has moved on;
  - is `NotFound` for a missing record or one in another scope, and `Invalid` for a `Superseded` event without a
    replacement or a replacement on any other transition.
- `GetHistoryAsync` lists exactly the committed events, oldest first, with the record's current revision, and
  respects its limit and `StartAfterRevision` cursor. A page past the end, or a record with no events, is `Found`
  and empty with no next cursor. A record in another scope is `NotFound`, the same as a missing one.
- `CheckSupersessionAsync` walks the replacement chain directly and transitively (`Cycle`), never allows a record to
  replace itself, and reports the replacement's status without deciding eligibility (a revoked replacement is
  `Allowed`, with its status). A record or replacement in another scope is `RecordNotFound` or
  `ReplacementNotFound`, the same as a missing one. `RecordNotFound` reports no replacement status, even when the
  replacement exists in the scope: with no record there is nothing to supersede.
- `GetManyAsync` answers every position exactly as `GetAsync` would answer it alone. More IDs than its maximum is
  `Invalid`.

**Candidate source**

- It filters by exact scope, by the requested statuses, and by minimum confidence (the minimum itself is included).
- It ranks before it limits, returns the strongest match first with a strictly higher relevance than a clearly
  weaker one, and gives each candidate a relevance in [0, 1].
- A record that matches none of the query's terms is not returned. Text that matches nothing is `Found` with no
  candidates. Each candidate is the record exactly as stored.
- Blank or over-long text, an empty or undefined status list, a minimum confidence outside [0, 1] and a limit
  outside its bounds are `Invalid`.
- The suite assumes only that the task summary is searched: every record it seeds carries its matching words
  there. Exact relevance values are **not** part of the contract, and neither is whether a record that matches
  only some of the terms is returned.

**Reuse-feedback store**

- A submission is returned exactly as it was submitted, on `Recorded` and on `AlreadyRecorded`, except that its
  exposures always come back ordered by record ID, whatever order they were listed in.
- The feedback ID is the idempotency key. An identical resubmission is `AlreadyRecorded`, including one that lists
  the same exposures in another order: the store normalizes the order before it stores or compares. The same ID with any
  different stored field (run, outcome, measure, time, benefits, attribution fields, evidence, trial label, scope,
  or any exposure; the attribution fields are varied on a comparative result's evaluator and round and on a human
  assessment's reviewer and assessment) is `Conflict`, the stored submission stays as it was, and it is handed back
  when the caller has authority over its scope.
- A colliding ID from a tenant the caller has no authority over is `Conflict`, and no stored content comes back.
- A non-finite measure, a blank measure kind, a benefit with no attribution, and the same record named twice in
  the exposures are `Invalid`.

## Deliberately excluded

These are real behaviours of the PostgreSQL adapter, but other stores do not have to share them. They stay
covered by that adapter's own tests:

- **Full-text relevance values**: the numbers `ts_rank_cd` produces, stemming, stopwords and query syntax.
- **Grants and the grant access log**: sharing across scopes, disclosure levels and access rows. The suite uses
  no grants, so every read it makes is an own-scope read.
- **Erasure and tombstones** written by `DeleteAsync`, which is not a port member.
- **Crypto-shredding**: sealing, key destruction and what a sealed row reads back as. A feedback store that seals
  rationales overrides `SealsRationale`; the suite then does not compare the rationale it hands back or decides a
  replay on.
- **The application role**: database privileges and the two-role deployment.
- **Append-only triggers**, and the database rejecting rows that did not come through the store.

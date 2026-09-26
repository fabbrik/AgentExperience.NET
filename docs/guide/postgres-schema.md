# PostgreSQL schema

**In short.** The schema lives in versioned SQL scripts embedded in the two storage packages: `0001`–`0003` and
`0005`–`0018` in `AgentExperience.Storage.Postgres` (there is no `0014`), and `0004` in
`AgentExperience.Storage.Postgres.Vectors`. You apply them explicitly, on every deploy, as the owner role, with
`ExperienceSchemaMigrator.MigrateAsync` (and `ExperienceVectorSchemaMigrator.MigrateAsync` for the vector channel).
The migrator is journaled, runs each script in its own transaction, and serializes concurrent hosts with an advisory
lock. A script that has been applied anywhere is never edited; the schema changes only by adding the next script.

Requires PostgreSQL 15, 16, 17 or 18. PostgreSQL 14 is not supported, because `0005` uses PostgreSQL 15 syntax.

## Applying it

Call `ExperienceSchemaMigrator.MigrateAsync` explicitly at startup, before using the store. The store never migrates
on its own, and nothing migrates on construction.

```csharp
await using var dataSource = NpgsqlDataSource.Create(ownerConnectionString);

var migration = await ExperienceSchemaMigrator.MigrateAsync(dataSource, cancellationToken);
// migration.AppliedScripts lists what this call applied; it is empty when the database was already current.
```

Then run `ApplyApplicationRolePrivilegesAsync` (see [Deploying with two roles](deployment.md#deploying-with-two-roles)).

- **Journaled.** Applied scripts are recorded in `agent_experience.schema_versions` (created by the runner), so a
  rerun applies nothing. A database whose `0001` was applied by hand is journaled on the next run without losing
  rows, because `0001` is idempotent.
- **One transaction per script.** A failing script rolls back its own transaction; scripts applied before it stay
  applied and journaled.
- **Serialized across processes.** The whole run holds a PostgreSQL session advisory lock on its own connection, so
  two hosts starting at once cannot apply the same script twice. The lock is always released. The vectors migrator
  shares the same journal and the same lock.
- **Permissions.** Run the migrator as the **owner** role of the two-role deployment. It needs `CREATE` on the
  database (for the `agent_experience` schema) and on that schema (for its tables), and it has to *own* every table
  whose triggers and guard functions a script creates or replaces — which it does, because it created them. It does
  **not** need to be a superuser, with one exception granted once by a superuser: `0010` and `0012` create functions
  whose `SET` clause names the two purge markers, and PostgreSQL 15+ lets a non-superuser name a custom setting there
  only with `GRANT SET ON PARAMETER agent_experience.purge_authorized, agent_experience.access_purge_authorized TO
  <owner>`. No script in the base package creates an extension. The stores never need more than
  `ApplyApplicationRolePrivilegesAsync` grants the application role, and a reader without `SELECT` on
  `experience_grants` falls back to the exact-scope predicate (see [Sharing](sharing.md)).
- **Connections.** The data source must allow at least two concurrent connections: one for the advisory lock and one
  for the scripts. A multiplexing data source (`NpgsqlDataSourceBuilder.EnableMultiplexing`) cannot hold a session
  advisory lock, because its commands do not stay on one physical connection, so it is not supported for migration.
  Build a non-multiplexing data source for the `MigrateAsync` call.
- **Timeouts.** The wait for the advisory lock is deliberately unbounded, so a run can queue behind another host for
  as long as the caller allows; the caller's `CancellationToken` is the only bound on it. Each *script*, by contrast,
  runs under Npgsql's ordinary command timeout (30 seconds by default), so a single script that takes longer fails
  with a timeout. Raise `Command Timeout` on the connection string if a script needs longer.

| Situation | Result |
| --- | --- |
| Nothing pending | returns an empty `AppliedScripts` |
| A script fails | throws `ExperienceStoreException` naming the failed script (no SQL text or row data), with the original failure as `InnerException` |
| Database unreachable | throws `ExperienceStoreException` |
| Caller cancellation (before the call, or while waiting for the lock) | throws `OperationCanceledException`, unwrapped; no lock left held |
| Caller cancellation once scripts are running | ignored: DbUp's upgrade has no cancellation point, so the run finishes and returns normally |

Scripts are selected only from the package's embedded `Migrations/*.sql` resources, in name order. DbUp variable
substitution is off, so `$body$` and `$1` in a script are left alone, and the runner does not log: nothing reaches
the console, a `Trace`/`Debug` listener, an `ILogger`, or an `AgentExperience.*` activity source, on a clean run or a
failing script (`MigratorLogSilenceTests` captures all four). The one thing outside the runner's reach is your own
driver logging: if you built the data source with `NpgsqlDataSourceBuilder.UseLoggerFactory`, Npgsql's
`Npgsql.Command` category logs each script's SQL text at `Information`, exactly as it logs every other command on
that data source. The scripts carry no row data; filter that category if you do not want DDL in your logs.

`PostgresExperienceRecordSchema.ScriptNames` and `GetScript` remain available for reading a script's SQL (for review
or for applying it through your own change-management tooling), but the migrator is the supported way to apply it.

### Script naming and ordering

Scripts are **append-only**. Each is named with a zero-padded numeric prefix (`0001_`, `0002_`, ...) and a short
description, and they run in ordinal name order. The journal records a script by name, so **a script that has been
journaled anywhere must never be edited or renamed**: databases that already applied it would silently keep the old
definition, and a rename would reapply it. Change the schema by adding the next-numbered script instead.

The two packages apply their own scripts but share one journal and one number sequence, so a gap in either
package's list is expected: `0004` belongs to the vectors package, and `0014` was never shipped.

## The scripts

### 0001: experience records

`0001_create_experience_records.sql` creates the `agent_experience` schema and the `experience_records` table:

- Scope, task, status, confidence, counter, revision, and timestamp columns, with `CHECK` constraints for non-blank
  scope and value ranges.
- A JSONB `payload` column for attempts, outcome, evidence, reflection, environment, and provenance.
- A `payload_version` column. The store owns versioning, so the domain types carry no version field.
- An index on `(tenant_id, application_id, project_id)`.

### 0002: lifecycle events

`0002_create_lifecycle_events.sql` adds the append-only `lifecycle_events` table:

- `event_id` as the primary key (the commit's idempotency key), the record ID, the same scope columns as
  `experience_records`, prior/current status, reason, producer, `occurred_at`/`recorded_at`, and
  `expected_revision`/`applied_revision`.
- `CHECK` constraints mirroring `0001` (non-empty IDs, non-blank scope, reason and producer, non-negative revision)
  plus `applied_revision = expected_revision + 1`, so a row written outside the store cannot desynchronize the log
  from the projection.
- A **unique** index on `(experience_id, applied_revision)`, so exactly one event can ever claim a given revision of
  a record. Two commits racing from the same revision collide here; the loser is reported as `StaleRevision`.
- Deliberately no foreign key to `experience_records`: a commit for a record outside the request scope is rolled
  back by the revision-checked projection update, and a foreign-key violation would report that expected condition
  as an infrastructure failure instead.

### 0003: text search

`0003_add_experience_search.sql` makes records searchable by text:

- A `search_vector` column, `GENERATED ALWAYS AS ... STORED` over `task_id`, the payload's `taskSummary`, and the
  payload's `reflection.lesson`, analyzed with the `english` configuration. Generated, not a trigger and not a column
  the store writes: it is derived from state that already exists, so it can never disagree with the record it indexes
  and no write path has to maintain it.
- A **GIN** index on `search_vector`. The vector is read far more often than written — a record's text never changes
  after it is created, only its status, revision, and `updated_at` do — so GIN's faster `@@` lookups are the right
  trade.
- A composite index on `(tenant_id, application_id, project_id, status, reuse_confidence)`, so a search decides scope,
  status, and the confidence floor from an index rather than scanning foreign scopes. It covers only the three
  *required* scope columns: `team_id`, `agent_id`, and `user_id` are matched with `IS NOT DISTINCT FROM`, which is
  not an indexable btree operator, so including them would not help. A deployment that scopes records by team, agent,
  or user still scans its whole project and filters those three in memory; if that matters at your row counts, add
  your own partial or expression index.
- `0001`'s index on `(tenant_id, application_id, project_id)` is now a prefix of that composite and therefore
  redundant, but it is deliberately left in place: scripts are append-only, and dropping an index `0001` created
  would rewrite history for every database that already applied it. The cost is one extra index maintained on write.
- The concatenated text is bounded with `left(..., 100000)` before it is analyzed. A `tsvector` may not exceed 1 MB,
  and in a *generated* column exceeding it is not a search failure but a failed `INSERT` — and a failed migration on
  a table that already holds such a row. The bound only ever truncates text that would have broken the write.

Adding the generated column rewrites the table, so on a large existing deployment apply this script in a maintenance
window like any other rewriting migration.

### 0004: embeddings (vectors package)

`0004_add_experience_embeddings.sql` ships in `AgentExperience.Storage.Postgres.Vectors` and is applied by
`ExperienceVectorSchemaMigrator.MigrateAsync`. It begins with `CREATE EXTENSION vector`, which needs a superuser
because pgvector is not a trusted extension, so it is kept out of the base package's list: a text-only deployment is
never made to run it. It creates `experience_embeddings`, with a foreign key to `experience_records`. See
[Indexing](indexing.md#what-is-embedded-and-what-is-stored) for its columns.

### 0005: sharing grants

`0005_create_experience_grants.sql` adds explicit sharing grants (see [Sharing and grants](sharing.md)):

- `experience_grants`, keyed by `grant_id`, holding the record it names, the owner scope, the recipient scope, the
  reason, the administrator's principal ID, `issued_at`/`expires_at`, and `revoked_at`/`revocation_reason`.
- `CHECK` constraints mirroring `0002` (non-empty IDs, non-blank scope, reason and administrator) plus two that carry
  the policy itself: `recipient_tenant_id = tenant_id AND recipient_application_id = application_id AND
  recipient_project_id = project_id`, so a grant crossing those boundaries is unstorable however it is written;
  `expires_at > issued_at`, so a grant that was already expired when issued is refused by the database's own clock;
  and `experience_grants_recipient_differs`, so a grant to the scope that already owns the record — which would
  permit nothing while leaving an audit row claiming otherwise — cannot be stored.
- `experience_grant_events`, append-only, with one row per `Issued` or `Revoked` action, carrying both scopes, the
  reason, the administrator, `administrator_authorized_at` (when the host established that authority), and
  `occurred_at`/`recorded_at`. Revoking appends; nothing is ever updated or deleted.
- A **unique partial** index, `ux_experience_grants_active_recipient`, over the record and the full recipient scope
  `WHERE revoked_at IS NULL`, with `NULLS NOT DISTINCT` (PostgreSQL 15 syntax) because a null optional scope field is
  an exact value here rather than a wildcard. It is what makes "revoke the grant you know about" actually end that
  recipient's access.
- `ix_experience_grants_active`, partial on the same `revoked_at IS NULL`, carrying every column the read predicate
  filters on: the record, the recipient scope in full, the expiry, and the owner scope.
- `ix_experience_grants_record` for listing a record's grants from its owner scope, and, on the event log,
  `(grant_id, recorded_at)` for one grant's trail plus `(experience_id, recorded_at)` and
  `(tenant_id, application_id, project_id, recorded_at)` for the two obvious audit questions.
- Deliberately no foreign key to `experience_records`, for the same reason as `0002`: a grant naming a record that is
  not in the owner scope is a typed `NotFound`, not an infrastructure failure.

### 0006: supersession and append-only

`0006_lifecycle_supersession_and_append_only.sql` records supersession's replacement and turns append-only from a
convention into a rule:

- `lifecycle_events.replacement_experience_id`, a nullable `uuid`, with two `CHECK` constraints:
  `(replacement_experience_id IS NOT NULL) = (current_status = 'Superseded')`, so a superseding event always names a
  replacement and no other event ever does; and `replacement_experience_id <> experience_id`, the one cycle a single
  row can state on its own. It is a column rather than a payload field because the replacement chain has to be
  walked in SQL to reject a cycle.
- Enumeration `CHECK`s on `prior_status` and `current_status`. The replacement rule compares `current_status`
  against the literal `'Superseded'`, and before this the column was constrained only to be non-blank — so a row
  storing `'superseded'` would have dodged the rule entirely.
- A partial index on `(experience_id, replacement_experience_id)` over the superseding rows, which is what the
  recursive chain walk follows.
- `BEFORE UPDATE OR DELETE` triggers on `lifecycle_events` and `experience_grant_events` that raise SQLSTATE `42501`
  on any attempt to rewrite or remove a stored event.
- A `BEFORE UPDATE` trigger on `experience_grants` that refuses to clear or change `revoked_at`, to reword a stored
  `revocation_reason`, or to move `expires_at` further out. Shortening an expiry and performing the revocation
  itself are still ordinary updates: it is the direction of travel that is constrained.

The script adds a nullable column and creates triggers, so it does not rewrite the table. It is written so a rerun
does nothing: the column is `IF NOT EXISTS`, and each constraint and trigger is created only when `pg_constraint` or
`pg_trigger` does not already have it — never dropped and recreated, which would leave a window in which the logs
were unguarded.

**Every `CHECK` is added `NOT VALID`, on purpose.** A database written through `0001`–`0005` can hold a `Superseded`
lifecycle event with no replacement, because the public port accepted one before Core's transition table was applied
by the store. A plain `ADD CONSTRAINT` validates immediately, so the script would abort at startup on exactly the
deployments that most need it. `NOT VALID` still binds every new and updated row; it only skips the scan of existing
ones. `0006`'s header carries the reconciliation query and the `VALIDATE CONSTRAINT` statements to run once it comes
back empty (`VALIDATE` takes only a `SHARE UPDATE EXCLUSIVE` lock, so it blocks neither reads nor writes).

**Read the limits of those triggers before relying on them.** They bind every writer that holds the privilege to
write, including one that bypasses this library. They do not bind a superuser, and they do not bind the tables' own
owner, since an owner can disable or drop a trigger and then write freely. In the
[two-role deployment](deployment.md#deploying-with-two-roles) the application role is not the owner and holds no
`UPDATE`, `DELETE` or `TRUNCATE` on the logs, so it is refused by the privilege system first. This is a guard against
a bug, a careless script, or a compromised application path, not tamper-proofing against an administrator holding the
owner's or a superuser's credentials. See [Lifecycle](lifecycle.md#append-only-enforced-by-the-database).

### 0007: confidence evidence

`0007_confidence_evidence.sql` adds the evidence ledger and guards the columns it starts moving:

- `confidence_evidence`: `evidence_id` as the primary key, the record and the event it rode in on, the evidence's
  kind and source, the run, the verification round or the reviewer identity, `counted`, `recorded_at`, and
  `applied_revision` — plus `independence_key`, `GENERATED ALWAYS AS ... STORED` from the source, run, round, and
  reviewer. `CHECK` constraints make each source carry exactly the identifiers its key is made of: without them a
  machine row with no round (or a human row with no reviewer) would generate a `NULL` key, which a unique index
  cannot deduplicate, so every such submission would count.
- A **partial** unique index on `(experience_id, independence_key) WHERE counted`. Partial rather than plain,
  because a plain one would have to reject a later submission for a taken key — and the submission belongs in the
  audit trail whether or not it moves a counter.
- Columns on `lifecycle_events` that make an update reconstructable from the log alone: `actor`, the evidence ID,
  kind, source, run, round, reviewer, rule version and detail, and the prior and new score and counters. A `CHECK`
  using `num_nonnulls(...) IN (0, 11)` makes them all present or all absent, because a new score with no prior one
  to compare it against says nothing at all. Another refuses an event that is both a supersession and a confidence
  update.
- `enforce_record_projection` (from `0006`) replaced with a version that guards `reuse_confidence`,
  `supporting_validations`, and `contradictions`: they change only together with a revision that moved forward
  **and** only to the values a lifecycle event already recorded for exactly that revision. Advancing the revision
  alone is not enough, so `UPDATE … SET reuse_confidence = 1, revision = revision + 1` is refused like any other
  direct write, with SQLSTATE `42501`. It is why the store writes the event before the projection.
- The script's header carries a `CREATE UNIQUE INDEX CONCURRENTLY` runbook for
  `ux_lifecycle_events_confidence_evidence`: a plain build takes a `SHARE` lock and blocks appends, which is
  imperceptible on a small log and a write outage on a long one.
- `BEFORE UPDATE OR DELETE` and `BEFORE TRUNCATE` triggers making `confidence_evidence` append-only, reusing
  `0006`'s function. A row that could be edited or removed would free an independence key, and the same observation
  could then be counted twice.

Like `0006`, every `CHECK` it adds to the already-populated `lifecycle_events` is `NOT VALID`: the new columns are
`NULL` on existing rows and would in fact validate, but a scan of a large append-only log at startup is a cost no
deployment asked for. The script's header carries the confirmation query and the `VALIDATE CONSTRAINT` statements.
The new table's own constraints are plain — it starts empty, so there is nothing to scan. The same limits apply to
its triggers as to `0006`'s.

### 0008: reuse feedback

`0008_reuse_feedback.sql` adds the append-only reuse feedback ledger (see [Reuse feedback](reuse-feedback.md)):

- `reuse_feedback`: `feedback_id` as the primary key (the submission's idempotency key), the run, the same scope
  columns as `experience_records`, the run's outcome, `claimed_benefit` and `benefit`, `attribution_source`, the
  reviewer identity *or* the evaluator and verification round, the rationale, the measure's kind and value, the
  trial label, and `observed_at`/`recorded_at`.
- The `CHECK` that carries the whole story: `(attribution_source = 'None') = (benefit = 'Unknown')`. "Nothing
  attributed this" and "benefit unknown" are one fact, so they cannot drift into a row claiming an improvement
  nothing evidenced. `claimed_benefit` sits beside it, recorded and never promoted: what a caller believes is data
  about the caller, not evidence about a record.
- Further `CHECK`s making each attribution shape carry exactly what it must: a human row a reviewer, an
  `assessment_id`, and no evaluator or evidence list; a comparative row an evaluator, a round, and a non-empty
  `evidence_ids`, and no reviewer or assessment. Both carry `attributed_at`. The round is the machine independence
  key's second half for a comparative row and **audit only** for a human one, whose evidence is keyed on the reviewer
  and the run instead.
- `evidence_ids` is stored so an auditor sees what a moved score rested on rather than only the evaluator's own
  summary of it — Core cross-checks each piece against the round the result names before it is accepted.
- `reuse_feedback_exposures`: one row per record the run saw, keyed `(feedback_id, experience_id)`, carrying
  `ordinal` and `attributed`, plus the `evidence_id` derived from `(feedback_id, experience_id)` — present exactly
  when `attributed`. `ordinal` is the *normalized* order (by experience ID), not the caller's, and is bounded by
  `CHECK (ordinal >= 0 AND ordinal < 64)`, which mirrors `ExperienceReuseFeedback.MaxExposedRecords` and is the
  schema's half of the only bound on a submission's fan-out.
- **`evidence_id` says which ID, not that it landed.** It is written with the exposure, before any confidence
  submission is attempted, because the exposure must be durable first. An attributed exposure whose record turned
  out ineligible or unresolved, or whose commit failed, therefore has an `evidence_id` with no row in
  `confidence_evidence`. That is the outstanding work, not a dangling reference: read it with a `LEFT JOIN` — the
  script's header carries the query — and an inner join would silently drop exactly the rows worth looking at.
- Unique indexes on `evidence_id` (partial, where present) and on `(feedback_id, ordinal)`. The derivation is a
  pure function of the feedback and the record, so a collision on the first means it was bypassed rather than that
  two observations coincided. The script's header carries the `CREATE UNIQUE INDEX CONCURRENTLY` runbook for both,
  for a database whose schema was applied by hand and may already hold rows.
- The exposures-to-submissions foreign key is added `ALTER TABLE … NOT VALID`, with the confirmation query and the
  `VALIDATE CONSTRAINT` statement in the script's header — for the same hand-applied case. Everything else is a
  constraint on a table this script creates, so it starts empty and there is nothing to scan.
- Deliberately **no** foreign key to `experience_records`, matching `0007`: a run that saw an ID resolving to
  nothing in its scope must still be recordable, and a foreign key would turn that fact into a write failure.
- `BEFORE UPDATE OR DELETE` and `BEFORE TRUNCATE` triggers on both tables, reusing `0006`'s function. Promoting a
  recorded exposure into an attribution after the fact is exactly what they stop. The same limits apply as to
  `0006`'s.

### 0009: grant access log and lifetime ceiling

`0009_grant_access_log.sql` adds the append-only grant access ledger and the database's own grant-lifetime ceiling:

- `experience_grant_access`: `access_id` as the primary key, the `grant_id` the read predicate actually used, the
  record and the `record_revision` that was disclosed, the record's owner scope, the recipient scope the read was
  made in, the reading `principal_id` (**`NOT NULL`** — a row that cannot say who does not answer the question),
  the optional `correlation_id`, and `occurred_at`/`recorded_at`. One row per record a grant **delivered** — see
  [Recording who read a shared record](sharing.md#recording-who-read-a-shared-record) for exactly what is and is not
  recorded.
- `CHECK`s mirroring `0005`'s: non-empty IDs, non-blank scope, and — restated on the row that claims a grant
  carried a record across a boundary — `experience_grant_access_same_boundary` and
  `experience_grant_access_recipient_differs`. A row saying a record left its tenant, application, or project is
  unstorable here even if some other writer managed to store the grant that would have allowed it.
- Indexes on `(grant_id, occurred_at)` — "who read anything through this grant" — and on
  `(tenant_id, application_id, project_id, experience_id, occurred_at)` — "who saw our team's experience, and
  when". The script's header carries the `CREATE INDEX CONCURRENTLY` runbook for a hand-applied database.
- Deliberately **no** foreign key to `experience_grants` or `experience_records`, matching `0002`, `0007`, and
  `0008`: the trail must outlive whatever it describes. `grant_id` joins to `experience_grants` and
  `experience_grant_events` by hand, which is how an auditor moves from "who read it" to "who permitted it"; the
  script's header carries that query.
- `BEFORE UPDATE OR DELETE` and `BEFORE TRUNCATE` triggers, reusing `0006`'s function. Erasing that a record was
  handed to someone is exactly what they stop, and the same limits apply as to `0006`'s.
- `experience_grants_lifetime_bounded` on the **existing** grants table, added `ALTER TABLE … NOT VALID` because a
  database issuing grants since `0005` may already hold an unbounded one. It exempts a revoked row
  (`revoked_at IS NOT NULL OR …`), because PostgreSQL re-checks a `CHECK` on every `UPDATE` and revoking such a
  grant is the one remedy the runbook prescribes — without the exemption it would be permanent and unrevocable.
  The header carries the query that finds them and the `VALIDATE CONSTRAINT` statement; `0006`'s trigger still
  refuses any `UPDATE` that moves `expires_at` outward, so they are ended by revoking rather than shortened.
- `experience_grants_issued_not_future`, a `BEFORE INSERT` trigger refusing a grant dated more than a minute ahead.
  The ceiling above is relative to `issued_at`, so without it a bypassing writer could store an effectively
  permanent grant simply by dating it a century forward; a `CHECK` cannot say this, because it may not call
  `now()`.
- Retention: this ledger is deliberately **not** swept by a record erasure, because who read a record before it was
  deleted outlives the record. It is the table most likely to grow without bound in a deployment that shares
  heavily, so plan its retention before enabling auditing at scale; `0012` adds the purge.

### 0010: delete and expire

`0010_delete_and_expire.sql` adds the one erasure path (see [Deletion and retention](deletion-and-retention.md)):

- `experience_records.deleted_at`, nullable, so no existing row is rewritten, plus
  `experience_records_tombstone_shape` — added `ALTER TABLE … NOT VALID` like every other `CHECK` on an existing
  table — which makes "erased" one shape rather than a flag a writer could set over a payload that is still there.
- `agent_experience.purge_experience_record`, a `SECURITY DEFINER` function holding the whole erasure: the scope
  and revision guards, the seven tables it sweeps, and the tombstone. `agent_experience.purge_expired_grants` does
  the same for expired grants and their events.
- `agent_experience.purge_authorized()`, which reads the transaction-scoped marker the guards recognise, and
  replacements for `0006`'s `reject_event_log_mutation` and `reject_audited_grant_delete` and `0007`'s
  `enforce_record_projection`. They are replaced with `CREATE OR REPLACE`, so every `ENABLE ALWAYS` binding
  survives and no table is unguarded for an instant; nothing is dropped, disabled, or recreated. `UPDATE` and
  `TRUNCATE` stay refused unconditionally, and `DELETE` is admitted only under the marker and only on the five
  tables an erasure sweeps — `experience_grant_access` is deliberately not one of them.
- `agent_experience.reject_record_removal`, and the only two triggers this script creates:
  `experience_records_no_delete` and `experience_records_no_truncate`, both `ENABLE ALWAYS`. A bare
  `DELETE FROM agent_experience.experience_records` used to succeed from any session with `DELETE` on the table —
  orphaning the whole audit trail, none of which has a foreign key back to the record, and **freeing the ID**, so
  that a record re-created under it inherits every grant issued over the old content. It is refused now with no
  marker clause and no exception at all, because the erasure never deletes that row: it updates it into a
  tombstone, which is the point.
- The projection guard gains two rules: a tombstone can never be updated again, by anyone, and `deleted_at` can be
  set only inside the purge. The marked exception is shape-checked rather than merely marker-checked — a live row,
  to an empty-payload tombstone, in its own scope, with its own `payload_version` and a `created_at` equal to the
  deletion instant, one revision forward — so a marked transaction may make that one transition and no other. The
  scope columns are part of that check for a concrete reason: without them one marked `UPDATE` could tombstone a
  record *into another tenant's scope*, leaving the owning scope seeing `NotFound` for its own erased record.
- `ix_experience_records_live_by_age`, partial on `deleted_at IS NULL`, which is the retention sweep's whole
  predicate; plus the `confidence_evidence (experience_id)` and `reuse_feedback_exposures (experience_id)` indexes
  `0007` and `0008` each deferred to this script, because this is the query that justifies them. **All three are
  built with plain `CREATE INDEX` inside the migrator's per-script transaction**, which takes a `SHARE` lock and
  blocks writes to those tables for the duration — and one of them is over `experience_records`, the table this
  library writes most, so on an established database this is a larger write outage than `0007`'s, `0008`'s or
  `0009`'s. `CREATE INDEX CONCURRENTLY` cannot run in a transaction block at all, so it cannot simply be swapped;
  the script's header carries the out-of-band runbook (add the column, build all three `CONCURRENTLY`, then
  migrate, at which point `IF NOT EXISTS` makes the script's own statements no-ops), exactly as `0007`, `0008`
  and `0009` do for theirs.
- `REVOKE ALL … FROM PUBLIC` on both purge functions, and `GRANT EXECUTE … TO CURRENT_USER`. Without it,
  PostgreSQL's default `EXECUTE`-to-`PUBLIC` on a `SECURITY DEFINER` function would make erasure available to
  every role that can connect.
- The script's header carries the honesty statement, the retained list (including `payload_version`), the
  privilege note, the `CONCURRENTLY` runbook, the dead-heap-tuple limit, and the confirm-then-`VALIDATE` step.

### 0011: grant disclosure

`0011_grant_disclosure.sql` adds a grant's disclosure level (see [Disclosure levels](sharing.md#disclosure-levels)):

- `experience_grants.disclosure text NOT NULL DEFAULT 'LessonOnly'`, with `experience_grants_disclosure_known`
  (`'LessonOnly'` or `'LessonAndApproach'`). The default covers every existing grant and any writer that bypasses
  this library. **This changes behaviour on upgrade:** every existing grant becomes `LessonOnly`, so a borrowed
  record's `Approach:` line stops being injected until the owner revokes the grant and issues a `LessonAndApproach`
  one.
- **Deployment order:** run `0011`, then deploy the new build, and stop older writers first (see
  [Upgrading the grant schema](sharing.md#upgrading-the-grant-schema)).
- A nullable `disclosure` on `experience_grant_events` and on `experience_grant_access`, each with a
  `*_disclosure_known` `CHECK` and a `*_disclosure_recorded` `CHECK (disclosure IS NOT NULL)` added `NOT VALID`.
  New rows must carry a level; rows written before `0011` stay `NULL` — "not recorded" — and are never re-checked.
  Leave both `NOT VALID`: those rows cannot pass a `VALIDATE`, and nothing can backfill an append-only trail. A
  hand-written event or access row now has to name a level too.
- `enforce_grant_monotonicity()` replaced in place with `0006`'s whole body plus `disclosure` among the identity
  pins, so a live grant's level cannot be changed by `UPDATE` (`42501`); revoke it and issue a new one instead.

### 0012: grant access retention

`0012_grant_access_retention.sql` adds the access ledger's retention path (see
[Retention for the grant access log](deletion-and-retention.md#retention-for-the-grant-access-log)):

- `agent_experience.purge_grant_access`, `SECURITY DEFINER` with `search_path` pinned, `EXECUTE` revoked from
  `PUBLIC` and granted to the migrating role: bounded (1…500, `NULL` is 500), scoped to an owner scope exactly or
  as a subtree, age on `recorded_at`, and refusing (`CutoffTooRecent`) any cutoff later than 30 days before
  `clock_timestamp()`, or a `NULL` one.
- `agent_experience.access_purge_authorized()`, its own marker (reset when the function returns), and
  `reject_event_log_mutation()` replaced in place with `0010`'s body unchanged plus one exception: a `DELETE` on
  `experience_grant_access` under that marker of a row at least 30 days old. `UPDATE` and `TRUNCATE` stay refused;
  `0010`'s marker still admits nothing on that table; nothing is dropped, disabled or recreated.
- `ix_experience_grant_access_retention (tenant_id, application_id, project_id, recorded_at, access_id)`, built with
  plain `CREATE INDEX` inside the migrator's transaction, which blocks appends to the ledger while it builds; the
  header carries the `CONCURRENTLY` runbook for building it out of band first.

### 0013: role separation hardening

`0013_role_separation_hardening.sql` hardens the schema for the [two-role deployment](deployment.md#deploying-with-two-roles):

- `ALTER FUNCTION … SET search_path = pg_catalog, agent_experience, pg_temp` on the three `SECURITY DEFINER` purge
  functions and on every guard trigger function (`reject_event_log_mutation`, `enforce_grant_monotonicity`,
  `reject_audited_grant_delete`, `enforce_record_projection`, `reject_record_removal`,
  `reject_future_grant_issue`). No body is restated and no trigger recreated, so no table is unguarded while it
  applies; the purge functions keep their marker-reset `SET` clauses.
- `REVOKE ALL … FROM PUBLIC` on the three purge functions, restated so a hand-widened ACL is narrowed back.
- It grants nothing to a named role: that is `ApplyApplicationRolePrivilegesAsync`'s job, on every deploy.

There is no `0014`.

### 0015: verified independence

`0015_verified_independence.sql` makes an assessment single-use (see [Confidence](confidence.md)):

- `confidence_evidence.assessment_id uuid NULL` and `lifecycle_events.confidence_assessment_id uuid NULL`, each with
  a `CHECK` keeping it on human rows only and off the empty UUID, added `NOT VALID` so neither ledger is scanned; the
  header has the `VALIDATE` statements (every existing row is `NULL`, so both pass).
- `ux_confidence_evidence_assessment`, unique on `(experience_id, assessment_id) WHERE assessment_id IS NOT NULL`,
  built with plain `CREATE UNIQUE INDEX`, which blocks evidence appends while it builds; the header carries the
  `CONCURRENTLY` runbook for building it out of band first.
- No table, function, trigger or grant, so the application role's manifest is unchanged: its table-level `INSERT`
  and `SELECT` on both ledgers cover the new columns, and it has no `UPDATE` on either.
- The closed round a finalized record vouches for needs no schema: it travels in the payload as `closedRoundId`,
  written only when finalization closed a round (a payload with none is byte for byte what it was), and read back as
  `ExperienceRecord.ClosedRoundId`. The payload version stays `1`; an older reader ignores the field. A record
  finalized before this change has none, so machine evidence about its run is refused unless the host opts out.

### 0016: crypto-shredding

`0016_crypto_shredding.sql` gives [crypto-shredding](crypto-shredding.md) its database side, and changes nothing for
a deployment that stays in plaintext mode:

- `experience_records.search_vector_sealed tsvector NULL`, with a partial GIN index on it, and
  `reuse_feedback_exposures.rationale_sealed text NULL`.
- `ix_experience_records_unsealed`, a partial index over live `payload_version = 1` rows: the upgrade job's worklist,
  which empties as the job runs.
- Three `NOT VALID` checks: a live sealed row (`payload_version = 2`) has exactly the sealed shape — one sealed
  property, the `(sealed)` task ID, a sealed search vector; only a live sealed row has a sealed search vector; and
  `rationale_sealed` holds only the sealed format.
- `guard_sealed_record_erasure`, a `BEFORE UPDATE` trigger function on the tombstone transition: it refuses to
  tombstone a sealed row unless the transaction declares, with `agent_experience.erasure_destroys_key`, that it
  destroys the key, and it clears `search_vector_sealed`, so `0010`'s purge function is not restated.
- `agent_experience.seal_experience_record`, `SECURITY DEFINER` with `search_path` pinned and `pg_temp` last, and
  `EXECUTE` revoked from `PUBLIC`: the upgrade job's one write, admitting only a live plaintext row at the expected
  revision into its sealed shape. `ApplyApplicationRolePrivilegesAsync` grants it only with `AllowSealing`.

### 0017: grant argument disclosure

`0017_grant_argument_disclosure.sql` adds the third disclosure level (see [Disclosure levels](sharing.md#disclosure-levels)),
and changes nothing that is shown on upgrade:

- `experience_grants.approach_arguments jsonb NULL`: the owner's argument allowlist. No default, so nothing is
  rewritten; every existing grant keeps its level and has none.
- The three `*_disclosure_known` checks — on grants, grant events and access rows — dropped and re-added under the
  same names with `'LessonApproachAndArguments'` in the list, in one transaction, and idempotent by content.
- `experience_grants_approach_arguments_level` (keys present exactly at the new level) and
  `experience_grants_approach_arguments_shape` (a non-empty object of non-empty string arrays).
- The widened checks on grants and grant events are re-validated in the script (both tables are small), so they stay
  validated as `0011` left them. The one on `experience_grant_access` and the two new ones are `NOT VALID`: every
  existing row already satisfies them, and scanning a large access ledger inside the migration would hold its lock
  for the whole scan. Until you run `VALIDATE CONSTRAINT` (only `SHARE UPDATE EXCLUSIVE`; the header lists the
  statement), `pg_constraint.convalidated` reads `false` for those three.
- `enforce_grant_monotonicity()` restated with `0011`'s whole body plus `approach_arguments` among the identity pins,
  and `0013`'s `search_path` pin restated after it, because `CREATE OR REPLACE` resets it.
- No privilege change: the application role's table-level `INSERT` and `SELECT` cover the column, and it gets no
  `UPDATE`.
- **Deployment order:** run `0017`, then deploy the new build (see
  [Upgrading the grant schema](sharing.md#upgrading-the-grant-schema)).

### 0018: evidence admission

`0018_evidence_admission.sql` records which verification mode admitted each piece of confidence evidence (see
[What the opt-out admitted is kept visible](confidence.md#what-the-opt-out-admitted-is-kept-visible)):

- `confidence_evidence.admission text NULL` and `lifecycle_events.confidence_admission text NULL`, each with a
  `CHECK` admitting `NULL`, `'Verified'` or `'HostTrusted'` only (the event's only on a confidence event), added
  `NOT VALID` so neither ledger is scanned; the header has the `VALIDATE` statements.
- No table, index, function, trigger or grant, so the application role's manifest is unchanged: its table-level
  `INSERT` and `SELECT` on both ledgers cover the new columns, and it has no `UPDATE` on either.
- Exposure-bound evidence itself needs no schema: a run's exposures and a record's origin travel in the payload as
  `provenance.exposedTo` and `origin`, written only when set. The payload version stays `1`; an older reader ignores
  both. A record written before exposure-bound evidence existed has neither, so it reads back `HostWritten` and exposed to nothing, and
  evidence about its run is refused unless the host opts out.

**The base package's schema excludes the embedding table, and that is deliberate.** The `vector` extension and the
`experience_embeddings` table belong to `AgentExperience.Storage.Postgres.Vectors` and are applied by *its* migrator.
Nothing in the base package creates an extension, and nothing in it reads or writes the embedding table — except the
erasure, which deletes a record's embedding when the table exists (guarded by `to_regclass`).

## Script comments that were written before the work they point at shipped

Because a journaled script is never edited, a few script *comments* still describe later work as future work, and
name it by the planning story it was scheduled under. They ship inside the package as embedded resources, so here is
what each one now means. None of them changes what a script does; they are comments only.

| Script | Its comment says | What actually shipped |
| --- | --- | --- |
| `0006` | Purging an event is an operator action (`ALTER TABLE … DISABLE TRIGGER`) "until the library ships a purge path (roadmap story 4.5 …)" | Shipped as `0010`, whose header says so and supersedes that runbook: one `SECURITY DEFINER` purge function under a transaction-scoped marker, no trigger ever disabled. Do not use `0006`'s runbook — see [Deletion and retention](deletion-and-retention.md) |
| `0007` | Listing the evidence ledger, a foreign key to `experience_records`, and retention over `confidence_evidence` "all belong to roadmap story 4.5" | Retention shipped in `0010`: erasing a record removes every evidence row naming it, with the index that needs. The foreign key was deliberately **not** added (`0010` explains why). Listing the ledger through the port was decided against: lifecycle history already carries each counted update's prior and new values, and no acceptance criterion needs uncounted duplicates |
| `0008` | Retention of the feedback ledger is "deferred to roadmap story 4.5", to be done with `0006`'s runbook | Shipped in `0010`: erasing a record removes its exposure rows and any submission left empty, with the index that needs. `0006`'s runbook is superseded as above |
| `0008` | The aggregations "roadmap story 4.4 needs — by run, by trial label, by scope" will come with their own indexes | The reuse baseline measured reuse through in-memory port doubles, not SQL over this ledger, so no aggregation query and no index was added. Add one with the first query that needs it |
| `0006`, `0010`, `0012` | The guards do not bind the tables' owner, "which the application role is because it created them"; the purge path is "an auditability mechanism, not a privilege boundary"; an application role that is not the migrating role must be granted `EXECUTE` by hand | The two-role deployment is now the supported one: the application role owns nothing and holds no `DELETE` on any ledger, so neither the owner's escape hatch nor a hand-set marker is available to it, and `ApplyApplicationRolePrivilegesAsync` grants `EXECUTE` when the host opts in. The owner and superusers remain unbound. See [Deploying with two roles](deployment.md#deploying-with-two-roles) |
| `0010` | "Backups, replicas, WAL and logical-replication streams … are host-owned and out of reach of this schema", and the dead tuple "STILL CARRIES THE ERASED TEXT" | Still exactly true in plaintext mode. In encrypted mode every one of those copies holds only ciphertext whose key the erasure destroyed; what stays readable is the derived search data. See [Crypto-shredding](crypto-shredding.md) |
| `0009` | Retention of the grant access log is "deferred to roadmap story 4.5", to be done with `0006`'s runbook | Erasing a record deliberately **keeps** access rows — they carry no payload and answer "who read this before it was deleted". Their retention shipped in `0012` as its own path, by age, never younger than 30 days, under its own marker — not `0006`'s runbook. See [Retention for the grant access log](deletion-and-retention.md#retention-for-the-grant-access-log) |

## Data semantics

- **One write path per change.** Each create is a single `INSERT`. The only update is a lifecycle commit, which is
  always paired with its event in one transaction. The only deletion is `DeleteAsync` and the retention sweep that
  runs it, which erase payload and leave a tombstone; apart from the two purges — expired grants with their events,
  and grant access rows past their retention (`0012`) — no other path *in this library* removes a record, an event,
  or a ledger row, and for the record row itself `0010`'s removal guard makes that true of the schema rather than only
  of the library — a bare `DELETE` or `TRUNCATE` is refused from every session, marker or not. (The crypto-shredding
  upgrade job's `seal_experience_record` rewrites a plaintext payload into its sealed shape, and nothing else.)
- **UTC timestamps.** Every timestamp is stored and returned in UTC. `CreatedAt`, `UpdatedAt`, and a lifecycle
  event's `OccurredAt` are columns, and PostgreSQL keeps microsecond precision, so sub-microsecond ticks are
  truncated on write. Nested timestamps are stored in the payload at full precision.
- **Tool-call arguments** are stored as JSON and read back normalized to `string`, `bool`, `long` (integers that
  fit), `double`, `null`, `Dictionary<string, object?>`, or `List<object?>`. Dictionary key order is not preserved.
  Whole-number doubles (for example `1.0`) are written as JSON integers, so they read back as `long`. Values that
  cannot be serialized to JSON (for example `NaN`, infinities, or cyclic graphs) make the create `Invalid`.
- **Query order** is newest `CreatedAt` first, then `ExperienceId` in PostgreSQL `uuid` byte order, which differs
  from .NET `Guid` comparison. `Limit` must be from 1 to 500 (default 50). `Statuses` is either null (all statuses)
  or a non-empty list.
- **Search order** is descending `ts_rank_cd` relevance, then `ExperienceId` in PostgreSQL `uuid` byte order. `Limit`
  must be from 1 to 200 (default 50), and `EligibleStatuses` must be non-empty — an empty set is `Invalid` rather
  than widened to "every status", so a caller can never accidentally ask for records it considers ineligible.
- PostgreSQL cannot store the NUL character (U+0000) in `text` or `jsonb`, so a record or scope containing it is
  `Invalid` and never reaches the database.

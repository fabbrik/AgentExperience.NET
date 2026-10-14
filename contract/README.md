# The language-neutral contract

Microsoft Agent Framework ships in .NET, Python and Go. This directory states what makes a lesson a lesson, independently
of .NET, so that a port in another language writes records the .NET library reads, renders the same block from them,
and derives the same task text. A lesson learned in one language can then help an agent in another.

**Version: `1.0`.** It describes records at `payload_version` 1, the current one. The version is `major.minor`: a
change that only adds cases bumps the minor version, and a change to anything already published (a fixture, the
schema, the SQL or this page) bumps the major version. A port states the contract it passes as "contract 1.N": it
passes every fixture of that version, and with it every earlier 1.x fixture.

The contract is this page, the record schema, the two SQL statements, and the fixtures. The fixtures are generated from
the .NET implementation and checked against it on every build (`tests/AgentExperience.Contract.Tests`), so they cannot
drift from it. Where this page and a fixture disagree, the fixture is right; where both are silent on wording, the
.NET source this page links to is the reference.

## What is in scope, and what is not

In scope for contract 1.x:

- **The stored record:** the columns of `agent_experience.experience_records` and its `payload` column, how a captured
  run plus a verification becomes one, and the IDs it is stored under.
- **The injected block:** how stored records become the Historical Reference block an agent is shown.
- **The task text:** how a request's messages become the text retrieval searches with.
- **Text retrieval:** the SQL a port issues, and how its results are ranked.
- **The error class:** how a captured error is reduced to the short class a lesson and a block show.

Out of scope, and not to be written or changed by a contract 1.x port:

- **Grants.** A port reads its own scope only (`p_with_grants => false`) and never writes `experience_grants`.
- **Crypto-shredding.** A port reads and writes plaintext rows only (`payload_version` 1). A sealed row
  (`payload_version` 2) is not readable without the .NET key store; skip it.
- **Provenance signing.** A port writes no `provenanceSignature`, and must not change one it reads.
- **Vectors.** A port uses text retrieval only.
- **The lifecycle after finalization.** Reuse evidence, confidence updates, reinforcement, contest, supersession,
  revocation, deletion and retention stay .NET-only: a port finalizes, injects and searches, and never moves a stored
  record's status, counters or revision.
- **The other derived IDs.** Only the record, initial event and reflection IDs below are in scope; the reuse-evidence
  IDs (tags 4 and 5) belong to the lifecycle above.
- **Hosting.** Dependency injection, middleware wiring and how the block is placed in a conversation (the .NET adapter
  sends it as one user-role message by default) are each framework's own.

A port runs against a schema the .NET migrator applied: `ExperienceSchemaMigrator.MigrateAsync`, then
`ApplyApplicationRolePrivilegesAsync` for the application role with `EnableRowLevelSecurity = true`, which is the
configuration in which that role may execute the text search function. A port never creates or alters schema objects.

## Layout

```
contract/
  README.md                          this page
  schema/experience-record.v1.json   JSON Schema (draft 2020-12) for one stored record: columns and payload
  sql/declare-bounds.sql             the bounds every transaction declares first
  sql/search-experience-text.sql     the text search
  fixtures/
    finalize/<case>/                 input.json -> expected-capture.json, expected-record.json, expected-event.json
    inject/<case>/                   records.json + options.json -> expected-block.txt
    task-text/<case>/                messages.json -> expected.json
    search/<case>/                   seed.json + query.json -> expected.json (needs PostgreSQL)
    error-class/cases.json           error -> class
  lock.json                          the contract version and a SHA-256 of every other file here
```

Every file is UTF-8 without a byte-order mark, with LF line endings. JSON is indented by two spaces; every invisible
character inside a string (control, format, line or paragraph separator) is written as a `\uXXXX` escape, and a
character outside the Basic Multilingual Plane may be written as an escaped surrogate pair. Compare JSON by value, and
`expected-block.txt` byte for byte.

## How a port uses it

For each fixture, the port reads the inputs, runs its own implementation, and compares its output with the expected
file:

- **finalize:** run capture and finalization on `input.json`, and compare what capture answered for each attempt with
  `expected-capture.json`, the stored record with `expected-record.json`, and the initial lifecycle event with
  `expected-event.json`. Every record must also validate against the schema.
- **inject:** read each record in `records.json` (the stored form, plus the ranking retrieval gave it), render with
  `options.json`, and compare the block with `expected-block.txt`.
- **task-text:** derive the task text from `messages.json` and compare it with `expected.json` (`null` when nothing
  qualifies).
- **search:** against a database the .NET migrator applied, insert `seed.json`, run the two statements with
  `query.json`, and compare the returned rows with `expected.json`'s `rows`; apply the ranking below and compare with
  `truncated` and `ranked`.
- **error-class:** reduce each `error` and compare with its `class`.

A port that passes every fixture of a version supports that version. What the .NET side checks of every stored record
in the fixtures (each finalize `expected-record.json`, each record in an inject `records.json`, each search
`seed.json`), in `tests/AgentExperience.Contract.Tests`: it validates against the schema; it is inserted into
PostgreSQL as a port inserts it (below, as the application role with row-level security on) and read back through the
PostgreSQL record store's own `GetAsync`, and the record read back, written in the stored form again, is the fixture
exactly. A record a port writes in this form therefore reads back through the .NET library.

## The stored record

One record is one row of `agent_experience.experience_records`. The schema describes it as
`{ "columns": { ... }, "payload": { ... } }`: the eighteen columns a writer supplies, named as in the table, and the
`payload` column's JSON.

A port inserts exactly these columns, inside a transaction that first ran `sql/declare-bounds.sql`:

`experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status,
reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload`

Everything else in the row is the database's: `search_vector` is a generated column over the task ID, the task summary
and the lesson; a trigger sets `reflection_model_authored` from the payload; `deleted_at` is null.

**Encodings.**

| Value | Form |
| --- | --- |
| UUID | lower-case, hyphenated (`000000f1-0000-4000-8000-000000000000`) |
| Instant | ISO 8601 in UTC with the offset `+00:00` (never `Z`), trailing zeros of the fraction dropped (`2026-03-02T09:00:30+00:00`). Payload instants keep up to seven fractional digits; the `created_at` and `updated_at` columns are `timestamptz`, which keeps whole microseconds, so at most six: truncate, never round. |
| Duration | `[-][d.]hh:mm:ss[.fffffff]`, the .NET constant `TimeSpan` format (`00:00:03.5000000`) |
| Enum | the member name, exactly as written (`Validated`, `Verified`, `Pass`); never a number |
| Number | a JSON number. `reuse_confidence` is a double: a validated record's is `2/3`, `0.6666666666666666` |
| Optional scope field | JSON `null` (SQL `NULL`), never the empty string |
| Payload member marked optional | omitted when absent, never written as `null`: `closedRoundId`, `origin` (written only as `Finalized`), `provenanceSignature`, the reflection's `authorship` (written only as `Model`), the provenance's `exposedTo` |
| Other payload members | always written, `null` when empty |
| Tool-call arguments | the sanitized arguments as a JSON object of any JSON values; a redacted secret is `""` |

A reader must not depend on member order or whitespace: `jsonb` keeps neither.

**What the schema also holds.** The default capture limits (at most 10 attempts, 50 tool calls per attempt, 50 argument
fields with names of at most 100 code units and no `.`, `[` or `]`, results and errors of at most 4,000) and the status
rules: a `Quarantined` record has a null reflection, `reuse_confidence` 0 and `supporting_validations` 0; a `Validated`
or `Reinforced` one has a reflection; `revision` is at least 1; `closedRoundId` appears only with `origin` (which can
appear alone, for a run finalized with no closed round). The schema counts string lengths in code points, the contract
in UTF-16 code units, so its length bounds are necessary rather than sufficient.

**IDs.** A finalized record's IDs are derived, so a retry re-derives them. They are UUIDv8 (RFC 9562's custom
version): version nibble 8, RFC 4122 variant.

- `experience_id` = derive(run ID, tag `1`, scope); the initial event's `event_id` = derive(run ID, tag `2`, scope);
  the reflection's `reflectionId` = derive(run ID, tag `3`, no scope).
- To derive one, take SHA-256 over the 16 bytes of the namespace UUID `0b6a8a3f-1c2d-4f5e-9a70-3d1c9f2b8e41`, then the 16 bytes of
  the run ID (both in RFC 4122 byte order, as the UUID is written), then the tag byte, then, with a scope, each of
  `tenant_id, application_id, project_id, team_id, agent_id, user_id` in that order: a null field is the single byte
  `0x00`; a present one is `0x01`, its UTF-8 length as a 4-byte big-endian integer, and its UTF-8 bytes. Take the first
  16 bytes of the hash, set the version nibble (`byte[6] = byte[6] & 0x0F | 0x80`) and the variant
  (`byte[8] = byte[8] & 0x3F | 0x80`), and read them as a UUID in the same byte order.

Every finalize fixture's `expected-record.json` and `expected-event.json` let a port check its derivation.
Reference: `ExperienceFinalizationService.Derive` in
[`ExperienceFinalizationService.cs`](../src/AgentExperience.Core/Finalization/ExperienceFinalizationService.cs).

## Finalization: from a captured run to a stored record

`input.json` holds the run as its host observed it, before sanitization (`run`), the ID its completion was recorded
under, the host's verification (`verification`), and the options that change the result (`options`). The steps, in
order:

1. **Capture sanitizes.** Each tool call's arguments go through the `ToolArguments` policy; each tool call's and
   attempt's result and error go through the `ToolResult` policy as a single field named `value`.
   - A field whose name is a secret name is redacted to `""`, whatever its value, before anything inside it is read.
     The default secret names, matched ignoring case: `password`, `secret`, `token`, `apiKey`, `api_key`,
     `authorization`, `connectionString`.
   - A field whose name is allowlisted (matched exactly) is kept; an object or array value is walked one level deeper
     under the same rules. `options.allowedArgumentNames` is the `ToolArguments` allowlist; `null` means the default,
     which allowlists nothing, so no argument value is kept.
   - Every other field is omitted.
   - A payload deeper than 4 levels, with more than 50 fields, a value over 1,000,000 code units, a field name over 100
     code units, or a field name containing `.`, `[` or `]` is refused whole: capture answers `SanitizationRejected`
     and nothing of that attempt is stored.
   - A result or error over 4,000 code units after sanitization is replaced by
     `[truncated: value exceeded the configured limit]`.
   - An attempt's tool calls beyond the 50th are dropped. A run keeps at most 10 attempts: a later one is refused with
     `CapacityExceeded`.
   - The attempts capture keeps are numbered from 0 in the order they were recorded, whatever the host numbered them;
     `expected-capture.json` lists capture's answer for each input attempt, in input order.
   - The records a run was shown (`provenance.exposedTo`) are recorded through capture as they are delivered; a run
     starts exposed to nothing.
   - Tool names, the task description and the environment are not sanitized.

   Reference: [`DefaultSanitizer.cs`](../src/AgentExperience.Core/Sanitization/DefaultSanitizer.cs),
   [`AgentExperienceDefaults.cs`](../src/AgentExperience.Core/DependencyInjection/AgentExperienceDefaults.cs),
   [`InMemoryExperienceCaptureService.cs`](../src/AgentExperience.Core/Capture/InMemoryExperienceCaptureService.cs).

2. **Verification aggregates**, rule version `1.0.0`. Only evidence of the closed round and the current artifact
   revision counts (no closed round, or one for another revision, is `Unknown` with no evidence examined). Per required
   check, the evidence with its check ID and an accepted kind (`*` accepts any): none is `Unknown`, any `Fail` is
   `Fail`, any `Unknown` is `Unknown`, else `Pass`. Any failing check makes the run `Failed`, else any unknown one
   `Unknown`, else `Verified`; no required check is `Unknown`. The completion score is the share of required checks
   that passed. The outcome's `evidence` is the evidence that counted; its `reason` and `evaluatedAt` (the finalization
   instant) are in the fixtures. Reference:
   [`VerificationAggregator.cs`](../src/AgentExperience.Core/Verification/VerificationAggregator.cs).

3. **The deterministic reflector writes the lesson**, template version `1.1.0`, producer
   `AgentExperience.DefaultExperienceReflector/1.1.0`, and only for a `Verified` run: an unverified run is stored
   without a reflection, so a stored lesson always opens `"Verified after {n} attempt(s)."` (the reflector's other
   openings, `Did not verify` and `Unverified`, never reach a stored record). Then `" Failed: attempt {k} — {class}."` per failed
   attempt (or `" Failed: {count} attempts; last: attempt {k} — {class}."` for more than two), then
   `" Worked: attempt {m}."` when the final attempt of a verified run has no error, then `" Checks: [{ids}]."`. No tool
   name, argument value or error text beyond its class ever reaches the lesson. The approach lists quote captured text
   cut to 500 code units; list items are cut to 1,000 and lists to 32; every cut ends in `…` (U+2026) and never splits
   a surrogate pair. Reference:
   [`DefaultExperienceReflector.cs`](../src/AgentExperience.Core/Reflections/DefaultExperienceReflector.cs).

4. **The record is stored.** A verified, reflected run is `Validated`, with `reuse_confidence` `2/3` and one supporting
   validation; any other is `Quarantined`, with no reflection, `0` and `0`. `created_at` and `updated_at` are the
   finalization instant truncated to microseconds; `origin` is `Finalized`; `closedRoundId` is the closed round. The
   attempts are stored as capture kept them.

5. **The initial lifecycle event.** The .NET finalizer inserts the row as `Candidate` at revision 0 and then commits one
   event moving it to its status, which takes it to revision 1; `expected-record.json` is the row after that commit,
   and `expected-event.json` the event columns finalization decides. The store adds the record's scope columns,
   `actor` (the authorizing principal) and `recorded_at` (its own clock), and leaves the confidence columns null. A port
   that writes the row directly in its final state at revision 1 must still write that event, or the record has no
   history.

## The injected block

`records.json` is a list of `{ "record": <stored record>, "ranking": { "score", "components" } }`, best first.
`options.json` holds the options that change the block: `rendering` (`Compact`, the default, or `Verbose`),
`failureDetail` (`ErrorClass`, the default, `Excerpt` or `None`), `maxRecords` (8), `maxBytes` (16,384) and
`approachArguments` (per tool name, the argument keys whose stored values a `Tried:` line may show).

The list is first cut to `maxRecords`. Records are then written in order; the first that does not fit in `maxBytes`
(UTF-8 bytes of the whole block, header and footer included) is dropped with every record after it, and a record is
never cut to fit. When nothing fits, the block is empty.

What the block holds per record, in each layout, the bounds (tool names cut at 96 code units, 20 calls per line, the
last 4 attempts, argument values cut at 64 and 512 per line), the per-call `[returned]` and `[failed: <class>]`
markers, and how record text is neutralized (block and record delimiters and their look-alikes, field labels at the
start of a line, invisible characters, Unicode line separators, and brackets in tool names) are described in
[Injection](../docs/guide/injection.md#the-payload) and implemented in
[`HistoricalReferenceWriter.cs`](../src/AgentExperience.MicrosoftAgentFramework/Injection/HistoricalReferenceWriter.cs).
The fixtures pin each of them.

## The task text

`messages.json` lists an invocation's messages in order, each with its `role`, its `source` (`External` for new input,
which is also what a message without a source counts as; `ChatHistory` for replayed history; `AIContextProvider`; or
any other name), its `contents` (`text` parts with their text, and any other part type, which carries none) and whether
it is stamped as carrying an injected block. `maxLength` (512) and `followUpThreshold` (64) are counted in UTF-16 code
units. The rule is in [`ExperienceTaskText.cs`](../src/AgentExperience.MicrosoftAgentFramework/ExperienceTaskText.cs):
the latest external user message with text after the last replayed one, cleaned of control and format characters,
joined after the previous user message with ` — ` when shorter than the threshold, and cut to the maximum length
without splitting a surrogate pair.

## Text retrieval

Every transaction a port opens first runs [`sql/declare-bounds.sql`](sql/declare-bounds.sql), transaction-local:
`@auth_tenant` is `=` followed by the tenant; each other bound is `=` followed by the value, or the empty string for
no bound. The search is then [`sql/search-experience-text.sql`](sql/search-experience-text.sql), with named
parameters (`@name`; translate to your driver's placeholder syntax):

| Parameter | Value |
| --- | --- |
| `tenant_id` ... `user_id` | the reader's scope; optional fields `NULL` |
| `task_text` | the task text, non-blank, at most 4,096 UTF-16 code units. Longer text is refused, not cut: .NET retrieval rejects it and the invocation gets no block. Derive it with a maximum length of at most 4,096 (512 by default), which cuts without splitting a surrogate pair. |
| `statuses` | `text[]`: `{Validated,Reinforced}` |
| `min_confidence` | `0.5` |
| `limit` | `51` (the candidate limit, 50, plus one to detect truncation) |
| `exclude_model_authored` | `false` by default; `true` leaves out records whose reflection a model wrote |
| `with_grants` | `false` in contract 1.x |
| `minimum_matched_terms` | `3` |

The columns come back in this order: the eighteen record columns, then `relevance` (`real`), `shared_by_grant`,
`permitting_grant_id`, `permitting_grant_disclosure`. Rows arrive in the statement's order: by `relevance` (the share of
the query's terms the row contains), then by text rank, then by `experience_id`. Read them in the order returned.
`relevance` is a 32-bit float: widen it to a double without rounding through decimal text (`0.6` becomes
`0.6000000238418579`). `expected.json` holds the widened double, which is also the value ranking uses.

**From rows to ranked records**, in this order:

1. **Safety checks**, over the first 50 rows (the candidate limit): a row whose scope is not exactly the requested
   scope, or an `experience_id` that appears twice, fails the whole result. Nothing is returned, not even the rows
   that were fine.
2. **Truncation.** Keep the first 50 rows in the statement's order. When the statement returned 51, drop the 51st and
   report the result truncated (`expected.json`'s `truncated`).
3. **Filtering.** Leave out a row whose status is not `Validated` or `Reinforced`, and, when model-authored lessons are
   excluded, one whose reflection a model wrote. (The statement has already applied both; this is defence in depth.)
4. **Scoring and ordering**, below.

**Ranking.** Each kept row is scored with the default weights, components in this order:

| Component | Value | Weight |
| --- | --- | --- |
| `Relevance` | `relevance`, clamped to [0, 1] | 0.35 |
| `Confidence` | `reuse_confidence` | 0.25 |
| `Recency` | `2^(-age / 30 days)`, age = now − `updated_at` in seconds; 1 when not positive | 0.15 |
| `Status` | `Reinforced` 1.0, `Validated` 0.5 | 0.15 |
| `EnvironmentCompatibility` | the share of preferred environment attributes the record's metadata matches exactly; 1 with none preferred | 0.10 |

The score is the sum of value × weight, added in that order. Records are ordered by score, descending, ties by
`experience_id` as text, ascending, ordinal. Compare scores to 1e-9. Reference:
[`ExperienceRetrievalService.cs`](../src/AgentExperience.Core/Retrieval/ExperienceRetrievalService.cs).

## The error class

A captured error is reduced to at most three distinct tokens it recognises, in the order they first appear, joined
with `, `: an exception-type name as written (a capitalised word ending in `Exception` or `Error`, at most 40
characters), an exit code (`exit 2`), an HTTP status (`HTTP 503`, `status 503`), a POSIX errno name, or `Timeout`. Only
the first 8,192 characters are scanned; with no token the class is `unclassified error`. A call's marker shows only the
class's first token. Reference: [`ErrorClass.Shared.cs`](../src/Shared/ErrorClass.Shared.cs).

## Changing the contract

`tests/AgentExperience.Contract.Tests` regenerates every fixture from the library and fails on any difference. To
accept a change on purpose:

1. Bump the version on this page and in `ContractFiles.ContractVersion`: the minor version when the change only adds
   files (new cases), the major version when any file already locked changed or went away.
2. Run the tests with `AGENTEXPERIENCE_CONTRACT_FIXTURES_UPDATE=1` (the search family and the round trip need Docker).
   It rewrites the fixtures, checks the kind of bump against the previous `lock.json`, and writes the new one.
3. Add the digest it reports to `ContractFiles.LockDigests`, read the diff, and commit it.

`lock.json` holds a SHA-256 of every file here, and `ContractFiles.LockDigests` (test source, which the update switch
never rewrites) holds the SHA-256 of `lock.json` for each published version. The test fails when any file differs from
the lock or the lock from its published digest, so neither regenerating nor editing `lock.json` by hand can change a
published version: only a new version, reviewed, can.

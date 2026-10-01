# Upgrades from databases the published previews created

**In short.** For each published preview that shipped a migration, this suite starts a PostgreSQL (pgvector)
container, lets that preview's own code create and fill a database, upgrades it with today's migrator exactly as the
[CHANGELOG's runbook](../../CHANGELOG.md#upgrade-in-this-order) says, and then, as the application role, reads every
item back through today's stores, carries the lifecycle on, and compares the upgraded schema with a fresh install of
today's. A failure names the preview, the PostgreSQL major and the item that did not survive.

The other upgrade tests in this repository (`ExperienceSchemaMigratorTests`, `PostgresGrantAccessAuditTests`,
`PostgresApplicationRoleTests`) build their "old" database from a prefix of today's scripts, run by hand, with
today's store writing the rows. This suite does not: the old database is whatever the *published* packages wrote.

## Coverage

| Case | Schema it leaves | Deployment | Seeder |
| --- | --- | --- | --- |
| `0.1.0-preview.1` | `0001` to `0010`, and the vectors package's `0004` | one role: the application's role ran the migrator and owns everything | [`Preview1`](../AgentExperience.Upgrade.Seeders/Preview1) |
| `0.1.0-preview.2` | `0001` to `0018` (no `0014`), and `0004` | two roles: an owner migrates, the application role holds the manifest | [`Preview2`](../AgentExperience.Upgrade.Seeders/Preview2) |
| `0.1.0-preview.2 (crypto-shredding)` | the same | the same, with every component sealing what it writes | [`Preview2`](../AgentExperience.Upgrade.Seeders/Preview2), with `--keys` |

`0.1.0-preview.3` and `0.1.0-preview.4` added no migration, so the `0.1.0-preview.2` database is theirs too, and they
have no seeder of their own. `0.1.0-preview.1` had no crypto-shredding, so it has no encrypted case. The suite runs on
every PostgreSQL major CI's `postgres` matrix runs (16 by default; `AGENTEXPERIENCE_POSTGRES_MAJOR` selects another),
and asserts the server it reached reports that major.

## What each seeder writes

The seeders are console programs under [`tests/AgentExperience.Upgrade.Seeders`](../AgentExperience.Upgrade.Seeders).
Each project exact-pins one preview's `AgentExperience.Abstractions`, `AgentExperience.Core`,
`AgentExperience.Storage.Postgres` and `AgentExperience.Storage.Postgres.Vectors` from nuget.org (the directory's
`nuget.config` clears every other source and maps every package there), and compiles the shared sources in `Shared/`,
where `#if PREVIEW1` / `PREVIEW2` marks each place the published surfaces differ. Nothing in a seeder writes SQL: the
database is created by that preview's migrators and filled through its capture, finalization and lifecycle services and
its stores. The dataset, with fixed IDs and payload timestamps:

- finalized records in two scopes (team A and team B of one project): Validated with supporting evidence, Contested
  by contradicting evidence, Superseded by the validated one, Quarantined by a failed verification, and a Validated
  record shared with the other team;
- on `0.1.0-preview.2`, two more finalized runs, each delivered the validated record through the capture service's
  `RecordExposure`, so their records carry `Provenance.ExposedTo`: evidence naming the first is admitted `Verified`
  during seeding, and the second is left for the suite to name after the upgrade;
- sharing grants: one live, one revoked (on `0.1.0-preview.2`, at `LessonAndApproach` and at
  `LessonApproachAndArguments` with an argument allowlist), and one that expires seconds after seeding;
- two audited reads through grants, which write grant access log rows;
- two reuse feedback submissions, one unattributed and one human-attributed (whose rationale is sealed in
  crypto-shredding mode);
- one record erased into a tombstone;
- embeddings on the two eligible records.

In crypto-shredding mode the seeder gives every component one `ExperienceEncryption` over an envelope key store: the
test-only `FileWrappedKeyRepository` (in `Shared/`, and linked into this project) keeps the wrapped keys in a file,
under a fixed key-encryption key, so the suite re-opens the same keys with today's code.

The seeder then reads everything back through that preview's API and writes a JSON manifest of it: IDs, scopes,
statuses, revisions, lifecycle event, evidence, grant and feedback IDs, each item's full content exactly as that
preview returned it, record queries, text searches, a batch read (`0.1.0-preview.2`), supersession checks, the grant
access log, and each embedding's scan and vector search hits with their relevance. Reuse feedback is recorded as the
store holds it: the seeder replays each submission and keeps the `AlreadyRecorded` answer. The tombstone's revision
is recorded from a read. The manifest also records the informational version of every AgentExperience assembly the
seeder loaded; the suite requires each to be the preview's version and the commit its tag names, so a seeder can
never quietly run this repository's code instead.

## What the suite checks, after the upgrade

As the owner: today's `ExperienceSchemaMigrator.MigrateAsync` and `ExperienceVectorSchemaMigrator.MigrateAsync` apply
exactly the scripts that preview's migrators had not (today, `0011` to `0018` for `0.1.0-preview.1`, nothing for
`0.1.0-preview.2`), then `ApplyApplicationRolePrivilegesAsync` grants the application role its manifest. For
`0.1.0-preview.1` the single-role database first moves to two roles with the SQL in
[Upgrading an existing single-role database](../../docs/guide/deployment.md#upgrading-an-existing-single-role-database),
which the suite reads from that document and runs as it is written: the owner role it creates takes ownership, and
the role the preview used stays the application role.

Then, as the application role, with today's crypto-shredding over the same keys in the encrypted case:

- **reads:** every record and its lifecycle history; every record again as a batch (`GetManyAsync`), and the
  preview's own batch read; each scope's record query and text search (over the sealed search vector in the encrypted
  case); the supersession checks; the tombstone (it reads as deleted, at the recorded revision, and re-creating a record
  under its ID is refused); each grant and its history; the shared read; the grant access log; each embedding's scan,
  its stored vector (read from the table, since no API returns one, and required to be exactly the vector written) and
  a vector search, whose hits and relevances must be exactly the preview's; the storage mode (every live record,
  search vector and feedback rationale sealed in the encrypted case with its data key still active, and nothing
  sealed otherwise); and `0021`'s authorship backfill (story 14.4): every sealed row unknown, every tombstone the fixed `false`, every seeded plaintext
  row deterministic, and every text search answering the same with `ExcludeModelAuthored` on. In the plaintext cases
  the suite itself, before the upgrade, inserts two SQL copies of `a-validated` in a project of its own, one whose
  payload says `authorship: Model` (no published preview could write that), and checks that only that one is
  flagged and that an excluding search leaves it out;
- **writes:** a new transition (Validated to Reinforced); new confidence evidence, refused by today's verifying
  service with the documented reason, admitted `HostTrusted` by the opt-out, and, for `0.1.0-preview.2`, admitted
  `Verified` about the run the preview exposed; the expiring grant, which after it expires admits nothing and is
  purged by `PurgeExpiredAsync`; a new grant issued, read through and revoked; an erasure of an upgraded record (its
  data key destroyed in the encrypted case); new reuse feedback naming an upgraded record; and a new embedding write;
- **the catalog:** the migration journal lists every current script exactly once; and the schema matches a fresh
  install of today's, created in a second database with the same roles and the same privileges call. The comparison
  covers relations (with their row-level security), row-level security policies, columns (type, nullability, default, generation), constraints (and whether each is validated),
  indexes, triggers (and their enabled state), function definitions (`pg_get_functiondef`, with owner, security,
  settings and privileges), sequences, table, column, function and schema privileges, and the installed extensions,
  and names the first difference;
- **last**, each reuse feedback submission is replayed: the ledger has no read port, and the store answers
  `AlreadyRecorded`, with what it holds, only when every field and exposure is identical. It runs last because a
  replay writes when the ledger has lost the row; `Recorded` is reported as a lost item.

Items are compared semantically: the manifest's JSON is deserialized into today's type, strictly (a member today's
type cannot hold, because it was removed or renamed, fails), and the two are compared as JSON, object members in any
order. A step that throws is reported as that step's failure, and the run goes on to the next.

## Where the data changes by design

These items cannot read back unchanged, because a later preview changed their meaning on purpose. The suite asserts
the documented behaviour instead:

| Item | After the upgrade | Documented in |
| --- | --- | --- |
| A `0.1.0-preview.1` grant | `LessonOnly`: `0011` gives every existing grant that level, so a borrowed record loses its `Approach:` line until the grant is reissued; the shared read reports `GrantDisclosure = LessonOnly` | CHANGELOG `0.1.0-preview.2`, Behaviour changes; [`0011`](../../docs/guide/postgres-schema.md#0011-grant-disclosure) |
| A `0.1.0-preview.1` grant event or grant access row | no recorded disclosure (`null`): rows written before `0011` stay "not recorded" | [`0011`](../../docs/guide/postgres-schema.md#0011-grant-disclosure) |
| A `0.1.0-preview.1` record | `Origin = HostWritten`, no `ClosedRoundId`, exposed to nothing: the payload never carried them | CHANGELOG `0.1.0-preview.2`, Breaking changes; [`0015`](../../docs/guide/postgres-schema.md#0015-verified-independence), [`0018`](../../docs/guide/postgres-schema.md#0018-evidence-admission) |
| A reflection from any published preview | `Authorship = Deterministic`: the payload never carried it, and no published preview had a model-backed reflector | [Limits of model-authored lessons](../../docs/guide/finalization.md#limits-of-model-authored-lessons) |
| A record from any published preview | no `ProvenanceSignature`: signing is opt-in and did not exist. With signing on, its run vouches for nothing unless its ID is listed in `TrustUnsignedRecordIds` | [Signing provenance](../../docs/guide/confidence.md#signing-provenance) |
| `0.1.0-preview.1` confidence evidence | no recorded admission (`null`), counted as unrecorded | [`0018`](../../docs/guide/postgres-schema.md#0018-evidence-admission) |
| New evidence naming a run that vouches for nothing | refused (`Unverified`) by today's default, verifying lifecycle service: `HostWrittenRun` for a `0.1.0-preview.1` run, `NotExposed` for a `0.1.0-preview.2` run exposed to nothing. The documented opt-out, `IndependenceVerification.TrustHostSuppliedIdentifiers`, admits it, labelled `HostTrusted` | CHANGELOG `0.1.0-preview.2`, Upgrade step 5 and Breaking changes |
| The `0.1.0-preview.1` roles | ownership moves to a new owner role; the preview's role becomes the application role and owns nothing | [Upgrading an existing single-role database](../../docs/guide/deployment.md#upgrading-an-existing-single-role-database) |

The comparison enforces the same list from the other side: a member today's API returns that the preview's did not
fails unless it is one of these, new by design, each listed with its reason in `UpgradeReport.NewByDesign`:
`closedRoundId`, `origin`, `exposedTo`, `provenanceSignature`, `authorship`, `assessmentId`, `admission`,
`disclosure`, `approachArguments`, `grantDisclosure` and `grantApproachArguments`. Everything else must read back
identical.

## Running it

Docker, and network access to nuget.org for the seeders' first restore:

```bash
dotnet test tests/AgentExperience.Upgrade.Tests --configuration Release
AGENTEXPERIENCE_POSTGRES_MAJOR=18 dotnet test tests/AgentExperience.Upgrade.Tests --configuration Release
AGENTEXPERIENCE_TEST_RLS=on dotnet test tests/AgentExperience.Upgrade.Tests --configuration Release
```

With `AGENTEXPERIENCE_TEST_RLS=on` (story 15.1) the privileges call, on the upgraded database and on the fresh one it
is compared with, switches row-level security on, the suite checks that every covered table has it, and everything
after the upgrade is read and written behind the policies.

The suite builds each seeder once per run, restoring it in locked mode from its committed `packages.lock.json`, into
a directory of its own under the system's temporary directory (`--artifacts-path`), which it deletes when the run
ends; nothing is written into the source tree. CI restores the seeders, locked, in the step before the tests, so the
test step needs no network. The seeders are not in `AgentExperience.NET.sln`, so a solution build, `dotnet pack` and
the release packages never include them. They switch NuGet auditing off: their dependency graph is the frozen one the
preview shipped, and an advisory published later must not turn their build red.

## Adding a preview

A preview that ships no migration needs nothing: the newest seeder already covers its schema. Say so in the Coverage
table. A preview that ships a migration, once it is published:

1. Copy the newest seeder project to `tests/AgentExperience.Upgrade.Seeders/PreviewN`, pin the new version in its four
   `PackageReference`s, and define `PREVIEWN`.
2. Build it, and fix what the compiler reports with `#if PREVIEWN` blocks in `Shared/Seeder.cs`. Exercise what the new
   schema added in the dataset, and keep writing only through the preview's own API.
3. Commit its `packages.lock.json`. `CompatibilityPinAgreementTests` exempts this directory from its floor rule,
   because the pins are deliberately old, and holds it to its own: only `PreviewN` projects, each pinning its four
   packages at `[0.1.0-preview.N]`, each lock file resolving them to exactly that, and the set equal to the suite's
   `PublishedPreview` list. No other project is exempt.
4. Add a `PublishedPreview` entry naming the last script its migrator applies, the commit its tag names
   (`git rev-list -n1 v0.1.0-preview.N`) and whether it has crypto-shredding; add any member its API newly returns to
   `UpgradeReport.NewByDesign`, with its reason; and add a row above for any item whose meaning the upgrade changes by
   design. The suite derives what today's migrators must apply from what the seeder's migrators did, so an older
   preview's entry never needs updating when a script is added.

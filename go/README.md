# AgentExperience for Go

A Go core of AgentExperience that implements the [language-neutral contract](../contract/README.md), so a lesson
learned by a Go program is stored in the format the .NET and Python libraries read, and the other way round.

**Status: pre-release, contract 1.2, no Microsoft Agent Framework integration yet.** It passes every fixture of
contract 1.2 (and so every 1.0 and 1.1 fixture). No version is tagged or published. The module path
`github.com/fabbrik/AgentExperience.NET/go` and the package name `agentexperience` are provisional until the project
is named. It is Apache-2.0, as the [repository](../LICENSE) is.

- **Go 1.24 or later for the core, 1.25 or later for the PostgreSQL store** (the pgx releases without known
  vulnerabilities need 1.25; CI runs govulncheck). Microsoft Agent Framework's Go SDK (v0.1.x) requires Go 1.26, so
  a program using it builds with that minimum anyway.
- **Two modules.** The core, `github.com/fabbrik/AgentExperience.NET/go`, requires no module at all: its `go.mod` has
  no `require`, and a test checks that no file of it, tests included, imports anything but the standard library.
  The PostgreSQL store is its own module, `github.com/fabbrik/AgentExperience.NET/go/postgres` (in `postgres/`), which
  adds [pgx](https://github.com/jackc/pgx) v5; its tests also use testcontainers-go and a JSON Schema validator. Until
  the core is tagged, the store's `go.mod` builds it from the parent directory (a `replace`).

## What it does

- **Capture and finalize.** `StartRun` returns a `RunCapture` that sanitizes a run as it is recorded: secret-named
  fields are redacted, argument values are kept only when allowlisted, a number a double cannot hold finitely is
  refused, and the default limits apply. `Finalize` aggregates the host's verification, writes the deterministic
  lesson for a verified run, and returns the stored record (`Validated`, or `Quarantined` without a lesson) and its
  initial lifecycle event. The IDs are derived, so a retry derives the same ones.
- **The stored form.** `ExperienceRecord.ToStored` and `FromStored` write and read the record exactly as the
  contract's schema describes it. Tool-call arguments are an `*Object` (members in order) whose numbers are
  `json.Number`, kept exactly as written: `DecodeJSON` never decodes a number into a `float64`. `ReadNumber` reads one
  the way .NET does (an integer that fits in 64 bits as an `int64`, anything else as a `float64`).
- **The injected block.** `RenderBlock` turns ranked records into the Historical Reference block, byte for byte as
  the .NET adapter writes it, in the compact or verbose layout, within the record and byte limits, with record text
  neutralized.
- **The task text.** `DeriveTaskText` derives the text retrieval searches with from an invocation's messages.
- **The error class.** `ErrorClass` reduces a captured error to its short class.
- **Search and ranking.** `SearchParameters` and `AuthorizationBounds` are the contract statements' parameters;
  `Rank` applies the contract's safety checks, truncation and ranking to the rows a search returns.
- **PostgreSQL** (the `postgres` package). `Store` writes a finalized record and its initial event as the application
  role, reads records back, and runs the contract's text search, every transaction first declaring its bounds. The
  contract's two SQL files are embedded and run as written (a test keeps the copies in `postgres/sql/` identical to
  `contract/sql/`); only their `@name` placeholders are numbered for the driver. The store needs a schema the .NET
  migrator applied, with row-level security on; it never creates or alters a schema object. `Search` skips a sealed
  row (`payload_version` 2), and `Get` answers `ErrSealed` for one (and `nil, nil` for none).
  - **Connections are the host's.** `NewStore` takes anything that begins a transaction: a `*pgxpool.Pool`, a
    `*pgx.Conn` or a `pgx.Tx`, each connecting as the application role. `Open` is the convenience that builds a pool
    from a connection string (close it with `Close`). Each operation is one transaction. The store is safe for
    concurrent use exactly when its source is: a pool is, a single connection or transaction is not.

It does nothing outside contract 1.x: no grants, crypto-shredding, provenance signing or vectors, and no lifecycle
after finalization. It writes no logs at all, so no record content is ever logged.

[`example_test.go`](example_test.go) captures a run, finalizes it and renders the block; `go test` runs it and checks
its output. In short:

```go
capture, _ := ae.StartRun(ae.CapturedRun{RunID: runID, TaskID: "refund-stuck-pending", Scope: scope,
	Environment: environment, Provenance: provenance, StartedAt: start},
	ae.SanitizationAllowing("strategy"), ae.DefaultCaptureLimits())
outcome, _ := capture.AppendAttempt(attempt) // Recorded, SanitizationRejected, CapacityExceeded, ...
_, _ = capture.Complete(ae.RunCompleted, end)
run := capture.Run()

finalized, _ := ae.Finalize(run, ae.Verification{ClosedRound: &round, RequiredChecks: checks, Evidence: evidence,
	CurrentArtifactRevision: "rev-1", FinalizedAt: now})

pool, _ := pgxpool.New(ctx, os.Getenv("AGENTEXPERIENCE_CONNSTRING")) // as the application role
store, _ := postgres.NewStore(pool)
bounds := ae.BoundsOf(scope)
_ = store.InsertFinalized(ctx, bounds, &finalized, "host-principal", nil)
rows, _ := store.Search(ctx, bounds, ae.NewSearchParameters(scope, "refund ticket stuck in pending"))
ranked, _ := ae.Rank(rows, scope, ae.Now(), nil, false)
block, _ := ae.RenderBlock(ranked.Ranked, ae.BlockOptions{})
```

## Where Go differs from .NET, and what the port does

Every rule is .NET's, and Go's standard library differs from .NET in places
([contract 1.1](../contract/README-1.1.md#where-a-ports-own-library-differs-from-net) pins most of them):

- **UTF-16.** A Go string is UTF-8; every length, cut and limit is counted in UTF-16 code units (`UTF16Len`,
  `UTF16Prefix`), and a cut never splits a surrogate pair. Ordinal order is UTF-16 order, not byte order.
- **Lone surrogates.** A Go string cannot hold one. JSON decoding turns one into U+FFFD, which is exactly what .NET's
  UTF-8 encoding writes for it in a derived ID, so the encoding fixture's UTF-16 arrays decode with `unicode/utf16`.
- **Whitespace, words and case.** Go's regexp `\s` is ASCII only (no `\v`, U+0085 or Unicode spaces), its `\b`
  knows only ASCII words, and `strings.EqualFold`, `unicode.ToUpper` and regexp's `(?i)` fold the long s, the
  Kelvin sign or the dotless i to ASCII letters. The port uses .NET's `char.IsWhiteSpace`, `\b` word characters and
  ordinal-ignore-case rules instead (U+001C to U+001F are not whitespace, U+0085 is).
- **The error class** is a .NET regular expression with a Unicode `\b`, which Go's regexp (RE2) cannot express. A
  small backtracking matcher with .NET's leftmost-first priority runs the same pattern, linear in the input.
- **Numbers.** .NET's round-trip (`1E+20`, `-0`) and fixed (`0.00`, 15 significant digits, half away from zero)
  formats are reproduced; a `real` relevance is widened from float32 exactly.
- **Instants and durations** are 100-nanosecond ticks with .NET's range checks, not `time.Time`: every way of making
  an instant (parsing, `InstantFromTicks`, `InstantFromTime`, `Add`) refuses one outside 0001-01-01 to
  9999-12-31T23:59:59.9999999 UTC.
- **Text Go cannot hold.** .NET text is UTF-16, and its only text a Go string cannot hold is a lone surrogate, which
  reaches Go as invalid UTF-8 (WTF-8 writes one as three bytes). The task text drops each invalid byte, as .NET
  drops a lone surrogate; a real U+FFFD is kept.
- **The Unicode version.** Character classes (whitespace, word characters, control and format characters, marks)
  come from the `unicode` package of the Go toolchain that builds the program: Unicode 15.0.0 for Go 1.24 and 1.25,
  17.0.0 for Go 1.27 (`unicode.Version`). .NET's tables follow its own runtime's Unicode version, so a character
  assigned or given another category between the two versions can be classified differently: kept by one and
  neutralized by the other in a block or a task text, or a word character to one error class and not the other. Every
  character the contract's fixtures use is classified the same in all of them, and CI runs the fixtures on Go 1.24 and
  the latest stable.

## Running the tests

From this directory (the core module) and from `postgres/` (the store's module):

```bash
gofmt -l . && go vet ./... && go test -race ./...
```

In the core, `go test ./...` runs the unit tests and the conformance runner ([`contract_test.go`](contract_test.go)), which loads
every fixture family from [`../contract/fixtures`](../contract/fixtures) and compares this package's output exactly:
blocks and task text byte for byte, JSON by value, with a number written as an integer never equal to one written
with a fraction or an exponent. It checks every contract file against `contract/lock.json`, and fails with a clear
message if the lock names a contract version this package does not support (1.0, 1.1 and 1.2 are). In `postgres/`,
`go test ./...` validates every record of the fixtures, and every record the core writes for a finalize fixture,
against the contract's JSON Schema (there, because it needs a validator and the core module requires none). The
schema's patterns are ECMA-262 regular expressions and the validator
([jsonschema v6](https://github.com/santhosh-tekuri/jsonschema)) evaluates them with Go's RE2: the two agree on every
construct the schema uses except `\s`, so the schema's one `[^\s]` is translated into ECMA-262's whitespace class, and
any other `\s`, `\S` or multiline flag fails the test until it is translated too.

**PostgreSQL** (the `postgres` build tag, in `postgres/`): each finalize fixture is written through the Go store and read back row
for row, every search fixture is run and ranked, and sealed rows, tombstones, the exact scope, row-level security,
`recorded_at` and concurrent use on a pool are checked. Each test database (dropped when its test ends) is migrated with the .NET migrator's own scripts from
[`src/AgentExperience.Storage.Postgres/Migrations`](../src/AgentExperience.Storage.Postgres/Migrations), in order,
and given the application role's privileges from the contract's
[`sql/application-role-privileges.sql`](../contract/sql/application-role-privileges.sql). The server is a
testcontainers-go `postgres` container (`AGENTEXPERIENCE_POSTGRES_MAJOR`, 16 by default), or the server `PGHOST`,
`PGPORT`, `PGUSER` and `PGPASSWORD` name (a superuser). With neither the tests skip, unless
`AGENTEXPERIENCE_REQUIRE_POSTGRES=1`, when they fail. On a Mac with Rancher Desktop or Colima, testcontainers-go may
need:

```bash
cd postgres && TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock TESTCONTAINERS_RYUK_DISABLED=true go test -race -tags postgres ./...
```

CI runs the `go` job for the core on Go 1.24, for both modules on Go 1.25 and the latest stable Go (`go mod tidy`
leaves each module unchanged, gofmt, go vet, staticcheck and govulncheck on the latest stable, and the tests with the
race detector), and the PostgreSQL tests, also with the race detector, on PostgreSQL 16 and 18. To run a PostgreSQL
leg as CI does, set `AGENTEXPERIENCE_POSTGRES_MAJOR` (16 or 18) and `AGENTEXPERIENCE_REQUIRE_POSTGRES=1`, and run
`go test -race -tags postgres ./...` in `postgres/`.

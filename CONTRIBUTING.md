# Contributing to AgentExperience.NET

Thanks for your interest. This project is a preview, so the most helpful contributions right now are bug reports,
design feedback on open issues, and focused pull requests.

## Before you start

- For anything beyond a small fix, **open an issue first** so we can agree on the approach.
- Read the [architecture spine](_sdlc/planning-artifacts/architecture/architecture-agenticexperience.net-2026-09-06/ARCHITECTURE-SPINE.md) and [reuse boundaries](_sdlc/specs/spec-agentexperience-net/reuse-boundaries.md). They define rules the code must keep.
- The [guide](docs/guide/README.md) describes current behaviour; [Known limits and documented boundaries](docs/known-limits.md) says what it does not do.

## Ground rules

- **Keep the core portable.** `AgentExperience.Abstractions` and `AgentExperience.Core` must not reference MAF, EF Core, PostgreSQL, model providers, or OpenTelemetry. `DependencyBoundaryTests` enforces this.
- **Reuse upstream infrastructure.** Don't build a new agent runtime, tool runner, session store, vector database, or telemetry exporter. Integrate with the existing ones at adapter boundaries.
- **No private chain-of-thought.** Capture observable facts only, and sanitize before anything is stored.
- **Tests are required.** Every behavior change needs tests. Unit and adapter tests must run without network, database, or model credentials.
- **Warnings are errors.** The build uses `TreatWarningsAsErrors` and generates XML documentation, so public APIs need doc comments.
- **Docs move with the code.** A change to behaviour, an option or a default updates the matching page under `docs/guide/`. `MarkdownLinkTests` fails on a broken relative link or a missing `#anchor`.

## Development

Requires the .NET SDK pinned in `global.json` (10.0.302, or a later feature band). Every project targets `net10.0`
only, so no other runtime is needed.

```bash
dotnet restore
dotnet build
dotnet test                                    # everything; the storage tests need Docker
```

**Docker.** The storage suites, the pgvector compatibility proof (`PostgresVectorProof`), the sample's PostgreSQL
mode, the retrieval benchmark's PostgreSQL golden and the upgrade suite start PostgreSQL (with pgvector) containers through Testcontainers. They run against PostgreSQL 16 unless `AGENTEXPERIENCE_POSTGRES_MAJOR`
names another supported major (15, 16, 17 or 18), which is how CI runs them on each; any other value fails loudly.
If Testcontainers' Ryuk container fails under your Docker setup, set `TESTCONTAINERS_RYUK_DISABLED=true`. The upgrade
suite (`tests/AgentExperience.Upgrade.Tests`) also needs nuget.org the first time: it builds its seeders, which restore
the published previews' packages, into a temporary directory (see its [README](tests/AgentExperience.Upgrade.Tests/README.md)). With
`AGENTEXPERIENCE_TEST_ENCRYPTION=on`, the two storage test projects run again, unmodified, in crypto-shredding mode;
CI runs them both ways.

**Without Docker**, run the projects that need no container whole, filter the three that mix both, and skip the
three built around a PostgreSQL container:

```bash
dotnet test tests/AgentExperience.Abstractions.Tests
dotnet test tests/AgentExperience.Core.Tests
dotnet test tests/AgentExperience.MicrosoftAgentFramework.Tests
dotnet test tests/AgentExperience.Storage.InMemory.Tests
dotnet test tests/AgentExperience.ReuseBaseline
dotnet test tests/AgentExperience.Release.Tests
dotnet test tests/AgentExperience.Sample.QuickStart.Tests
dotnet test experiments/AgentExperience.LiveReuse.Tests
dotnet test tests/AgentExperience.CompatibilityProof --filter "FullyQualifiedName!~Postgres"
dotnet test tests/AgentExperience.Sample.EndToEnd.Tests --filter "FullyQualifiedName!~Postgres"
dotnet test tests/AgentExperience.RetrievalQuality --filter "FullyQualifiedName!~Postgres"
```

Skipped without Docker: `tests/AgentExperience.Storage.Postgres.Tests`, `tests/AgentExperience.Storage.Postgres.Vectors.Tests`,
`tests/AgentExperience.Upgrade.Tests`. In the three filtered projects every container-backed class has `Postgres` in its
name (`PostgresVectorProof`, `SamplePostgresModeTests`, `BatchReReadPostgresEquivalenceTests`,
`PostgresRetrievalQualityTests`, ...).
`ContributingWithoutDockerTests` fails if a test project is in neither list, or if a command here would run a class
that uses a container. `AgentExperience.Release.Tests` needs nuget.org on its first restore: it
downloads the last published preview's packages as the package-validation baseline.

No test anywhere needs model credentials: every model and embedding in the suite is a deterministic in-test fake.

Package versions are locked with `packages.lock.json`. Commit lock-file changes together with the `PackageReference` change that caused them. Versions are declared in each project (no central package management: the floating-dependency probe rewrites them per project), so a release test fails when two projects reference different versions of the same package; bump a package everywhere at once.

## Repository layout

```
src/
  AgentExperience.Abstractions/             domain contracts and ports (BCL only)
  AgentExperience.Core/                     sanitization, capture, verification, reflection, finalization, lifecycle, confidence, indexing, retrieval, reuse feedback, key management
  AgentExperience.MicrosoftAgentFramework/  MAF adapter: run and tool capture, Historical Reference injection (Microsoft.Agents.AI [1.22.0, 2.0.0))
  AgentExperience.Storage.Postgres/         PostgreSQL store, text search, grants, access log, reuse feedback ledger, deletion, crypto-shredding, schema migrator (0001-0003, 0005-0018)
  AgentExperience.Storage.Postgres.Vectors/ pgvector embedding index, conditional writes, scoped re-index, vector search (0004)
  AgentExperience.Storage.InMemory/         development and tests only: in-memory record store, candidate source and reuse-feedback store, guarded to development and test environments
  Shared/                                   source files linked into more than one package (not a project): the core-port validation rules
tests/
  AgentExperience.Abstractions.Tests/       contract and dependency-boundary tests
  AgentExperience.Core.Tests/               sanitizer, capture, verification, reflection, lifecycle, indexing, retrieval tests
  AgentExperience.MicrosoftAgentFramework.Tests/  real ChatClientAgent runs against a scripted fake model
  AgentExperience.Storage.Conformance/      the store conformance suite: abstract tests every implementation of the storage ports must pass
  AgentExperience.Storage.Postgres.Tests/   store tests, mostly against a PostgreSQL container, and the PostgreSQL run of the conformance suite
  AgentExperience.Storage.Postgres.Vectors.Tests/  embedding index and hybrid retrieval, against a pgvector container
  AgentExperience.Storage.InMemory.Tests/   the in-memory run of the conformance suite, the production guard, search and concurrency; no Docker
  AgentExperience.CompatibilityProof/       executable proofs for MAF hooks, context providers, pgvector, redaction
  AgentExperience.Sample.EndToEnd.Tests/    the sample's seven stages, its determinism, and what it does not claim
  AgentExperience.Sample.QuickStart.Tests/  the quick-start demo in stand-in mode: run 2 fails less because of the injected lesson, and its golden output
  AgentExperience.ReuseBaseline/            the controlled reuse experiment and its golden reports
  AgentExperience.RetrievalQuality/         the retrieval benchmark: a labelled corpus, recall and precision per adapter, golden reports (docs/benchmarks.md)
  AgentExperience.Release.Tests/            release gates: the public API baseline, pin agreement, the security-suite map, workflow guards, documentation links
  AgentExperience.Upgrade.Tests/            upgrades databases the published previews created to today's schema, against a pgvector container, and reads everything back
  AgentExperience.Upgrade.Seeders/          one console program per published preview that ships a migration, pinning that preview's packages from nuget.org; built and run by the upgrade tests, not in the solution
samples/
  AgentExperience.Sample.EndToEnd/          one runnable command: capture, verify, reflect, persist, retrieve, inject, record reuse
  AgentExperience.Sample.QuickStart/        the README quick start's wiring, run twice on one ticket: the one-minute demo
experiments/
  AgentExperience.LiveReuse/                opt-in, pre-registered reuse experiment against a real model; never run by CI
  AgentExperience.LiveReuse.Tests/          that experiment's harness, proven offline against scripted models
benchmarks/
  AgentExperience.Benchmarks/               BenchmarkDotNet over the hot paths, on the in-memory store and PostgreSQL; run by hand, never by CI (docs/benchmarks.md)
eng/                                        release tooling: package verification and the dependency probes
docs/                                       the guide, known limits, telemetry contract, security-suite map, compatibility evidence, benchmark baseline, original research
_sdlc/                                      product brief, PRD, architecture, epics, and specs
```

## Pull requests

- Keep each PR to one concern, and explain the *why* in the description.
- Use [Conventional Commits](https://www.conventionalcommits.org/) for commit messages (`feat:`, `fix:`, `docs:`, `chore:`).
- Make sure `dotnet build` and `dotnet test` pass locally. CI (`.github/workflows/ci.yml`) runs on every pull request:
  `build-and-test`, `pack` (with package verification), the `postgres` matrix (15 to 18, plaintext and
  crypto-shredding) and the MAF floor (`maf-compatibility (pinned)`) must pass. The newest-MAF leg and the
  `floating-dependencies` probe also run, but on a pull request they only report; they gate pushes to `main` and the
  weekly schedule.
- **Changing a public API is a reviewed diff.** `tests/AgentExperience.Release.Tests` holds an approval baseline of all
  six packages' public surface, and any change fails it. If the change is deliberate, regenerate the baseline with
  `AGENTEXPERIENCE_ACCEPT_API_CHANGES=true dotnet test tests/AgentExperience.Release.Tests --filter "FullyQualifiedName~PublicApi"`
  and commit the resulting `PublicApi/*.verified.txt` diff with your change, so reviewers see it. The baseline never
  updates itself.
- **Breaking what is already published is a declared diff too.** `dotnet pack` runs package validation against the
  last published preview (`AgentExperiencePackageValidationBaseline` in `Directory.Build.props`; restore downloads it
  from nuget.org into the NuGet cache) and fails on any binary break: a removed or renamed member, a changed
  signature, `init` changed to `set`. If the break is deliberate, restore, then run
  `dotnet pack src/<Project> -c Release -p:AgentExperienceReleaseBuild=true -p:ApiCompatGenerateSuppressionFile=true`.
  It rewrites that project's `CompatibilitySuppressions.xml` with every break the package now has, so commit the whole
  file and review its diff. Then add a CHANGELOG bullet starting with `**Breaking` that names the break; a release test
  checks that one exists whenever a suppression file does. The two checks answer different questions: the snapshot
  records the source shape of the whole surface, attributes and defaults included, so additions and attribute changes
  are left to it (strict mode stays off); package validation records compatibility with what consumers already have.
  The PublicApiAnalyzers `PublicAPI.Shipped.txt`/`Unshipped.txt` files are deliberately not used: they would record the
  surface a second time, in two more files per project, without adding either.
- **Moving a package pin** means updating `docs/compatibility-evidence.md` in the same PR; the release tests fail if
  the compatibility proof and the shipping packages disagree on a version. Releasing is described in
  [RELEASING.md](RELEASING.md).
- **A new known limit** is a row in [docs/known-limits.md](docs/known-limits.md), which the release gate counts.
- **A change to `.github/workflows/`** must pass on the GitHub runner before review closes: push the branch and wait for
  the workflow run. A workflow that only passed locally, or only in a test that parses the YAML, is not done.
- **State code facts with a verified `file:line`.** When a design note, spec or review cites how existing code behaves,
  cite the file and line you read, not what you remember; a claim nobody checked has been wrong before.
- **Hand work on explicitly.** If a review or triage leaves something for a later change, record it in that change's
  plan or in the deferred-work ledger in the same PR, not only in a review thread, where it will be lost.
- **Tests must fail when the behaviour breaks.** For each behaviour a PR adds, check that the covering test fails if
  you break the code it covers (a quick mutation by hand is enough); a test that still passes is not coverage.

By contributing, you agree that your contributions are licensed under the [Apache-2.0 License](LICENSE).

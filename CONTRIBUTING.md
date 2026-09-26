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

Requires the .NET SDK pinned in `global.json` (10.0.302, or a later feature band) and the .NET 8 and .NET 9 runtimes,
because every test project that exercises the packages runs on `net8.0`, `net9.0` and `net10.0`.

```bash
dotnet restore
dotnet build
dotnet test                                    # everything; the storage tests need Docker
dotnet test --framework net10.0                # only the net10.0 part
```

**Docker.** The storage suites, the pgvector compatibility proof (`PostgresVectorProof`) and the sample's PostgreSQL
mode start PostgreSQL (with pgvector) containers through Testcontainers. They run against PostgreSQL 16 unless `AGENTEXPERIENCE_POSTGRES_MAJOR`
names another supported major (15, 16, 17 or 18), which is how CI runs them on each; any other value fails loudly.
If Testcontainers' Ryuk container fails under your Docker setup, set `TESTCONTAINERS_RYUK_DISABLED=true`. With
`AGENTEXPERIENCE_TEST_ENCRYPTION=on`, the two storage test projects run again, unmodified, in crypto-shredding mode;
CI runs them both ways.

**Without Docker**, skip the container-backed classes:

```bash
dotnet test --filter "FullyQualifiedName!~PostgresVectorProof&FullyQualifiedName!~ExperienceSchemaMigratorTests&FullyQualifiedName!~MigratorLogSilenceTests&FullyQualifiedName!~PlainPostgresMigrationTests&FullyQualifiedName!~PostgresApplicationRoleTests&FullyQualifiedName!~PostgresBatchReadTests&FullyQualifiedName!~PostgresConfidenceEvidenceTests&FullyQualifiedName!~CryptoShredding&FullyQualifiedName!~PostgresDeletionTests&FullyQualifiedName!~PostgresExperienceCandidateSourceTests&FullyQualifiedName!~PostgresExperienceRecordStoreTests&FullyQualifiedName!~PostgresFinalizationTests&FullyQualifiedName!~PostgresGrantAccessAuditTests&FullyQualifiedName!~PostgresGrantTests&FullyQualifiedName!~PostgresLifecycleCommitTests&FullyQualifiedName!~PostgresRetentionReachTests&FullyQualifiedName!~PostgresReuseFeedbackTests&FullyQualifiedName!~PostgresServerVersionTests&FullyQualifiedName!~PostgresSupersessionAndAppendOnlyTests&FullyQualifiedName!~PostgresVerifiedIndependenceTests&FullyQualifiedName!~ApplicationRoleVectorsTests&FullyQualifiedName!~HybridRetrievalIntegrationTests&FullyQualifiedName!~PostgresDeindexingTests&FullyQualifiedName!~PostgresEmbeddingIndexTests&FullyQualifiedName!~ErasureTelemetryTests&FullyQualifiedName!~BatchReReadPostgresEquivalenceTests&FullyQualifiedName!~SamplePostgres"
```

No test anywhere needs model credentials: every model and embedding in the suite is a deterministic in-test fake.

Package versions are locked with `packages.lock.json`. Commit lock-file changes together with the `PackageReference` change that caused them.

## Repository layout

```
src/
  AgentExperience.Abstractions/             domain contracts and ports (BCL only)
  AgentExperience.Core/                     sanitization, capture, verification, reflection, finalization, lifecycle, confidence, indexing, retrieval, reuse feedback, key management
  AgentExperience.MicrosoftAgentFramework/  MAF adapter: run and tool capture, Historical Reference injection (Microsoft.Agents.AI [1.22.0, 2.0.0))
  AgentExperience.Storage.Postgres/         PostgreSQL store, text search, grants, access log, reuse feedback ledger, deletion, crypto-shredding, schema migrator (0001-0003, 0005-0018)
  AgentExperience.Storage.Postgres.Vectors/ pgvector embedding index, conditional writes, scoped re-index, vector search (0004)
tests/
  AgentExperience.Abstractions.Tests/       contract and dependency-boundary tests
  AgentExperience.Core.Tests/               sanitizer, capture, verification, reflection, lifecycle, indexing, retrieval tests
  AgentExperience.MicrosoftAgentFramework.Tests/  real ChatClientAgent runs against a scripted fake model
  AgentExperience.Storage.Postgres.Tests/   store tests, mostly against a PostgreSQL container
  AgentExperience.Storage.Postgres.Vectors.Tests/  embedding index and hybrid retrieval, against a pgvector container
  AgentExperience.CompatibilityProof/       executable proofs for MAF hooks, context providers, pgvector, redaction
  AgentExperience.Sample.EndToEnd.Tests/    the sample's seven stages, its determinism, and what it does not claim
  AgentExperience.ReuseBaseline/            the controlled reuse experiment and its golden reports
  AgentExperience.Release.Tests/            release gates: the public API baseline, pin agreement, the security-suite map, workflow guards, documentation links
samples/
  AgentExperience.Sample.EndToEnd/          one runnable command: capture, verify, reflect, persist, retrieve, inject, record reuse
eng/                                        release tooling: package verification and the dependency probes
docs/                                       the guide, known limits, telemetry contract, security-suite map, compatibility evidence, original research
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
  five packages' public surface, and any change fails it. If the change is deliberate, regenerate the baseline with
  `AGENTEXPERIENCE_ACCEPT_API_CHANGES=true dotnet test tests/AgentExperience.Release.Tests --filter "FullyQualifiedName~PublicApi"`
  and commit the resulting `PublicApi/*.verified.txt` diff with your change, so reviewers see it. The baseline never
  updates itself.
- **Moving a package pin** means updating `docs/compatibility-evidence.md` in the same PR; the release tests fail if
  the compatibility proof and the shipping packages disagree on a version. Releasing is described in
  [RELEASING.md](RELEASING.md).
- **A new known limit** is a row in [docs/known-limits.md](docs/known-limits.md), which the release gate counts.

By contributing, you agree that your contributions are licensed under the [Apache-2.0 License](LICENSE).

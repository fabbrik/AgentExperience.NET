# Contributing to AgentExperience.NET

Thanks for your interest. This project is in early development, so the most helpful contributions right now are bug reports, design feedback on open issues, and focused pull requests.

## Before you start

- For anything beyond a small fix, **open an issue first** so we can agree on the approach.
- Read the [architecture spine](_bmad-output/planning-artifacts/architecture/architecture-agenticexperience.net-2026-09-06/ARCHITECTURE-SPINE.md) and [reuse boundaries](_bmad-output/specs/spec-agentexperience-net/reuse-boundaries.md). They define rules the code must keep.

## Ground rules

- **Keep the core portable.** `AgentExperience.Abstractions` and `AgentExperience.Core` must not reference MAF, EF Core, PostgreSQL, model providers, or OpenTelemetry. `DependencyBoundaryTests` enforces this.
- **Reuse upstream infrastructure.** Don't build a new agent runtime, tool runner, session store, vector database, or telemetry exporter. Integrate with the existing ones at adapter boundaries.
- **No private chain-of-thought.** Capture observable facts only, and sanitize before anything is stored.
- **Tests are required.** Every behavior change needs tests. Unit and adapter tests must run without network, database, or model credentials.
- **Warnings are errors.** The build uses `TreatWarningsAsErrors` and generates XML documentation, so public APIs need doc comments.

## Development

```bash
dotnet restore
dotnet build
dotnet test                                                   # needs Docker for CompatibilityProof
dotnet test --filter "FullyQualifiedName!~CompatibilityProof" # everything else, no Docker
```

Package versions are locked with `packages.lock.json`. Commit lock-file changes together with the `PackageReference` change that caused them.

## Pull requests

- Keep each PR to one concern, and explain the *why* in the description.
- Use [Conventional Commits](https://www.conventionalcommits.org/) for commit messages (`feat:`, `fix:`, `docs:`, `chore:`).
- Make sure `dotnet build` and `dotnet test` pass locally. CI runs both on every PR.

By contributing, you agree that your contributions are licensed under the [Apache-2.0 License](LICENSE).

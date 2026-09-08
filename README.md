# AgentExperience.NET

Portable .NET contracts for capturing, sanitizing, verifying, and reusing AI agent experience — framework- and storage-independent by design.

## Layout

```
AgentExperience.NET.sln
global.json                              # pins the .NET SDK
Directory.Build.props                    # shared build settings + package metadata
src/
  AgentExperience.Abstractions/          # domain contracts and ports (zero PackageReferences; BCL only)
tests/
  AgentExperience.Abstractions.Tests/    # contract tests for AgentExperience.Abstractions
.github/workflows/ci.yml                 # restore/build/test on push and PR
```

`AgentExperience.Abstractions` has no dependency on Microsoft Agent Framework, EF Core, PostgreSQL, model providers, or OpenTelemetry — see `tests/AgentExperience.Abstractions.Tests/DependencyBoundaryTests.cs`, which enforces this in CI.

Story 1.1's AC6 ("a minimal solution, Abstractions project, contract tests, and CI build exist") is satisfied by this file's documented `dotnet restore`/`build`/`test` commands succeeding in `.github/workflows/ci.yml` — there is no dedicated unit test for the scaffold itself.

## Requirements

- [.NET SDK 10.0.302](https://dotnet.microsoft.com/) or newer within the range `global.json` allows (`rollForward: latestFeature`).

## Build and test

```bash
dotnet restore
dotnet build
dotnet test
```

All contract tests run in-memory with no network or database dependency.

## License

[Apache-2.0](./LICENSE)

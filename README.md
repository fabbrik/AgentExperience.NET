# AgentExperience.NET

Portable .NET contracts for capturing, sanitizing, verifying, and reusing AI agent experience — framework- and storage-independent by design.

## Layout

```
AgentExperience.NET.sln
global.json                              # pins the .NET SDK
Directory.Build.props                    # shared build settings + package metadata
src/
  AgentExperience.Abstractions/          # domain contracts and ports (zero PackageReferences; BCL only)
  AgentExperience.Core/                  # default sanitization pipeline (Abstractions + Microsoft.Extensions.Compliance.Redaction only)
  AgentExperience.MicrosoftAgentFramework/  # MAF adapter: captures invocations and tool calls (pinned Microsoft.Agents.AI 1.20.0)
tests/
  AgentExperience.Abstractions.Tests/    # contract tests for AgentExperience.Abstractions
  AgentExperience.Core.Tests/            # sanitizer conformance + DefaultSanitizer tests for AgentExperience.Core
  AgentExperience.MicrosoftAgentFramework.Tests/  # real ChatClientAgent runs against a scripted fake model
.github/workflows/ci.yml                 # restore/build/test on push and PR
```

`AgentExperience.Abstractions` has no dependency on Microsoft Agent Framework, EF Core, PostgreSQL, model providers, or OpenTelemetry — see `tests/AgentExperience.Abstractions.Tests/DependencyBoundaryTests.cs`, which enforces this in CI.

`AgentExperience.Core` may depend only on `AgentExperience.Abstractions` and `Microsoft.Extensions.Compliance.Redaction` (AD-1) — same forbidden list (MAF, EF Core, PostgreSQL, model providers, OpenTelemetry) applies, enforced by `tests/AgentExperience.Core.Tests/DependencyBoundaryTests.cs`.

MAF is referenced only by `AgentExperience.MicrosoftAgentFramework` (and the proof-only `tests/AgentExperience.CompatibilityProof`) — see `src/AgentExperience.MicrosoftAgentFramework/README.md`.

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

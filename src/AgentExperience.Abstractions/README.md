# AgentExperience.Abstractions

> **Preview — not production ready.** This is a `0.1.0-preview` package. Public APIs may change between previews,
> and the [Known limits](https://github.com/fabbrik/AgentExperience.NET#known-limits) table in the repository README
> lists every unresolved item; any one blocks a production-readiness claim, and this version makes none. The
> [Documented boundaries](https://github.com/fabbrik/AgentExperience.NET#documented-boundaries) beside it state
> exactly what no code change can remove; they do not block that claim.

The adapter-independent domain contracts and ports of AgentExperience.NET: what an agent tried, what happened, how
it was verified, and the lesson drawn from it — plus the ports every storage, indexing, and sanitization adapter
implements.

**Dependencies: the BCL only.** No Microsoft Agent Framework, EF Core, Npgsql, DbUp, OpenTelemetry, or model-provider
package. Dependency-boundary tests enforce this on every build, and release verification re-checks it from the built
package's nuspec.

## What is in it

| Area | Types |
| --- | --- |
| A captured run | `ExperienceRun`, `Attempt`, `ToolCallRecord`, `Outcome`, `Evidence`, `EnvironmentFingerprint`, `Provenance` |
| Identity and authority | `Scope` (where a record lives), `AuthorizationContext` (what the host authorized — always established by the host, never taken from model output) |
| A durable lesson | `ExperienceRecord`, `Reflection`, `ExperienceStatus`, `LifecycleEvent`, `StoredLifecycleEvent`, `ConfidenceUpdate` |
| Sanitization port | `ISanitizer`, `RawPayload`, `SanitizedPayload`, `SanitizationDecision` |
| Storage ports | `IExperienceRecordStore`, `IExperienceCandidateSource`, `IExperienceGrantStore`, `IExperienceGrantAccessLog`, `IExperienceReuseFeedbackStore` |
| Derived-data ports | `IExperienceEmbeddingIndex`, `IExperienceEmbeddingGenerator` |
| Reuse feedback | `ExperienceReuseFeedback`, `HumanReuseAssessment`, `ComparativeEvaluationResult`, `ReuseMeasure` |

Every port returns a structured result with an outcome enum rather than throwing for an expected refusal —
`Denied`, `NotFound`, `Invalid`, `Conflict`, `StaleRevision` and so on — so a host can tell "not allowed" from "not
there" from "not stored".

## Using it

Most hosts never reference this package directly: `AgentExperience.Core` and the adapters bring it in. Reference it
on its own when you are implementing a port out of tree — a different store, index, or sanitizer — and want nothing
else.

```csharp
using AgentExperience.Abstractions;

// Established by the host from its own authentication, never built from request input or model output.
var authorization = new AuthorizationContext(
    TenantId: "tenant-1",
    PrincipalId: currentUser.Id,
    Roles: currentUser.Roles,
    IssuedAt: DateTimeOffset.UtcNow,
    ApplicationId: "support",
    ProjectId: "tickets");

var scope = new Scope(TenantId: "tenant-1", ApplicationId: "support", ProjectId: "tickets", TeamId: "team-a");

if (!authorization.Permits(scope))
{
    // Every adapter in this repository makes exactly this check before touching storage.
}
```

## Implementing a port out of tree

The in-tree adapters are the reference implementations, and their tests are the conformance suite in practice. Read
the port's XML documentation before implementing it: several obligations cannot be expressed in the type system and
do not fail at compile time — for example, a store that accepts a lifecycle event carrying a `Confidence` payload has
to persist it and enforce its independence key, or it will report `Committed` while silently dropping the payload.
The repository README lists every such break under "Port changes".

The public surface of this package is pinned by an approval baseline
(`tests/AgentExperience.Release.Tests/PublicApi/`), so any change to it is a reviewed diff.

## More

- Repository and full documentation: <https://github.com/fabbrik/AgentExperience.NET>
- Release procedure and verification checks: `RELEASING.md` in the repository
- License: Apache-2.0

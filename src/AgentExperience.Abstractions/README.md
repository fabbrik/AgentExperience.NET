# AgentExperience.Abstractions

> **Preview — not production ready.** This is a `0.1.0-preview` package, and it claims no production readiness.
> Public APIs may change between previews. Read
> [Known limits and documented boundaries](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/known-limits.md)
> before you rely on it.

The domain types and ports of [AgentExperience.NET](https://github.com/fabbrik/AgentExperience.NET): what an agent
tried, what happened, how it was verified, and the lesson drawn from it — plus the interfaces every storage,
indexing, and sanitization adapter implements.

**Dependencies: the BCL only.** No Microsoft Agent Framework, EF Core, Npgsql, DbUp, OpenTelemetry, or
model-provider package. Dependency-boundary tests enforce this on every build, and release verification re-checks it
from the built package. Targets `net8.0`, `net9.0` and `net10.0`.

## Do you need it directly?

Usually not: `AgentExperience.Core` and the adapters bring it in. Reference it on its own when you implement a port
out of tree — a different store, index, sanitizer or key store — and want nothing else.

## What is in it

| Area | Types |
| --- | --- |
| A captured run | `ExperienceRun`, `Attempt`, `ToolCallRecord`, `Outcome`, `Evidence`, `EnvironmentFingerprint`, `Provenance` |
| Identity and authority | `Scope` (where a record lives), `AuthorizationContext` (what the host authorized — always established by the host, never taken from model output) |
| A durable lesson | `ExperienceRecord`, `Reflection`, `ExperienceStatus`, `LifecycleEvent`, `StoredLifecycleEvent`, `ConfidenceUpdate` |
| Sanitization port | `ISanitizer`, `RawPayload`, `SanitizedPayload`, `SanitizationDecision` |
| Storage ports | `IExperienceRecordStore`, `IExperienceCandidateSource`, `IExperienceGrantStore`, `IExperienceGrantAccessLog`, `IExperienceReuseFeedbackStore` |
| Derived-data ports | `IExperienceEmbeddingIndex`, `IExperienceEmbeddingGenerator` |
| Key custody | `IExperienceKeyStore`, for crypto-shredding |
| Reuse feedback | `ExperienceReuseFeedback`, `HumanReuseAssessment`, `ComparativeEvaluationResult`, `ReuseMeasure` |

Every port returns a structured result with an outcome enum rather than throwing for an expected refusal —
`Denied`, `NotFound`, `Invalid`, `Conflict`, `StaleRevision` and so on — so a host can tell "not allowed" from "not
there" from "not stored".

## Using it

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
    // Every adapter in the repository makes exactly this check before touching storage.
}
```

`Permits` requires the same tenant, and every non-null bound on the context to equal the scope's field exactly. A
null bound leaves that field unrestricted; a null scope field is never a wildcard.

## Implementing a port out of tree

The in-tree adapters are the reference implementations, and their tests are the conformance suite in practice. Read
the port's XML documentation before implementing it: several obligations cannot be expressed in the type system and
do not fail at compile time — for example, a store that accepts a lifecycle event carrying a `Confidence` payload has
to persist it and enforce its independence key, or it will report `Committed` while silently dropping the payload.
The full list is in
[For implementers of the ports](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/lifecycle.md#for-implementers-of-the-ports).

The public surface of this package is pinned by an approval baseline in the repository, so any change to it is a
reviewed diff.

## More

- Documentation: [guide and glossary](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/README.md)
- Changes between previews: [changelog](https://github.com/fabbrik/AgentExperience.NET/blob/main/CHANGELOG.md)
- License: Apache-2.0

# Reflective execution: engineering report

Status: experimental, off by default. Shipped in `0.1.0-preview.19` (step events and the harness: the release after it). This is the
report the implementation prompt asked for. It states what is built, what was run and what was not.

## Architecture implemented

The loop wraps the built agent from outside (`UseReflectiveExecution`, called before `UseAgentExperience`). Every attempt is a captured
invocation of **one** `ExperienceRun` (`ContinuesRunId`, `ShouldCompleteRun`), finalized once through the host's own `Verify`.

- **Core** (`AgentExperience.Core.ReflectiveExecution`, no MAF dependency): `ReflectionExecutionCoordinator` (the loop),
  `IReflectionEvaluator`, `IReplanningStrategy`, `IRetryAuthorizer` (default denies), `ReflectionExecutionOptions` (disabled by default),
  `ReflectionStep` (structured, text-free steps), `ReflectionStopReason`, evaluation validation.
- **MAF** (`AgentExperience.MicrosoftAgentFramework.ReflectiveExecution`): `ReflectiveExecutionAgent` (the wrapper),
  `AddReflectiveExecution` / `UseReflectiveExecution`, `DeterministicReflectionEvaluator`, `ChatClientReflectionEvaluator`,
  counters on their own meter.
- **Strategies:** failure-driven and deterministic (rules), requirement-driven, model-assisted (`IChatClient`), composite; a custom
  `IReflectionEvaluator` or `IReplanningStrategy` replaces any of them.
- **Not added:** `IReflectionStrategy`, `IReflectionExecutionCoordinator` and `ReflectionDecision` (the prompt's proposed names): composition of
  an evaluator and a replanning strategy covered the need without a larger hierarchy. `RequireVerifiedSuccess` is not an option because
  only the host's check can end a loop as verified, always.

Design and decisions: [reflective-execution-design.md](reflective-execution-design.md). Usage and limits: [the guide](../guide/reflective-execution.md).

## Files changed

Added: `src/AgentExperience.Core/ReflectiveExecution/*`, `src/AgentExperience.MicrosoftAgentFramework/ReflectiveExecution/*`,
`samples/AgentExperience.Sample.Reflective/`, `experiments/AgentExperience.ReflectionEval/`, the guide, the design and this report,
tests under `tests/*/ReflectiveExecution/` and `tests/AgentExperience.Sample.QuickStart.Tests/Reflective/`.
Modified (additive): `OpenRunRegistry` (an internal `CloseRunAsync` that closes with the last attempt's real status),
`AgentExperienceRegistration` and the builder extension (a compose hook), the public API baselines, README, guide index,
known-limits pointer, telemetry note, CHANGELOG.

## Public API

New, all experimental: `AddReflectiveExecution`, `UseReflectiveExecution`, `ReflectiveExecutionOptions` (`Limits`, `Evaluator`, `Replanning`,
`Authorizer`, `TrustedCheck`, `OnEvent`, `OnStep`), the Core types above. No existing signature, default, stored format or migration changed;
the API baselines grew and lost nothing.

## Verification

Commands run on the final tree, with the real results:

| Command | Result |
| --- | --- |
| `dotnet build` | 0 errors |
| Abstractions / Core / MAF / InMemory / SQLite / CLI / ReuseBaseline tests | 117 / 1415 / 1478 / 292 / 247 / 102 / 170 passed |
| QuickStart (carries the sample's tests) / LiveReuse / CiTriage | 16 / 208 / 75 passed |
| CompatibilityProof / EndToEnd / RetrievalQuality / Contract (non-PostgreSQL) | 15 / 29 / 24 / 72 passed |
| `AgentExperience.Release.Tests` | 198 of 200 passed; the 2 failures (`PackageValidationBaselineTests`, `CompatibilityPinAgreementTests`) are caused by stale `.claude/worktrees` copies on the author's machine and fail on a clean `main` too |
| PostgreSQL-backed suites | not run locally; run by CI on every pull request, and they passed on the pull requests that carried this work |

The prompt's 17 cases map to tests as follows. Unauthorized tool actions: the loop adds no tools, and a test asserts the agent's tools and task
are unchanged; **blocking** a tool call is the existing approval boundary's job, not this library's.

| # | Case | Where |
| --- | --- | --- |
| 1-2 | success without reflection; failure corrected | `ReflectionExecutionCoordinatorTests`, `ReflectiveExecutionTests` |
| 3 | invalid correction | evaluation validator tests; prompt-injected evaluator test |
| 4 | retry exhaustion | coordinator tests; `ReflectionStepTests` |
| 5-6 | cancellation; provider failure or timeout | coordinator tests |
| 7 | verifier rejects a model's claim | coordinator and MAF tests |
| 8 | unauthorized tool actions | tools unchanged (see above) |
| 9 | tenant isolation | MAF tests |
| 10-11 | multi-attempt persistence; unverified is not promoted | MAF tests |
| 12 | lesson exposure and provenance | MAF tests (second task is handed the lesson) |
| 13 | repeated strategy | coordinator tests |
| 14 | interaction with failure recall | MAF tests |
| 15 | disabled is a pass-through | coordinator and MAF tests |
| 16 | non-idempotent operations | side-effect policy tests |
| 17 | sanitization and prompt injection | validator, strategies and MAF tests |

## Security

The loop cannot change identity or scope, add or approve tools, run a command, skip `Verify`, or write a record. A passing `TrustedCheck` is
the only way to stop as `VerifiedSuccess`; an evaluator's `Satisfied` is `EvaluatorSatisfied` (not verified) or `NotRetryable` against a failing
check. Retries need `IRetryAuthorizer` (default denies) and a side-effect policy (default never retries semantically). Evaluator and
replanning output is validated, bounded and screened; the correction note reaches the model as labelled, untrusted reference text.
Steps and counters carry counts and enums only.

## Limitations

- **No benefit demonstrated.** The harness and the sample use a scripted model; they show mechanics, not improvement. A real-model,
  pre-registered run is described in the harness README and has not been done.
- The evaluator sees a bounded summary of a **finished** attempt, not plans or intermediate results. Replanning is a note the model may or may not follow.
- The correction note is model-derived text in the conversation; screening is not a boundary (KL-18).
- All attempts share one session. Streaming is not retried. Tokens are counted only when the provider reports them.
- `TrustedCheck` is the host's statement and is separate from `Verify`; if they disagree the run can stop "verified" and be stored as failed.
- Historical-experience reuse is not a step of its own; use the existing injection telemetry and the run id.
- Python, Go, the CLI and MCP are unchanged.

## Further improvements

Essential before any claim of benefit: a pre-registered real-model comparison. Optional: plan-level critique before acting, streaming support,
an evaluator that can request authorized read-only diagnostics, and a Python/Go port if there is demand.

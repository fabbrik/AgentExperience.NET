# Reflective execution: architecture assessment and design

Status: **implemented, experimental** (Core coordinator, MAF wrapper, evaluators, sample; see the guide for what shipped and its limits). Nothing here is a production guarantee, and no benefit is claimed: the library's own
holdout is how a host finds out whether reflection helps its tasks.

## 1. Two concepts that must stay apart

- **Experience reflection** (exists): turning a *finished, verified* run into a durable lesson for later runs
  (`IExperienceReflector`, finalization, `Reflections/`).
- **Execution reflection** (this design): during one task, evaluate an attempt, diagnose it, propose a corrected
  strategy and, if the host permits, attempt again within a budget.

The second feeds the first only through the existing pipeline. A model's judgement that an attempt worked is **advisory
input to a retry decision** and is never verification evidence.

## 2. What the repository already provides (do not rebuild)

| Need | Already there | Where |
| --- | --- | --- |
| Group retries as one run | `ExperienceRunDescriptor.ContinuesRunId` makes an invocation the next `Attempt` of an open run | `ExperienceCaptureOptions.cs` |
| Keep a run open between attempts | `ShouldCompleteRun`, bounded by `MaxAttemptsPerOpenRun` and `MaxOpenRunDuration` | `ExperienceCaptureOptions.cs`, `OpenRunRegistry.cs` |
| Read the run ID after an attempt | `RunIdStateKey` in the session `StateBag` | `ExperienceCaptureAgentBuilderExtensions.cs` |
| Trusted outcome | the host's verifier through `ResolveFinalization` and finalization; independence rules | Core `Verification/`, `Finalization` |
| Lessons from earlier runs | injection by request text; recall at moment of tool failure | `Injection/`, `Recall` |
| Sanitizing and refusing model-authored text | `ModelAuthoredContentGuard`, `ReflectionScreening` | Core `Reflections/` |
| Measuring benefit | injection holdout, `ExperienceBenefit` | MAF `AgentExperienceBenefit.cs` |
| Telemetry | `ExperienceDiagnostics`, `docs/telemetry-names.json` | Core `Diagnostics/` |

## 3. What is missing

1. Something that **drives** the retry loop: decides whether to retry, builds the next attempt's input, enforces
   budgets, stops on explicit conditions.
2. A **structured evaluation** contract (assessment, issues, proposed strategy, advisory confidence), validated and
   bounded.
3. **Replanning** that preserves the task and constraints and cannot widen permissions.
4. **Ineffective-strategy detection** (the same correction proposed twice).
5. A **side-effect policy**: no semantic retry of a non-idempotent task without an explicit host decision.
6. **Reflection telemetry** that separates "a retry happened", "a retry improved the result" and "a retry reached
   independently verified success".

## 4. Decision: the loop lives above the agent, not in its middleware

A retry means invoking the agent again. A middleware layer inside capture cannot re-enter itself, and capture is
documented as the outermost layer. So the coordinator **wraps the built agent from outside**:

```
host call ──► ReflectiveExecution (coordinator, outermost)
                 │  attempt 1..N, one AgentSession, continuation id in StateBag
                 ▼
              capture (UseAgentExperience)  ── records each invocation as an Attempt of ONE run
                 ▼
              injection / recall / tools / model
```

Consequences:

- Every internal attempt is an `Attempt` of **one** `ExperienceRun`, so reflection steps never create extra records.
- Tools, approvals, tenant identity and scope are untouched: the coordinator calls the same agent with the same
  identity and cannot add tools or permissions.
- `UseReflectiveExecution` is therefore offered as a wrapper (`AIAgent` decorator), documented as **outside**
  `UseAgentExperience`, not as inner middleware. Applying it inside capture is detected and refused at build time.

## 5. Components

Core (portable, no MAF, no new dependencies):

- `ReflectionAssessment` (enum): `Satisfied`, `NeedsCorrection`, `Unrecoverable`.
- `ReflectionIssue`: category (`ToolFailure`, `UnmetRequirement`, `InvalidResult`, `MissingInformation`,
  `Contradiction`, `Other`), description, evidence. All text bounded and screened.
- `ReflectionEvaluation`: assessment, issues, proposed strategy, `RecommendRetry`, advisory `Confidence` (never the
  verified reuse confidence).
- `IReflectionEvaluator`: `EvaluateAsync(ReflectionAttemptContext, ct)`. Implementations: deterministic rules,
  composite (first non-`Satisfied` wins, an `Unrecoverable` always wins), and a model-backed one in the MAF project.
- `IRetryAuthorizer`: host-controlled gate called before every retry; default denies.
- `ReflectionExecutionOptions`: `Enabled=false`, `MaxReflectionCycles`, `MaxAttempts`, `MaxDuration`,
  `MaxToolCalls` (where measurable), `MaxTokens` (where reported), `SideEffects` (`Idempotent`, `RequiresApproval`,
  `NeverRetry`; default `NeverRetry` for the semantic path).
- `ReflectionExecutionCoordinator<TResult>`: the loop, over a host delegate `AttemptAsync(ReflectionAttemptInput, ct)`
  so Core stays MAF-free. Returns `ReflectionExecutionResult` with every attempt's outcome and the stop reason.
- `ReflectionStopReason`: `VerifiedSuccess`, `EvaluatorSatisfied`, `CyclesExhausted`, `AttemptsExhausted`,
  `DurationExhausted`, `BudgetExhausted`, `RetryDenied`, `NotRetryable`, `RepeatedStrategy`, `EvaluationFailed`,
  `Cancelled`.

MAF project:

- `ReflectiveExecutionAgent` (decorator) and `UseReflectiveExecution(...)`: owns the session, writes the
  continuation id for `ResolveRun`, and sets `ShouldCompleteRun` to false until the coordinator closes the run.
- `ChatClientReflectionEvaluator`: `IChatClient` evaluator, structured JSON, validated with the existing
  `ModelAuthoredContentGuard`.
- `AddReflectiveExecution(...)`.

## 6. Loop semantics

1. Run attempt 1 through the host delegate. No model reflector is called if the host's trusted check passes.
2. Collect a bounded, sanitized summary of the attempt (outcome, error class, failed tool names and error classes,
   sanitized result excerpt). Raw tool arguments and results are never passed to the evaluator.
3. Ask the **trusted check** first (`ReflectionTrustedCheck`: the host's verifier result, a test exit code). A trusted
   pass stops the loop with `VerifiedSuccess`. A model's "looks fine" does not.
4. If the trusted check fails or is absent, call the evaluator. Its output is validated: unknown fields rejected,
   lengths capped, text screened. An invalid evaluation is `EvaluationFailed` and stops; it is never retried blindly.
5. Distinguish **technical** retry (transient error class, same input) from **semantic** retry (needs a corrected
   strategy). Semantic retries consult `SideEffects` and the `IRetryAuthorizer`.
6. Build the next input: original task text unchanged, plus a labelled, bounded `Reflection note` as untrusted
   reference text, never as system or developer instruction. The strategy hash is compared with earlier ones;
   a repeat stops with `RepeatedStrategy`.
7. Repeat until a stop reason. The last attempt closes the run so finalization runs once.

## 7. Memory integration

- One run, N attempts: finalization already reflects on the failure and the success together; the existing verifier
  decides the outcome.
- A failed or unverified end is finalized as it is today (quarantine and retention rules unchanged). Nothing is promoted.
- Recall at moment of failure keeps working inside each attempt. The coordinator does not add a second recovery path.
- The injection holdout is decided by run ID; all attempts share one run, so an arm is never mixed within a task.

## 8. Security stance

Reflection output and retrieved lessons are untrusted. The coordinator cannot: change identity or scope, add tools,
approve a tool, run a command, skip the verifier, or write a record. Telemetry carries counts, enums and stop reasons,
never prompts, tool arguments or model text.

## 9. Compatibility

- New types only, in new files; no existing public signature changes.
- Disabled by default; with it off, the wrapper is a pass-through.
- No new package dependencies; no migration; no storage change; Python, Go, CLI and MCP untouched.
- The public API baseline (`PackageValidation`) is updated for the new surface; the CHANGELOG records it as experimental.

## 10. Evaluation plan

A harness under `experiments/` compares four arms on externally verified success, attempts, tool calls and latency:
baseline, reflection only, memory only, both. Deterministic fake agents first (proves mechanics only); a real-model run
is separate, pre-registered and reported with the same intervals as the holdout. No improvement is claimed until it
has a result.

## 11. What changed from this design while implementing it

- **The trusted check lives in the MAF wrapper**, not in the capture callbacks. `ShouldCompleteRun` is synchronous and runs
  before any async check could, so the composed callback keeps the run open on every attempt except the last permitted one,
  and the wrapper closes the run itself when the loop stops sooner.
- **Closing sooner needed one internal addition**, `OpenRunRegistry.CloseRunAsync`, which completes the run with the **last
  attempt's** status. Reusing the duration bound's close would have recorded the run as `Cancelled`, a wrong outcome.
- **No telemetry instruments yet.** Reflection reports through `OnEvent`; OpenTelemetry names are a follow-up (they are
  generated from tests and listed in `docs/telemetry-names.json`).
- **Tokens are not measured** by the wrapper.
- **Python, Go, CLI and MCP are untouched**, as intended.

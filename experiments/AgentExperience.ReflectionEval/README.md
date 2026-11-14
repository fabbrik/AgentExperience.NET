# Reflective execution: the four-arm harness

Compares, on the same tasks: **baseline** (neither), **reflection only**, **memory only** (a stored lesson, no retry) and
**reflection + memory**. It reports externally verified success (the host's own build check, never a model's word),
attempts, tool calls, model calls and elapsed time. Tokens and cost are not reported: the scripted model has no usage to
report, and the harness does not invent it.

```bash
dotnet run --project experiments/AgentExperience.ReflectionEval            # print the table
dotnet run --project experiments/AgentExperience.ReflectionEval -- out.md  # also write it
```

## What this does and does not show

It runs against a **scripted model that obeys what it is handed**, so the result is fixed by construction: it shows the
mechanics (a retry happens only with reflection; a stored lesson removes the need for one; verified success is decided by
the build, not the model). **It is not evidence that reflective execution or memory improves a real model.** No
improvement is claimed.

## A real-model run (not done)

To find out whether reflection helps, run it the way this repository runs its other real-model experiments, and report it
with the same intervals:

1. Fix before running: the task set (real repositories judged by their own tests), the model, the budgets
   (`MaxAttempts`, `MaxReflectionCycles`), the number of runs per arm, and the single comparison that counts (reflection
   versus baseline, verified success).
2. Replace `ScriptedModel` in the sample with an `IChatClient` for the chosen model; keep the host's own test run as
   `TrustedCheck` and `Verify`.
3. Run all four arms on the same tasks; record verified success, attempts, tool calls, tokens, cost and latency.
4. Report Wilson and Newcombe intervals as the holdout does, including a null or negative result.

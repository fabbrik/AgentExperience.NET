# Reflective execution sample (experimental)

Two runs on one scratch project, deterministic, no key and no network:

```bash
dotnet run --project samples/AgentExperience.Sample.Reflective
dotnet run --project samples/AgentExperience.Sample.Reflective -- --compare
```

**Sample A, a reflective coding agent.** The first attempt applies `bump-version`, the build fails, the evaluator
proposes "pin the dependency", the retry applies `pin-version`, and the host's own build check passes. Both attempts are
one captured run, finalized once, stored as one verified lesson.

**Sample B, reflection plus experience.** A related task starts with the lesson Sample A stored, applies the right fix on
its first attempt, and the reflection loop never has to retry.

`--compare` runs four arms (neither, reflection only, memory only, both). **It shows the mechanics, not a benefit:** the
scripted model does whatever it is handed, so the numbers say nothing about a real model. Whether reflection or memory
helps your tasks is what the injection holdout and a real model run are for; no improvement is claimed here.

To try a real model, replace `ScriptedModel` in `Program.cs` with any `IChatClient`. The loop, the build check and the
stored lessons work the same way. Read [Reflective execution](../../docs/guide/reflective-execution.md) first.

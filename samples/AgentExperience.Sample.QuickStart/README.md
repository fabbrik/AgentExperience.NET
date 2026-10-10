# AgentExperience.NET — the quick-start demo

One command, no key, no Docker, no network:

```bash
dotnet run --project samples/AgentExperience.Sample.QuickStart
```

It wires one agent exactly as the [README quick start](../../README.md#quick-start) does
(`services.AddAgentExperience(...)`, `.UseInMemoryStorageForDevelopment()`, `GetAgentExperienceContextProvider()`,
and a `ChatClientAgent` with `.AsBuilder().UseAgentExperience(provider)`), then gives it the same ticket twice:

```text
Model: a scripted stand-in, not a real model (set OPENAI_API_KEY to use one)
Task (both runs): Ticket #4812: a refund is stuck on a lock. Triage it.

Run 1 (no memory yet): tried retry-immediately ✗ → wait-for-lock ✓  (1 failed attempt)
Run 2 (lesson injected): tried wait-for-lock ✓  (0 failed attempts)
```

and then prints the Historical Reference block run 2's model was sent. Run it twice and the output is the same.

## What happens

- **The task.** `run_refund_check(ticketId, strategy)` ([RefundDesk.cs](RefundDesk.cs)) accepts three strategies, and
  only `wait-for-lock` releases the refund. A wrong one says "not that one" and nothing else.
- **Verify.** After each run, `options.Verify` reads the desk's own state: was the refund released? That is the
  evidence, never what the model says. Run 1 passes, so it is stored as a `Validated` lesson.
- **Retrieve and inject.** Run 2's task text matches run 1's, so the lesson is found and injected. The ticket text is
  the same in both runs to keep the demo simple; text search needs only three of the new task's words (fewer for a
  shorter task) to match the stored task and lesson.
- **The approach.** `AgentExperienceDefaults.SanitizationAllowing("strategy")` keeps the `strategy` argument at
  capture, and `ExperienceInjectionOptions.ApproachArguments` lets the block's `Tried:` line show it. Without those two
  lines the block names only the tool. Run 1 was one invocation, so it is one attempt that lists both calls in order;
  the last one is the call that worked.
- **One fixture beyond the model.** `options.Capture` fixes the recorded environment, so the block's preconditions
  are the same on every machine. A real host keeps the default.

## The model is a stand-in

By default the model is [ScriptedModel.cs](ScriptedModel.cs), a script, not a model. It reads only what a real model
is sent: it tries the strategies in their listed order, one per call, unless its input holds a Historical Reference
whose `Worked:` attempt has a call marked `[returned]` with a strategy, which it then tries first. (The tool throws on
a non-zero exit, so run 1's wrong strategy is recorded, and marked, as a failed call.) So run 2's change comes from what the library
retrieved and injected, not from the script, but it shows the loop working, not that it helps a real model. For that,
see [Does it help?](../../README.md#does-it-help).

To use a real model, set `OPENAI_API_KEY` (never printed). `OPENAI_MODEL` picks the model (default `gpt-4.1-mini`), and
`OPENAI_BASE_URL` points at any OpenAI-compatible endpoint, for example Gemini's
(`https://generativelanguage.googleapis.com/v1beta/openai/`) or a local Ollama (`http://localhost:11434/v1/`):

```bash
OPENAI_API_KEY=... OPENAI_MODEL=gemini-3.1-flash-lite \
  OPENAI_BASE_URL=https://generativelanguage.googleapis.com/v1beta/openai/ \
  dotnet run --project samples/AgentExperience.Sample.QuickStart
```

A real model is free to do anything, so its output varies from run to run, and one pair of runs measures nothing.

`tests/AgentExperience.Sample.QuickStart.Tests` runs the stand-in mode and compares its output with a checked-in copy,
byte for byte; set `AGENTEXPERIENCE_QUICKSTART_GOLDEN_UPDATE=1` to rewrite it on purpose.

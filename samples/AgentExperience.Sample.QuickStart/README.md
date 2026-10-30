# AgentExperience.NET — the quick-start demo

One command, no key, no Docker, no network:

```bash
dotnet run --project samples/AgentExperience.Sample.QuickStart
```

It wires one agent exactly as the [README quick start](../../README.md#quick-start) does
(`services.AddAgentExperience(...)` with `UseDevelopmentIdentity()`, `.UseInMemoryStorageForDevelopment()`, and a
`ChatClientAgent` named `triage-ticket` with `.AsBuilder().UseAgentExperience(provider)`), then gives it the same ticket
twice:

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

## Recall at the moment of failure

```bash
dotnet run --project samples/AgentExperience.Sample.QuickStart -- --recall
```

The same wiring with `options.Recall = new ExperienceRecallOptions()`, and two changes: the working strategy is
`skip-ledger-check`, the last one the stand-in tries, and run 2 is a different ticket whose words match nothing
stored, so nothing is injected before it starts:

```text
Run 1 (no memory yet): tried retry-immediately ✗ → wait-for-lock ✗ → skip-ledger-check ✓  (2 failed attempts)
Run 2 (hint after a failure): tried retry-immediately ✗ → skip-ledger-check ✓  (1 failed attempt)
```

Run 2's first call fails exactly as run 1's did (the same tool, the same exception), so the library looks that failure
up, finds run 1's verified lesson, which recovered from it, and puts a short hint naming what worked in the
instructions of the model's next call. The demo prints the hint. See
[Recall at the moment of failure](../../docs/guide/injection.md#recall-at-the-moment-of-failure).

## The recipe

```bash
dotnet run --project samples/AgentExperience.Sample.QuickStart -- --recipe
```

The default two runs, wired with one `options.Recipe(...)` call in place of the separate sanitization and injection
allowlists: it consents to the `strategy` argument and vouches for what the working call returned, so run 2's block
shows `run_refund_check(strategy="wait-for-lock") [returned: "exit=0 the refund is released"]` on the attempt that
worked. A failed call never carries an excerpt. See
[Showing what worked](../../docs/guide/injection.md#showing-what-worked-the-recipe).

## Memory in a local file, across processes

```bash
dotnet run --project samples/AgentExperience.Sample.QuickStart -- --sqlite experience.db
dotnet run --project samples/AgentExperience.Sample.QuickStart -- --sqlite experience.db
```

The same wiring with `.UseSqlite(path)` instead of the in-memory storage, and one run per process. The first process
has no memory and learns; the second, with nothing in memory, finds the lesson the first one stored in the file and is
handed it. The schema is created on the first start (`ExperienceSqliteSchema.MigrateAsync`, which applies nothing once
the file is current). Delete the file to start over.

## The model is a stand-in

By default the model is [ScriptedModel.cs](ScriptedModel.cs), a script, not a model. It reads only what a real model
is sent: it tries the strategies in their listed order, one per call, unless its input holds a Historical Reference
whose `Worked:` attempt has a call marked `[returned]` (or, under `--recipe`, `[returned: "…"]`) with a strategy, which it then tries first (and, under
`--recall`, a hint in its instructions whose `worked:` line has one, which it tries before anything else). (The tool throws on
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

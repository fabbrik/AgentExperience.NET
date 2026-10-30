# CI triage: the library on real work

Story 26.1. A console tool that triages this repository's own failed CI runs. For each failure it names the files most
likely to need the fix, or says the failure is flaky. It learns through AgentExperience's public API, wired the way a
user would wire it, and the library's own holdout judges whether what it learned helps.

## What it does

1. **Reads the CI history** through `gh api`: every completed run of the `CI` workflow, its jobs, and the failed
   jobs' logs (`actions/jobs/{id}/logs`; `gh run view --log-failed` is not used, since it comes back empty for some
   runs). Logs are cached in `.cache/`, which is gitignored.
2. **Labels each failure** from the repository's own later history. A failure is the final attempt of a `CI` run with
   conclusion `failure`. The first later completed `CI` run on the same branch that succeeded decides its label:
   - the same commit: `flaky`;
   - another commit, with `git diff --name-only failSha greenSha` at most 10 files: `code`, with those files as the
     fix;
   - more than 10 files: `ambiguous`;
   - no green run yet: `pending`.

   A different commit with an empty diff counts as `flaky`. A commit that is not in the clone and cannot be fetched is
   `unavailable`. Ambiguous, pending and unavailable failures are never triaged; the ledger lists them as skipped.
3. **Replays the failures in time order** on a simulated clock. Each one gets a triage event at the time it failed and
   a finalize event at the time its green run finished. So a lesson can only reach a failure that happened after its
   fix was known.
4. **Triages** with a MAF `ChatClientAgent` and two read-only tools:
   - `changed_files`: the files the failing commit changed;
   - `read_file`: `git show failSha:path`, at most 8,000 characters.

   The agent sees only what existed when the run failed: the failed jobs' logs, trimmed to their error regions (at
   most 12,000 characters), and those two tools. It answers with JSON: `{"kind": "code"|"flaky", "files": [...]}`.
5. **Finalizes out of band** when the fix is known, through `ExperienceFinalizationService.FinalizeAsync`. There is one
   required check, `triage-correct` (`HostCheck`):
   - a right answer verifies, and its run becomes a lesson;
   - a wrong or unparseable one is stored quarantined and is never reused.

   A flaky failure is right when the agent says flaky. A code failure is right when the agent says code and at least
   one of its (at most five) files is among the fix files.
6. **Estimates** with `AgentExperienceBenefit.EstimateAsync`.

## Wiring (public API only)

- `AddAgentExperience(...)`:
  - `ResolveIdentity` returns one fixed tenant and scope, so capture and injection use the same scope;
  - `TaskId = "ci-triage"`;
  - no `Verify`, because outcomes arrive later and are finalized out of band;
  - the `path` argument of `read_file` is kept and shown on a lesson's `Tried:` line;
  - capture retention is raised, so simulated time cannot expire a captured run.
- Storage: `.UseInMemoryStorageForDevelopment()`, `.UsePostgres(...)` or `.UseSqlite(...)`.
- `.UseAgentExperience(provider)` on the agent.
- `Injection.Holdout = new ExperienceHoldoutOptions { Rate = 0.5 }`.

## Running it

```bash
# Offline: built-in fake CI data and a deterministic stand-in model. No network, no key.
# Writes to results/scripted/ (gitignored).
dotnet run --project experiments/AgentExperience.CiTriage -- --scripted

# Live: needs gh logged in, and an OpenRouter key.
export OPENROUTER_API_KEY=...                  # never printed or written anywhere
export OPENROUTER_MODEL=openai/gpt-oss-120b    # the default
dotnet run --project experiments/AgentExperience.CiTriage

# Live, keeping the lessons in PostgreSQL between passes (migrates the schema first). Pass the connection string
# through the environment, not on the command line, where a password lands in the shell history:
export AGENTEXPERIENCE_CITRIAGE_PG="Host=...;Username=...;Password=..."
dotnet run --project experiments/AgentExperience.CiTriage
# --postgres <conn> also works, and wins over the variable. --scripted ignores both and always stays in memory.
```

- **In memory** (the default), every pass is a full replay from the first failure, and the ledger is rewritten.
- **With PostgreSQL**, the lessons persist. Use a throwaway database owned by one role. The tool migrates the
  schema and then runs on the same connection, so that role is both the schema owner and the application role. This is
  not the two-role production deployment.
  - The ledger is appended to.
  - Failures it records as settled are skipped.
  - These are retried: `pending` and `unavailable` failures, `skipped: stopped`, `skipped: fetch-error`, and triages that
    ended in a provider error.
  - **No hindsight across passes.** Retrieval cannot hide a lesson finalized later in simulated time. So a resumed pass
    skips, as `skipped: before-resume`, every failure earlier than the latest fix an earlier pass finalized.
- **The ledger and report are written even when a pass throws**, with whatever ran.
- **An empty or unparseable reply is asked again once**, in the same run with tools off. Some models (gpt-oss) end a
  tool-using turn with an empty message or keep calling tools until the iteration cap. The ledger notes "asked again for
  the answer".
- `AGENTEXPERIENCE_CITRIAGE_REPLIES=<dir>` writes each triage's conversation outline and reply there, for diagnosis.
  Keep it outside the repository.
- **A provider error is never finalized.** The library counts that run as Pending, and the ledger gives it no arm and
  no score, so infrastructure noise never counts against an arm.

**Finalizing later, with SQLite** (`--storage sqlite:<path>`; `--storage memory` and `--storage postgres:<conn>` select
the other two). The lessons and every captured run live in one local file, so passes resume like PostgreSQL's, and one
thing changes: a failure still waiting for its fix (`pending`) is triaged anyway, and its captured run is left in the
file's run store, unfinalized, with `awaiting fix: run <id>` in the ledger. A later pass, in a new process, finalizes
that stored run once the fix has landed -- it does not triage the failure again -- and scores the earlier prediction
against the fix. `--scripted` may use a SQLite file (it is local); `--scripted --scripted-later` replays the same fake
history a day later, when its waiting failure has its fix, so two scripted passes show the whole round trip offline:

```bash
dotnet run --project experiments/AgentExperience.CiTriage -- --scripted --storage sqlite:/tmp/triage.db
dotnet run --project experiments/AgentExperience.CiTriage -- --scripted --scripted-later --storage sqlite:/tmp/triage.db
```

Outputs go to `results/`:

- `ci-triage-ledger.tsv`: run id, time, branch, label, arm, predicted, correct, cost and a note;
- `ci-triage-YYYY-MM-DD.md`: per-arm runs and successes, the verdict with its interval, and the Pending and Unverified
  counts.

## Cost and caps

The OpenRouter client is the live experiment's:

- the provider routing policy `require_parameters`;
- the empty-completion guard, which retries once on an empty completion.

Every pass has hard caps. When one is reached, the tool stops cleanly, writes what ran, and exits with code 2. It also
stops when three triages in a row end in a provider error, such as a bad key or no credit. Failures it did not reach are
in the ledger as `skipped: stopped`. Runs it already triaged are still finalized, since that needs no model.

| Flag | Default |
| --- | --- |
| `--budget-usd` | 0.50 |
| `--max-calls` | 400 |
| `--max-tokens` | 4,000,000 |
| `--input-price` | 0.10 (USD per million tokens, assumed) |
| `--output-price` | 0.50 (USD per million tokens, assumed) |

The spend is estimated from the tokens the provider reports and these prices. Check the model's current prices on
OpenRouter and pass them if they differ. On this repository's history (about 40 failures, about 23 of them
triageable), one pass makes a few calls per failure and should stay well under the default cap.

## Status: the first live pass (2026-10-29)

[`results/ci-triage-2026-10-29.md`](results/ci-triage-2026-10-29.md), ledger in
[`results/ci-triage-ledger.tsv`](results/ci-triage-ledger.tsv). Model `openai/gpt-oss-120b`, about USD 0.12.

- **Scope.** 40 failures:
  - 21 triaged (20 code, 1 flaky);
  - 16 skipped as ambiguous (the fix changed more than 10 files);
  - 2 pending, 1 with no log.
- **Accuracy is low: 5 of 21 correct.** 8 replies held no answer even after the tool asked again. gpt-oss often ends a
  tool-using turn with an empty message. The triage agent, not the library, is the weak part today: its prompt, its
  tools and the model.
- **The library's estimate.**
  - Treatment: 1 of 7 correct. Holdout: 3 of 9.
  - Difference -0.19, interval -0.52 to +0.24. Verdict `NoEvidence`, for lack of runs.
  - No Pending or Unverified runs.
  - It does not show that the lessons help, and it does not show that they hurt.
- **Problems found on real work, and fixed.**
  - An empty reply first made the run unscorable.
  - Then, asking again through the agent started a *new* captured run. The run the holdout had assigned was never
    finalized, and quietly dropped out of the comparison as Pending.
  - The tool now asks again directly to the model, outside the agent. A test checks that every arm stays finalized.
- **Library findings** (Epic 26):
  - outcomes that arrive after the process ends cannot be finalized (26.2);
  - there is no public way to wait for the holdout's background writes;
  - an injection result carries no run id;
  - a lesson cannot carry the agent's answer;
  - a second invocation on the same session is a new run, which a user who retries inside one conversation will not
    expect.

## What the verdict can and cannot say yet

- **Probably `NoEvidence`.** The holdout assigns an arm only to a run that had a lesson to inject. Each arm then needs at
  least 20 runs with a verified outcome. With about 23 triageable failures, a pass cannot reach that, so its verdict is
  `NoEvidence` for lack of runs, and the report says so plainly. `NoEvidence` is not "no effect".
- **Only failures whose fix is already known.** A captured run lives only in memory
  (`InMemoryExperienceCaptureService`), so it cannot be finalized after the process ends. Story 26.2 is durable capture.
- **The ground truth is the fix that was actually made.** An answer that would also have fixed the failure, in another
  file, counts as wrong.
- **Some flaky failures never appear.** Only a run's final attempt is listed. A failure that a re-run of the same run
  turned green is a success, so the replay never sees it.
- **A green commit on a different lineage can inflate the fix files.** An example is a branch rebased, or merged with
  main, before it went green. The diff then holds unrelated files: below the 10-file cap they count as fix files, and
  above it the failure is `ambiguous`.
- **One pass, one look.** Running pass after pass until the verdict says `Helps` would overstate it.

Tests: `dotnet test experiments/AgentExperience.CiTriage.Tests`. They run offline and use no model.

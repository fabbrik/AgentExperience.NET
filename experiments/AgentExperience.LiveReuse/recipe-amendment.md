# Proposed amendment: the recipe arm (story 28.4)

**Status: proposed, not registered, not run.** Nothing here changes `preregistration.realtask.json`, the harness or any
result. It is written now, before any recipe result exists, so a later live run cannot shape it. To run it, copy the
design below into `preregistration.realtask.json` as a dated entry of its `amendments` list (with
`"resultsExisted": false` for the recipe conditions), implement the condition in the harness, and only then spend a
budget on it.

## Why

The real-task experiment found that the *content* of a lesson matters: `memory-enabled`, whose `Tried:` line names the
file the working `write_file` call wrote, beat `memory-placebo`, which shows the same block without that path. Story
28.4 adds the next piece of content a lesson can carry: a short excerpt of what the working call *returned*, vouched for
by the host at capture (`AgentExperienceOptions.Recipe`, rendered as `tool(...) [returned: "…"]` on the `Worked:`
attempt only). The question is whether that excerpt helps beyond the path.

## Design

Everything not named here is `realtask@2` as registered: the task set, tools, verification, phases, repeats, sampling,
limits, primary metric (`tool_calls` to the first green `run_tests`, capped at 30), gate, exclusions, refusal and retry
rules.

- **New condition `memory-recipe`:** `memory-enabled` plus a recipe on `run_tests`. Its excerpt callback returns, for a
  call that exited 0, the fixed text `exit 0: all 6 tests OK` and nothing for any other call (so the excerpt says the
  working sequence went green, a known shape, never test output), and on `write_file` it returns the first line of the
  written content, cut to 120 characters (the import line the fix changed). Capture's sanitizer and the default
  200-character bound apply; the injection allowlists `RecipeExcerpts = ["run_tests", "write_file"]`.
- **Primary comparison:** `memory-recipe` against `memory-enabled`, with the registered gate's four terms (mean
  `tool_calls` lower, exact one-sided sign test over services at alpha 0.05, success rate not lower, refused writes not
  higher). It answers "does the vouched excerpt add to the path".
- **Secondary, never gated:** `memory-recipe` against `memory-disabled`, reported with the same statistics.
- **Order:** the recipe condition joins the registered condition order after `memory-enabled`, under the same
  per-service shuffle.
- **Learning phase:** the learning runs are captured with the recipe on (so the stored records carry excerpts); the
  existing conditions render from the same records without the excerpt allowlist, which renders them byte for byte as
  before (a test pins this), so adding the arm does not change the other arms' blocks.

## Expected outcome and how it can fail

The honest prior is "no demonstrated benefit": the path already names the fix, and the excerpt mostly restates it. A
benefit would show as fewer `read_file` calls before the first `write_file`. A harm would show as more refused writes
(the model copying the excerpt's import line into the wrong file). Either result is reported as it is.

## How to run it, once registered

`dotnet run --project experiments/AgentExperience.LiveReuse -c Release -- --experiment realtask` with the provider's
variables, as for every real-task run (see README.md); the offline `--scripted` mode must cover the new condition before
the first live run.

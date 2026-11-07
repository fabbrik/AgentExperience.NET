# Dogfood: the library on this repository's own coding sessions

Every result so far comes from a lab experiment or a replay. This folder holds what it takes to judge the library on
real work instead: the [Claude Code recipe](../docs/guide/claude-code.md) running on the sessions that build this
repository, checked by this repository's own tests, measured by the library's own holdout.

**Status: not switched on. No session has run, so there is no result yet.** Switching it on is a person's step, by
design: it installs hooks into local Claude Code settings and approves the command `aexp` may run.

| File | What it is |
| --- | --- |
| [`aexp.config.json`](aexp.config.json) | The config, with a comment on every choice. Copied to `.aexp/config.json`, which is gitignored. |
| [`verify.sh`](verify.sh) | The verifier: `dotnet build`, then every Docker-free test project from [CONTRIBUTING.md](../CONTRIBUTING.md#development) with `--no-build`. Exit 0 only if all pass. A test keeps its list equal to CONTRIBUTING's. |
| [`preregistration.json`](preregistration.json) | The design, written before any session ran: unit, outcome, arms, when to read the result, what is excluded, the limits. |

## What it measures

- **Arms.** The holdout withholds lessons (and failure hints) from half the sessions, drawn once per Claude Code session.
- **Unit.** A session is what is randomized. A run is one turn of a session that changed something; the `Stop` hook
  runs `verify.sh` on it. Runs of one session share its arm, so they are clustered; the estimate counts runs and treats
  them as independent. `aexp` cannot count sessions. The pre-registration says how that limits the reading.
- **Outcome.** The verifier's exit code at the end of the run: 0 is verified, anything else failed.
- **Reading.** Once, when both arms first have 20 verified runs. A second look at 50 per arm, stated as a second look.

## Switching it on (a person's steps)

Do these yourself, in a terminal, not through an agent. Until a preview ships `aexp benefit --markdown`, install the
tool from this checkout instead of the first command below:

```bash
dotnet pack src/AgentExperience.Cli -o artifacts/aexp -p:VersionSuffix=dogfood.1
dotnet tool install --global AgentExperience.Cli --version 0.1.0-dogfood.1 --add-source artifacts/aexp
```

Setup, three commands, from the repository root:

```bash
dotnet tool install --global AgentExperience.Cli --prerelease   # puts aexp on PATH, where the hooks call it
mkdir -p .aexp && cp dogfood/aexp.config.json .aexp/config.json  # setup keeps an existing config
aexp setup claude-code                                           # hooks into .claude/settings.json (gitignored here)
```

Run `aexp setup claude-code --print` first to see what it would write. Then:

1. **Review the verifier.** Read `.aexp/config.json` and `dogfood/verify.sh`. `cmp dogfood/aexp.config.json
   .aexp/config.json` must print nothing: the copy is the reviewed file.
2. **Approve it:** `aexp trust`. It prints the command and the program it resolves to. Never let an agent run it.
3. **Protect it.** Add the deny rules `aexp setup claude-code` printed to `~/.claude/settings.json`, plus one for the
   verifier script, which lives in the repository where the agent works:

   ```json
   "Edit(//**/dogfood/verify.sh)", "Edit(//**/dogfood/aexp.config.json)"
   ```

   These are Claude Code's own checks, not an operating-system boundary: a Bash command can still write those files.
   Every such change shows in the pull request's diff. `aexp trust` covers the config's bytes, not the script's.
4. **Work as usual.** Run one dogfood session at a time: two `Stop` hooks building the same checkout at once can
   fail each other's build.

A `Stop` that took longer than 540 s, or ran while the config was untrusted, stores the run without a verdict and says
so; `aexp finalize --run <id>` finishes it later. When this was written the script took about two minutes after a
warm build.

## Reading the result

```bash
aexp benefit                                            # counts per arm and the verdict, any time
aexp benefit --markdown dogfood/results-2027-01-15.md   # the results page, on the day of a reading
```

The page has the counts per arm, the rates, the difference with its 95% interval, the verdict, the pending and
unverified runs, the reading rules, and what the page can and cannot say. Commit it as written, and update the
README's [Real work](../README.md#real-work) section with its verdict, whichever way it comes out. `aexp lessons` shows
each lesson's own comparison.

Read only at the pre-registered points. `aexp benefit` prints the verdict with the counts, so you will see it before a
reading point; that must not move the reading. To count sessions roughly, keep copies of `.aexp/hook.log` (it names
each session's runs and keeps about the last 512 KB); a count from it is approximate and must be labelled so.

## Stopping

- **For a while:** remove the `aexp hook` entries from `.claude/settings.json`. The store keeps what it has.
- **For good:** remove the hooks, delete `.aexp/`, remove the deny rules, and remove the approval from `trusted.json`
  in your user config directory (`~/.config/aexp/`). Write the results page first if a reading point was reached.
- **Changing the design** (the config, the verifier, the rate) after sessions ran: add an amendment to
  `preregistration.json` with the date and why, before the next session.

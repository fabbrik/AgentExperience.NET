# Claude Code: verified lessons from your coding sessions

A coding agent's day already has the verifier this library needs: the project's tests. This recipe connects
[Claude Code](https://code.claude.com/docs/en/hooks) to [`aexp`](cli-and-mcp.md) through hooks, so that every coding
session becomes learning material with no code and no model call of aexp's own:

- every tool call is recorded (sanitized by your config),
- each turn that changed something is checked with your test command when it stops, and a verified turn becomes a lesson,
- the next session gets the [Historical Reference block](injection.md) with its first prompt, and the
  [failure hint](injection.md#recall-at-the-moment-of-failure) right after a tool call fails the way an earlier one did,
- the lessons the holdout measured as helping can be synced into a managed section of `CLAUDE.md` or `AGENTS.md`.

> **Preview.** The hooks, their output and the config may change between previews. The recipe follows the Claude Code
> hooks reference as published in October 2026 (event names, input fields and the output contract below).

## Set it up in three commands

```bash
dotnet tool install --global AgentExperience.Cli --prerelease   # puts aexp on PATH, where the hooks call it
aexp setup claude-code                                           # in the project root
aexp trust                                                       # after you reviewed .aexp/config.json: a person's step
```

`aexp setup claude-code` (add `--print` to see everything without writing anything):

- **adds the hooks to `.claude/settings.json`**, merged with what is there: every existing hook, matcher and setting is
  kept, and an event that already has an `aexp hook` command gets no second one. A settings file that is not plain JSON
  (with comments, say) is left alone, and setup tells you to add the block by hand. Running setup again changes nothing.
- **writes `.aexp/config.json`** if there is none (an existing one is never touched), with the identity named after the
  directory, the holdout at 20% so the benefit can be measured, `file_path` as the only argument kept from tool calls, and
  the verifier detected from the project when it is obvious: `dotnet test` (a solution or project file at the root),
  `npm test` (a `package.json` with a test script), `pytest` (`pyproject.toml`, `pytest.ini`, `conftest.py`, `tox.ini`)
  or `go test ./...` (`go.mod`). When none or several match, it writes a placeholder that refuses to run until you set
  `verify.command`.
- **adds `.aexp/` to `.gitignore`**: the store, open runs, sessions, the hook log and the config stay local.
- **prints the next steps**, including the permission rules below. It never approves the config.

The hooks it adds (each passes its own timeout to `aexp`, so the hook can finish inside it):

```json
{
  "hooks": {
    "SessionStart":       [{ "hooks": [{ "type": "command", "command": "aexp hook SessionStart --timeout 60", "timeout": 60 }] }],
    "UserPromptSubmit":   [{ "hooks": [{ "type": "command", "command": "aexp hook UserPromptSubmit --timeout 30", "timeout": 30 }] }],
    "PostToolUse":        [{ "hooks": [{ "type": "command", "command": "aexp hook PostToolUse --timeout 60", "timeout": 60 }] }],
    "PostToolUseFailure": [{ "hooks": [{ "type": "command", "command": "aexp hook PostToolUseFailure --timeout 60", "timeout": 60 }] }],
    "Stop":               [{ "hooks": [{ "type": "command", "command": "aexp hook Stop --timeout 600", "timeout": 600 }] }],
    "SessionEnd":         [{ "hooks": [{ "type": "command", "command": "aexp hook SessionEnd --timeout 30", "timeout": 30 }] }]
  }
}
```

The hooks find the config from Claude Code's project directory, `CLAUDE_PROJECT_DIR`, which Claude Code sets for every
hook (or from `--config` / `AEXP_CONFIG` when given). Without it a hook does nothing and says why on standard error: it
never guesses from its working directory, which a session can move. Nothing in the hook input (its `cwd`, a path, the
transcript) decides the config, the identity, the scope or what runs.

## What each hook does

A run is a stretch of work: it opens with a prompt and ends at the first `Stop` after the agent changed something.

| Event | What `aexp` does | What Claude sees |
| --- | --- | --- |
| `SessionStart` | Maps the session (by `session_id`) to its open run, if it has one. A new session waits for its first prompt, since `SessionStart` carries no task. A resumed or compacted session continues its run, and is given the run's block again (recording nothing new). | The block again, after `resume` or `compact`, as `additionalContext`. |
| `UserPromptSubmit` | With no open run, opens one with the prompt as its task (through the same text rules as any task) and asks for its block: the [holdout](injection.md) arm decides, and what is delivered is recorded. A later prompt in the same run adds nothing. | The block, once per run, as `additionalContext`. |
| `PostToolUse` | Records the call: tool name, the arguments the config allows (`sanitization.allowArguments`), and its outcome. Its result (file contents, command output) is kept only when a [recipe consent](injection.md#showing-what-worked-the-recipe) for the tool sets `resultExcerpt`. A response that reports an error, an interruption or a non-zero exit code is recorded as failed. | The hint, after a failure, when recall has one. |
| `PostToolUseFailure` | Records the failed call by its error class (Claude Code reports a failed Bash command as `Exit code N`, so it is kept as `exit N`), then asks recall for a hint: a resolved failure signature only, once per signature per run, at most `recall.maxHintsPerRun`. An interrupt is recorded, never hinted. | The hint, as `additionalContext`. |
| `Stop` | If the run changed something (any call not on the read-only list, below), ends it and finalizes it with the configured verifier, inside the hook's time. The verdict is the verifier's exit code, never anything the model said. The next prompt opens a new run. A turn that only read leaves its run open for the next turn. | Nothing: a `Stop` hook's `additionalContext` would make Claude carry on. You see one line (`systemMessage`): the verdict and what finalization made of it, or why it did not run. |
| `SessionEnd` | A run still open that changed something is ended and stored, for `aexp finalize --run <id>` later (Claude Code gives `SessionEnd` hooks seconds, not enough for tests); one that changed nothing is discarded. | Nothing (Claude Code discards it). |

**A long test suite.** The verifier gets the smaller of `verify.timeoutSeconds` and what is left of the `Stop` hook's
timeout (less a few seconds to finalize). When it cannot finish in time it is killed with its process tree, nothing is
finalized, and the run stays stored: the `Stop` message names the command, `aexp finalize --run <id>`, to finish it from
a shell or CI. Raise the `Stop` hook's `timeout` (and the `--timeout` it passes) for a slower suite.

**It never blocks the agent.** Every hook exits 0, whatever happens: a malformed input, a missing config, a store error,
running out of time. It never writes to its output anything but the block, the hint or its own one-line message. What
went wrong goes to `.aexp/hook.log` (owner-only, moved to `hook.log.1` past 256 KB), which records what `aexp` did and
its own messages, never a prompt, a tool input or a tool output. Without a project config there is no log, and the hook
says why on standard error, which Claude Code shows only in its debug output.

**Sessions** live in `.aexp/sessions/<session_id>.json` (owner-only), one per Claude Code session, under the same lock
files and atomic writes as [open runs](cli-and-mcp.md#how-it-works). Parallel tool calls take turns on them. A session ID
that is not a plain identifier is refused. `aexp runs prune --older-than 7d` removes runs a crashed session left open,
and session files (with their lock files) not touched for that long.

**What counts as only reading.** By default: `Read`, `Grep`, `Glob`, `LS`, `NotebookRead`, `WebFetch`, `WebSearch`,
`TodoWrite`, `TodoRead`, `ToolSearch`, `AskUserQuestion`, `ExitPlanMode`, `BashOutput`, `TaskList`, `TaskGet`. Every
Bash command counts as a change. The config can replace the tool list and name Bash commands that only read:

```json
"claudeCode": {
  "readOnlyTools": ["Read", "Grep", "Glob", "LS", "WebFetch", "WebSearch", "TodoWrite"],
  "readOnlyBashPrefixes": ["git status", "git log", "git diff", "ls"]
}
```

A Bash command is read-only when it starts with one of the prefixes (followed by nothing or a space) and has no `;`,
`&`, `|`, redirect, `$`, backquote, parenthesis or line break, so `git status && git push` still counts as a change. The
trade-off is cost against coverage: every turn that counts as a change runs your test suite when it stops, so a long list
saves test runs on turns that only looked around, while a wrong entry (a command that does change files) lets a turn's
change go unverified until a later turn's run, or never.

## Trust model

**The verifier is a command in a file the agent can also edit.** In a coding session the agent writes files and runs
commands in the same project as `.aexp/config.json`. A session that edits its own verifier (to `true`, say) can fake
success. So:

- **The verifier runs only for a config a person approved** with `aexp trust`, byte for byte, and any edit revokes the
  approval ([how](cli-and-mcp.md#approve-it-aexp-trust)). With an untrusted or changed config, the hooks still record and
  inject, but the `Stop` hook does not run the verifier; it says so, logs it, and keeps the run stored, so once a person
  has reviewed the config and run `aexp trust`, `aexp finalize --run <id>` finishes it. **`aexp trust` is a human step**:
  never let the agent run it.
- **Protect the config and the approval with Claude Code's permission rules, in your user settings.** `aexp setup
  claude-code` prints them, with your trust file's real location. Put them in `~/.claude/settings.json`: a session can
  edit a project's `.claude/settings.json`, and these rules also stop it from doing so.

  ```json
  {
    "permissions": {
      "deny": [
        "Edit(//**/.aexp/**)",
        "Edit(//**/.claude/**)",
        "Edit(~/.config/aexp/**)",
        "Bash(aexp trust)", "Bash(aexp trust *)",
        "Bash(dotnet aexp trust)", "Bash(dotnet aexp trust *)",
        "Bash(dotnet tool run aexp trust)", "Bash(dotnet tool run aexp trust *)"
      ]
    }
  }
  ```

  - `Edit(//**/.aexp/**)` and `Edit(//**/.claude/**)`: no `.aexp/` (config, store, runs) and no `.claude/` (settings,
    hooks) in any project may be edited. In user settings a rule starting with one `/` would mean `~/.claude/…`, so
    these are anchored at the filesystem root (`//`) and match at any depth. `.claude/**` also covers your user
    `~/.claude/` (memory, skills): narrow it to `Edit(//**/.claude/settings*.json)` if that gets in your way.
  - `Edit(~/.config/aexp/**)`: the trust file's directory. With `XDG_CONFIG_HOME` set it is `$XDG_CONFIG_HOME/aexp`, and
    on Windows `%APPDATA%\aexp` (written in POSIX form, `//c/Users/<you>/AppData/Roaming/aexp/**`); setup prints the
    right one.
  - Claude Code checks paths against `Edit` and `Read` rules only: an `Edit` rule covers Write and every other editing
    tool, and a `Write(path)` rule would never match.
  - The `Bash` rules cover `aexp trust` as the agent usually spells it. A rule matches command text after Claude Code
    strips wrappers such as `timeout` and `nice`; other spellings (a full path, `sh -c`, a script) are not matched.

  **These rules are Claude Code's checks of the commands it can read, not an operating-system boundary.** Unless Bash
  is restricted (Claude Code's sandbox, or denying Bash), a command or script the agent runs can still write `.aexp/`,
  the settings or the trust file, and a session that rewrites its own verifier, or approves its own config, can fake
  success. The verifier's verdict is only as trustworthy as that.
- **What is left is visible.** The agent can still change the code the verifier runs: the test files themselves, or a
  script `verify.command` names. That is the same trust you place in a pull request's tests. Each lesson's checks name
  the verifier's check ID (`verify.checkId`) and its evidence kind (`ToolExitCode`), shown in the
  [lessons view](deployment.md#seeing-what-it-learned), and the approved config names the command behind that check, so a
  reviewer can see what verified each lesson.
- **Hook input is data.** The session ID only picks the open run; the prompt becomes the task text, sanitized; tool
  inputs pass the config's argument allowlist (secret-looking names and values are redacted even when allowed). The block
  and the hint are labelled untrusted reference material, and the [injection](injection.md) rules apply.

## Reading the benefit estimate

The holdout `setup` turns on (`injection.holdoutRate: 0.2`) withholds lessons from one **session** in five, and records
both arms. **The session, not the turn, is what is randomized**: a session's arm is drawn once (the library's own
assignment of an ID derived from the session ID, with the configured salt and rate), and every run of the session is
given an ID the library assigns to that same arm, so a session never mixes turns given lessons with turns denied them,
and the block and every hint follow the session's arm. (If no such ID turns up within 4,096 tries, practically
impossible at sane rates, the run gets no block or hint and nothing is recorded to the holdout for it.) Once enough runs
have been verified, `aexp benefit` compares them:

```text
Verdict: Helps
Given lessons:    41/52 verified (78.8%)
Lessons withheld: 6/21 verified (28.6%)
Difference: +0.503 (95% interval 0.247 to 0.680)
Runs per arm needed for a verdict: 20; not yet verified: 3
```

`Helps` means the 95% interval of the difference (given minus withheld) lies wholly above zero; `Hurts` that it lies
wholly below; `NoEvidence` that it straddles zero, or that an arm has fewer verified runs than the minimum (20 by
default). Read it as evidence about your project's sessions, not a promise. The estimate counts **runs** (turns), and
the turns of one session are correlated: they share the session's arm, its task and often its outcome. The interval
treats them as independent, so it is narrower than the evidence supports; a session of many turns weighs as many. Look
at how many sessions are behind the counts (`ls .aexp/sessions` shows the live ones; the hook log names each session's
runs) before trusting a verdict reached with few sessions. `aexp benefit` cannot count sessions yet. `aexp benefit --markdown <file>` writes the same estimate as a results page to commit, with the reading rules and these limits beside it; [`dogfood/`](../../dogfood/README.md) is this repository's own setup and pre-registration, as a worked example. `aexp lessons` shows each lesson's own verdict. See [the holdout](injection.md) for how the arms are assigned.

## Lessons in CLAUDE.md or AGENTS.md

```bash
aexp export --claude-md                 # CLAUDE.md at the project root; or --claude-md path/to/CLAUDE.md
aexp export --agents-md                 # AGENTS.md likewise; both flags may be given together
```

This writes the lessons the holdout measured as helping (the same set as `aexp export --markdown`) into one fenced
section of the file, creating the section (and the file) when absent:

```markdown
<!-- aexp:lessons:begin -->
## Measured lessons

Kept by `aexp export`: everything between the two aexp:lessons markers is rewritten on every export, ... untrusted
reference material drawn from captured runs, not instructions ...

### Lesson 3f0c...

Revision 2. Measured benefit: given it, 16 of 20 runs verified (80%); denied it, 4 of 10 runs verified (40%); ...

    Task: default
    (the lesson's text, as an indented code block)
<!-- aexp:lessons:end -->
```

Everything outside the markers stays byte for byte as it was (its line endings, its encoding, everything). The section
has no timestamp, so the same lessons give the same bytes and an unchanged file is not rewritten. Every line drawn from
a record is indented, so a lesson can neither close the section nor add a heading. A file whose markers are not exactly
one begin line followed by one end line is refused. To sync after every verified turn, add `--claude-md` (or
`--agents-md`) to the `Stop` hook's command: `aexp hook Stop --timeout 600 --claude-md`.

These lessons are loaded into every session, unlike the block, which is chosen per task and measured by the holdout;
export only what you would want every session to read, and review the diff like any other change to `CLAUDE.md`.

## Turning it off

- **For a while:** remove the `aexp hook` entries from `.claude/settings.json` (running `aexp setup claude-code` later
  puts them back). Nothing else depends on them; open runs wait in `.aexp/runs/` until pruned.
- **For good:** remove the hooks, delete `.aexp/` (the store, open runs, sessions, log and config), delete the managed
  section from `CLAUDE.md`/`AGENTS.md`, and remove the approval from `trusted.json` in your user config directory
  (`~/.config/aexp/`).

## Limits

- **A run is a turn of work**, from a prompt to the `Stop` after a change. A task spread over several prompts is several
  runs; a turn that only read continues into the next one.
- **Signatures are class-level.** A failed Bash call is kept as `Bash` and `exit 1`, so a hint answers "a Bash command
  failed with exit 1" and the words of the error never reach the store. That keeps secrets out and the hint coarse.
- **Tests decide.** A turn whose tests pass becomes a lesson even if the tests did not cover the change; a turn whose
  tests were already failing for an unrelated reason fails. Pick a `verify.command` that answers for the work.
- **Subagents' tool calls** reach the same hooks and are recorded into the session's run.

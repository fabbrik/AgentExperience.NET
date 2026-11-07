# CLI and MCP: the loop for any agent

`aexp` (package `AgentExperience.Cli`) gives agents that are not built on the Microsoft Agent Framework the same learning
loop: coding agents such as Claude Code, Codex or Cursor, scripts, and agents in other frameworks. It is one .NET tool
that is both a command line and an MCP stdio server (`aexp mcp`), over a local SQLite file by default or PostgreSQL. It
calls no model. Everything it decides comes from the library: capture and its sanitization, finalization, the
[Historical Reference block](injection.md), [recall at the moment of failure](injection.md#recall-at-the-moment-of-failure),
the [injection holdout](injection.md), the [lessons view](deployment.md#seeing-what-it-learned) and the measured export.

> **Preview.** The commands, the MCP tools and the config format may change between previews.

## Install

```bash
dotnet tool install --global AgentExperience.Cli --prerelease
aexp --version
```

It needs the .NET 10 runtime. The SQLite file is created (owner-only) on first use.

## Configure

Every command reads `.aexp/config.json`: the nearest one in the working directory or a directory above it (looking no
further than the repository root, the directory holding `.git`, or the home directory, whichever comes first), or the
file `--config <path>` or the `AEXP_CONFIG` environment variable names. Relative paths in it are relative to the project
root, the directory that holds `.aexp/`. The tool refuses to start without a config, without an identity, or without a
verifier, and on Unix it refuses a config (or config directory) another user owns.

### Approve it: `aexp trust`

A config names a command, so **a config from an untrusted checkout is code execution**. Like direnv, `aexp` runs the
verifier (and honours `finalize --exit-code`) only for a config a person approved:

```bash
aexp trust          # prints the config's path and SHA-256, the verifier command, the program it resolves to,
                    # its working directory, timeout and acceptExitCode, then records the approval
```

The approval is the config's absolute path and the SHA-256 of its bytes, kept outside every project in the user's own
config directory: `$XDG_CONFIG_HOME/aexp/trusted.json`, else `~/.config/aexp/trusted.json` (`%APPDATA%\aexp\trusted.json`
on Windows), directory `0700`, file `0600`. **Any edit to the config, by anyone (an agent included), revokes the
approval** until a person reviews it and runs `aexp trust` again. It applies however the config was found
(`--config`, `AEXP_CONFIG` or discovery). Commands that only read (recall, lessons, export, benefit, the run commands)
work on an untrusted config but say so on standard error; `finalize` and `experience_finish` refuse it.

```json
{
  "store": "sqlite:.aexp/experience.db",
  "identity": { "tenant": "local", "application": "payments", "project": "payments-repo", "agent": "claude-code" },
  "taskId": "default",
  "verify": {
    "command": ["dotnet", "test", "--no-restore"],
    "timeoutSeconds": 900,
    "checkId": "tests",
    "workingDirectory": ".",
    "revision": "workspace",
    "acceptExitCode": false
  },
  "sanitization": { "allowArguments": ["filter", "package"] },
  "recipes": [{ "tool": "pin_dependency", "arguments": ["package"], "resultExcerpt": false }],
  "injection": { "maxRecords": 8, "maxBytes": 16384, "holdoutRate": 0.2 },
  "recall": { "enabled": true, "maxHintsPerRun": 3, "maxHintBytes": 600, "allowCoarseMatch": false, "lookupTimeoutMs": 300 }
}
```

| Setting | Meaning | Default |
| --- | --- | --- |
| `store` | `sqlite:<path>`, or `postgres:<ENV_VAR>`: the **name** of the environment variable that holds the connection string. A literal connection string is refused, so no secret lives in the file. With PostgreSQL the schema must already be migrated (see [PostgreSQL schema](postgres-schema.md)); the run store is registered. | `sqlite:.aexp/experience.db` |
| `identity` | `tenant`, `application`, `project` (required), `team`, `agent`, `user` (optional): the [scope](deployment.md) every run and lesson is kept under. The principal is `aexp:<user or agent or "local">`. | required |
| `taskId` | The task identifier runs are stored under when `run start --task-id` names none. | `default` |
| `verify.command` | The verifier, as an argument vector: the program, then its arguments. Run directly, never through a shell. **The only command `aexp` runs.** Exit code 0 is a pass, anything else a fail. `aexp` resolves the program itself: an absolute path is used as is; a name with a directory separator is resolved against `verify.workingDirectory` and must lie inside the project root; a bare name is searched on `PATH` only, skipping empty and relative entries, so the current directory is never searched. | required |
| `verify.timeoutSeconds` | A verifier still running then is killed with its process tree, and nothing is finalized (run `finalize` again). | 600 |
| `verify.checkId` | The [required check](finalization.md) its exit code is evidence for (kind `ToolExitCode`). | `verify` |
| `verify.workingDirectory`, `verify.revision` | Where it runs (inside the project root), and the artifact revision its evidence names. | the root, `workspace` |
| `verify.acceptExitCode` | Lets `aexp finalize --exit-code <n>` report a verdict the host obtained itself (for example a CI job that already ran the tests). Off, so an agent cannot hand in its own verdict. | `false` |
| `sanitization.allowArguments` | Tool argument names capture keeps. Everything else is dropped; secret-looking names and values are redacted even when allowed ([Capture](capture.md)). | none |
| `recipes` | [Recipe consents](injection.md#showing-what-worked-the-recipe): the arguments a lesson may show for a tool, and whether its result may be shown as an excerpt. | none |
| `injection` | The block's record and byte limits, and the [holdout](injection.md) rate (and salt). | the library's |
| `recall` | Recall at the moment of failure, its bounds, and the lookup timeout. Recall only ever answers when the agent asks (`recall --failure`, `experience_failure_hint`). | on, the library's bounds |
| `claudeCode` | For the [Claude Code hooks](claude-code.md#what-each-hook-does): `readOnlyTools` (replaces the default list) and `readOnlyBashPrefixes`, the calls that do not make a turn worth verifying. | the default list; no Bash prefix |

Unknown settings are refused, so a misspelt key never silently turns something off.

## Use it over MCP

Register the server with your agent, run in the project (it finds the config from its working directory):

```bash
claude mcp add aexp -- aexp mcp
```

| Tool | Arguments | What it does |
| --- | --- | --- |
| `experience_recall` | `task` | Starts the session's run with the task text and returns the block for it, or says no lesson applies. Call it once, at the start. |
| `experience_failure_hint` | `tool`, `error`, optional `task` | Right after a tool call fails: what worked after the same failure in an earlier verified run, if anything did. |
| `experience_record_call` | `tool`, optional `arguments` (an object), `result` or `error`, optional `task` | Records one tool call of the run. Sanitized before it is kept. |
| `experience_finish` | optional `status` (`completed`, `failed`, `cancelled`) | Ends the run, runs the configured verifier, and finalizes: a verified run becomes a lesson. The model only asks; it cannot choose the check or its verdict. |
| `experience_lessons` | none | The lessons this project has learned, as a table and their text. |

A session holds one open run at a time, started lazily by the first tool that needs it (with the task text
`experience_recall` gave, or a `task` argument). After `experience_finish` the next call starts a new run. Every output
is bounded (16 KB); the descriptions of the tools that return lessons say their output is untrusted reference material.
An error is an MCP tool error with a short, fixed message written by `aexp`; the detail (paths, the operating system's
words) goes to the server's standard error, for the operator only. The server keeps serving.

**When finishing fails.** `experience_finish` ends the run once. If finalizing then fails (an untrusted config, a
verifier timeout, a store error, a cancelled call), the run stays *ended, not finalized*, the error names its run ID and
the exact command `aexp finalize --run <id>`, and calling `experience_finish` again only finalizes. **MCP clients time
tool calls out** (often after a minute or so) and cancel the call, which also stops the verifier: keep
`verify.timeoutSeconds` below your client's tool timeout, or let a slow check run from a shell or CI with
`aexp finalize --run <id>`.

## Use it from a shell

```bash
run=$(aexp run start --task "Fix the failing payments build")
aexp recall --run "$run"                                   # the block for the run's task; empty when none applies
aexp run call --run "$run" --tool run_tests --args '{"filter":"payments"}' --error "exit 2: error CS0246"
aexp recall --run "$run" --failure --tool run_tests --error "exit 2: error CS0246"   # the hint, if any
aexp run call --run "$run" --tool add_reference --args '{"package":"Ledger"}' --result "added"
aexp run call --run "$run" --tool run_tests --result "all passed"
aexp run end --run "$run"                                  # [--status completed|failed|cancelled]
aexp finalize --run "$run"                                 # runs the verifier; any later process may do this
```

| Command | Prints |
| --- | --- |
| `aexp run start --task <text> [--task-id <id>]` | The run ID. |
| `aexp run call --run <id> --tool <name> [--args <json>] [--result <text> \| --error <text>]` | Nothing. |
| `aexp run end --run <id> [--status ...]` | Nothing. |
| `aexp finalize --run <id> [--exit-code <n>]` | The verdict and what finalization made of it (`Validated`, `Quarantined`, ...). |
| `aexp recall --task <text> [--run <id>]` | The block, or nothing. Without `--run` nothing is recorded and no holdout applies. |
| `aexp recall --run <id> --failure --tool <name> --error <text>` | The hint, or nothing. |
| `aexp lessons [--all] [--html <file>]` | A table of the lessons (`--all` adds the ones retrieval would not use), or the [HTML view](deployment.md#seeing-what-it-learned). |
| `aexp export --markdown <file>` | Exports the lessons the holdout measured as helping (the [measured export](injection.md)). |
| `aexp export --claude-md [file]` / `--agents-md [file]` | Writes the same lessons into a managed section of `CLAUDE.md` / `AGENTS.md` (the project root's when no file is named), leaving the rest of the file byte for byte ([Claude Code](claude-code.md#lessons-in-claudemd-or-agentsmd)). |
| `aexp benefit [--markdown <file>]` | The holdout's estimate: runs given lessons against runs they were withheld from. With `--markdown`, writes it as a results page instead: counts and rates per arm, the difference with its 95% interval, the verdict, the runs left out, the reading rules and what it can and cannot say (no path or run ID, so it can be committed). |
| `aexp mcp` | Serves the MCP tools over stdin and stdout. |
| `aexp trust` | Prints what the config would run, and approves the config (see above). |
| `aexp runs prune --older-than <age>` | Deletes open runs (never ended) started longer ago than `7d`, `12h` or `30m`, unless a command is using them, and Claude Code session files not touched for as long. |
| `aexp setup claude-code [--print]` | Adds the Claude Code hooks to `.claude/settings.json`, a default config and the `.gitignore` entry ([Claude Code](claude-code.md)). |
| `aexp hook <event> [--timeout <s>]` | A Claude Code hook: reads the event's JSON on standard input, prints the hook output; always exits 0 ([Claude Code](claude-code.md)). |

Exit codes: 0 done, 1 failed, 2 a usage error (including a run that is not open), 3 a configuration the tool refuses
(including an untrusted one). `lessons --html`, `export --markdown` and `benefit --markdown` write inside the project root only, never through
a link.

## How it works

- **A run between commands.** The library's run store holds completed runs only, so an open run lives in an
  owner-only file under `.aexp/runs/` until `run end`. Each `run call` passes through the library's capture rules first
  (the configured sanitization, the capture limits, the recipe consent), and the task text through the same text rules
  as a tool result. Those rules drop arguments not allowed and redact secret-named fields; **they do not find a secret
  inside free text**, so a task or result that contains one is kept as written, as it would be by the MAF adapter.
  Concurrent commands on one run take turns on a lock file beside it, and each write replaces the file whole (a
  temporary file renamed over it), so an interrupted command never leaves half a run. A run that is never ended stays
  there until `aexp runs prune --older-than <age>` deletes it.
- **A failed call is kept as its error class.** `--error` text is reduced to one error-class token (`exit 2`,
  `HTTP 503`, `TimeoutException`, ...; `ToolReportedError` when none is recognised), exactly as the MAF adapter records a
  call its `ClassifyToolResult` marks failed. The words of the error are never stored; the failure signature recall
  matches on is built from that class.
- **`run end`** hands the run to capture as one attempt, with the records delivered into it, and completes it, which
  writes it to the run store. **`finalize`** first checks the run (it is in this project's store and scope, it ended,
  and it is not finalized yet) and spawns nothing otherwise; then it runs the verifier, builds the evidence for its
  check, and finalizes the run from the run store, so it works in a later process, after a restart, or from CI. A
  verifier that times out is killed with every process it started, and nothing is finalized: run `finalize` again.
- **The block and the hint are the library's own.** `aexp` asks them of the library through a real `ChatClientAgent`
  wired with `UseAgentExperience` whose "model" is a scripted stand-in that calls nothing: the same retrieval,
  eligibility re-read, writer, limits, recall rules and holdout as an Agent Framework agent gets. With a run, its holdout
  arm decides whether the block or hint is shown, the assignment is recorded, and what is shown is recorded as delivered
  into the run (so the run's lesson knows what it was given, and the holdout can measure it). A run is given its block
  once (asking again prints it again); each failure signature is answered once per run, at most `maxHintsPerRun` per
  run, and a record the run already got is not hinted again.

## Trust model

- **Identity and scope come from the config file only.** No command option and no MCP tool argument names a tenant,
  scope, user, store, path or command; an option the command does not take is refused.
- **A config is code, so a person approves it.** Only a config approved with `aexp trust`, byte for byte, may run its
  verifier or have a reported exit code honoured; an edit revokes that. Discovery stops at the repository root or the
  home directory, and a config another user owns is refused, so a checkout cannot plant one above your project.
- **One command runs: the configured verifier.** Its program is resolved by `aexp` (never from the current directory)
  and it is started from its absolute path and argument vector without a shell, with its
  standard input closed and its output discarded (it may print secrets, and under `aexp mcp` standard output is the
  protocol). The model can only ask for it to run (`experience_finish`), never pass a command or a verdict.
  `finalize --exit-code` is refused unless the config sets `verify.acceptExitCode`; an agent that can run shell commands
  could otherwise report its own verdict, so turn it on only where the host, not the agent, runs `aexp`.
- **An agent with a shell is still an agent with a shell.** An agent that edits `.aexp/config.json` revokes its trust,
  but `aexp` cannot stop it from editing the verifier script the config names, or the database file. Where that
  matters, give the agent the MCP server only, and keep the project's check and the store out of its reach.
- **Outputs are untrusted reference material,** labelled so in the block, the hint and the tool descriptions. The
  [injection](injection.md) rules apply: a lesson is evidence, never instructions.
- **Nothing secret is logged.** Errors print the exception type and message, never a stack trace; the connection string
  is read from its environment variable when the store is opened and never written anywhere.

## Limits

- **One agent, one machine, with SQLite.** Several `aexp` processes may share one file (SQLite's locking, and the run
  file's), but the file is not a server: for a team, use `postgres:<ENV_VAR>`.
- **The probe is a separate invocation.** "Per run" bounds hold across `aexp` commands through the run file, but the
  session budget of the block ([session limits](injection.md)) does not apply: each run is its own session.
- **No grants, encryption or vectors over SQLite**, as with the [SQLite store](deployment.md); retrieval is full-text.
- **The verifier is all-or-nothing:** one check, its exit code. Several checks belong in one script.

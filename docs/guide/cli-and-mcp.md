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

Every command reads `.aexp/config.json`: the nearest one in the working directory or a directory above it, or the file
`--config <path>` or the `AEXP_CONFIG` environment variable names. Relative paths in it are relative to the project
root, the directory that holds `.aexp/`. The tool refuses to start without a config, without an identity, or without a
verifier.

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
| `verify.command` | The verifier, as an argument vector: the program, then its arguments. Run directly, never through a shell. **The only command `aexp` runs.** Exit code 0 is a pass, anything else a fail. | required |
| `verify.timeoutSeconds` | A verifier still running then is killed with its process tree, and nothing is finalized (run `finalize` again). | 600 |
| `verify.checkId` | The [required check](finalization.md) its exit code is evidence for (kind `ToolExitCode`). | `verify` |
| `verify.workingDirectory`, `verify.revision` | Where it runs, and the artifact revision its evidence names. | the root, `workspace` |
| `verify.acceptExitCode` | Lets `aexp finalize --exit-code <n>` report a verdict the host obtained itself (for example a CI job that already ran the tests). Off, so an agent cannot hand in its own verdict. | `false` |
| `sanitization.allowArguments` | Tool argument names capture keeps. Everything else is dropped; secret-looking names and values are redacted even when allowed ([Capture](capture.md)). | none |
| `recipes` | [Recipe consents](injection.md#showing-what-worked-the-recipe): the arguments a lesson may show for a tool, and whether its result may be shown as an excerpt. | none |
| `injection` | The block's record and byte limits, and the [holdout](injection.md) rate (and salt). | the library's |
| `recall` | Recall at the moment of failure, its bounds, and the lookup timeout. Recall only ever answers when the agent asks (`recall --failure`, `experience_failure_hint`). | on, the library's bounds |

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
An error is an MCP tool error with a short message; the server keeps serving.

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
| `aexp benefit` | The holdout's estimate: runs given lessons against runs they were withheld from. |
| `aexp mcp` | Serves the MCP tools over stdin and stdout. |

Exit codes: 0 done, 1 failed, 2 a usage error (including a run that is not open), 3 a configuration the tool refuses.

## How it works

- **A run between commands.** The library's run store holds completed runs only, so an open run lives in an
  owner-only file under `.aexp/runs/` until `run end`. Each `run call` passes through the library's capture rules first
  (the configured sanitization, the capture limits, the recipe consent), so only sanitized content is ever written there.
  Concurrent commands on one run take turns on the file.
- **A failed call is kept as its error class.** `--error` text is reduced to one error-class token (`exit 2`,
  `HTTP 503`, `TimeoutException`, ...; `ToolReportedError` when none is recognised), exactly as the MAF adapter records a
  call its `ClassifyToolResult` marks failed. The words of the error are never stored; the failure signature recall
  matches on is built from that class.
- **`run end`** hands the run to capture as one attempt, with the records delivered into it, and completes it, which
  writes it to the run store. **`finalize`** runs the verifier, builds the evidence for its check, and finalizes the run
  from the run store, so it works in a later process, after a restart, or from CI.
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
- **One command runs: the configured verifier.** It is started from its argument vector without a shell, with its
  standard input closed and its output discarded (it may print secrets, and under `aexp mcp` standard output is the
  protocol). The model can only ask for it to run (`experience_finish`), never pass a command or a verdict.
  `finalize --exit-code` is refused unless the config sets `verify.acceptExitCode`; an agent that can run shell commands
  could otherwise report its own verdict, so turn it on only where the host, not the agent, runs `aexp`.
- **An agent with a shell is still an agent with a shell.** `aexp` cannot stop an agent that can edit the project from
  editing `.aexp/config.json`, the verifier, or the database file. Where that matters, give the agent the MCP server
  only, and keep the config and the store out of its reach.
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

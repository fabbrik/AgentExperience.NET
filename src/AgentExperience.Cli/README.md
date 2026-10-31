# AgentExperience.Cli (`aexp`)

Memory of what *worked*, verified by your own check, for any agent: Claude Code, Codex, Cursor, a script, or an agent in
another framework. One .NET tool that is both a command line (`aexp`) and an MCP stdio server (`aexp mcp`), over a local
SQLite file by default or PostgreSQL. It drives the same library the Microsoft Agent Framework adapter uses, so the
lessons, the failure hints and the measurement are the library's own.

> **Preview — not production ready.** This is a `0.1.0-preview` package, and it claims no production readiness. Its
> commands and configuration may change between previews. Read
> [Known limits and documented boundaries](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/known-limits.md)
> before you rely on it.

```bash
dotnet tool install --global AgentExperience.Cli --prerelease
```

Then put a `.aexp/config.json` in your project:

```json
{
  "identity": { "tenant": "local", "application": "my-service", "project": "my-repo" },
  "verify": { "command": ["dotnet", "test"], "timeoutSeconds": 900, "checkId": "tests" }
}
```

`verify.command` is the only command `aexp` ever runs: an argument vector, never a shell line. Exit code 0 is a pass. A
run becomes a lesson only when that check passes.

A config names a command, so a person approves it before `aexp` runs it: review it, then run `aexp trust` in the
project. The approval is recorded (path and SHA-256) in your own user config directory, and any edit to the config
revokes it until it is approved again.

## As an MCP server

Register `aexp mcp` with your agent (for example `claude mcp add aexp -- aexp mcp`, run in the project). It offers five
tools: `experience_recall` (the lessons for a task, at its start), `experience_failure_hint` (what worked after the same
failure before), `experience_record_call` (each tool call), `experience_finish` (ends the run and asks for the configured
check), and `experience_lessons`. No tool takes identity, scope, a path or a command: those are the config file's. Lessons
and hints are labelled untrusted reference material.

## As a command line

```bash
run=$(aexp run start --task "Fix the failing payments build")
aexp recall --run "$run"                                   # the lessons for this task, if any
aexp run call --run "$run" --tool run_tests --error "exit 2: ..."
aexp recall --run "$run" --failure --tool run_tests --error "exit 2: ..."   # what worked after this failure
aexp run call --run "$run" --tool run_tests --result "all passed"
aexp run end --run "$run"
aexp finalize --run "$run"                                 # runs the configured check; may run in a later process
aexp lessons
```

## With Claude Code

```bash
aexp setup claude-code    # in the project root: hooks, a default config, the .gitignore entry
aexp trust                # after you reviewed .aexp/config.json: a person's step, never the agent's
```

The hooks record every tool call, check each turn that changed something with the configured test command when it
stops, and give the next session the lessons and, after a failure, the hint. `aexp export --claude-md` keeps the lessons
measured as helping in a managed section of `CLAUDE.md`. Guide:
[Claude Code](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/claude-code.md).

The full guide, the configuration reference and the trust model are in
[CLI and MCP](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/cli-and-mcp.md).

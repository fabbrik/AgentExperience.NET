# Contract 1.19: additions

This page lists what contract 1.19 adds to [contract 1.0](README.md) and the pages of 1.1 to 1.18
([1.1](README-1.1.md), [1.2](README-1.2.md), [1.3](README-1.3.md), [1.4](README-1.4.md), [1.5](README-1.5.md),
[1.6](README-1.6.md), [1.7](README-1.7.md), [1.8](README-1.8.md), [1.9](README-1.9.md), [1.10](README-1.10.md),
[1.11](README-1.11.md), [1.12](README-1.12.md), [1.13](README-1.13.md), [1.14](README-1.14.md), [1.15](README-1.15.md),
[1.16](README-1.16.md), [1.17](README-1.17.md) and [1.18](README-1.18.md)). Those pages are still normative and
unchanged, except where this page names the schema a port now validates against in place of another: a minor version
only adds files, this page among them. The contract's version is the one `lock.json` records (`contractVersion`), which
the .NET suite holds in `ContractFiles.ContractVersion`.

A port that passes contract 1.19 passes every 1.0 to 1.18 fixture and the fixtures below.

## The recipe excerpt on a tool call

.NET story 28.4 lets a host vouch, at capture, for a short excerpt of what a working tool call returned: the *recipe*
(the version that was pinned, the key that was set). It is stored on the call, in the payload, as one optional member:

```json
{ "toolCallId": "...", "sequenceNumber": 0, "toolName": "pin_dependency", "arguments": { "package": "lib" },
  "startedAt": "...", "duration": "00:00:00.8000000", "result": "ok", "error": null,
  "recipeExcerpt": "pinned lib to 2.4.1" }
```

- **Optional, omitted when absent.** Like the other optional payload members (`closedRoundId`, `origin`, ...), it is
  written only when set and never as `null`. A record without one is byte for byte what it was, so every earlier
  fixture is unchanged. Only a call whose `error` is `null` carries one.
- **The schema.** Contract 1.0's `schema/experience-record.v1.json` refuses any member a tool call does not list, and a
  published file never changes, so 1.19 adds [`schema/experience-record.v1-1.19.json`](schema/experience-record.v1-1.19.json):
  the same schema with `recipeExcerpt` (a string) added to `$defs.toolCall.properties`, and nothing else. **A 1.19 port
  validates records against it** in place of the 1.0 file. Every record that validates against the 1.0 schema validates
  against it.
- **Read and kept.** A port reads the member when present, keeps it on its tool call, and writes it back exactly as
  read (absent when absent), so a record a port reads and writes again is unchanged. A port is not asked to capture
  one: no finalize fixture carries one, and how a host vouches for an excerpt is each framework's own.
- **Signing stays .NET-only, and so does confirmation.** The .NET library signs claims version 4
  (`HMAC-SHA256.aexp-prov.v4`, which covers the excerpt) only under `SignClaimsVersion = 4`, and with signing configured
  it shows an excerpt only when a version 4 signature verifies. As for every signature, a port writes none and never
  changes one it reads.
- **Port limitation: a port does not confirm an excerpt.** A port cannot verify signatures, so a port that turns
  `recipeExcerpts` on renders whatever excerpt the stored record carries, signed or not. A party that can write the
  store can therefore put text on a port's `Worked:` line that a signing .NET reader would suppress. Turn it on in a
  port only over a store whose writers you trust as much as the lessons themselves; with an empty `recipeExcerpts` (the
  default) nothing is shown.

## Rendering it

The inject options (`inject` family's `options.json`) gain one optional member, `recipeExcerpts`: the tool names whose
excerpts the block may show. Absent (as in every earlier case) or empty shows none, and the block is then exactly what
it was. With it set, on the attempt the `Worked:` line names, and on no other line:

- a call that recorded no `error`, whose tool name is in `recipeExcerpts` (ordinal) and whose `recipeExcerpt` is
  present and not blank (whitespace only counts as blank) writes `[returned: "<excerpt>"]` after its name and
  arguments, **in place of** its `[returned]` marker;
- the excerpt is written exactly as an allowlisted string argument value is (contract 1.0's argument-value rules:
  invisible characters and whitespace collapsed to single spaces and trimmed, markers and labels neutralized, double
  quotes and look-alikes turned into `'`, `->` and `→` broken up), except that it is cut to **200** characters, not 64,
  with `[...]` after the closing quote when cut; the marker then closes with `]`;
- a failed call, a call on an earlier attempt, a tool not in `recipeExcerpts`, a blank excerpt and an excerpt that is
  empty once cleaned (only invisible characters, which become spaces and are trimmed) keep exactly the marker they had:
  `[returned: ""]` is never written.

The excerpt counts against the byte budget like the rest of the record, which is still dropped whole. (A borrowed
record's excerpt, which needs a grant, is .NET-only, like grants.)

## What 1.19 adds

- **schema:** [`experience-record.v1-1.19.json`](schema/experience-record.v1-1.19.json).
- **inject/recipe-excerpt**, **inject/recipe-excerpt-verbose**: a record whose calls all carry excerpts (a failed
  attempt's, a failed call's on the working attempt, one needing neutralization, one over 200 characters, a tool not
  allowlisted, a blank one, one of only invisible characters), rendered with `recipeExcerpts` in both layouts.
- **inject/recipe-excerpt-not-allowlisted**: the same record without `recipeExcerpts`: a port reads every excerpt and
  shows none.

Generated through the .NET Historical Reference writer and the PostgreSQL store's payload mapping, like every other
`inject` case, and checked against them on every build.

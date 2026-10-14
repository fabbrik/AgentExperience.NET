# Contract 1.1: additions

This page lists what contract 1.1 adds to [contract 1.0](README.md). The 1.0 README is still normative and is
unchanged: within a major version, a published file is never edited, so its version line still reads `1.0`. The
contract's version is the one `lock.json` records (`contractVersion`), which the .NET suite holds in
`ContractFiles.ContractVersion`. A minor version only adds files, this page among them. Changing or removing any
locked file, a README included, is a major version.

A port that passes contract 1.1 passes every 1.0 fixture and every fixture below.

## What 1.1 adds

- **`fixtures/encoding/cases.json`**, a new family. Each case is `{ "kind", "input", "valid", "canonical" }`, and
  `canonical` is `null` when `valid` is false. For each case, read or derive `input` as its `kind` says, then check
  that it is accepted exactly when `valid` is true, with `canonical` as the result:

  | Kind | `input` | `canonical` |
  | --- | --- | --- |
  | `instant` | an instant as a stored record carries one | the instant in the record's form (UTC, `+00:00`, trailing zeros of the fraction dropped) |
  | `duration` | a duration in the constant format | the duration in the record's form (seven fractional digits when there is a fraction) |
  | `non-blank` | a required check ID | the ID, accepted when it is not blank |
  | `task-id` | a task ID | the ID, accepted when it is not blank |
  | `experience-id` | `runId` and `scopeUtf16`: each scope field as an array of UTF-16 code units (0 to 65535), or `null` | the derived `experience_id`; these cases are always valid |

- **`fixtures/error-class/cases-1.1.json`**: more errors and their classes. Reduce every `cases*.json` of the family:
  `cases.json` (1.0) and `cases-1.1.json`.
- **finalize:** `argument-numbers`, `secret-names-ordinal-ignore-case`, `separator-task-and-check-ids`.
- **inject:** `argument-numbers`, `ordinal-ignore-case-neutralization`.
- **task-text:** `information-separators-are-not-whitespace`, `separators-only-latest`.
- **`sql/application-role-privileges.sql`**: see [The privileges file](#the-privileges-file).

## Where a port's own library differs from .NET

Every rule in the contract is .NET's. A port's standard library differs from .NET in places no 1.0 fixture covered,
and 1.1 pins each of them with a fixture:

- **Whitespace** is .NET's `char.IsWhiteSpace`. The information separators U+001C to U+001F are control characters,
  not whitespace, even though Python's `str.isspace` counts them. Every non-blank rule (task IDs, check IDs and kinds,
  revisions) is .NET's `string.IsNullOrWhiteSpace`, under which U+0085 is blank. See `encoding/` (`non-blank`,
  `task-id`), the finalize case `separator-task-and-check-ids`, the task-text cases
  `information-separators-are-not-whitespace` and `separators-only-latest`, and the error class.
- **The record schema's pattern is not the non-blank rule.** The schema's `task_id` pattern `[^\s]` is an ECMA-262
  regular expression, and in ECMA-262 neither U+0085 nor U+001C to U+001F is whitespace. So the schema accepts a task
  ID of U+0085 alone, which is blank to .NET (`encoding/`, `task-id`). Check the non-blank rule yourself, and evaluate
  the schema's patterns as ECMA-262 does, not with your language's own regular expressions.
- **Ignoring case** is ordinal: each character is compared by its simple upper-case mapping, with no culture and no
  special casing. The dotless i (U+0131), the long s (U+017F), the dotted capital I (U+0130) and the Kelvin sign
  (U+212A) therefore fold to no ASCII letter. Secret names (the finalize case `secret-names-ordinal-ignore-case`) and
  the block's labels and delimiters (the inject case `ordinal-ignore-case-neutralization`) are matched this way.
- **The error class** is .NET's regular expression (`RegexOptions.CultureInvariant`), pinned by
  `error-class/cases-1.1.json`:
  - `\s` does not match U+001C to U+001F.
  - `\w` and `\b` count non-spacing marks (Mn), connector punctuation (Pc) and the zero-width joiner as word
    characters, but not enclosing (Me) or spacing (Mc) marks.
  - The case-insensitive parts fold no non-ASCII letter to an ASCII one.
- **IDs:** a scope field is encoded the way .NET's `Encoding.UTF8` encodes it, so a lone or reversed surrogate is
  U+FFFD (`EF BF BD`). Each field is encoded on its own, so a high surrogate that ends one field never pairs with a
  low surrogate that starts the next. An empty string is a present field (`0x01` and length 0), unlike `null`.
  `encoding/` (`experience-id`) gives scope fields as UTF-16 code units because a JSON string cannot carry a lone
  surrogate.
- **Instants and durations** are read the way .NET's `System.Text.Json` reads them (`encoding/`, `instant` and
  `duration`):
  - **Refused, not normalized:** hour 24, a minute or second of 60, an offset beyond ±14:00, offset minutes of 60 or
    more, a date that does not exist, and a duration beyond .NET's `TimeSpan`.
  - **For an instant:** a lower-case `t` or `z` is refused, and so is an unpadded field. More than seven fractional
    digits are accepted and cut to seven.
  - **For a duration:** unpadded fields (`1:2:3`) are accepted, and more than seven fractional digits are refused.
  - **An instant with no offset is outside the contract.** .NET reads it in the machine's local time zone, so no case
    holds one, and a record never carries one.

## Numbers in tool-call arguments

Capture keeps an argument that is a JSON number as the number it was given. A reader reads an integer that fits in a
64-bit integer as that integer, and any other number as a double:

- `-0` and `-0.0` read back as zero.
- An integer beyond 64 bits reads back as the nearest double.

A block shows the integer as written, and the double in its shortest round-trip form (`1E+20`). This is pinned by the
finalize and inject cases `argument-numbers`, which use only integers that a double holds exactly.

**Not pinned in 1.1.** The following are outside the contract:

- a number that a double cannot hold exactly;
- a number beyond a double's range;
- NaN and the infinities.

No fixture holds one. In particular, 1.1 pins neither a block rendering an overflowing number as `Infinity` nor
capture refusing a non-finite number. .NET currently accepts such numbers at capture: it records NaN, an infinity, or
a JSON number beyond a double's range. When it reads them back, the values have changed, or are infinities that .NET
cannot write as JSON again. That is a known .NET bug, tracked separately. A port may refuse such numbers at capture,
and must not write them.

## The privileges file

[`sql/application-role-privileges.sql`](sql/application-role-privileges.sql) holds the statements that
`ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync` runs, with the default options, on a schema the
migrator has just applied. A .NET test keeps the two identical.

**Use it only for a port's own test setup.** Never run it in production, and never run it against a schema the .NET
migrator manages: there, call the .NET method, which also verifies the result.

How to run it:

- Run it as the schema's owner, in one transaction, replacing every `:"application_role"` with the role's quoted
  identifier.
- The statements under `-- mode: always` run in both modes.
- The statements under `-- mode: row-level-security` run only with `EnableRowLevelSecurity = true`, which is the mode
  a contract 1.x port runs against.
- Each statement is one line ending in `;`. A reader should refuse any other mode marker.

What it leaves out:

- **The row-level security policies and helper functions.** The .NET method re-creates them in row-level security
  mode. On a freshly migrated schema this changes nothing, because migration 0019 created them.
- **The verification** the .NET method runs afterwards.
- **The optional `agent_experience.experience_embeddings` table**, which belongs to the vectors package.

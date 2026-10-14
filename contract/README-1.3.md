# Contract 1.3: additions

This page lists what contract 1.3 adds to [contract 1.0](README.md), [contract 1.1](README-1.1.md) and
[contract 1.2](README-1.2.md). Those pages are still normative and unchanged: a minor version only adds files, this
page among them. The contract's version is the one `lock.json` records (`contractVersion`), which the .NET suite holds
in `ContractFiles.ContractVersion`.

A port that passes contract 1.3 passes every 1.0, 1.1 and 1.2 fixture and the fixtures below.

## What 1.3 adds

- **finalize:** `sub-microsecond-instants` and `sub-microsecond-instants-at-a-second-boundary`.

## Instants below the microsecond

Contract 1.0 states the rule ([Encodings](README.md), the Instant row) but every earlier fixture used whole seconds,
so nothing checked it. From 1.3 two cases hold instants with 100-ns ticks below the microsecond:

- **The finalization instant is truncated, never rounded, to whole microseconds in UTC before anything is derived
  from it.** The outcome's `evaluatedAt`, the reflection's `createdAt`, the `created_at` and `updated_at` columns and
  the initial lifecycle event's `occurred_at` all hold the truncated instant. `2026-03-03T02:00:30.1234567+00:00`
  becomes `2026-03-03T02:00:30.123456+00:00`, not `.123457`; `2026-03-03T03:00:59.9999996+00:00` becomes
  `2026-03-03T03:00:59.999999+00:00`, never the next second.
- **The instants the record copies from the run as the host gave them** (the provenance's `recordedAt`, each attempt's
  and tool call's `startedAt`, each evidence item's `capturedAt`) are payload instants: they keep all seven
  fractional digits, as contract 1.0 says. A port must not truncate them, and must not lose the seventh digit
  reading or writing them.

The fixtures' instants are written as contract 1.0 writes any instant: in UTC with the offset `+00:00`, trailing
zeros of the fraction dropped.

**Not added:** a search case whose seed holds an erased (tombstoned) row. A search case's `seed.json` is a list of
stored records, each with its columns and a payload, and inserts only the eighteen record columns; a tombstone has no
payload and is marked by a column (`deleted_at`) the seed does not carry, and the search fixtures do not read a record
by ID. Expressing it needs a change to the seed format, which a minor version does not make.

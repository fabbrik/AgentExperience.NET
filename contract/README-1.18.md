# Contract 1.18: additions

This page lists what contract 1.18 adds to [contract 1.0](README.md) and the pages of 1.1 to 1.17
([1.1](README-1.1.md), [1.2](README-1.2.md), [1.3](README-1.3.md), [1.4](README-1.4.md), [1.5](README-1.5.md),
[1.6](README-1.6.md), [1.7](README-1.7.md), [1.8](README-1.8.md), [1.9](README-1.9.md), [1.10](README-1.10.md),
[1.11](README-1.11.md), [1.12](README-1.12.md), [1.13](README-1.13.md), [1.14](README-1.14.md), [1.15](README-1.15.md),
[1.16](README-1.16.md) and [1.17](README-1.17.md)). Those pages are still normative and unchanged, except where this
page names the one statement a port now runs in place of another: a minor version only adds files, this page among
them. The contract's version is the one `lock.json` records (`contractVersion`), which the .NET suite holds in
`ContractFiles.ContractVersion`.

A port that passes contract 1.18 passes every 1.0 to 1.17 fixture and the fixtures below.

## The supersession lock, scoped to the request

Contract 1.16's `sql/lifecycle.sql` locks the record being superseded and its replacement with
`lock-supersession-rows`, which names no scope:

```sql
SELECT experience_id FROM agent_experience.experience_records WHERE experience_id = ANY(@lock_ids) ORDER BY experience_id FOR UPDATE
```

Without row-level security, a caller naming another tenant's record ID as the replacement therefore took a row lock on
that tenant's record for the rest of its transaction. It read and wrote nothing there (`check-supersession` reads the
replacement only in the request scope, so the outcome was `ReplacementNotAllowed` as for a record that does not exist),
but one tenant could make another's writes wait, and time them.

**From 1.18, the .NET store and every port run [`sql/lifecycle-1.18.sql`](sql/lifecycle-1.18.sql)'s
`lock-supersession-rows-scoped` in its place**, in lifecycle.sql's COMMIT step 3, with the same `@lock_ids` and the
request scope bound as every other lifecycle statement binds it (`@tenant_id` ... `@user_id`, NULL for an unset
optional field). It locks only the rows of the request scope, still in experience-ID order, so two supersessions naming
each other still cannot deadlock. Nothing else changes: `check-supersession` follows it, and a replacement in another
scope reads as not found exactly as before (`ReplacementNotAllowed`, with no current status). No outcome, result or
table a fixture pins moves, so the 1.16 `lifecycle-sql` fixtures are unchanged and still pass.

1.16's `lock-supersession-rows` stays in `lifecycle.sql` because a published file never changes; a 1.18 port does not
run it. The statement is generated from the .NET store's source, like the rest of `lifecycle.sql`, and checked against
it on every build. The application role needs no new privilege.

## Ranking near ties, pinned to the bit

Ranking orders records by score, highest first, and breaks an exact tie by experience ID as text, ascending. Fixtures
compare scores within 1e-12, so before 1.18 a port whose arithmetic differed from .NET's in the last bit could pass every
fixture and still order two nearly tied records the other way.

**retrieval-policy/ranking-near-tie** is a new case in 1.12's `retrieval-policy` family (same files, same comparison):
two pairs of records whose scores differ by one ulp, the higher score on the larger experience ID. The order (lesson 2,
lesson 1, lesson 4, lesson 3) is compared exactly, as every `ranked` list is. A port reproduces it only with .NET's
arithmetic:

- **Recency and confidence decay are `2^(-age / halfLife)` correctly rounded**, `age` and `halfLife` each .NET's
  `TimeSpan.TotalSeconds` (whole ticks as a double, divided by 10,000,000), the quotient negated and rounded to a double
  before the power. .NET's `Math.Pow` is the platform C library's `pow`, which is correctly rounded on the Linux the
  fixtures are checked on. Lesson 1's recency, `2^(-803580 / 2592000)`, is `0.8066288166995434`; an implementation
  2 ulps off (Go's `math.Pow` gives `0.8066288166995436`) ties lesson 1 with lesson 2, and the tie-break then puts
  lesson 1 first. The case's inputs are far from rounding midpoints, so any correctly rounded `pow` gives them exactly.
- **Each component's contribution is its value times its weight, rounded to a double, then added** to the running
  total, which starts at 0 and takes the five in component order (relevance, confidence, recency, status,
  environment). A compiler that fuses the product into the sum (a fused multiply-add) ties lessons 3 and 4.

## What 1.18 adds

- **sql:** [`lifecycle-1.18.sql`](sql/lifecycle-1.18.sql), one statement, `lock-supersession-rows-scoped`.
- **retrieval-policy/ranking-near-tie**: one new case in an existing family, generated through .NET's
  `ExperienceRetrievalService` like the others.

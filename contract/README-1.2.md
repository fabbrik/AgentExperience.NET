# Contract 1.2: additions

This page lists what contract 1.2 adds to [contract 1.0](README.md) and [contract 1.1](README-1.1.md). Both pages are
still normative and unchanged: a minor version only adds files, this page among them. The contract's version is the
one `lock.json` records (`contractVersion`), which the .NET suite holds in `ContractFiles.ContractVersion`.

A port that passes contract 1.2 passes every 1.0 and 1.1 fixture and the fixture below.

## What 1.2 adds

- **finalize:** `argument-numbers-beyond-double-range`.

## Capture refuses a number it could not write again

Contract 1.1 left numbers beyond a double's range, NaN and the infinities outside the contract
([Numbers in tool-call arguments](README-1.1.md#numbers-in-tool-call-arguments)). From 1.2, capture refuses them:

- **A JSON number beyond a double's range** in an allowlisted argument, at any depth, makes capture refuse the whole
  attempt with `SanitizationRejected`, as it refuses an argument nested too deep or too wide. Nothing of the attempt
  is stored, and the attempts capture keeps are numbered from 0 in the order they were recorded. A number is beyond a
  double's range when reading it as a double, rounding to nearest with ties to even, gives an infinity. That holds
  for the exponent form (`1e400`, `-1e400`) and the integer form alike: a 401-digit integer is refused, and so is the
  integer midway between `double.MaxValue` and the next power of two, which rounds up. A port whose language reads an
  integer literal as an unbounded integer must still refuse one that a double cannot hold finitely.
- **NaN and the infinities**, which a host may hold as a native floating-point value (in .NET, a `double`, `float`,
  `Half`, `NFloat`, or either part of a `Complex`), are refused the same way. A JSON fixture cannot hold one, so no
  case does; this page is the rule.
- **Under a secret name**, at any depth, such a number is redacted without being read, like any secret value, and the
  attempt is kept. **Under a name that is not allowlisted** it is omitted, like any other value, and the attempt is
  kept.
- The refusal names the argument's path, never its value. The wording of a refusal's reason is not part of the
  contract, and no fixture checks it.

A number beyond a 64-bit integer that a double holds finitely (`1.5e19`) is kept as the number it was given, and so
is a number just above `double.MaxValue` that rounds down to it, in integer or exponent form
(`1.7976931348623158e308`). So a stored record never holds a number that reads back as an infinity, and a port never
writes one.

**Still not pinned:**

- a number within a double's range that a double cannot hold exactly, including one just above `double.MaxValue`
  that rounds down to it. Capture keeps it, and a reader reads it as the nearest double, as 1.1 says. A stored record
  holding one does not read back unchanged, so no fixture record holds one;
- a number too small for a double (underflow, such as `1e-400`), which reads as zero. No fixture holds one, and a
  port may keep or refuse it.

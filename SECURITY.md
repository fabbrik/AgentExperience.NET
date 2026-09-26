# Security Policy

## Supported versions

AgentExperience.NET is a **preview** (`0.1.0-preview.N`) and is not production ready. The
[Known limits](README.md#known-limits) table lists every unresolved item a code change could fix, and the
[Documented boundaries](README.md#documented-boundaries) table states exactly what no code change can remove; several
of the boundaries are security-relevant (erasure that cannot reach derived search data in backups, and a withdrawn
record that stays in a reused session). Only the latest preview is supported, and security fixes land on the `main`
branch.

The tests behind the four security properties this library claims — tenant isolation, sanitization, revoked records,
and untrusted injected context — are mapped in one place in [`docs/security-suite.md`](docs/security-suite.md).

## How releases are published

Packages reach nuget.org only through `.github/workflows/release.yml`, and only when a maintainer pushes a version
tag on a commit already on `main` and a required reviewer approves the `nuget-release` deployment. The workflow
re-runs the release checks on the tagged commit and pushes the packages it verified with a short-lived key from
NuGet Trusted Publishing (GitHub OIDC). No NuGet API key is stored in the repository or its secrets, and every other
workflow is read-only; a release test fails if either changes. [`RELEASING.md`](RELEASING.md) step 10 has the detail.

## Reporting a vulnerability

Please **do not open a public issue** for security problems.

Report vulnerabilities privately through GitHub: go to the repository's **Security** tab and choose **Report a vulnerability**. Include:

- the affected component (for example, sanitization, capture, or the MAF adapter);
- steps to reproduce, or a proof of concept;
- the impact you expect.

You should receive an acknowledgement within 7 days. We will keep you updated on the fix and credit you in the advisory unless you prefer to stay anonymous.

## Scope notes

This library processes agent inputs, tool arguments, and results that may contain sensitive data. The following are especially in scope:

- data that passes sanitization and reaches storage or model context unredacted;
- evidence or scope checks that can be bypassed to make unverified experience look trusted;
- one tenant or scope reading another's experience.

Sanitization cannot detect every possible secret in arbitrary text. That documented limitation is not a vulnerability by itself, but bypasses of configured redaction or rejection rules are.

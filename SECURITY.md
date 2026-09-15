# Security Policy

## Supported versions

AgentExperience.NET is in early development and has no released versions yet. Security fixes land on the `main` branch.

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

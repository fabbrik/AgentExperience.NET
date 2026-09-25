# Releasing AgentExperience.NET

This is the release procedure and the release verification checks, **in the order they run**. Every check is a
command you can paste into an interactive shell; each prints an explicit OK or FAILED line rather than leaving a
judgement to whoever is running it, and each multi-line check runs in a `( ... )` subshell so a failure never closes
your terminal. Automation
builds, tests, packs, and verifies on every push (`.github/workflows/ci.yml`). **Publishing takes two maintainer
actions**: pushing a version tag, and approving the deployment it starts (step 10). Only
`.github/workflows/release.yml` can publish. It re-runs these checks on the tagged commit, and pushes to nuget.org
with a short-lived key from NuGet Trusted Publishing, so no NuGet API key is stored in the repository or its secrets.
A release test holds that workflow to its constraints, and fails if any other workflow gains a publish step, a NuGet
key, or a write permission.

## What a release is today

Every release from this repository is a **preview** — `0.1.0-preview.N`, set once in `Directory.Build.props` — and
claims no production readiness. That is not modesty; it is the acceptance criterion's own condition: *support limits
are documented before claiming production readiness, and any unresolved item blocks that claim.* The root README's
[Known limits](README.md#known-limits) table is non-empty, so the claim is blocked. Step 9 checks that the version
says so.

The version is deliberately not `1.0.0`. With no version property at all, `dotnet pack` would emit `1.0.0` — a
stability promise this codebase declines to make while it still ships documented breaking changes between previews.

To cut the next preview, bump the suffix (`preview.1` → `preview.2`) in `Directory.Build.props`, and nothing else.

## Prerequisites

- The commit you are releasing, checked out, with a clean tree.
- Docker running: the storage tests start PostgreSQL containers (16 by default; step 3 runs every supported major).
  If Testcontainers' Ryuk container fails under your Docker setup (Rancher Desktop, for example),
  `export TESTCONTAINERS_RYUK_DISABLED=true` first.
- The .NET 8 and .NET 9 runtimes installed beside the pinned SDK: the packages target `net8.0`, `net9.0` and
  `net10.0`, and every test project that exercises them runs on all three.
- Network access to nuget.org (restore, and the probes in step 6).

```bash
( test -z "$(git status --porcelain)" && echo "Clean tree OK" || echo "FAILED: the working tree is not clean" )
```

## The checks

### 1. The exact pinned SDK

`global.json` rolls forward for day-to-day development; a release does not.

```bash
( pinned="$(sed -nE 's/.*"version": *"([^"]+)".*/\1/p' global.json)"; actual="$(dotnet --version)"
  if [ "$actual" = "$pinned" ]; then echo "SDK OK: $actual"; else echo "FAILED: SDK $actual is in use, global.json pins $pinned"; false; fi )
```

### 2. Locked restore and a deterministic release build

`--locked-mode` fails if any `packages.lock.json` disagrees with what its project asks for, so the build uses exactly
the dependency graph that was reviewed. `AgentExperienceReleaseBuild` turns on `ContinuousIntegrationBuild` for the
five shipping projects only, which maps their source paths to `/_/` so the packages do not depend on where the
repository was cloned. Never pass `-p:ContinuousIntegrationBuild=true` itself: as a global property it would reach
the test projects too, and the tests that locate checked-in files through `[CallerFilePath]` would then look under
`/_/`.

```bash
dotnet restore --locked-mode
dotnet build --no-restore --configuration Release -p:AgentExperienceReleaseBuild=true
```

### 3. The full test suite

Core, PostgreSQL, the MAF adapter, the end-to-end sample, the reuse baseline, the compatibility proofs, and the
release gates, on every supported target framework (`net8.0`, `net9.0` and `net10.0`; the sample, its tests and the reuse
baseline run on `net10.0` only), against PostgreSQL 16. This is also the security suite: every test in
[`docs/security-suite.md`](docs/security-suite.md) runs here, and there is no separate, weaker security build.

```bash
dotnet test --no-build --configuration Release
```

Then the container-backed suites once per supported PostgreSQL major. The list is read from CI's `postgres` matrix,
which a release test holds equal to the list the fixtures accept, so this runs exactly what CI runs:

```bash
( ok=true
  majors="$(sed -nE 's/^ *postgres: \[([0-9, ]+)\].*/\1/p' .github/workflows/ci.yml | tr -d ' ' | tr ',' ' ')"
  [ -n "$majors" ] || { echo "FAILED: no postgres matrix in .github/workflows/ci.yml"; ok=false; }
  for major in $majors; do
    for project in tests/AgentExperience.Storage.Postgres.Tests tests/AgentExperience.Storage.Postgres.Vectors.Tests \
                   tests/AgentExperience.CompatibilityProof tests/AgentExperience.Sample.EndToEnd.Tests; do
      AGENTEXPERIENCE_POSTGRES_MAJOR="$major" dotnet test "$project" --no-build --configuration Release \
        || { echo "FAILED: $project on PostgreSQL $major"; ok=false; }
    done
    # Story 6.4: the store and vector suites again, unmodified, in crypto-shredding mode.
    for project in tests/AgentExperience.Storage.Postgres.Tests tests/AgentExperience.Storage.Postgres.Vectors.Tests; do
      AGENTEXPERIENCE_TEST_ENCRYPTION=on AGENTEXPERIENCE_POSTGRES_MAJOR="$major" dotnet test "$project" --no-build --configuration Release \
        || { echo "FAILED: $project on PostgreSQL $major, crypto-shredding mode"; ok=false; }
    done
  done
  $ok && echo "PostgreSQL matrix OK: $majors" )
```

### 4. The named gates, one by one

All of these already ran in step 3. Run them again by name so the release log shows each gate passing on its own
line — and so a gate that was accidentally filtered out of step 3 cannot hide.

```bash
# Story 4.5: deletion and retention, against a real PostgreSQL (16 unless AGENTEXPERIENCE_POSTGRES_MAJOR says otherwise).
dotnet test tests/AgentExperience.Storage.Postgres.Tests --no-build --configuration Release --filter "FullyQualifiedName~PostgresDeletionTests"

# Story 6.1: the two-role deployment. The application role owns nothing, cannot rewrite, remove or truncate a
# ledger whatever marker it sets, and reaches a purge only when the host opts in. (Every other store test in that
# project also runs as the application role.)
dotnet test tests/AgentExperience.Storage.Postgres.Tests --no-build --configuration Release --filter "FullyQualifiedName~PostgresApplicationRoleTests"

# The schema migrator reaches no console, Trace, ILogger or activity sink, on a clean run or a failing script.
dotnet test tests/AgentExperience.Storage.Postgres.Tests --no-build --configuration Release --filter "FullyQualifiedName~MigratorLogSilenceTests"

# Release gates: the public API baseline of all five assemblies, the compatibility proof's agreement with the
# shipping pins, the security-suite map, and the workflow guard (only release.yml publishes, as step 10 describes).
dotnet test tests/AgentExperience.Release.Tests --no-build --configuration Release

# A failing baseline leaves *.received.txt behind (gitignored, so look for it directly); there must be none,
# and all five baselines must be there.
( dir=tests/AgentExperience.Release.Tests/PublicApi
  if [ ! -d "$dir" ]; then echo "FAILED: $dir is missing"; false
  elif [ "$(find "$dir" -name '*.verified.txt' | wc -l)" -ne 5 ]; then echo "FAILED: $dir does not hold five baselines"; false
  elif [ -n "$(find "$dir" -name '*.received.*')" ]; then echo "FAILED: a public API baseline does not match: $(find "$dir" -name '*.received.*')"; false
  else echo "API baseline OK"; fi )
```

A public API change is never accepted here. If the baseline fails, the change goes back through review:
`AGENTEXPERIENCE_ACCEPT_API_CHANGES=true dotnet test tests/AgentExperience.Release.Tests --filter "FullyQualifiedName~PublicApi"`
regenerates it, and the resulting `git diff` is what gets reviewed and committed.

### 5. Every pin has source-backed evidence

[`docs/compatibility-evidence.md`](docs/compatibility-evidence.md) carries one row per SDK, MAF, storage, and
telemetry pin — MAF's range and every floor, including the `net8.0`-only ones: where it is declared, its nuget.org
source, the content hash NuGet restored, and the test that proves it. These two loops check that the document and the committed lock files describe
the same packages, byte for byte, in every target framework's section.

```bash
(
  ok=true
  # The document must cite hashes at all, or the first loop passes vacuously.
  [ "$(grep -cE '`[A-Za-z0-9+/]{86}==`' docs/compatibility-evidence.md)" -gt 0 ] \
    || { echo "FAILED: docs/compatibility-evidence.md cites no content hashes"; ok=false; }
  # Every hash the evidence cites is one a lock file actually restored...
  for h in $(grep -oE '`[A-Za-z0-9+/]{86}==`' docs/compatibility-evidence.md | tr -d '`'); do
    grep -qF "$h" src/*/packages.lock.json || { echo "FAILED: the evidence cites a hash no lock file has: $h"; ok=false; }
  done
  # ...and every package a shipping project pins directly has an evidence row.
  for h in $(grep -h -A4 '"type": "Direct"' src/*/packages.lock.json | sed -nE 's/.*"contentHash": "([^"]+)".*/\1/p' | sort -u); do
    grep -qF "$h" docs/compatibility-evidence.md || { echo "FAILED: a direct pin has no evidence row: $h"; ok=false; }
  done
  $ok && echo "Pin evidence OK"
)
```

Then read the document's **Last verified** date. If any pin or floor moved since, re-verify the moved rows against
nuget.org and update the date before continuing.

### 6. The MAF compatibility matrix, and the floating dependencies

`Microsoft.Agents.AI` is a range, `[floor, next major)` (story 7.2), so both ends are support claims and both probes
**are** release blockers: the floor, and the newest stable version inside the range. Both run on a throwaway copy of
the **tracked files** (HEAD plus uncommitted changes to them); untracked files are not copied, and the script warns
when `src/` or `tests/` has any. The floor restores the committed lock files exactly (`--locked-mode`); a newer
version has to re-evaluate them.

```bash
eng/probe-maf-version.sh pinned   # the range's floor: must print "... PASSED" and exit 0
eng/probe-maf-version.sh          # newest stable inside the range: must print "... PASSED" and exit 0; record it in docs/compatibility-evidence.md
```

If the newest stable MAF on nuget.org is outside the range (a new major), the second command says so and probes the
newest version inside it. Probing the new major itself (`eng/probe-maf-version.sh <version>`) is information for the
evidence document, not a release blocker; widening the range is a deliberate change.

Every other dependency is a floor, which claims every later release in its major, so the newest ones must pass too
(story 6.3). This one is a release blocker as well. Like the MAF probe it works on a throwaway copy of the tracked files:

```bash
eng/probe-floating-dependencies.sh   # must print "... PASSED" and exit 0; record the resolved table in the evidence
```

### 7. Pack, and verify the packages themselves

Assertions are made against the built `.nupkg` and `.snupkg` files, not the csproj files: ten artifacts at
`0.1.0-preview.N`; license, readme, tags, and repository metadata with the SourceLink commit; "Preview" in the
description, release notes, and readme; exactly the `net8.0`, `net9.0` and `net10.0` builds under `lib/`, and the
**exact** dependency set — ids and version ranges — in each framework's dependency group (the `net8.0` group also
carrying the `net8.0`-only floors, and no other group carrying them), so Abstractions and Core carry no MAF,
Npgsql, DbUp, Pgvector, model-provider or OpenTelemetry dependency; one repository commit across all five packages,
equal to `git rev-parse HEAD`; and, per framework, in each symbol package, a PDB whose id matches its assembly's
CodeView debug entry, whose every SourceLink target points at this repository, whose every path is mapped to `/_/`,
and whose assembly is marked reproducible.

```bash
rm -rf artifacts/packages
dotnet pack --no-build --configuration Release --output artifacts/packages -p:AgentExperienceReleaseBuild=true
dotnet run eng/verify-packages.cs -- artifacts/packages
```

### 8. The sample, twice, byte for byte

The sample's promise is one command, no Docker, no database, no credentials, and the same transcript every time.

```bash
( out="$(mktemp -d)"
  dotnet run --project samples/AgentExperience.Sample.EndToEnd --configuration Release --no-build > "$out/run-1.txt" \
    && dotnet run --project samples/AgentExperience.Sample.EndToEnd --configuration Release --no-build > "$out/run-2.txt" \
    && diff -u "$out/run-1.txt" "$out/run-2.txt" \
    && diff -u tests/AgentExperience.Sample.EndToEnd.Tests/GoldenTranscript.txt "$out/run-1.txt" \
    && echo "Sample OK" || echo "FAILED: the sample did not run, or its transcript differs (diff above)"
  rm -rf "$out" )
```

### 9. The production-readiness gate

A release may drop the preview suffix only when the Known limits table is empty **and** every item on the
maintainers' deferred-work ledger is closed. A row leaves the table only by fixing the limit it describes. Until
then, the version must say preview:

```bash
( limits="$(grep -cE '^\| KL-[0-9]+ \|' README.md || true)"
  if [ "$limits" -gt 0 ]; then
    if grep -qE '<VersionSuffix>preview\.[1-9][0-9]*</VersionSuffix>' Directory.Build.props; then
      echo "$limits known limit(s): this is a preview release, and it is versioned as one."
    else
      echo "FAILED (BLOCKED): $limits known limit(s), but Directory.Build.props does not version this as a preview."; false
    fi
  else
    echo "No known limits. Confirm the deferred-work ledger is closed before dropping the preview suffix."
  fi )
```

Before moving on, record the commit steps 1–9 verified; step 10 refuses to tag anything else:

```bash
verified="$(git rev-parse HEAD)"; echo "Verified commit: $verified"
```

### 10. Tag and approve (maintainer only)

Only after steps 1–9 have passed on the exact commit being released, in the same shell that recorded `$verified`,
and only once that commit is on `main`. Tag it and push the tag:

```bash
( version="$(sed -nE 's/.*<VersionPrefix>([^<]+)<.*/\1/p' Directory.Build.props)-$(sed -nE 's/.*<VersionSuffix>([^<]+)<.*/\1/p' Directory.Build.props)"
  git fetch origin main
  if [ -z "${verified:-}" ] || [ "$(git rev-parse HEAD)" != "$verified" ]; then
    echo "FAILED: HEAD is not the commit steps 1-9 verified ($verified); re-run the checks"; false
  elif ! git merge-base --is-ancestor "$verified" origin/main; then
    echo "FAILED: $verified is not on origin/main; merge it first"; false
  else
    git tag -a "v$version" -m "AgentExperience.NET $version (preview)" "$verified" \
      && git push origin "v$version" \
      && echo "Tagged v$version: now approve the nuget-release deployment" || { echo "FAILED: see above"; false; }
  fi )
```

The tag starts `.github/workflows/release.yml`. Its `verify` job first refuses a tag that is not `v` plus the version
in `Directory.Build.props`, and a commit that is not reachable from `main`. It then re-runs, on the tagged commit,
the pinned SDK (step 1), the locked restore and release build (step 2), the full test suite (step 3), pack and
package verification (step 7), and the production-readiness gate (step 9). It builds the release notes from this
version's `CHANGELOG.md` section, and fails if there is none. Last, it uploads the packages it verified.

Then **approve the deployment**. Open the run under the repository's **Actions** tab, choose **Review deployments**,
select `nuget-release`, and approve. The `publish` job then:

1. downloads exactly the files `verify` uploaded (it does not check out or build anything);
2. exchanges the job's GitHub OIDC token for a short-lived nuget.org key through `NuGet/login`;
3. runs `dotnet nuget push "artifacts/packages/*.nupkg" --source https://api.nuget.org/v3/index.json --skip-duplicate`,
   which uploads each `.snupkg` alongside its `.nupkg`;
4. only then creates the GitHub release for the tag: a prerelease when the version has a suffix, with that
   version's `CHANGELOG.md` section and the Known limits table as its notes. A preview's release notes say what it
   does not promise.

If `publish` fails part-way, re-run the failed job from the Actions tab (it needs approving again): `--skip-duplicate`
skips the packages nuget.org already has, and an existing GitHub release is left as it is. If `verify` fails,
nothing was published: fix the cause on `main`, delete the tag (`git push origin :refs/tags/vX` and `git tag -d vX`),
and tag again. A version pushed by mistake cannot be deleted from nuget.org — **unlist** it (and, if it is harmful,
mark it deprecated) on its nuget.org page, then publish the next preview.

#### One-time setup

Both halves must exist before the first tagged release. Without the policy, `publish` cannot log in; without the
environment's reviewers, it would run without an approval.

- **The nuget.org Trusted Publishing policy.** Signed in to nuget.org as `fabbrik76`, open **Trusted Publishing**
  from the account menu and add a GitHub Actions policy: owner `fabbrik`, repository `AgentExperience.NET`, workflow
  file `release.yml`, environment `nuget-release`. The policy lets the workflow's OIDC token be exchanged for a key
  only from that file, in that environment, in this repository, for packages `fabbrik76` owns.
- **The GitHub environment `nuget-release`.** In the repository's **Settings → Environments**, create
  `nuget-release` and add the maintainers as **required reviewers**. Under **Deployment branches and tags**, choose
  **Selected branches and tags** and add the tag rule `v*`, so no other ref can reach the environment. Add no
  environment secrets: the workflow needs none.

#### Fallback: publishing by hand

If the workflow cannot publish (GitHub Actions or the Trusted Publishing exchange unavailable), a maintainer with a
nuget.org API key scoped to these five packages can publish from the verified working copy instead, in the same
shell that recorded `$verified`. Packages go first and the tag second, so a failed push never leaves a tag pointing
at a release that does not exist; `--skip-duplicate` makes a retried push safe after a partial failure. The tag
push still starts `release.yml`: approving its `publish` job then skips every package already pushed and creates
the GitHub release; rejecting it leaves the release to be created by hand, with the Known limits table pasted into
its notes.

```bash
( if [ -z "${verified:-}" ] || [ "$(git rev-parse HEAD)" != "$verified" ]; then
    echo "FAILED: HEAD is not the commit steps 1-9 verified ($verified); re-run the checks"; false
  else
    version="$(sed -nE 's/.*<VersionPrefix>([^<]+)<.*/\1/p' Directory.Build.props)-$(sed -nE 's/.*<VersionSuffix>([^<]+)<.*/\1/p' Directory.Build.props)"
    dotnet nuget push "artifacts/packages/*.nupkg" --source https://api.nuget.org/v3/index.json --api-key "$NUGET_API_KEY" --skip-duplicate \
      && git tag -a "v$version" -m "AgentExperience.NET $version (preview)" "$verified" \
      && git push origin "v$version" \
      && echo "Published and tagged $version" || { echo "FAILED: see above; packages already pushed stay pushed, and a re-run skips them"; false; }
  fi )
```

That key lives with the maintainer, never in this repository or its secrets. Revoke it on nuget.org once it has
been used.

#!/usr/bin/env bash
# The MAF compatibility probe (story 4.3, AD-F; story 7.2, KL-13). Runs the MAF adapter's test suite and the MAF
# proofs from AgentExperience.CompatibilityProof against one Microsoft.Agents.AI version, and reports the result
# visibly. It never edits your working tree: it exports the tracked files (HEAD plus uncommitted changes to them)
# into a temporary directory, re-pins MAF there, and throws the copy away. Untracked files are NOT copied; the
# script warns when src/ or tests/ has any.
#
#   eng/probe-maf-version.sh            # the newest stable Microsoft.Agents.AI inside the declared range (CI's latest leg)
#   eng/probe-maf-version.sh 1.23.0     # a specific version, inside the range or not
#   eng/probe-maf-version.sh pinned     # the range's floor, from the committed lock files (CI's pinned leg)
#
# The adapter declares a range, [floor, next major), in
# src/AgentExperience.MicrosoftAgentFramework/AgentExperience.MicrosoftAgentFramework.csproj. Both ends are support
# claims: the floor is what the committed lock files resolve, and a host whose graph asks for more gets the newest
# 1.x with no warning. So both legs gate CI on push and on the weekly schedule; on a pull request the latest leg
# reports without blocking (docs/compatibility-evidence.md). A version outside the range is probed on request
# only, and its result is information, not a claim.
#
# Exit code 0 when the adapter passed against that version, 1 when it did not, 2 when the probe could not start.
set -euo pipefail

root="$(git rev-parse --show-toplevel)"
declared="$(sed -nE 's/.*Include="Microsoft\.Agents\.AI" Version="([^"]+)".*/\1/p' \
  "$root/src/AgentExperience.MicrosoftAgentFramework/AgentExperience.MicrosoftAgentFramework.csproj")"
# [floor, N.0.0): the only shape CompatibilityPinAgreementTests allows for MAF.
pinned="$(echo "$declared" | sed -nE 's/^\[([0-9]+\.[0-9]+\.[0-9]+), *[0-9]+\.0\.0\)$/\1/p')"
bound="$(echo "$declared" | sed -nE 's/^\[[0-9]+\.[0-9]+\.[0-9]+, *([0-9]+)\.0\.0\)$/\1/p')"
if [ "$(printf '%s\n' "$declared" | grep -c .)" -ne 1 ] || [ -z "$pinned" ] || [ -z "$bound" ] \
  || [ "$bound" -ne "$(( ${pinned%%.*} + 1 ))" ]; then
  echo "Microsoft.Agents.AI is declared as '$declared', not once as [x.y.z, (x+1).0.0); this probe does not understand it." >&2
  exit 2
fi

version="${1:-}"
if [ "$version" = "pinned" ]; then
  version="$pinned"
elif [ -z "$version" ]; then
  # nuget.org's flat container lists versions oldest first; a stable version is bare x.y.z. Only versions below
  # the range's upper bound: the latest leg proves the newest version a host can resolve without a warning.
  # Guarded so a network failure reaches the empty-version check below instead of exiting under pipefail.
  # MAF_PROBE_INDEX_URL overrides the version list (a file:// URL works), so the range filter can be exercised
  # against a list that has a version past the bound before nuget.org has one.
  index="$( { curl -fsSL --retry 3 --retry-all-errors "${MAF_PROBE_INDEX_URL:-https://api.nuget.org/v3-flatcontainer/microsoft.agents.ai/index.json}" \
    | grep -oE '"[0-9]+\.[0-9]+\.[0-9]+"' | tr -d '"'; } || true)"
  version="$(echo "$index" | awk -F. -v bound="$bound" '$1 != "" && $1 + 0 < bound + 0' | tail -n 1)"
  newest="$(echo "$index" | tail -n 1)"
  if [ -n "$newest" ] && [ "$newest" != "$version" ]; then
    echo "NOTE: the newest stable Microsoft.Agents.AI is $newest, outside the declared range $declared; probing $version." >&2
  fi
fi

if [ -z "$version" ]; then
  echo "Could not determine a Microsoft.Agents.AI version to probe." >&2
  exit 2
fi

untracked="$(git -C "$root" status --porcelain --untracked-files=all -- src tests | grep '^??' || true)"
if [ -n "$untracked" ]; then
  echo "WARNING: untracked files under src/ or tests/ are not part of the probed copy:" >&2
  echo "$untracked" >&2
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
# `git stash create` snapshots uncommitted changes to tracked files without touching the stash list or the
# working tree; it prints nothing when there are none, and HEAD is exported instead.
snapshot="$(git -C "$root" stash create)"
git -C "$root" archive "${snapshot:-HEAD}" | tar -x -C "$work"

# The floor is probed exactly as committed, so the lock files apply. Any other version is pinned exactly in the
# adapter and in the proof, so the run tests that version and nothing NuGet might pick instead.
if [ "$version" != "$pinned" ]; then
  # The proof pins every other package it backs exactly at its shipping floor. A newer MAF may raise one of those
  # floors (as 1.22.0 raised the Microsoft.Extensions.* train), which a host resolves without complaint, so here
  # the proof's other exact pins become floors: the gate must fail on the adapter, not on the proof's own pins.
  proof="$work/tests/AgentExperience.CompatibilityProof/AgentExperience.CompatibilityProof.csproj"
  sed -i.bak -E 's/(<PackageReference Include="[^"]+" Version=")\[([0-9]+\.[0-9]+\.[0-9]+)\]"/\1\2"/' "$proof"
  rm -f "$proof.bak"
  for project in \
    "$work/src/AgentExperience.MicrosoftAgentFramework/AgentExperience.MicrosoftAgentFramework.csproj" \
    "$work/tests/AgentExperience.CompatibilityProof/AgentExperience.CompatibilityProof.csproj"; do
    sed -i.bak -E "s/(Include=\"Microsoft\.Agents\.AI\" Version=\")[^\"]+\"/\1[$version]\"/" "$project"
    rm -f "$project.bak"
    grep -q "Include=\"Microsoft.Agents.AI\" Version=\"\[$version\]\"" "$project" \
      || { echo "Failed to re-pin Microsoft.Agents.AI in $project" >&2; exit 2; }
  done
fi

echo "Probing Microsoft.Agents.AI $version (declared: $declared) in $work"

# Two stages, reported separately: "does the dependency graph even resolve" and "do the tests pass". An
# exact pin elsewhere in the graph (for example the compatibility proof's pins at each floor) can make a newer MAF unresolvable
# before a single test runs, and that is a different finding from a behavioural break.
# The pinned leg restores the committed lock graph exactly (--locked-mode); any other version has to
# re-evaluate it, because the re-pin changes what the projects ask for.
# The tests tagged Category=DeclaredPins assert the versions the committed csproj and lock files name, which a
# re-pin changes on purpose, so only the floor's run includes them (the floating-dependency leg filters them the
# same way).
if [ "$version" = "$pinned" ]; then
  restore_mode="--locked-mode"
  adapter_filter=""
else
  restore_mode="--force-evaluate"
  adapter_filter="Category!=DeclaredPins"
fi

stage="restore"
status=0
(
  cd "$work" \
    && dotnet restore tests/AgentExperience.MicrosoftAgentFramework.Tests "$restore_mode" \
    && dotnet restore tests/AgentExperience.CompatibilityProof "$restore_mode"
) || status=$?

# A newer version must be what the adapter's tests actually resolve, or a pass would name a version that never ran.
if [ "$status" -eq 0 ] && ! grep -A4 '"Microsoft.Agents.AI": {' "$work/tests/AgentExperience.MicrosoftAgentFramework.Tests/packages.lock.json" \
    | grep -qF "\"resolved\": \"$version\""; then
  echo "The adapter's tests did not resolve Microsoft.Agents.AI $version." >&2
  status=1
fi

if [ "$status" -eq 0 ]; then
  stage="test"
  # Chained with &&: `set -e` does not apply inside a subshell whose status is tested with ||, so
  # without it only the last command's status would count. A proof filter that matches zero tests is a
  # failure too, not a vacuous pass.
  (
    cd "$work" \
      && dotnet test tests/AgentExperience.MicrosoftAgentFramework.Tests --no-restore --configuration Release \
        ${adapter_filter:+--filter "$adapter_filter"} \
      && dotnet test tests/AgentExperience.CompatibilityProof --no-restore --configuration Release \
        --filter "FullyQualifiedName~MafHooksProof|FullyQualifiedName~ContextProviderFitProof" | tee "$work/proof.log" \
      && { grep -qE 'Total: +[1-9]' "$work/proof.log" \
        || { echo "The CompatibilityProof filter matched zero tests." >&2; false; }; }
  ) || status=$?
fi

major="${version%%.*}"
floor_major="${pinned%%.*}"
if [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] && [ "$major" -ge "$floor_major" ] && [ "$major" -lt "$bound" ] \
  && [ "$(printf '%s\n%s\n' "$pinned" "$version" | sort -t. -k1,1n -k2,2n -k3,3n | head -n 1)" = "$pinned" ]; then
  in_range="yes"
else
  in_range="no"
fi

if [ "$status" -eq 0 ]; then
  verdict="PASSED"
  if [ "$in_range" = "yes" ]; then
    detail="The adapter's tests and the MAF proofs pass against $version, inside the declared range $declared."
  else
    detail="The adapter's tests and the MAF proofs pass against $version, which is outside the declared range $declared. That is information, not a support claim: widening the range is a deliberate change (docs/compatibility-evidence.md)."
  fi
elif [ "$stage" = "restore" ]; then
  verdict="FAILED (dependencies do not resolve)"
  detail="$version cannot be restored alongside this repository's other dependencies, so no test ran. The NU1xxx lines above name the conflicting package."
else
  verdict="FAILED (tests)"
  detail="The dependency graph resolves against $version, but the adapter does not build or its tests fail; see the log above for what broke."
fi
if [ "$status" -ne 0 ] && [ "$in_range" = "yes" ]; then
  detail="$detail $version is inside the declared range $declared, so a host can resolve it with no warning: fix the adapter so it works at both ends of the range, or raise the floor past the broken release (docs/compatibility-evidence.md, triage)."
fi

echo "MAF compatibility probe: Microsoft.Agents.AI $version $verdict"
echo "$detail"

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  {
    echo "### MAF compatibility probe: Microsoft.Agents.AI $version — $verdict"
    echo
    echo "| Declared range | Probed | Inside the range | Result |"
    echo "| --- | --- | --- | --- |"
    echo "| $declared | $version | $in_range | **$verdict** |"
    echo
    echo "$detail"
  } >> "$GITHUB_STEP_SUMMARY"
fi

[ "$status" -eq 0 ] && exit 0 || exit 1

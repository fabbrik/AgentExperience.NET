#!/usr/bin/env bash
# The MAF compatibility probe (story 4.3, AD-F). Runs the MAF adapter's test suite and the MAF proofs from
# AgentExperience.CompatibilityProof against a Microsoft.Agents.AI version other than the pinned one, and
# reports the result visibly. It never edits your working tree: it exports the tracked files (HEAD plus
# uncommitted changes to them) into a temporary directory, re-pins MAF there, and throws the copy away.
# Untracked files are NOT copied; the script warns when src/ or tests/ has any.
#
#   eng/probe-maf-version.sh            # the newest stable Microsoft.Agents.AI on nuget.org
#   eng/probe-maf-version.sh 1.21.0     # a specific version
#   eng/probe-maf-version.sh pinned     # the pinned version, through the same path (CI's gating leg)
#
# Exit code 0 when the adapter passed against that version, 1 when it did not. A pass is information, not
# a support claim: the supported version is the one pinned in
# src/AgentExperience.MicrosoftAgentFramework/AgentExperience.MicrosoftAgentFramework.csproj, and moving it
# is a deliberate change (docs/compatibility-evidence.md). In CI this runs as the non-blocking `latest`
# leg of the maf-compatibility job.
set -euo pipefail

root="$(git rev-parse --show-toplevel)"
pinned="$(sed -nE 's/.*Include="Microsoft\.Agents\.AI" Version="\[([^]]+)\]".*/\1/p' \
  "$root/src/AgentExperience.MicrosoftAgentFramework/AgentExperience.MicrosoftAgentFramework.csproj")"

version="${1:-}"
if [ "$version" = "pinned" ]; then
  version="$pinned"
elif [ -z "$version" ]; then
  # nuget.org's flat container lists versions oldest first; a stable version is bare x.y.z.
  # Guarded so a network failure reaches the empty-version check below instead of exiting under pipefail.
  version="$( { curl -fsSL https://api.nuget.org/v3-flatcontainer/microsoft.agents.ai/index.json \
    | grep -oE '"[0-9]+\.[0-9]+\.[0-9]+"' | tail -n 1 | tr -d '"'; } || true)"
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

for project in \
  "$work/src/AgentExperience.MicrosoftAgentFramework/AgentExperience.MicrosoftAgentFramework.csproj" \
  "$work/tests/AgentExperience.CompatibilityProof/AgentExperience.CompatibilityProof.csproj"; do
  sed -i.bak -E "s/(Include=\"Microsoft\.Agents\.AI\" Version=\")\[[^]]+\]/\1[$version]/" "$project"
  rm -f "$project.bak"
  grep -q "Include=\"Microsoft.Agents.AI\" Version=\"\[$version\]\"" "$project" \
    || { echo "Failed to re-pin Microsoft.Agents.AI in $project" >&2; exit 2; }
done

echo "Probing Microsoft.Agents.AI $version (pinned: $pinned) in $work"

# Two stages, reported separately: "does the dependency graph even resolve" and "do the tests pass". An
# exact pin elsewhere in the graph (for example Core's DI abstractions) can make a newer MAF unresolvable
# before a single test runs, and that is a different finding from a behavioural break.
# The pinned leg restores the committed lock graph exactly (--locked-mode); any other version has to
# re-evaluate it, because the re-pin changes what the projects ask for.
if [ "$version" = "$pinned" ]; then
  restore_mode="--locked-mode"
else
  restore_mode="--force-evaluate"
fi

stage="restore"
status=0
(
  cd "$work" \
    && dotnet restore tests/AgentExperience.MicrosoftAgentFramework.Tests "$restore_mode" \
    && dotnet restore tests/AgentExperience.CompatibilityProof "$restore_mode"
) || status=$?

if [ "$status" -eq 0 ]; then
  stage="test"
  # Chained with &&: `set -e` does not apply inside a subshell whose status is tested with ||, so
  # without it only the last command's status would count. A proof filter that matches zero tests is a
  # failure too, not a vacuous pass.
  (
    cd "$work" \
      && dotnet test tests/AgentExperience.MicrosoftAgentFramework.Tests --no-restore --configuration Release \
      && dotnet test tests/AgentExperience.CompatibilityProof --no-restore --configuration Release \
        --filter "FullyQualifiedName~MafHooksProof|FullyQualifiedName~ContextProviderFitProof" | tee "$work/proof.log" \
      && { grep -qE 'Total: +[1-9]' "$work/proof.log" \
        || { echo "The CompatibilityProof filter matched zero tests." >&2; false; }; }
  ) || status=$?
fi

if [ "$status" -eq 0 ]; then
  verdict="PASSED"
  detail="The adapter's tests and the MAF proofs pass against $version. This is not a support claim: $pinned remains the only supported version until the pin is moved deliberately."
elif [ "$stage" = "restore" ]; then
  verdict="FAILED (dependencies do not resolve)"
  detail="$version cannot be restored alongside this repository's other exact pins, so no test ran. The NU1605/NU1608 lines above name the conflicting package. $pinned remains the supported version."
else
  verdict="FAILED (tests)"
  detail="The dependency graph resolves against $version, but the adapter does not build or its tests fail. $pinned remains the supported version; see the log above for what broke."
fi

echo "MAF compatibility probe: Microsoft.Agents.AI $version $verdict"
echo "$detail"

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  {
    echo "### MAF compatibility probe: Microsoft.Agents.AI $version — $verdict"
    echo
    echo "| Pinned (supported) | Probed | Result |"
    echo "| --- | --- | --- |"
    echo "| $pinned | $version | **$verdict** |"
    echo
    echo "$detail"
  } >> "$GITHUB_STEP_SUMMARY"
fi

[ "$status" -eq 0 ] && exit 0 || exit 1

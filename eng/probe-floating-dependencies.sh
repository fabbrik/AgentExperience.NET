#!/usr/bin/env bash
# The floating-dependency leg (story 6.3, KL-13). Every shipping PackageReference except Microsoft.Agents.AI is a
# floor: the version the committed lock files resolve, and the lowest a host can get. The default CI run proves
# the floors. This script proves the other end: it restores every floor at the NEWEST release in the same major
# (the same minor, for a 0.x package), builds, and runs the whole test suite against that graph, on every target
# framework and the default PostgreSQL major. The one exception is the tests tagged Category=DeclaredPins, which
# check the versions the committed csproj files declare; this script rewrites those on purpose.
#
#   eng/probe-floating-dependencies.sh
#
# Like eng/probe-maf-version.sh, it never edits your working tree: it exports the tracked files (HEAD plus
# uncommitted changes to them) into a temporary directory, rewrites the floors there, and throws the copy away.
# Untracked files are NOT copied; the script warns when src/ or tests/ has any. Every reference to a floored
# package is floated, in the test projects and the compatibility proof too, so the proof exercises the same
# versions as the packages it backs.
#
# Exit code 0 when restore, build and every test pass against the floated graph; 1 when any of them fails;
# 2 when the script could not set the probe up. In CI this gates the build: a floor admits every later release
# in its major, so a release that breaks the library breaks hosts, and the build must say so.
set -euo pipefail

root="$(git rev-parse --show-toplevel)"

untracked="$(git -C "$root" status --porcelain --untracked-files=all -- src tests | grep '^??' || true)"
if [ -n "$untracked" ]; then
  echo "WARNING: untracked files under src/ or tests/ are not part of the probed copy:" >&2
  echo "$untracked" >&2
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
snapshot="$(git -C "$root" stash create)"
git -C "$root" archive "${snapshot:-HEAD}" | tar -x -C "$work"

# Every floor a shipping project declares: a PackageReference whose Version is a bare x.y.z. An exact pin
# ([x.y.z]) is not a floor and is left alone.
floors="$(sed -nE 's/.*<PackageReference Include="([^"]+)" Version="([0-9]+\.[0-9]+\.[0-9]+)".*/\1 \2/p' \
  "$work"/src/*/*.csproj | sort -u)"
if [ -z "$floors" ]; then
  echo "No floors found in src/*/*.csproj; nothing to float." >&2
  exit 2
fi

# Every PackageReference in src/ must be one of the two shapes this script understands, on one line: a floor
# (above) or an exact pin. Anything else (a prerelease or four-part version, a range, Version on another line) would
# be silently left out of the float, and the leg would pass without testing it.
references="$(grep -h '<PackageReference ' "$work"/src/*/*.csproj | wc -l | tr -d ' ')"
understood="$(grep -hE '<PackageReference Include="[^"]+" Version="(\[[0-9]+\.[0-9]+\.[0-9]+\]|[0-9]+\.[0-9]+\.[0-9]+)"' \
  "$work"/src/*/*.csproj | wc -l | tr -d ' ')"
if [ "$references" != "$understood" ]; then
  echo "Only $understood of the $references PackageReferences in src/*/*.csproj are a one-line floor (x.y.z) or exact pin ([x.y.z]):" >&2
  grep -hE '<PackageReference ' "$work"/src/*/*.csproj \
    | grep -vE '<PackageReference Include="[^"]+" Version="(\[[0-9]+\.[0-9]+\.[0-9]+\]|[0-9]+\.[0-9]+\.[0-9]+)"' >&2
  exit 2
fi

duplicates="$(echo "$floors" | awk '{print $1}' | uniq -d)"
if [ -n "$duplicates" ]; then
  echo "These packages have different floors in different shipping projects: $duplicates" >&2
  exit 2
fi

projects=("$work"/src/*/*.csproj "$work"/tests/*/*.csproj "$work"/samples/*/*.csproj)
rows=""
while read -r id floor; do
  major="${floor%%.*}"
  rest="${floor#*.}"
  minor="${rest%%.*}"
  if [ "$major" = "0" ]; then float="0.$minor.*"; else float="$major.*"; fi
  pattern="${id//./\\.}"
  # A reference outside the project files (central package management, an Update item in a Directory.*
  # file) would stay at its old version while this reports it floated.
  if grep -rqsF "\"$id\"" "$work"/Directory.*.props "$work"/Directory.*.targets "$work"/Directory.Packages.props; then
    echo "$id is referenced in a Directory.* file, which this script does not rewrite." >&2
    exit 2
  fi
  for project in "${projects[@]}"; do
    if grep -qF "Include=\"$id\" Version=" "$project"; then
      sed -i.bak -E "s/(Include=\"$pattern\" Version=\")[^\"]+\"/\1$float\"/" "$project"
      rm -f "$project.bak"
      # Fixed-string checks: as a pattern, "10.*" would also match the unrewritten floor.
      grep -qF "Include=\"$id\" Version=\"$float\"" "$project" \
        && ! grep -F "Include=\"$id\" Version=" "$project" | grep -vqF "Version=\"$float\"" \
        || { echo "Failed to float every reference to $id in $project" >&2; exit 2; }
    fi
  done
  rows="$rows$id $floor $float"$'\n'
done <<< "$floors"

echo "Floating every shipping floor to the newest release in its major, in $work"

stage="restore"
status=0
# --force-evaluate: the committed lock files record the floors, and the float asks for something else.
( cd "$work" && dotnet restore --force-evaluate ) || status=$?

if [ "$status" -eq 0 ]; then
  stage="test"
  (
    cd "$work" \
      && dotnet build --no-restore --configuration Release \
      && dotnet test --no-build --configuration Release --filter "Category!=DeclaredPins"
  ) || status=$?
fi

# What each float actually resolved to, read from the shipping projects' re-evaluated lock files.
table="| Package | Floor (proven by the default run) | Floated to | Resolved |"$'\n'"| --- | --- | --- | --- |"
while read -r id floor float; do
  [ -n "$id" ] || continue
  # Only the shipping projects' direct references: a transitive entry for the same id (the MAF adapter's
  # Microsoft.Extensions.AI.Abstractions, say) is resolved to MAF's own floor and says nothing about the float.
  resolved="$(awk -v id="\"$id\": {" '
      index($0, id) { found = 1; direct = 0; next }
      found && /"type": "Direct"/ { direct = 1 }
      found && /"resolved":/ { if (direct) { gsub(/.*"resolved": "|".*/, ""); print } found = 0 }
    ' "$work"/src/*/packages.lock.json 2>/dev/null | sort -u | paste -sd ',' - || true)"
  table="$table"$'\n'"| $id | $floor | $float | ${resolved:-not resolved} |"
done <<< "$rows"

if [ "$status" -eq 0 ]; then
  verdict="PASSED"
  detail="Every shipping floor, floated to the newest release in its major, restores, builds, and passes the whole test suite."
elif [ "$stage" = "restore" ]; then
  verdict="FAILED (dependencies do not resolve)"
  detail="The floated graph does not restore; the NU1xxx lines above name the conflict. A host taking those versions would hit the same conflict."
else
  verdict="FAILED (build or tests)"
  detail="The floated graph restores, but the library does not build or its tests fail against it. A host resolving those versions would get the same result; see the log above."
fi

echo "$table"
echo "Floating-dependency probe: $verdict"
echo "$detail"

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  {
    echo "### Floating-dependency probe — $verdict"
    echo
    echo "$table"
    echo
    echo "$detail"
  } >> "$GITHUB_STEP_SUMMARY"
fi

[ "$status" -eq 0 ] && exit 0 || exit 1

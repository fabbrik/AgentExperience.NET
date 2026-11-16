#!/usr/bin/env bash
# The self-contained aexp binaries (story 29.3): one file per runtime, built from the Cli project with the properties
# its csproj sets under AexpBinaries, so a person or a coding agent can get aexp with no .NET SDK or runtime on the
# machine. release.yml builds them from the tagged commit and attaches them to the GitHub release; ci.yml builds the
# same files on every pull request and proves each one on its own hardware (adoption-path-binary). Both run this
# script, as both run scripts/release-version.sh.
#
#   eng/publish-binaries.sh <out dir>     # emptied first; then aexp-<rid>[.exe] for each runtime, and SHA256SUMS
#
# The runtimes are listed once, below, and a release test holds the list equal to the csproj's RuntimeIdentifiers.
# The restore is locked, against the binaries' own lock file (packages.binaries.lock.json; packages.lock.json is the
# tool package's and stays untouched); the publish runs with --no-restore because PublishSingleFile is set only with a
# runtime, and a locked restore with a runtime would refuse the lock file written without one. No trimming, no AOT.
# Each binary is about 40 MB, and a local run takes about a minute. The tool package is unaffected: a plain build,
# restore or pack never sees AexpBinaries.
#
# Exit code 0 when every file was built; a failed restore or publish exits non-zero.
set -euo pipefail

out="${1:-}"
if [ -z "$out" ]; then
  echo "usage: eng/publish-binaries.sh <out dir>" >&2
  exit 2
fi
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="$("$root/scripts/release-version.sh" dotnet)"
project="$root/src/AgentExperience.Cli"

# The runtimes, exactly the csproj's RuntimeIdentifiers (a release test checks).
runtimes="linux-x64 linux-arm64 osx-arm64 osx-x64 win-x64"

rm -rf "$out"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "Building aexp $version for: $runtimes"
dotnet restore "$project" --locked-mode -p:AexpBinaries=true

for rid in $runtimes; do
  echo "Publishing aexp-$rid"
  dotnet publish "$project" --no-restore --configuration Release \
    -p:AexpBinaries=true -p:AexpBinaryRuntime="$rid" -p:AgentExperienceReleaseBuild=true \
    --output "$tmp/$rid"
  # The publish directory also holds .pdb and .xml files; only the executable ships, renamed after its runtime.
  case "$rid" in
    win-*) built="$tmp/$rid/AgentExperience.Cli.exe"; name="aexp-$rid.exe" ;;
    *) built="$tmp/$rid/AgentExperience.Cli"; name="aexp-$rid" ;;
  esac
  test -f "$built" || { echo "FAILED: dotnet publish for $rid did not produce $built" >&2; exit 1; }
  cp "$built" "$out/$name"
  chmod +x "$out/$name"
done

# The sums, in sha256sum's format with bare file names, so `sha256sum -c` (or `shasum -a 256 -c`) checks them from
# inside the directory. macOS has no sha256sum; shasum writes the same format.
if command -v sha256sum >/dev/null 2>&1; then
  (cd "$out" && sha256sum -- aexp-* > SHA256SUMS)
else
  (cd "$out" && shasum -a 256 -- aexp-* > SHA256SUMS)
fi

echo "Built aexp $version:"
ls -l "$out"
cat "$out/SHA256SUMS"

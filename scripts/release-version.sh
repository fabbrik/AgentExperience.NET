#!/usr/bin/env bash
# The version a release carries, in each language's spelling, derived from Directory.Build.props:
#   release-version.sh [dotnet|python|go|all]     (default: all, "<dotnet> <python> <go>")
# P-preview.N is Python PaN (an explicit PEP 440 alpha: PEP 440 would normalise "preview" to rc) and Go vP-preview.N;
# no suffix is P, P and vP. RELEASING.md calls this script wherever it needs a version, and the release tests hold it to
# release.yml's derive_versions and to ReleaseVersions.Derive. PROPS overrides the props file (the tests use it).
set -euo pipefail

props="${PROPS:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/Directory.Build.props}"
prefix="$(sed -nE 's/.*<VersionPrefix>([^<]+)<.*/\1/p' "$props" | head -n 1)"
suffix="$(sed -nE 's/.*<VersionSuffix>([^<]+)<.*/\1/p' "$props" | head -n 1)"

[[ "$prefix" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "VersionPrefix '$prefix' is not MAJOR.MINOR.PATCH" >&2; exit 1; }
if [ -z "$suffix" ]; then
  python="$prefix"
elif [[ "$suffix" =~ ^preview\.([1-9][0-9]*)$ ]]; then
  python="${prefix}a${BASH_REMATCH[1]}"
else
  echo "VersionSuffix '$suffix' is neither empty nor preview.N" >&2; exit 1
fi
dotnet="$prefix${suffix:+-$suffix}"

case "${1:-all}" in
  dotnet) echo "$dotnet" ;;
  python) echo "$python" ;;
  go) echo "v$dotnet" ;;
  all) echo "$dotnet $python v$dotnet" ;;
  *) echo "usage: release-version.sh [dotnet|python|go|all]" >&2; exit 2 ;;
esac

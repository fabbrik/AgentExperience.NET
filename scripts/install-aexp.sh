#!/bin/sh
# Installs aexp, the AgentExperience.NET command, as a self-contained binary: no .NET SDK or runtime needed.
#
#   curl -fsSL https://raw.githubusercontent.com/fabbrik/AgentExperience.NET/main/scripts/install-aexp.sh | sh
#
# It detects the platform (Linux x86_64 or aarch64, macOS arm64 or x86_64, Windows x86_64 under Git Bash, MSYS or
# Cygwin), picks the newest GitHub release (previews included), downloads the binary for the platform and the
# release's SHA256SUMS, verifies the binary's SHA-256 sum against that file, and only then puts it on PATH as
# $HOME/.local/bin/aexp (aexp.exe on Windows). Nothing is installed on a mismatch.
#
# Environment:
#   AEXP_VERSION        a release's version (the tag without its v, e.g. 0.1.0-preview.20) instead of the newest
#   AEXP_DOWNLOAD_BASE  a URL, or a file:// directory, holding aexp-<rid> and SHA256SUMS; replaces the release URL
#                       and needs no version lookup (eng/prove-adoption-path.sh uses it to prove a local build)
#   AEXP_INSTALL_DIR    where to put aexp (default: $HOME/.local/bin)
#
# POSIX sh only (dash runs `sh` on Debian): no bash features. Every fetch goes through curl, so file:// works on all
# three platforms. Any platform or release this script cannot serve is named, with the .NET tool as the alternative:
#   dotnet tool install --global AgentExperience.Cli --prerelease
set -eu

repo="fabbrik/AgentExperience.NET"
alternative="Alternatively, with the .NET 10 SDK: dotnet tool install --global AgentExperience.Cli --prerelease"

fail() {
  echo "install-aexp: $*" >&2
  echo "$alternative" >&2
  exit 1
}

command -v curl >/dev/null 2>&1 || fail "curl is required to download aexp."

# The runtime, as the release names its assets.
os="$(uname -s)"
arch="$(uname -m)"
case "$os" in
  Linux)
    if ldd --version 2>&1 | grep -qi musl; then
      fail "the binaries are built for glibc; this Linux ($arch) uses musl, which they do not support."
    fi
    case "$arch" in
      x86_64 | amd64) rid="linux-x64" ;;
      aarch64 | arm64) rid="linux-arm64" ;;
      *) fail "no aexp binary is built for Linux on $arch." ;;
    esac
    ;;
  Darwin)
    case "$arch" in
      arm64) rid="osx-arm64" ;;
      x86_64) rid="osx-x64" ;;
      *) fail "no aexp binary is built for macOS on $arch." ;;
    esac
    ;;
  MINGW* | MSYS* | CYGWIN*)
    case "$arch" in
      x86_64) rid="win-x64" ;;
      *) fail "no aexp binary is built for Windows on $arch." ;;
    esac
    ;;
  *)
    fail "no aexp binary is built for $os ($arch)."
    ;;
esac
case "$rid" in
  win-*) asset="aexp-$rid.exe"; target="aexp.exe" ;;
  *) asset="aexp-$rid"; target="aexp" ;;
esac

# Where the files come from: a given base, or the release the version names (the newest one by default).
if [ -n "${AEXP_DOWNLOAD_BASE:-}" ]; then
  base="${AEXP_DOWNLOAD_BASE%/}"
  version="${AEXP_VERSION:-}"
  version="${version#v}"
  release="the download base $base"
else
  if [ -n "${AEXP_VERSION:-}" ]; then
    # The tag's version, with or without the tag's leading v.
    version="${AEXP_VERSION#v}"
  else
    # The newest release, prereleases included (the previews are), from the GitHub API; no jq. Unauthenticated, the
    # API allows 60 requests an hour per address, so the failure names the way around the lookup.
    releases="$(curl -fsSL "https://api.github.com/repos/$repo/releases?per_page=1")" \
      || fail "could not list the releases of $repo (the GitHub API may be rate-limited: 60 requests an hour unauthenticated). Set AEXP_VERSION to a release's version (e.g. AEXP_VERSION=0.1.0-preview.20) to skip the lookup."
    version="$(printf '%s\n' "$releases" | sed -n 's/.*"tag_name": *"v\([^"]*\)".*/\1/p' | head -n 1)"
    [ -n "$version" ] || fail "$repo has no release yet."
  fi
  release="release v$version"
  base="https://github.com/$repo/releases/download/v$version"
  echo "Installing aexp $version ($rid) from $release"
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# The binary and the sums, from the same place.
for file in "$asset" SHA256SUMS; do
  if ! curl -fsSL -o "$tmp/$file" "$base/$file"; then
    fail "$release has no $file (at $base/$file); it may predate the binaries, or not be built for $rid. Download the .exe or binary from https://github.com/$repo/releases instead, or set AEXP_VERSION to a release that has it."
  fi
done

# The sum, from the line for this asset alone, checked by the system's tool from inside the directory. A miss, or no
# line for the asset, installs nothing.
grep " $asset\$" "$tmp/SHA256SUMS" > "$tmp/one.sum" || fail "SHA256SUMS of $release has no line for $asset."
if command -v sha256sum >/dev/null 2>&1; then
  (cd "$tmp" && sha256sum -c one.sum >/dev/null) || fail "the SHA-256 sum of $asset does not match SHA256SUMS; nothing was installed."
elif command -v shasum >/dev/null 2>&1; then
  (cd "$tmp" && shasum -a 256 -c one.sum >/dev/null) || fail "the SHA-256 sum of $asset does not match SHA256SUMS; nothing was installed."
else
  fail "neither sha256sum nor shasum is available to verify the download."
fi

# The version the binary carries, for the install line, when no release named it.
if [ -z "$version" ]; then
  chmod +x "$tmp/$asset"
  version="$("$tmp/$asset" --version 2>/dev/null | tr -d '\r')" || version="unknown"
fi

dir="${AEXP_INSTALL_DIR:-$HOME/.local/bin}"
mkdir -p "$dir"
chmod +x "$tmp/$asset"
mv -f "$tmp/$asset" "$dir/$target" || fail "could not replace $dir/$target (is aexp running? is the directory writable?)"

echo "Installed aexp $version ($rid) to $dir/$target"
case ":$PATH:" in
  *":$dir:"*) ;;
  *)
    echo "$dir is not on your PATH. Add it, for example:"
    echo "  export PATH=\"$dir:\$PATH\""
    ;;
esac
echo "Next: aexp init in your project root, then aexp trust after you reviewed .aexp/config.json."

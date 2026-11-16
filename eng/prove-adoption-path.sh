#!/usr/bin/env bash
# The adoption path, proven end to end (story 29.2). What the README promises a stranger -- install aexp, run init and
# trust, and get a verified lesson back -- is done here as that stranger would do it, from what CI built for this
# commit and nothing else: install aexp, then on a scratch project run `aexp init`, `aexp trust`, a run the configured
# verifier fails and a run it passes, finalize both, and assert that the next `aexp recall` prints the lesson,
# `aexp lessons` lists it and `aexp benefit` reports the holdout's estimate. Every assertion is on what the tool printed
# or wrote; nothing here reaches into the library.
#
#   eng/prove-adoption-path.sh <packages dir>       # tool mode: the directory `dotnet pack --output <dir>` filled
#   eng/prove-adoption-path.sh --binaries <dir>     # binary mode: the directory eng/publish-binaries.sh filled
#
# Tool mode: CI runs it on Linux, macOS and Windows from the pack job's artifact, so every OS proves the same bytes
# (ci.yml, adoption-path). Locally: dotnet pack --configuration Release --output /tmp/aexp-pack -p:AgentExperienceReleaseBuild=true
# and point the script at that directory. The package is installed from that directory only, never from nuget.org, at
# the version scripts/release-version.sh derives, so the published package of the same version cannot stand in. The
# script refuses to run while AgentExperience.Cli is already installed as a global tool (it would uninstall yours);
# otherwise it uninstalls the tool and deletes the scratch directory when it exits, pass or fail.
#
# Binary mode (story 29.3): no dotnet at all. The install script a stranger runs (scripts/install-aexp.sh) is run with
# AEXP_DOWNLOAD_BASE pointing at the directory (file://, never the network) and AEXP_INSTALL_DIR inside the scratch
# directory, so it picks this machine's runtime, checks the sum and installs the binary there; AEXP_EXPECT_RUNTIME=<rid>,
# when set, asserts which binary it chose. Before that, a copy of the directory with one hex digit of that binary's sum
# changed must make the install script fail and install nothing. Then the same checks as tool mode. CI runs it on five
# runners with no .NET installed (ci.yml, adoption-path-binary).
#
# In both modes the trust approval is written to a config directory inside the scratch directory, never to yours. The
# verifier is the one the CLI tests use: it passes once the file `fixed` exists in the project (sh on Unix, cmd on
# Windows).
#
# Exit code 0 when every check passed, 1 at the first check that did not, 2 when the proof could not start.
set -euo pipefail

usage() {
  echo "usage: eng/prove-adoption-path.sh <packages dir>   (a directory holding AgentExperience.Cli.<version>.nupkg)" >&2
  echo "       eng/prove-adoption-path.sh --binaries <dir> (a directory holding aexp-<rid> binaries and SHA256SUMS)" >&2
  exit 2
}

mode=tool
packages=""
binaries=""
case "${1:-}" in
  --binaries)
    mode=binaries
    binaries="${2:-}"
    [ -n "$binaries" ] && [ -d "$binaries" ] && [ $# -eq 2 ] || usage
    binaries="$(cd "$binaries" && pwd)"
    ;;
  "" | -*)
    usage
    ;;
  *)
    packages="$1"
    [ -d "$packages" ] && [ $# -eq 1 ] || usage
    packages="$(cd "$packages" && pwd)"
    ;;
esac
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="$("$root/scripts/release-version.sh" dotnet)"

case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*)
    windows=1
    verifier='["cmd","/c","if exist fixed (exit 0) else (exit 1)"]'
    verifier_text='if exist fixed (exit 0) else (exit 1)'
    ;;
  *)
    windows=0
    verifier='["sh","-c","test -f fixed"]'
    verifier_text='test -f fixed'
    ;;
esac

if [ "$mode" = tool ]; then
  # Git Bash sets HOME on Windows, and `dotnet tool install --global` puts the shim under ~/.dotnet/tools everywhere
  # (under DOTNET_CLI_HOME instead of HOME when that is set).
  export PATH="${DOTNET_CLI_HOME:-$HOME}/.dotnet/tools:$PATH"

  nupkg="$packages/AgentExperience.Cli.$version.nupkg"
  if [ ! -f "$nupkg" ]; then
    echo "FAILED: no AgentExperience.Cli.$version.nupkg in $packages; it holds:" >&2
    ls -la "$packages" >&2
    exit 1
  fi

  # Captured first: under pipefail, `grep -q` exiting early would turn awk's SIGPIPE into a failed pipeline.
  global_tools="$(dotnet tool list --global)"
  if printf '%s\n' "$global_tools" | awk 'NR > 2 { print tolower($1) }' | grep -qx 'agentexperience.cli'; then
    echo "AgentExperience.Cli is already installed as a global tool; this proof installs and uninstalls it, so it will not run over yours." >&2
    echo "Uninstall it first (dotnet tool uninstall --global AgentExperience.Cli), or run the proof on another machine." >&2
    exit 2
  fi
else
  if [ ! -f "$binaries/SHA256SUMS" ]; then
    echo "FAILED: no SHA256SUMS in $binaries; it holds:" >&2
    ls -la "$binaries" >&2
    exit 1
  fi
fi

pass() { echo "ok: $*"; }
fail() { echo "FAILED: $*" >&2; exit 1; }

# The checks: each names what it looked at, and a miss prints the whole output before failing.
expect_contains() {
  local what="$1" needle="$2" text="$3"
  case "$text" in
    *"$needle"*) pass "$what" ;;
    *) printf '%s\n' "$text" >&2; fail "$what: the output does not contain '$needle'" ;;
  esac
}
expect_not_contains() {
  local what="$1" needle="$2" text="$3"
  case "$text" in
    *"$needle"*) printf '%s\n' "$text" >&2; fail "$what: the output contains '$needle'" ;;
    *) pass "$what" ;;
  esac
}
expect_starts_with() {
  local what="$1" prefix="$2" text="$3"
  case "$text" in
    "$prefix"*) pass "$what" ;;
    *) printf '%s\n' "$text" >&2; fail "$what: the output does not start with '$prefix'" ;;
  esac
}
expect_empty() {
  local what="$1" text="$2"
  if [ -z "$text" ]; then pass "$what"; else printf '%s\n' "$text" >&2; fail "$what: expected no output"; fi
}
expect_equal() {
  local what="$1" expected="$2" text="$3"
  if [ "$text" = "$expected" ]; then pass "$what"; else fail "$what: expected '$expected', got '$text'"; fi
}
expect_file() {
  local what="$1" path="$2"
  if [ -f "$path" ]; then pass "$what"; else fail "$what: no file at $path"; fi
}

scratch="$(mktemp -d)"
installed=0
cleanup() {
  local status=$?
  # Out of the scratch directory first: Windows will not delete the directory a process is in.
  cd "$root" 2>/dev/null || cd /
  if [ "$installed" -eq 1 ]; then
    dotnet tool uninstall --global AgentExperience.Cli >/dev/null \
      || echo "WARNING: could not uninstall AgentExperience.Cli; run: dotnet tool uninstall --global AgentExperience.Cli" >&2
  fi
  rm -rf "$scratch" || echo "WARNING: could not remove $scratch" >&2
  if [ "$status" -eq 0 ]; then
    if [ "$mode" = tool ]; then
      echo "Adoption path: PASSED (tool mode: AgentExperience.Cli $version from $packages)"
    else
      echo "Adoption path: PASSED (binary mode: aexp $version from $binaries, no dotnet)"
    fi
  else
    echo "Adoption path: FAILED (exit $status)" >&2
  fi
}
trap cleanup EXIT

# The trust approval lands in the user's config directory: this run's own, inside the scratch directory, so the proof
# leaves nothing behind there either (XDG_CONFIG_HOME on Unix; APPDATA, as a Windows path, on Windows).
mkdir -p "$scratch/config"
if [ "$windows" -eq 1 ]; then
  export APPDATA; APPDATA="$(cygpath -w "$scratch/config")"
else
  export XDG_CONFIG_HOME="$scratch/config"
fi

# Runs aexp, prints its stdout (CR stripped: on Windows the tool writes CRLF), and fails the proof when it exits
# non-zero, with what it wrote to stderr. Its output is always assigned to a variable first (`out="$(aexp_ok ...)"`,
# whose failure `set -e` catches) and never written inline as an `expect_*` argument, where `fail` would only end the
# substitution's subshell and a crashed command would pass as empty output.
aexp_ok() {
  local out status=0
  out="$(aexp "$@" 2>"$scratch/stderr" | tr -d '\r')" || status=$?
  if [ "$status" -ne 0 ]; then
    cat "$scratch/stderr" >&2
    fail "aexp $* exited $status"
  fi
  printf '%s' "$out"
}

if [ "$mode" = tool ]; then
  echo "Installing AgentExperience.Cli $version from $packages (and nowhere else)"
  dotnet tool install --global AgentExperience.Cli --version "$version" --source "$packages"
  installed=1
  command -v aexp >/dev/null || { echo "PATH=$PATH" >&2; ls -la "$HOME/.dotnet/tools" >&2 || true; fail "aexp is not on PATH after the install"; }
else
  # The binary this machine gets: the install script decides from uname, as it would for a stranger; this is the same
  # mapping, so the proof knows which line of SHA256SUMS to corrupt and which name to expect in the install line.
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64 | Linux-amd64) rid=linux-x64 ;;
    Linux-aarch64 | Linux-arm64) rid=linux-arm64 ;;
    Darwin-arm64) rid=osx-arm64 ;;
    Darwin-x86_64) rid=osx-x64 ;;
    MINGW*-x86_64 | MSYS*-x86_64 | CYGWIN*-x86_64) rid=win-x64 ;;
    *) fail "no aexp binary is built for $(uname -s) $(uname -m)" ;;
  esac
  if [ "$windows" -eq 1 ]; then asset="aexp-$rid.exe"; else asset="aexp-$rid"; fi
  expect_file "the binaries directory holds $asset" "$binaries/$asset"
  installer="$root/scripts/install-aexp.sh"

  # curl takes file:// on every OS; on Windows the path has to be a drive path (file:///C:/...), which cygpath -m gives.
  file_url() {
    if [ "$windows" -eq 1 ]; then echo "file:///$(cygpath -m "$1")"; else echo "file://$1"; fi
  }

  # First, the tampered copy: the same files, with one hex digit of this binary's sum changed. The install script must
  # fail and install nothing (the directory it was given stays absent or empty).
  mkdir -p "$scratch/tampered"
  cp "$binaries"/aexp-* "$binaries/SHA256SUMS" "$scratch/tampered/"
  sed -i.bak "/ $asset\$/ {
s/^0/1/
t
s/^[0-9a-f]/0/
}" "$scratch/tampered/SHA256SUMS"
  rm -f "$scratch/tampered/SHA256SUMS.bak"
  if cmp -s "$binaries/SHA256SUMS" "$scratch/tampered/SHA256SUMS"; then fail "the tampered SHA256SUMS is unchanged"; fi
  tampered_status=0
  tampered_out="$(AEXP_DOWNLOAD_BASE="$(file_url "$scratch/tampered")" AEXP_INSTALL_DIR="$scratch/tampered-bin" sh "$installer" 2>&1)" || tampered_status=$?
  if [ "$tampered_status" -eq 0 ]; then printf '%s\n' "$tampered_out" >&2; fail "the install script accepted a binary whose sum does not match SHA256SUMS"; fi
  pass "the install script refuses a binary whose sum does not match SHA256SUMS (exit $tampered_status)"
  expect_contains "the refusal names the sum mismatch" "does not match SHA256SUMS" "$tampered_out"
  if [ -e "$scratch/tampered-bin" ] && [ -n "$(ls -A "$scratch/tampered-bin")" ]; then
    ls -la "$scratch/tampered-bin" >&2
    fail "the install script installed something from the tampered copy"
  fi
  pass "nothing was installed from the tampered copy"

  # Then the real install, from the directory itself, into the scratch directory's bin, which leads PATH from here on.
  echo "Installing aexp $version from $binaries through scripts/install-aexp.sh (and nowhere else)"
  export PATH="$scratch/bin:$PATH"
  install_out="$(AEXP_DOWNLOAD_BASE="$(file_url "$binaries")" AEXP_INSTALL_DIR="$scratch/bin" sh "$installer" 2>&1 | tr -d '\r')" \
    || { printf '%s\n' "$install_out" >&2; fail "scripts/install-aexp.sh failed"; }
  printf '%s\n' "$install_out"
  expect_contains "the install line names the version" "Installed aexp $version" "$install_out"
  if [ -n "${AEXP_EXPECT_RUNTIME:-}" ]; then
    expect_contains "the install line names the expected runtime" "($AEXP_EXPECT_RUNTIME)" "$install_out"
  fi
  resolved="$(command -v aexp || true)"
  case "$resolved" in
    "$scratch/bin/"*) pass "aexp resolves to the installed binary ($resolved)" ;;
    *) fail "aexp resolves to '$resolved', not under $scratch/bin" ;;
  esac
fi
printed="$(aexp_ok --version)"
expect_equal "aexp --version prints $version" "$version" "$printed"

project="$scratch/project"
mkdir -p "$project/.git"
cd "$project"

init="$(aexp_ok init --verify "$verifier")"
expect_file "aexp init wrote .aexp/config.json" "$project/.aexp/config.json"
gitignore="$(tr -d '\r' < "$project/.gitignore")"
expect_contains "aexp init added .aexp/ to .gitignore" ".aexp/" "$gitignore"
expect_contains "aexp init names aexp trust as the next step" "aexp trust" "$init"

trust="$(aexp_ok trust)"
expect_contains "aexp trust approved the config" "Trusted:" "$trust"
expect_contains "aexp trust showed the verifier" "$verifier_text" "$trust"

task="Fix the failing build in the payments service"

# Run A: the verifier fails (no `fixed` yet), so there is no lesson to give.
run_a="$(aexp_ok run start --task "$task")"
aexp_ok run call --run "$run_a" --tool run_tests --error "exit 2: build failed" >/dev/null
aexp_ok run end --run "$run_a" >/dev/null
finalized_a="$(aexp_ok finalize --run "$run_a")"
expect_contains "run A: finalize reports the verifier's failure" "verifier exit 1 (fail)" "$finalized_a"
recalled_a="$(aexp_ok recall --task "$task")"
expect_empty "run A: recall has nothing to give" "$recalled_a"

# Run B: the same failure, then the fix; the verifier passes once `fixed` exists, and the run becomes a validated lesson.
run_b="$(aexp_ok run start --task "$task")"
aexp_ok run call --run "$run_b" --tool run_tests --args '{"filter":"payments"}' \
  --error "Process exited with code 2: error CS0246: The type 'Ledger' could not be found" >/dev/null
aexp_ok run call --run "$run_b" --tool add_reference --args '{"package":"Ledger"}' --result "added Ledger" >/dev/null
aexp_ok run call --run "$run_b" --tool run_tests --args '{"filter":"payments"}' --result "all 42 tests passed" >/dev/null
aexp_ok run end --run "$run_b" >/dev/null
echo fixed > "$project/fixed"
finalized_b="$(aexp_ok finalize --run "$run_b")"
expect_contains "run B: finalize reports the verified run as Validated" "verifier exit 0 (pass); finalization Validated" "$finalized_b"

# The next recall for the task is handed the lesson, in the library's own block (no --run: no holdout arm is drawn).
block="$(aexp_ok recall --task "$task")"
expect_starts_with "recall opens the Historical Reference block" "=== BEGIN HISTORICAL REFERENCE" "$block"
expect_contains "recall names the call that worked" "add_reference [returned]" "$block"
expect_contains "recall closes the block" "=== END HISTORICAL REFERENCE ===" "$block"

lessons="$(aexp_ok lessons)"
expect_contains "aexp lessons lists the validated lesson" "Validated" "$lessons"

# Run C asks for its block as a run, so the holdout draws its arm (given or withheld, at the configured 20%), and is
# verified; benefit then has one run in one arm to report. Which arm is random, so no per-arm count is asserted.
run_c="$(aexp_ok run start --task "$task")"
aexp_ok recall --run "$run_c" >/dev/null
aexp_ok run call --run "$run_c" --tool run_tests --result "all passed" >/dev/null
aexp_ok run end --run "$run_c" >/dev/null
aexp_ok finalize --run "$run_c" >/dev/null

benefit="$(aexp_ok benefit)"
expect_contains "aexp benefit reports a verdict" "Verdict:" "$benefit"
expect_not_contains "aexp benefit reads a store that tracks the holdout" "does not track the holdout" "$benefit"
aexp_ok benefit --markdown results.md >/dev/null
expect_file "aexp benefit --markdown wrote the results page" "$project/results.md"

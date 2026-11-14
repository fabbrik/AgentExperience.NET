#!/usr/bin/env bash
# The adoption path, proven end to end (story 29.2). What the README promises a stranger -- install aexp, run init and
# trust, and get a verified lesson back -- is done here as that stranger would do it, from the packed tool package and
# nothing else: install the .nupkg CI packed for this commit as a global tool, then on a scratch project run
# `aexp init`, `aexp trust`, a run the configured verifier fails and a run it passes, finalize both, and assert that the
# next `aexp recall` prints the lesson, `aexp lessons` lists it and `aexp benefit` reports the holdout's estimate. Every
# assertion is on what the tool printed or wrote; nothing here reaches into the library.
#
#   eng/prove-adoption-path.sh <packages dir>     # the directory `dotnet pack --output <dir>` filled
#
# CI runs it on Linux, macOS and Windows from the pack job's artifact, so every OS proves the same bytes (ci.yml,
# adoption-path). Locally: dotnet pack --configuration Release --output /tmp/aexp-pack -p:AgentExperienceReleaseBuild=true
# and point the script at that directory. The package is installed from that directory only, never from nuget.org, at
# the version scripts/release-version.sh derives, so the published package of the same version cannot stand in. The
# script refuses to run while AgentExperience.Cli is already installed as a global tool (it would uninstall yours);
# otherwise it uninstalls the tool and deletes the scratch directory when it exits, pass or fail. The trust approval is
# written to a config directory inside the scratch directory, never to yours. The verifier is the one the CLI tests use:
# it passes once the file `fixed` exists in the project (sh on Unix, cmd on Windows).
#
# Exit code 0 when every check passed, 1 at the first check that did not, 2 when the proof could not start.
set -euo pipefail

packages="${1:-}"
if [ -z "$packages" ] || [ ! -d "$packages" ]; then
  echo "usage: eng/prove-adoption-path.sh <packages dir>   (a directory holding AgentExperience.Cli.<version>.nupkg)" >&2
  exit 2
fi
packages="$(cd "$packages" && pwd)"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="$("$root/scripts/release-version.sh" dotnet)"

# Git Bash sets HOME on Windows, and `dotnet tool install --global` puts the shim under ~/.dotnet/tools everywhere
# (under DOTNET_CLI_HOME instead of HOME when that is set).
export PATH="${DOTNET_CLI_HOME:-$HOME}/.dotnet/tools:$PATH"

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
    echo "Adoption path: PASSED (AgentExperience.Cli $version from $packages)"
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

echo "Installing AgentExperience.Cli $version from $packages (and nowhere else)"
dotnet tool install --global AgentExperience.Cli --version "$version" --source "$packages"
installed=1
command -v aexp >/dev/null || { echo "PATH=$PATH" >&2; ls -la "$HOME/.dotnet/tools" >&2 || true; fail "aexp is not on PATH after the install"; }
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

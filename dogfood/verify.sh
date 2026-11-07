#!/usr/bin/env bash
# The dogfood verifier (dogfood/aexp.config.json names it): builds the solution, then runs every test project that needs
# no Docker, exactly as CONTRIBUTING.md's "Without Docker" list does, each with --no-build. Exit 0 only if the build
# and every project pass; it stops at the first failure. aexp bounds it with verify.timeoutSeconds and kills it after.
# A test (ContributingWithoutDockerTests) keeps the list below equal to CONTRIBUTING.md's.
set -u
cd "$(dirname "$0")/.." || exit 2
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

dotnet build --nologo --verbosity quiet || exit 1

run() {
  dotnet test "$@" --no-build --nologo --verbosity quiet || exit 1
}

run tests/AgentExperience.Abstractions.Tests
run tests/AgentExperience.Core.Tests
run tests/AgentExperience.MicrosoftAgentFramework.Tests
run tests/AgentExperience.Storage.InMemory.Tests
run tests/AgentExperience.Storage.Sqlite.Tests
run tests/AgentExperience.Cli.Tests
run tests/AgentExperience.ReuseBaseline
run tests/AgentExperience.Release.Tests
run tests/AgentExperience.Sample.QuickStart.Tests
run experiments/AgentExperience.LiveReuse.Tests
run experiments/AgentExperience.CiTriage.Tests
run tests/AgentExperience.CompatibilityProof --filter "FullyQualifiedName!~Postgres"
run tests/AgentExperience.Sample.EndToEnd.Tests --filter "FullyQualifiedName!~Postgres"
run tests/AgentExperience.RetrievalQuality --filter "FullyQualifiedName!~Postgres"
run tests/AgentExperience.Contract.Tests --filter "FullyQualifiedName!~Postgres"

exit 0

module github.com/fabbrik/AgentExperience.NET/go/postgres

go 1.25.0

require (
	github.com/fabbrik/AgentExperience.NET/go v0.1.0-preview.14
	github.com/jackc/pgx/v5 v5.11.0
	github.com/santhosh-tekuri/jsonschema/v6 v6.0.3
)

require (
	github.com/jackc/pgpassfile v1.0.0 // indirect
	github.com/jackc/pgservicefile v0.0.0-20240606120523-5a60cdf6a761 // indirect
	github.com/jackc/puddle/v2 v2.2.2 // indirect
	github.com/stretchr/testify v1.12.1 // indirect
	golang.org/x/sync v0.22.0 // indirect
	golang.org/x/text v0.41.0 // indirect
)

// The core is developed in the same repository, so a build here uses the parent directory. A consumer ignores this
// replace and gets the core at the version required above: each release tags every module at the same version
// (RELEASING.md).
replace github.com/fabbrik/AgentExperience.NET/go => ../

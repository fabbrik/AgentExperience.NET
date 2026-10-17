module github.com/fabbrik/AgentExperience.NET/go/telemetry

go 1.26.0

require (
	github.com/fabbrik/AgentExperience.NET/go v0.1.0-preview.15
	github.com/fabbrik/AgentExperience.NET/go/inmemory v0.1.0-preview.15
	github.com/fabbrik/AgentExperience.NET/go/maf v0.1.0-preview.15
	github.com/microsoft/agent-framework-go v0.1.1
	go.opentelemetry.io/otel v1.47.0
	go.opentelemetry.io/otel/metric v1.47.0
	go.opentelemetry.io/otel/sdk v1.47.0
	go.opentelemetry.io/otel/sdk/metric v1.47.0
	go.opentelemetry.io/otel/trace v1.47.0
)

require (
	github.com/cespare/xxhash/v2 v2.3.0 // indirect
	github.com/go-logr/logr v1.4.4 // indirect
	github.com/go-logr/stdr v1.2.2 // indirect
	github.com/google/jsonschema-go v0.4.3 // indirect
	github.com/google/uuid v1.6.0 // indirect
	go.opentelemetry.io/auto/sdk v1.2.1 // indirect
	go.opentelemetry.io/otel/log v1.47.0 // indirect
	golang.org/x/sys v0.49.0 // indirect
	golang.org/x/text v0.43.0 // indirect
)

// The modules of this repository are developed together, so a build here uses their directories. A consumer ignores
// these replaces and gets them at the versions required above: each release tags every module at the same version
// (RELEASING.md).
replace (
	github.com/fabbrik/AgentExperience.NET/go => ../
	github.com/fabbrik/AgentExperience.NET/go/inmemory => ../inmemory
	github.com/fabbrik/AgentExperience.NET/go/maf => ../maf
	github.com/fabbrik/AgentExperience.NET/go/postgres => ../postgres
	github.com/fabbrik/AgentExperience.NET/go/postgres/pgtest => ../postgres/pgtest
)

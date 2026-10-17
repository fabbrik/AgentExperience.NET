module github.com/fabbrik/AgentExperience.NET/go/inmemory

go 1.25.0

require github.com/fabbrik/AgentExperience.NET/go v0.1.0-preview.15

require golang.org/x/text v0.41.0

// The core is developed in the same repository, so a build here uses the parent directory. A consumer ignores this
// replace and gets the core at the version required above: each release tags every module at the same version
// (RELEASING.md).
replace github.com/fabbrik/AgentExperience.NET/go => ../

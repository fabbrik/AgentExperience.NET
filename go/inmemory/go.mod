module github.com/fabbrik/AgentExperience.NET/go/inmemory

go 1.25.0

require github.com/fabbrik/AgentExperience.NET/go v0.0.0-00010101000000-000000000000

require golang.org/x/text v0.41.0

// The core is developed in the same repository; until a version of it is tagged, it is built from the parent
// directory.
replace github.com/fabbrik/AgentExperience.NET/go => ../

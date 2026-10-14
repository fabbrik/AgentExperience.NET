// Package agentexperience is a Go core of AgentExperience that implements the AgentExperience.NET language-neutral
// contract 1.2 (the repository's contract directory).
//
// Pre-release. It finalizes a captured run into a stored Experience Record, renders the injected Historical
// Reference block, derives the task text, classifies errors, and computes the text-search parameters and the
// ranking. The postgres subpackage writes records to and searches them in a PostgreSQL schema the .NET migrator
// applied; it is its own module, so this one requires no module at all. It has no Microsoft Agent Framework
// dependency.
//
// The contract is generated from .NET, where a string is UTF-16: every length, cut and limit here is counted in
// UTF-16 code units, never in bytes or runes. Whitespace, word characters and ignoring case are .NET's, not Go's
// (Go's regexp \s is ASCII only and its \b is ASCII words, and strings.EqualFold and regexp's (?i) fold the long s
// and the Kelvin sign to ASCII letters). JSON numbers in tool-call arguments are kept exactly as written.
//
// Nothing here logs: no record content leaves the package except through the values it returns.
package agentexperience

// ContractVersion is the contract version this package implements.
const ContractVersion = "1.2"

// SupportedContractVersions are the contract versions this package passes: every fixture of each.
var SupportedContractVersions = []string{"1.0", "1.1", "1.2"}

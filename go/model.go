package agentexperience

import (
	"maps"
	"strings"
	"unicode/utf16"
)

// The run's model (contract 1.9): capture records the model an agent runs on in the run's environment metadata, so a
// record names the model that learned it and a run's own record names the model that used its lessons. It is
// bookkeeping for the benefit estimate's split by model only: the reflector never lists it as a precondition, the
// block never shows it, and the ranking's environment compatibility ignores it.

// ModelMetadataKey is the environment metadata key a run's model is recorded under (.NET's
// HoldoutOutcomeRow.ModelMetadataKey).
const ModelMetadataKey = "agentexperience.model"

// ModelMaxLength is the most UTF-16 code units a recorded model id keeps.
const ModelMaxLength = 256

// NormalizeModel is the model id a run records for the id a chat client reports: trimmed of .NET white space at both
// ends, cut to at most ModelMaxLength UTF-16 code units (one fewer when the last would be a high surrogate) and
// trimmed at the end again; "" (nothing recorded) for an empty or all-white-space id.
func NormalizeModel(modelID string) string {
	trimmed := strings.TrimFunc(modelID, isWhitespace)
	if trimmed == "" {
		return ""
	}
	units := utf16.Encode([]rune(trimmed))
	if len(units) <= ModelMaxLength {
		return trimmed
	}
	cut := ModelMaxLength
	if utf16.IsSurrogate(rune(units[cut-1])) && units[cut-1] < 0xDC00 {
		cut--
	}
	return strings.TrimRightFunc(string(utf16.Decode(units[:cut])), isWhitespace)
}

// EnvironmentWithModel is environment with model (as NormalizeModel gives it) under ModelMetadataKey: unchanged when
// model is "", or when the environment already has the key (the host's value wins, even a blank one). The metadata
// map is copied, never changed in place.
func EnvironmentWithModel(environment EnvironmentFingerprint, model string) EnvironmentFingerprint {
	if model == "" {
		return environment
	}
	if _, ok := environment.Metadata[ModelMetadataKey]; ok {
		return environment
	}
	metadata := make(map[string]string, len(environment.Metadata)+1)
	maps.Copy(metadata, environment.Metadata)
	metadata[ModelMetadataKey] = model
	environment.Metadata = metadata
	return environment
}

// withoutModel is metadata without ModelMetadataKey: the same map when it has none.
func withoutModel(metadata map[string]string) map[string]string {
	if _, ok := metadata[ModelMetadataKey]; !ok {
		return metadata
	}
	without := make(map[string]string, len(metadata))
	for key, value := range metadata {
		if key != ModelMetadataKey {
			without[key] = value
		}
	}
	return without
}

// recordedModel is the model a record's environment names: nil when the entry is absent or .NET-blank; any other
// value exactly as stored.
func recordedModel(record *ExperienceRecord) *string {
	if record == nil {
		return nil
	}
	model, ok := record.Environment.Metadata[ModelMetadataKey]
	if !ok || isNullOrWhitespace(model) {
		return nil
	}
	return &model
}

// RecordedModel is the model a record's environment names (ModelMetadataKey): nil when absent, empty or white space
// only; any other value exactly as stored, untrimmed.
func RecordedModel(record *ExperienceRecord) *string { return recordedModel(record) }

package agentexperience

import (
	"crypto/sha256"
	"encoding/binary"
)

// The derived IDs: UUIDv8 (RFC 9562's custom version) over SHA-256, so a retried finalization re-derives them.

var idNamespace = MustParseUUID("0b6a8a3f-1c2d-4f5e-9a70-3d1c9f2b8e41")

// The derivation tags of contract 1.x.
const (
	ExperienceIDTag   byte = 1
	InitialEventIDTag byte = 2
	ReflectionIDTag   byte = 3
)

// DeriveID is SHA-256 over the namespace's 16 bytes, the run ID's 16 bytes, the tag byte and, with a scope, each
// scope field (0x00 when unset; else 0x01, its UTF-8 length as a 4-byte big-endian integer, and its UTF-8 bytes),
// cut to 16 bytes and stamped with version 8 and the RFC 4122 variant. UTF-8 is as .NET encodes it: anything that
// is not a valid character (a lone surrogate, in .NET) is U+FFFD.
func DeriveID(runID UUID, tag byte, scope *Scope) UUID {
	hash := sha256.New()
	hash.Write(idNamespace[:])
	hash.Write(runID[:])
	hash.Write([]byte{tag})
	if scope != nil {
		for _, field := range scope.Fields() {
			if field == nil {
				hash.Write([]byte{0})
				continue
			}
			encoded := utf8Bytes(*field)
			var length [4]byte
			binary.BigEndian.PutUint32(length[:], uint32(len(encoded)))
			hash.Write([]byte{1})
			hash.Write(length[:])
			hash.Write(encoded)
		}
	}
	var id UUID
	copy(id[:], hash.Sum(nil)[:16])
	id[6] = id[6]&0x0F | 0x80
	id[8] = id[8]&0x3F | 0x80
	return id
}

// ExperienceIDFor is a finalized record's experience_id.
func ExperienceIDFor(runID UUID, scope Scope) UUID { return DeriveID(runID, ExperienceIDTag, &scope) }

// InitialEventIDFor is a finalized record's initial lifecycle event_id.
func InitialEventIDFor(runID UUID, scope Scope) UUID {
	return DeriveID(runID, InitialEventIDTag, &scope)
}

// ReflectionIDFor is a finalized record's reflectionId.
func ReflectionIDFor(runID UUID) UUID { return DeriveID(runID, ReflectionIDTag, nil) }

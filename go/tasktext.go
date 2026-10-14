package agentexperience

import (
	"errors"
	"strconv"
	"strings"
	"unicode/utf8"
)

// The task text: how a request's messages become the text retrieval searches with. The latest external user
// message with text after the last replayed one, cleaned of control and format characters, joined after the
// previous user message with " — " when shorter than the follow-up threshold, and cut to the maximum length without
// splitting a surrogate pair. Lengths are UTF-16 code units.

// Task-text defaults and bounds.
const (
	DefaultTaskTextMaxLength = 512
	FollowUpThreshold        = 64
	FollowUpSeparator        = " — "
	// MaxTaskTextLength is the longest task text the search accepts; longer text is refused, not cut.
	MaxTaskTextLength = 4096
)

// Message sources.
const (
	SourceExternal    = "External"
	SourceChatHistory = "ChatHistory"
)

const (
	zwnj            = 0x200C
	zwj             = 0x200D
	wavingBlackFlag = 0x1F3F4
	tagFirst        = 0xE0020
	tagLast         = 0xE007E
	cancelTag       = 0xE007F
	maxFlagTags     = 8
)

// MessagePart is one part of a message: "text" with its text, or any other type (an image, a file), which carries
// none.
type MessagePart struct {
	Type string
	Text *string
}

// Message is one message of an invocation. Source is where the framework says it came from: empty (which counts as
// External), External (new input), ChatHistory (replayed history), AIContextProvider, or any other. CarriesBlock
// marks a message stamped as carrying an injected block.
type Message struct {
	Role         string
	Contents     []MessagePart
	Source       string
	CarriesBlock bool
}

// DeriveTaskText is the task text, or false when no message qualifies.
func DeriveTaskText(messages []Message, maxLength, followUpThreshold int) (string, bool, error) {
	if maxLength < 1 || maxLength > MaxTaskTextLength {
		return "", false, errors.New("the maximum length must be between 1 and " + strconv.Itoa(MaxTaskTextLength))
	}
	if followUpThreshold < 0 {
		return "", false, errors.New("the follow-up threshold must not be negative")
	}

	var latest, beforeLatest, lastSeen *string
	for _, message := range messages {
		source := message.Source
		if source == "" {
			source = SourceExternal
		}
		external, history := source == SourceExternal, source == SourceChatHistory
		if history {
			// The latest request is never older than the last replayed message.
			latest, beforeLatest = nil, nil
		}
		if !equalsIgnoreCase(message.Role, "user") || !(external || history) {
			continue
		}
		text, ok := usableText(message)
		if !ok {
			continue
		}
		if external {
			latest, beforeLatest = &text, lastSeen
		}
		lastSeen = &text
	}

	if latest == nil {
		return "", false, nil
	}
	length := UTF16Len(*latest)
	if length >= maxLength {
		text, ok := clipTaskText(*latest, maxLength)
		return text, ok, nil
	}
	if length >= followUpThreshold || beforeLatest == nil {
		return *latest, true, nil
	}
	room := maxLength - length - UTF16Len(FollowUpSeparator)
	if room > 0 {
		if previous, ok := clipTaskText(*beforeLatest, room); ok {
			return previous + FollowUpSeparator + *latest, true, nil
		}
	}
	return *latest, true, nil
}

func usableText(message Message) (string, bool) {
	if message.CarriesBlock {
		return "", false
	}
	var parts []string
	for _, part := range message.Contents {
		if part.Type == "text" && part.Text != nil && *part.Text != "" {
			parts = append(parts, *part.Text)
		}
	}
	raw := strings.Join(parts, " ")
	if raw == "" || strings.Contains(raw, BlockBegin) {
		return "", false
	}
	cleaned := cleanTaskText(raw)
	if cleaned == "" {
		return "", false
	}
	// A marker split by an invisible character reads as the marker once that character is gone.
	probe := strings.Map(func(r rune) rune {
		if category(r) == "Cf" {
			return -1
		}
		return r
	}, cleaned)
	if strings.Contains(probe, BlockBegin) {
		return "", false
	}
	return cleaned, true
}

func joinable(r rune) bool {
	switch category(r) {
	case "Lu", "Ll", "Lt", "Lm", "Lo", "Mn", "Mc", "Me", "So", "Sk", "Sm", "Sc":
		return true
	}
	return false
}

// textRunes are a string's characters, with each byte that is not valid UTF-8 as a lone surrogate (U+D800). .NET
// text is UTF-16, and the only text it holds that Go cannot is a lone surrogate, which reaches Go as invalid UTF-8
// (WTF-8 writes one as three bytes, each of them invalid here). The task text drops a lone surrogate as .NET does, so
// it drops every invalid byte; a real U+FFFD is text, and is kept.
func textRunes(text string) []rune {
	runes := make([]rune, 0, len(text))
	for i := 0; i < len(text); {
		r, size := utf8.DecodeRuneInString(text[i:])
		if r == utf8.RuneError && size == 1 {
			r = 0xD800
		}
		runes = append(runes, r)
		i += size
	}
	return runes
}

func cleanTaskText(text string) string {
	runes := textRunes(text)
	var out strings.Builder
	pendingSpace := false
	var pendingJoiner rune = -1
	var lastKept rune = -1
	for index := 0; index < len(runes); {
		r := runes[index]
		index++
		if r >= 0xD800 && r <= 0xDFFF {
			continue // An unpaired surrogate: not text.
		}
		if isWhitespace(r) {
			pendingSpace = out.Len() > 0
			pendingJoiner, lastKept = -1, -1
			continue
		}
		if cat := category(r); cat == "Cc" || cat == "Cf" {
			// A joiner is decided on the next character that is kept.
			if (r == zwj || r == zwnj) && lastKept >= 0 && joinable(lastKept) && pendingJoiner < 0 {
				pendingJoiner = r
			}
			continue
		}
		if pendingSpace {
			out.WriteByte(' ')
			pendingSpace = false
		}
		if pendingJoiner >= 0 && joinable(r) {
			out.WriteRune(pendingJoiner)
		}
		pendingJoiner = -1
		out.WriteRune(r)
		lastKept = r
		if r == wavingBlackFlag {
			consumed := flagTags(runes, index)
			out.WriteString(string(runes[index : index+consumed]))
			index += consumed
		}
	}
	return out.String()
}

// flagTags is how many tag characters after a waving black flag form a valid subdivision flag (0 when they do not).
func flagTags(runes []rune, start int) int {
	tags := 0
	for index := start; index < len(runes); {
		r := runes[index]
		if r >= 0xD800 && r <= 0xDFFF {
			return 0
		}
		index++
		if r == cancelTag {
			if tags == 0 {
				return 0
			}
			return index - start
		}
		tags++
		if r < tagFirst || r > tagLast || tags > maxFlagTags {
			return 0
		}
	}
	return 0
}

func clipTaskText(text string, maxLength int) (string, bool) {
	if UTF16Len(text) > maxLength {
		text = trimCutEnd(UTF16Prefix(text, maxLength))
	}
	if isNullOrWhitespace(text) {
		return "", false
	}
	return text, true
}

func trimCutEnd(text string) string {
	return strings.TrimRightFunc(text, func(r rune) bool {
		return r == ' ' || r == zwnj || r == zwj || (r >= tagFirst && r <= tagLast)
	})
}

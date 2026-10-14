package agentexperience

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math"
	"reflect"
	"sort"
	"strconv"
	"strings"
	"unicode/utf8"
)

// The JSON values this package reads and writes. A value is one of: nil, bool, string, json.Number (a number kept
// exactly as written, never decoded into a float64), *Object (members in order), or []any. Decoding never loses a
// number: 15000000000000000000 stays that literal, and -0.0 stays -0.0.

// Member is one member of a JSON object.
type Member struct {
	Key   string
	Value any
}

// Object is a JSON object whose members keep their order.
type Object struct {
	members []Member
}

// NewObject is an object holding members, in order.
func NewObject(members ...Member) *Object {
	o := &Object{}
	for _, m := range members {
		o.Set(m.Key, m.Value)
	}
	return o
}

// Len is the number of members.
func (o *Object) Len() int {
	if o == nil {
		return 0
	}
	return len(o.members)
}

// Members are the members, in order. The slice is the object's own: do not modify it.
func (o *Object) Members() []Member {
	if o == nil {
		return nil
	}
	return o.members
}

// Keys are the member names, in order.
func (o *Object) Keys() []string {
	keys := make([]string, 0, o.Len())
	for _, m := range o.Members() {
		keys = append(keys, m.Key)
	}
	return keys
}

// Get is the value of the member named key.
func (o *Object) Get(key string) (any, bool) {
	if o == nil {
		return nil, false
	}
	for _, m := range o.members {
		if m.Key == key {
			return m.Value, true
		}
	}
	return nil, false
}

// Has reports whether the object has a member named key.
func (o *Object) Has(key string) bool {
	_, ok := o.Get(key)
	return ok
}

// Set sets the member named key, in place when it exists, else appended.
func (o *Object) Set(key string, value any) {
	for i := range o.members {
		if o.members[i].Key == key {
			o.members[i].Value = value
			return
		}
	}
	o.members = append(o.members, Member{key, value})
}

// MarshalJSON writes the object with its members in order.
func (o *Object) MarshalJSON() ([]byte, error) {
	return MarshalJSONValue(o)
}

// ObjectFromMap is a Go map as an object, its members in ordinal (UTF-16) order of their names: a Go map has no
// order of its own.
func ObjectFromMap[V any](m map[string]V) *Object {
	keys := make([]string, 0, len(m))
	for key := range m {
		keys = append(keys, key)
	}
	sort.Slice(keys, func(i, j int) bool { return utf16Compare(keys[i], keys[j]) < 0 })
	o := &Object{members: make([]Member, 0, len(keys))}
	for _, key := range keys {
		o.members = append(o.members, Member{key, m[key]})
	}
	return o
}

// DecodeJSON reads one JSON value, keeping every number exactly as written (json.Number) and every object's member
// order. It refuses trailing data, and an object that names a member twice (as .NET's JsonObject does).
func DecodeJSON(data []byte) (any, error) {
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.UseNumber()
	value, err := decodeValue(decoder)
	if err != nil {
		return nil, err
	}
	if _, err := decoder.Token(); !errors.Is(err, io.EOF) {
		return nil, errors.New("unexpected data after the JSON value")
	}
	return value, nil
}

func decodeValue(decoder *json.Decoder) (any, error) {
	token, err := decoder.Token()
	if err != nil {
		return nil, err
	}
	return decodeFrom(decoder, token)
}

func decodeFrom(decoder *json.Decoder, token json.Token) (any, error) {
	switch t := token.(type) {
	case json.Delim:
		switch t {
		case '{':
			object := &Object{}
			for decoder.More() {
				keyToken, err := decoder.Token()
				if err != nil {
					return nil, err
				}
				key, ok := keyToken.(string)
				if !ok {
					return nil, errors.New("an object member name is not a string")
				}
				if object.Has(key) {
					return nil, errors.New("an object names a member twice")
				}
				value, err := decodeValue(decoder)
				if err != nil {
					return nil, err
				}
				object.members = append(object.members, Member{key, value})
			}
			if _, err := decoder.Token(); err != nil {
				return nil, err
			}
			return object, nil
		case '[':
			items := []any{}
			for decoder.More() {
				value, err := decodeValue(decoder)
				if err != nil {
					return nil, err
				}
				items = append(items, value)
			}
			if _, err := decoder.Token(); err != nil {
				return nil, err
			}
			return items, nil
		}
		return nil, fmt.Errorf("unexpected delimiter %v", t)
	case string, bool, nil, json.Number:
		return t, nil
	}
	return nil, fmt.Errorf("unexpected JSON token %T", token)
}

// MarshalJSONValue writes a JSON value. Besides the decoded forms it accepts Go's own: a map with string keys (its
// members in ordinal order of their names), a slice or array, an integer, and a finite float (written as .NET
// writes a double, in its shortest round-trip form), and a nil *Object as null. NaN and the infinities cannot be
// written, and neither can a value nested deeper than MaxJSONDepth (which a cycle always is).
func MarshalJSONValue(value any) ([]byte, error) {
	var buffer bytes.Buffer
	if err := appendJSON(&buffer, value, 0); err != nil {
		return nil, err
	}
	return buffer.Bytes(), nil
}

// MaxJSONDepth is the deepest nesting MarshalJSONValue writes: .NET's default for System.Text.Json.
const MaxJSONDepth = 64

func appendJSON(buffer *bytes.Buffer, value any, depth int) error {
	if depth > MaxJSONDepth {
		return fmt.Errorf("the value is nested deeper than %d levels, or refers to itself", MaxJSONDepth)
	}
	switch v := value.(type) {
	case nil:
		buffer.WriteString("null")
	case bool:
		buffer.WriteString(strconv.FormatBool(v))
	case string:
		appendJSONString(buffer, v)
	case json.Number:
		if !isJSONNumber(string(v)) {
			return fmt.Errorf("%q is not a JSON number", string(v))
		}
		buffer.WriteString(string(v))
	case float64:
		number, err := doubleNumber(v)
		if err != nil {
			return err
		}
		buffer.WriteString(string(number))
	case float32:
		if math.IsNaN(float64(v)) || math.IsInf(float64(v), 0) {
			return errors.New("NaN and the infinities cannot be written as JSON")
		}
		buffer.WriteString(formatShortest(float64(v), 32))
	case *Object:
		if v == nil {
			buffer.WriteString("null")
			return nil
		}
		buffer.WriteByte('{')
		for i, m := range v.Members() {
			if i > 0 {
				buffer.WriteByte(',')
			}
			appendJSONString(buffer, m.Key)
			buffer.WriteByte(':')
			if err := appendJSON(buffer, m.Value, depth+1); err != nil {
				return err
			}
		}
		buffer.WriteByte('}')
	case []any:
		buffer.WriteByte('[')
		for i, item := range v {
			if i > 0 {
				buffer.WriteByte(',')
			}
			if err := appendJSON(buffer, item, depth+1); err != nil {
				return err
			}
		}
		buffer.WriteByte(']')
	default:
		normalized, ok := normalizeGoValue(value)
		if !ok {
			return fmt.Errorf("a %T is not a JSON value", value)
		}
		return appendJSON(buffer, normalized, depth+1)
	}
	return nil
}

// normalizeGoValue is a Go integer, map or slice as one of the decoded forms.
func normalizeGoValue(value any) (any, bool) {
	rv := reflect.ValueOf(value)
	// A pointer is followed a bounded number of times, so one that leads back to itself is refused, not looped on.
	for hops := 0; rv.Kind() == reflect.Pointer || rv.Kind() == reflect.Interface; hops++ {
		if rv.IsNil() {
			return nil, true
		}
		if hops == 16 {
			return nil, false
		}
		rv = rv.Elem()
	}
	switch rv.Kind() {
	case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
		return json.Number(strconv.FormatInt(rv.Int(), 10)), true
	case reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64, reflect.Uintptr:
		return json.Number(strconv.FormatUint(rv.Uint(), 10)), true
	case reflect.Float32:
		return float32(rv.Float()), true
	case reflect.Float64:
		return rv.Float(), true
	case reflect.String:
		return rv.String(), true
	case reflect.Bool:
		return rv.Bool(), true
	case reflect.Slice, reflect.Array:
		if rv.Kind() == reflect.Slice && rv.IsNil() {
			return nil, true
		}
		items := make([]any, rv.Len())
		for i := range items {
			items[i] = rv.Index(i).Interface()
		}
		return items, true
	case reflect.Map:
		if rv.Type().Key().Kind() != reflect.String {
			return nil, false
		}
		if rv.IsNil() {
			return nil, true
		}
		m := make(map[string]any, rv.Len())
		for _, key := range rv.MapKeys() {
			m[key.String()] = rv.MapIndex(key).Interface()
		}
		return ObjectFromMap(m), true
	}
	return nil, false
}

func appendJSONString(buffer *bytes.Buffer, s string) {
	const hex = "0123456789abcdef"
	buffer.WriteByte('"')
	for i := 0; i < len(s); {
		r, size := utf8.DecodeRuneInString(s[i:])
		switch {
		case r == utf8.RuneError && size == 1:
			buffer.WriteString(`\ufffd`)
		case r == '"':
			buffer.WriteString(`\"`)
		case r == '\\':
			buffer.WriteString(`\\`)
		case r == '\n':
			buffer.WriteString(`\n`)
		case r == '\r':
			buffer.WriteString(`\r`)
		case r == '\t':
			buffer.WriteString(`\t`)
		case r < 0x20 || r == 0x7F || r == 0x2028 || r == 0x2029:
			buffer.WriteString(`\u`)
			buffer.WriteByte(hex[r>>12&0xF])
			buffer.WriteByte(hex[r>>8&0xF])
			buffer.WriteByte(hex[r>>4&0xF])
			buffer.WriteByte(hex[r&0xF])
		default:
			buffer.WriteString(s[i : i+size])
		}
		i += size
	}
	buffer.WriteByte('"')
}

// isJSONNumber reports whether s is a number as JSON's grammar writes one.
func isJSONNumber(s string) bool {
	i := 0
	if i < len(s) && s[i] == '-' {
		i++
	}
	if i >= len(s) {
		return false
	}
	if s[i] == '0' {
		i++
	} else if s[i] >= '1' && s[i] <= '9' {
		for i < len(s) && s[i] >= '0' && s[i] <= '9' {
			i++
		}
	} else {
		return false
	}
	if i < len(s) && s[i] == '.' {
		i++
		start := i
		for i < len(s) && s[i] >= '0' && s[i] <= '9' {
			i++
		}
		if i == start {
			return false
		}
	}
	if i < len(s) && (s[i] == 'e' || s[i] == 'E') {
		i++
		if i < len(s) && (s[i] == '+' || s[i] == '-') {
			i++
		}
		start := i
		for i < len(s) && s[i] >= '0' && s[i] <= '9' {
			i++
		}
		if i == start {
			return false
		}
	}
	return i == len(s)
}

// isIntegerLiteral reports whether a JSON number is written as an integer: no fraction and no exponent.
func isIntegerLiteral(n json.Number) bool {
	return !strings.ContainsAny(string(n), ".eE")
}

// doubleNumber is a double as .NET's serializer writes it: its shortest round-trip form, an integral value with no
// fraction ("1", not "1.0"), and negative zero as "-0".
func doubleNumber(value float64) (json.Number, error) {
	if math.IsNaN(value) || math.IsInf(value, 0) {
		return "", errors.New("NaN and the infinities cannot be written as JSON")
	}
	return json.Number(formatRoundTrip(value)), nil
}

// ReadNumber reads a JSON number as the .NET reader reads a tool-call argument: an integer literal that fits in a
// 64-bit integer as that int64, and any other number as a float64. A number beyond a double's range is an error
// (.NET reads it as an infinity it cannot write again). -0 reads as the integer 0.
func ReadNumber(n json.Number) (any, error) {
	if isIntegerLiteral(n) {
		if value, err := strconv.ParseInt(string(n), 10, 64); err == nil {
			return value, nil
		}
	}
	value, err := strconv.ParseFloat(string(n), 64)
	if math.IsInf(value, 0) {
		return value, errors.New("the number is beyond a double's range")
	}
	if err != nil && !errors.Is(err, strconv.ErrRange) {
		return nil, errors.New("the value is not a JSON number")
	}
	return value, nil
}

// isFiniteNumber reports whether a JSON number reads as a finite double: rounding to nearest with ties to even,
// it does not overflow.
func isFiniteNumber(n json.Number) bool {
	value, err := strconv.ParseFloat(string(n), 64)
	if err != nil && math.IsInf(value, 0) {
		return false
	}
	return true
}

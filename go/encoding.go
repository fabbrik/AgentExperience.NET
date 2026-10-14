package agentexperience

import (
	"encoding/hex"
	"errors"
	"fmt"
	"math/big"
	"regexp"
	"strconv"
	"strings"
	"time"
)

// The contract's value encodings: instants, durations and UUIDs, exactly as the stored record writes them.

const (
	ticksPerSecond      = 10_000_000
	ticksPerMicrosecond = 10
	ticksPerDay         = 86_400 * ticksPerSecond
	// maxInstantTicks is .NET's DateTime.MaxValue: 9999-12-31T23:59:59.9999999.
	maxInstantTicks = 3_652_059*ticksPerDay - 1
)

var (
	instantPattern  = regexp.MustCompile(`^([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]+))?(Z|[+-][0-9]{2}:[0-9]{2})$`)
	durationPattern = regexp.MustCompile(`^(-)?(?:([0-9]+)\.)?([0-9]{1,2}):([0-9]{1,2}):([0-9]{1,2})(?:\.([0-9]{1,7}))?$`)
)

// Instant is a point in time in UTC with .NET's precision: whole 100-nanosecond ticks since
// 0001-01-01T00:00:00Z. Payload instants keep up to seven fractional digits.
type Instant struct {
	ticks int64
}

// InstantFromTicks is the instant ticks 100-nanosecond intervals after 0001-01-01T00:00:00Z. Like every way of
// making an instant, it refuses one outside the range of a .NET DateTimeOffset in UTC (0001-01-01 to
// 9999-12-31T23:59:59.9999999).
func InstantFromTicks(ticks int64) (Instant, error) {
	if ticks < 0 || ticks > maxInstantTicks {
		return Instant{}, fmt.Errorf("%d ticks is outside the range of a .NET DateTimeOffset", ticks)
	}
	return Instant{ticks}, nil
}

// Ticks are the 100-nanosecond intervals since 0001-01-01T00:00:00Z.
func (i Instant) Ticks() int64 { return i.ticks }

// ParseInstant reads an ISO 8601 instant with an offset (+00:00 or any other, or Z), normalized to UTC, the way
// .NET's System.Text.Json reads one: hour 24, a minute or second of 60, an offset beyond ±14:00 or with minutes of
// 60 or more, a date that does not exist, a lower-case t or z and an unpadded field are refused; more than seven
// fractional digits are cut to seven.
func ParseInstant(text string) (Instant, error) {
	m := instantPattern.FindStringSubmatch(text)
	if m == nil {
		return Instant{}, fmt.Errorf("'%s' is not an ISO 8601 instant with an offset", text)
	}
	year, month, day := atoi(m[1]), atoi(m[2]), atoi(m[3])
	if year < 1 || month < 1 || month > 12 || day < 1 || day > daysInMonth(year, month) {
		return Instant{}, fmt.Errorf("'%s' has a date that does not exist", text)
	}
	hours, minutes, seconds := atoi(m[4]), atoi(m[5]), atoi(m[6])
	if err := checkTime(text, hours, minutes, seconds); err != nil {
		return Instant{}, err
	}
	fraction := m[7]
	if len(fraction) > 7 {
		fraction = fraction[:7]
	}
	fraction += strings.Repeat("0", 7-len(fraction))
	ticks := daysFromCivil(year, month, day)*ticksPerDay +
		int64(hours*3600+minutes*60+seconds)*ticksPerSecond + int64(atoi(fraction))
	if offset := m[8]; offset != "Z" {
		offsetHours, offsetMinutes := atoi(offset[1:3]), atoi(offset[4:6])
		if offsetMinutes >= 60 {
			return Instant{}, fmt.Errorf("'%s' has an offset whose minutes are 60 or more", text)
		}
		total := offsetHours*60 + offsetMinutes
		if total > 14*60 {
			return Instant{}, fmt.Errorf("'%s' has an offset beyond 14:00", text)
		}
		sign := int64(1)
		if offset[0] == '-' {
			sign = -1
		}
		ticks -= sign * int64(total) * 60 * ticksPerSecond
	}
	if ticks < 0 || ticks > maxInstantTicks {
		return Instant{}, fmt.Errorf("'%s' is outside the range of a .NET DateTimeOffset", text)
	}
	return Instant{ticks}, nil
}

// MustParseInstant is ParseInstant for a constant known to be valid; it panics otherwise.
func MustParseInstant(text string) Instant {
	instant, err := ParseInstant(text)
	if err != nil {
		panic(err)
	}
	return instant
}

// InstantFromTime is a time.Time as an instant, to the 100 nanoseconds (sub-tick nanoseconds are truncated). A time
// whose UTC year is outside 1 to 9999 is refused.
func InstantFromTime(t time.Time) (Instant, error) {
	u := t.UTC()
	if u.Year() < 1 || u.Year() > 9999 {
		return Instant{}, fmt.Errorf("%s is outside the range of a .NET DateTimeOffset", u.Format(time.RFC3339Nano))
	}
	days := daysFromCivil(u.Year(), int(u.Month()), u.Day())
	seconds := int64(u.Hour()*3600 + u.Minute()*60 + u.Second())
	return Instant{days*ticksPerDay + seconds*ticksPerSecond + int64(u.Nanosecond()/100)}, nil
}

// Now is the current time from the system clock.
func Now() Instant {
	instant, err := InstantFromTime(time.Now())
	if err != nil {
		panic("agentexperience: the system clock is outside years 1 to 9999")
	}
	return instant
}

// IsZero reports whether the instant is the zero value, 0001-01-01T00:00:00Z: an instant no one set.
func (i Instant) IsZero() bool { return i.ticks == 0 }

// Time is the instant as a UTC time.Time.
func (i Instant) Time() time.Time {
	days, rest := i.ticks/ticksPerDay, i.ticks%ticksPerDay
	year, month, day := civilFromDays(days)
	seconds, fraction := rest/ticksPerSecond, rest%ticksPerSecond
	return time.Date(year, time.Month(month), day, int(seconds/3600), int(seconds/60%60), int(seconds%60),
		int(fraction*100), time.UTC)
}

// TruncateToMicroseconds is the instant in whole microseconds, truncated, never rounded: what a timestamptz column
// keeps.
func (i Instant) TruncateToMicroseconds() Instant {
	return Instant{i.ticks - i.ticks%ticksPerMicrosecond}
}

// Add is the instant d later (earlier for a negative d); a result outside the range of a .NET DateTimeOffset is
// refused.
func (i Instant) Add(d Duration) (Instant, error) {
	sum := i.ticks + d.ticks
	if (d.ticks > 0 && sum < i.ticks) || (d.ticks < 0 && sum > i.ticks) {
		return Instant{}, errors.New("the instant is outside the range of a .NET DateTimeOffset")
	}
	return InstantFromTicks(sum)
}

// Sub is the duration from other to i.
func (i Instant) Sub(other Instant) Duration { return Duration{i.ticks - other.ticks} }

// Before reports whether i is before other.
func (i Instant) Before(other Instant) bool { return i.ticks < other.ticks }

// String is the instant as the record writes it: UTC with the offset +00:00, trailing zeros of the fraction
// dropped (2026-03-02T09:00:30.25+00:00).
func (i Instant) String() string {
	days, rest := i.ticks/ticksPerDay, i.ticks%ticksPerDay
	year, month, day := civilFromDays(days)
	seconds, fraction := rest/ticksPerSecond, rest%ticksPerSecond
	text := fmt.Sprintf("%04d-%02d-%02dT%02d:%02d:%02d", year, month, day, seconds/3600, seconds/60%60, seconds%60)
	if fraction != 0 {
		text += "." + strings.TrimRight(fmt.Sprintf("%07d", fraction), "0")
	}
	return text + "+00:00"
}

// secondsZ is 2026-03-02T09:00:30Z: whole seconds, the form the verbose block shows.
func (i Instant) secondsZ() string {
	return i.String()[:19] + "Z"
}

// Duration is a duration in .NET ticks (100 ns), written in the constant TimeSpan format
// [-][d.]hh:mm:ss[.fffffff].
type Duration struct {
	ticks int64
}

// DurationFromTicks is a duration of ticks 100-nanosecond intervals.
func DurationFromTicks(ticks int64) Duration { return Duration{ticks} }

// Ticks are the duration's 100-nanosecond intervals.
func (d Duration) Ticks() int64 { return d.ticks }

// TotalSeconds is .NET's TimeSpan.TotalSeconds: the ticks as a double, divided by ticks per second.
func (d Duration) TotalSeconds() float64 { return float64(d.ticks) / ticksPerSecond }

// ParseDuration reads a duration in the constant format, as .NET does: unpadded fields (1:2:3) are accepted; hours
// over 23, minutes or seconds over 59, more than seven fractional digits and a duration beyond .NET's TimeSpan are
// refused.
func ParseDuration(text string) (Duration, error) {
	m := durationPattern.FindStringSubmatch(text)
	if m == nil {
		return Duration{}, fmt.Errorf("'%s' is not a duration in the format [-][d.]hh:mm:ss[.fffffff]", text)
	}
	hours, minutes, seconds := atoi(m[3]), atoi(m[4]), atoi(m[5])
	if err := checkTime(text, hours, minutes, seconds); err != nil {
		return Duration{}, err
	}
	days, ok := new(big.Int).SetString("0"+m[2], 10)
	if !ok {
		return Duration{}, fmt.Errorf("'%s' is not a duration", text)
	}
	fraction := m[6] + strings.Repeat("0", 7-len(m[6]))
	total := new(big.Int).Mul(days, big.NewInt(86_400))
	total.Add(total, big.NewInt(int64(hours*3600+minutes*60+seconds)))
	total.Mul(total, big.NewInt(ticksPerSecond))
	total.Add(total, big.NewInt(int64(atoi(fraction))))
	if m[1] == "-" {
		total.Neg(total)
	}
	if !total.IsInt64() {
		return Duration{}, fmt.Errorf("'%s' is beyond the range of a .NET TimeSpan", text)
	}
	return Duration{total.Int64()}, nil
}

// MustParseDuration is ParseDuration for a constant known to be valid; it panics otherwise.
func MustParseDuration(text string) Duration {
	duration, err := ParseDuration(text)
	if err != nil {
		panic(err)
	}
	return duration
}

// String is the duration in the constant format: seven fractional digits when there is a fraction.
func (d Duration) String() string {
	ticks := new(big.Int).Abs(big.NewInt(d.ticks))
	day := big.NewInt(ticksPerDay)
	days, rest := new(big.Int).QuoRem(ticks, day, new(big.Int))
	r := rest.Int64()
	seconds, fraction := r/ticksPerSecond, r%ticksPerSecond
	var text strings.Builder
	if d.ticks < 0 {
		text.WriteByte('-')
	}
	if days.Sign() != 0 {
		text.WriteString(days.String() + ".")
	}
	fmt.Fprintf(&text, "%02d:%02d:%02d", seconds/3600, seconds/60%60, seconds%60)
	if fraction != 0 {
		fmt.Fprintf(&text, ".%07d", fraction)
	}
	return text.String()
}

func checkTime(text string, hours, minutes, seconds int) error {
	if hours >= 24 || minutes >= 60 || seconds >= 60 {
		return fmt.Errorf("'%s' has hours over 23, or minutes or seconds over 59", text)
	}
	return nil
}

func atoi(digits string) int {
	value, err := strconv.Atoi(digits)
	if err != nil {
		panic("agentexperience: digits expected: " + digits)
	}
	return value
}

func isLeapYear(year int) bool {
	return year%4 == 0 && (year%100 != 0 || year%400 == 0)
}

func daysInMonth(year, month int) int {
	switch month {
	case 2:
		if isLeapYear(year) {
			return 29
		}
		return 28
	case 4, 6, 9, 11:
		return 30
	}
	return 31
}

// daysFromCivil is the number of days from 0001-01-01 to the date (proleptic Gregorian).
func daysFromCivil(year, month, day int) int64 {
	y := int64(year)
	if month <= 2 {
		y--
	}
	era := y / 400
	if y < 0 && y%400 != 0 {
		era--
	}
	yoe := y - era*400
	m := int64(month)
	var mp int64
	if m > 2 {
		mp = m - 3
	} else {
		mp = m + 9
	}
	doy := (153*mp+2)/5 + int64(day) - 1
	doe := yoe*365 + yoe/4 - yoe/100 + doy
	// Days since 1970-01-01, then shifted to 0001-01-01 (719162 days earlier).
	return era*146097 + doe - 719468 + 719162
}

// civilFromDays is the date days after 0001-01-01.
func civilFromDays(days int64) (int, int, int) {
	z := days - 719162 + 719468
	era := z / 146097
	if z < 0 && z%146097 != 0 {
		era--
	}
	doe := z - era*146097
	yoe := (doe - doe/1460 + doe/36524 - doe/146096) / 365
	y := yoe + era*400
	doy := doe - (365*yoe + yoe/4 - yoe/100)
	mp := (5*doy + 2) / 153
	d := doy - (153*mp+2)/5 + 1
	var m int64
	if mp < 10 {
		m = mp + 3
	} else {
		m = mp - 9
	}
	if m <= 2 {
		y++
	}
	return int(y), int(m), int(d)
}

// UUID is a UUID in RFC 4122 byte order, as it is written.
type UUID [16]byte

// ParseUUID reads a hyphenated UUID (8-4-4-4-12 hexadecimal digits, either case).
func ParseUUID(text string) (UUID, error) {
	var u UUID
	if len(text) != 36 || text[8] != '-' || text[13] != '-' || text[18] != '-' || text[23] != '-' {
		return u, fmt.Errorf("'%s' is not a hyphenated UUID", text)
	}
	compact := text[0:8] + text[9:13] + text[14:18] + text[19:23] + text[24:36]
	if _, err := hex.Decode(u[:], []byte(compact)); err != nil {
		return UUID{}, fmt.Errorf("'%s' is not a hyphenated UUID", text)
	}
	return u, nil
}

// MustParseUUID is ParseUUID for a constant known to be valid; it panics otherwise.
func MustParseUUID(text string) UUID {
	u, err := ParseUUID(text)
	if err != nil {
		panic(err)
	}
	return u
}

// String is the UUID as the contract writes one: lower-case, hyphenated.
func (u UUID) String() string {
	h := hex.EncodeToString(u[:])
	return h[0:8] + "-" + h[8:12] + "-" + h[12:16] + "-" + h[16:20] + "-" + h[20:32]
}

// IsZero reports whether the UUID is the empty one (all zeros).
func (u UUID) IsZero() bool { return u == UUID{} }

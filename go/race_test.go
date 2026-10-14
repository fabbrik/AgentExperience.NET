//go:build race

package agentexperience

// raceEnabled: the race detector makes sync.Pool drop entries at random, so allocation bounds do not hold.
const raceEnabled = true

namespace AgentExperience.CiTriage;

/// <summary>
/// The replay's clock: wall-clock time is the event being replayed (a failure's time, a green run's time), set by the
/// replay and never moved by anything else. Monotonic time and timers stay real, so the library's timeouts still work;
/// capture's retention is raised anyway, so no simulated jump could expire a captured run.
/// </summary>
public sealed class SimulatedClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Moves the clock to <paramref name="instant"/>. Time never runs backwards.</summary>
    public void AdvanceTo(DateTimeOffset instant)
    {
        if (instant < _now)
        {
            throw new InvalidOperationException($"The replay clock cannot move back from {_now:O} to {instant:O}.");
        }

        _now = instant;
    }
}

namespace AgentExperience.Sample.EndToEnd.Fixtures;

/// <summary>
/// A demonstration fixture, not a clock for real use: every reading is the previous one plus a
/// fixed step, so timestamps and durations are the same on every run of this sample.
/// </summary>
/// <remarks>
/// <para>
/// A real host leaves every <c>TimeProvider</c> at <see cref="TimeProvider.System"/>. This one is
/// wired into <see cref="AgentExperience.MicrosoftAgentFramework.ExperienceCaptureOptions.TimeProvider"/>,
/// <see cref="AgentExperience.MicrosoftAgentFramework.Injection.ExperienceInjectionOptions.TimeProvider"/>,
/// and retrieval's clock, which is what makes the transcript assertable.
/// </para>
/// <para>
/// <see cref="CreateTimer"/> deliberately delegates to the real system clock. The library's
/// timeouts -- retrieval's, capture's finalization bound, injection's eligibility re-check -- are
/// safety bounds on work that can hang, and a fixture that never fires a timer would turn a hung
/// PostgreSQL call into a hung sample.
/// </para>
/// </remarks>
internal sealed class SteppingTimeProvider : TimeProvider
{
    private readonly long _stepTicks;
    private long _ticks;

    /// <summary>Creates a clock that starts at <paramref name="start"/> and advances by <paramref name="step"/> per reading.</summary>
    /// <param name="start">The first instant this clock reports.</param>
    /// <param name="step">How far the clock moves between readings. Must be strictly positive.</param>
    public SteppingTimeProvider(DateTimeOffset start, TimeSpan step)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, TimeSpan.Zero);

        _ticks = start.UtcTicks;
        _stepTicks = step.Ticks;
    }

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => new(Advance(), TimeSpan.Zero);

    /// <inheritdoc />
    public override long GetTimestamp() => Advance();

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        TimeProvider.System.CreateTimer(callback, state, dueTime, period);

    /// <summary>Returns the current reading and moves the clock on by one step. Thread-safe.</summary>
    private long Advance() => Interlocked.Add(ref _ticks, _stepTicks) - _stepTicks;
}

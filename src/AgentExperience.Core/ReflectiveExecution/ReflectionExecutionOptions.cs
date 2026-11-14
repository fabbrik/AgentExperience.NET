namespace AgentExperience.Core.ReflectiveExecution;

/// <summary>
/// Limits and policy for <see cref="ReflectionExecutionCoordinator"/>. Disabled by default: with
/// <see cref="Enabled"/> <see langword="false"/> the coordinator runs the one attempt and stops.
/// </summary>
public sealed record ReflectionExecutionOptions
{
    /// <summary>Whether reflective retries run at all. Default <see langword="false"/>.</summary>
    public bool Enabled { get; init; }

    /// <summary>The most corrections (reflection cycles) after the first attempt. Default 2; at least 0.</summary>
    public int MaxReflectionCycles { get; init; } = 2;

    /// <summary>The most attempts in total, the first included. Default 3; at least 1.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>The longest the whole execution may take, measured on <see cref="TimeProvider"/>. Default 5 minutes.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The most tool calls across all attempts, where attempts report them; <see langword="null"/> for no bound.</summary>
    public int? MaxToolCalls { get; init; }

    /// <summary>The most tokens across all attempts, where the provider reports them; <see langword="null"/> for no bound.</summary>
    public long? MaxTokens { get; init; }

    /// <summary>How long one evaluator or authorizer call may take. Default 30 seconds.</summary>
    public TimeSpan EvaluationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether a semantic retry is allowed for a task that already acted. Default <see cref="SideEffectPolicy.NeverRetry"/>.</summary>
    public SideEffectPolicy SideEffects { get; init; } = SideEffectPolicy.NeverRetry;

    /// <summary>The longest correction note handed to an attempt, in UTF-16 code units. Default 1,000.</summary>
    public int MaxCorrectionLength { get; init; } = 1_000;

    /// <summary>The time source for <see cref="MaxDuration"/>.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Receives each <see cref="ReflectionStep"/> as it happens. Counts and enums only. An exception from it is swallowed:
    /// observing the loop never changes it.
    /// </summary>
    public Action<ReflectionStep>? OnStep { get; init; }

    /// <summary>Throws when a limit is out of range.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A bound is not positive where it must be.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaxReflectionCycles);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(EvaluationTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxCorrectionLength, 1);
        if (MaxToolCalls is < 0) { throw new ArgumentOutOfRangeException(nameof(MaxToolCalls)); }
        if (MaxTokens is < 0) { throw new ArgumentOutOfRangeException(nameof(MaxTokens)); }
        ArgumentNullException.ThrowIfNull(TimeProvider);
    }
}

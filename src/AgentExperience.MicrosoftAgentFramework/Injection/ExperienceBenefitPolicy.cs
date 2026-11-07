using AgentExperience.Abstractions;
using AgentExperience.Core.Holdout;

namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>
/// Acting on measured benefit (<see cref="ExperienceInjectionOptions.Benefit"/>): the provider keeps a cached estimate of
/// the lessons' benefit per scope and principal, read from the injection holdout as
/// <see cref="AgentExperienceBenefit.EstimateAsync"/> reads it, and lets the verdicts decide what is injected. Off by
/// default (<see cref="Enabled"/> <see langword="false"/>): nothing is read, cached, reordered or omitted.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it does.</b> A lesson measured <see cref="ExperienceBenefitVerdict.Hurts"/> is omitted before selection as
/// <see cref="InjectionOmissionReason.MeasuredHarmful"/>; when the scope's overall verdict is
/// <see cref="ExperienceBenefitVerdict.Hurts"/> every record is (withdrawal notices still go out). With
/// <see cref="PreferMeasuredHelpful"/>, lessons measured <see cref="ExperienceBenefitVerdict.Helps"/> move ahead of the
/// rest, each group keeping its retrieval order, before the record limit. <see cref="ExperienceBenefitVerdict.NoEvidence"/>
/// changes nothing, and neither does an estimate the store could not give (not tracked, or refused).
/// </para>
/// <para>
/// <b>It never delays or fails an invocation.</b> The estimate is cached per request scope and the authorization's
/// tenant and principal, so two principals never share one. An invocation uses what is cached as it is (nothing yet
/// means no effect) and, when it is missing or older than <see cref="RefreshInterval"/>, starts one background refresh
/// for that key. A refresh that throws, or runs past <see cref="RefreshTimeout"/>, keeps the previous estimate and is
/// reported only through telemetry, as the <c>benefit_refresh</c> operation.
/// </para>
/// <para>
/// <b>Verdicts can reverse.</b> Only assignments made within <see cref="Window"/> of a refresh count, so old evidence ages
/// out. The holdout itself (<see cref="ExperienceInjectionOptions.Holdout"/>) runs exactly as without this policy, and is
/// what supplies new evidence. A scope whose lessons are all withheld as harmful assigns no further runs, so its verdict
/// returns to <see cref="ExperienceBenefitVerdict.NoEvidence"/> once its evidence leaves the window, and its lessons are
/// injected, and measured, again.
/// </para>
/// <para>
/// Nothing here changes a stored record, its confidence or retrieval's scoring. An invalid value is refused with an
/// <see cref="ArgumentOutOfRangeException"/> when the provider is constructed, even with the policy off.
/// </para>
/// </remarks>
public sealed record ExperienceBenefitPolicy
{
    /// <summary>The default <see cref="RefreshInterval"/>: 10 minutes.</summary>
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(10);

    /// <summary>The default <see cref="RefreshTimeout"/>: 30 seconds.</summary>
    public static readonly TimeSpan DefaultRefreshTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The default <see cref="Window"/>: 30 days.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(30);

    /// <summary>The largest permitted <see cref="RefreshTimeout"/>: one day.</summary>
    public static readonly TimeSpan MaxRefreshTimeout = TimeSpan.FromDays(1);

    /// <summary>The policy switched off: the defaults, with <see cref="Enabled"/> <see langword="false"/>.</summary>
    public static ExperienceBenefitPolicy Off { get; } = new();

    /// <summary>Whether injection acts on measured benefit at all. <see langword="false"/> by default.</summary>
    public bool Enabled { get; init; }

    /// <summary>How old a cached estimate may grow before an invocation starts a refresh. Strictly positive; 10 minutes by default.</summary>
    public TimeSpan RefreshInterval { get; init; } = DefaultRefreshInterval;

    /// <summary>
    /// How long one refresh may take, on <see cref="ExperienceInjectionOptions.TimeProvider"/>, before it is abandoned and the
    /// previous estimate kept. Strictly positive and at most <see cref="MaxRefreshTimeout"/>; 30 seconds by default.
    /// </summary>
    public TimeSpan RefreshTimeout { get; init; } = DefaultRefreshTimeout;

    /// <summary>
    /// How far back, from each refresh, holdout assignments count (by <see cref="HoldoutOutcomeRow.AssignedAt"/>). Strictly
    /// positive; 30 days by default.
    /// </summary>
    public TimeSpan Window { get; init; } = DefaultWindow;

    /// <summary>
    /// The fewest runs with a verified outcome each arm needs before a verdict is anything but
    /// <see cref="ExperienceBenefitVerdict.NoEvidence"/> (<see cref="ExperienceBenefitOptions.MinimumRunsPerArm"/>). At least
    /// 1; <see cref="ExperienceBenefitOptions.DefaultMinimumRunsPerArm"/> by default.
    /// </summary>
    public int MinimumRunsPerArm { get; init; } = ExperienceBenefitOptions.DefaultMinimumRunsPerArm;

    /// <summary>
    /// Whether lessons measured <see cref="ExperienceBenefitVerdict.Helps"/> move ahead of the others, each group keeping its
    /// retrieval order, before the record limit. <see langword="false"/> by default.
    /// </summary>
    public bool PreferMeasuredHelpful { get; init; }

    /// <summary>
    /// Whether the policy acts on the verdicts for the invoking agent's own model rather than the pooled ones. When
    /// <see langword="true"/> the estimate is split by model (<see cref="ExperienceBenefitOptions.SplitByModel"/>), and an
    /// invocation reads its model as capture records it (<see cref="HoldoutOutcomeRow.ModelMetadataKey"/>: the host's value
    /// when its environment sets one, otherwise the agent's chat client's <c>ChatClientMetadata.DefaultModelId</c>). The
    /// scope's verdict is then the one for that consuming model, and a lesson's is pooled over every producing model for
    /// that consuming model. Where that group has <see cref="ExperienceBenefitVerdict.NoEvidence"/> (or the model is not
    /// known), the pooled verdict is used. <see langword="false"/> by default: every agent acts on the pooled verdicts.
    /// </summary>
    public bool PerModel { get; init; }

    /// <summary>The estimator's options this policy decides verdicts with.</summary>
    internal ExperienceBenefitOptions EstimatorOptions => new() { MinimumRunsPerArm = MinimumRunsPerArm, SplitByModel = PerModel };

    /// <summary>Refuses an interval, timeout or window that is not strictly positive, a timeout over the maximum, and a minimum below 1.</summary>
    internal void Validate(string paramName)
    {
        if (RefreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException($"{paramName}.{nameof(RefreshInterval)}", RefreshInterval, "The refresh interval must be strictly positive.");
        }

        if (RefreshTimeout <= TimeSpan.Zero || RefreshTimeout > MaxRefreshTimeout)
        {
            throw new ArgumentOutOfRangeException($"{paramName}.{nameof(RefreshTimeout)}", RefreshTimeout, $"The refresh timeout must be strictly positive and at most {MaxRefreshTimeout}.");
        }

        if (Window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException($"{paramName}.{nameof(Window)}", Window, "The window must be strictly positive.");
        }

        if (MinimumRunsPerArm < 1)
        {
            throw new ArgumentOutOfRangeException($"{paramName}.{nameof(MinimumRunsPerArm)}", MinimumRunsPerArm, "The minimum runs per arm must be at least 1.");
        }
    }
}

namespace AgentExperience.Core.Finalization;

/// <summary>
/// The host's decision on whether a finalized run may be persisted at all, carried in a
/// <see cref="FinalizeExperienceRequest"/>.
/// </summary>
/// <remarks>
/// <para>
/// Storage policy belongs to the host, not to this library: only the host knows its retention rules,
/// its data-residency obligations, and its own risk appetite. Core therefore calls no policy port and
/// makes no policy decision of its own -- it reads this value and obeys it. A decision that does not
/// permit storage stops finalization before any store call, so nothing at all is written, whatever
/// the run's verification says.
/// </para>
/// <para>
/// <see cref="Reason"/> is content-free: it is surfaced back to the host on the finalization result
/// and must never carry record payload, captured content, or private reasoning.
/// </para>
/// </remarks>
/// <param name="Permitted"><see langword="true"/> when the host permits this run to be persisted as an Experience Record.</param>
/// <param name="Reason">Optional, auditable, content-free explanation of the decision (most usefully, why storage was denied).</param>
public sealed record StorageDecision(bool Permitted, string? Reason = null)
{
    /// <summary>A decision that permits storage, with no reason attached.</summary>
    public static StorageDecision Permit { get; } = new(Permitted: true);

    /// <summary>Creates a decision that denies storage.</summary>
    /// <param name="reason">A content-free explanation of why storage was denied.</param>
    public static StorageDecision Deny(string? reason = null) => new(Permitted: false, reason);
}

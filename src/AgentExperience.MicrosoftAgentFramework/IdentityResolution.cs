using Microsoft.Agents.AI;

namespace AgentExperience.MicrosoftAgentFramework;

/// <summary>
/// The outcome of the one-call setup's identity step for the invocation on the current async flow: an identity, none,
/// or a failure. Capture resolves it and injection reuses it, so the host's resolver runs once per invocation.
/// </summary>
/// <param name="agent">The agent the step ran for.</param>
/// <param name="identity">The resolved identity, or <see langword="null"/> for none (or a failure).</param>
/// <param name="failure">Why the step failed, or <see langword="null"/>.</param>
internal sealed class IdentityResolution(AIAgent agent, ExperienceIdentity? identity, Exception? failure)
{
    private static readonly AsyncLocal<IdentityResolution?> CurrentResolution = new();

    /// <summary>The outcome for the invocation executing on the current async flow, if capture resolved one.</summary>
    internal static IdentityResolution? Current
    {
        get => CurrentResolution.Value;
        set => CurrentResolution.Value = value;
    }

    /// <summary>The resolved identity; <see langword="null"/> when there is none or the step failed.</summary>
    internal ExperienceIdentity? Identity { get; } = identity;

    /// <summary>Why the step failed, or <see langword="null"/>.</summary>
    internal Exception? Failure { get; } = failure;

    /// <summary>Whether this outcome was resolved for <paramref name="other"/>, the agent a context provider runs for.</summary>
    internal bool For(AIAgent? other) => CaptureScope.SameAgent(agent, other);
}

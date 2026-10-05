using AgentExperience.Core.Capture;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.MicrosoftAgentFramework;

/// <summary>The agent half of the one-call setup.</summary>
public static class AgentExperienceAgentBuilderExtensions
{
    /// <summary>
    /// Captures every invocation of the agent and, when <see cref="AgentExperienceOptions.Verify"/> is set, verifies and
    /// stores each completed run, all as configured by <c>services.AddAgentExperience(…)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This alone captures but injects nothing.</b> Injection is the other half: give the agent the container's
    /// context provider, so it is handed the lessons that apply before the model is called, and so each run records
    /// what it was given:
    /// </para>
    /// <code>
    /// AIAgent agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions { AIContextProviders = [services.GetAgentExperienceContextProvider()] })
    ///     .AsBuilder()
    ///     .UseAgentExperience(services)
    ///     .Build();
    /// </code>
    /// <para>
    /// For each invocation the identity is resolved first; with none, the invocation is neither captured nor injected.
    /// The run is described by the task identifier, the identity's scope, and the user's own words
    /// (<see cref="ExperienceRunContext.DerivedTaskText"/>, stored as written). Nothing here throws into the agent:
    /// failures go to <see cref="ExperienceCaptureOptions.OnCaptureFailure"/>, set through
    /// <see cref="AgentExperienceOptions.Capture"/>. The capture is disposed with the container.
    /// </para>
    /// <para>
    /// The container holds one context provider, so every agent built this way gets the same injection configuration;
    /// for per-agent injection settings, construct an <see cref="Injection.ExperienceContextProvider"/> per agent (the
    /// explicit wiring).
    /// </para>
    /// </remarks>
    /// <param name="builder">The agent builder.</param>
    /// <param name="services">The service provider built from the collection <c>AddAgentExperience</c> was called on.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><c>AddAgentExperience</c> was not called, or no storage was chosen.</exception>
    public static AIAgentBuilder UseAgentExperience(this AIAgentBuilder builder, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(services);

        var registration = AgentExperienceRegistration.From(services);
        var options = registration.CreateCaptureOptions(services);
        builder.UseExperienceCapture(services.GetRequiredService<IExperienceCaptureService>(), options, out var lifetime);

        // Disposed with the container, which stops this agent's open-run timers and any late finalization.
        services.GetRequiredService<AgentExperienceLifetimes>().Add(lifetime);
        return builder;
    }
}

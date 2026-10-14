using AgentExperience.Core.Capture;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.MicrosoftAgentFramework;

/// <summary>The agent half of the one-call setup.</summary>
public static class AgentExperienceAgentBuilderExtensions
{
    /// <summary>
    /// Captures every invocation of the agent, injects the lessons that apply before the model is called, and, when
    /// <see cref="AgentExperienceOptions.Verify"/> is set, verifies and stores each completed run, all as configured by
    /// <c>services.AddAgentExperience(…)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One call wires both halves.</b>
    /// </para>
    /// <code>
    /// // The name, reduced to "triage-bot", is the task identifier runs are stored under unless TaskId is set.
    /// AIAgent agent = new ChatClientAgent(chatClient, name: "Triage Bot", instructions: "…")
    ///     .AsBuilder()
    ///     .UseAgentExperience(services)
    ///     .Build();
    /// </code>
    /// <para>
    /// When the agent was not given an <see cref="ExperienceContextProvider"/> on its options (any one: the check is
    /// deliberately by type, to avoid double injection), and no earlier <c>UseAgentExperience</c> on the same pipeline
    /// wired it already, injection is wired at the agent level, through MAF's <see cref="AIAgentBuilder.UseAIContextProviders"/>, with the container's context
    /// provider: the same block, the same session account. Over a <see cref="ChatClientAgent"/> it sees what the agent
    /// would hand it on its options: the chat history the agent replays (so a short follow-up is joined with an earlier
    /// turn's request), and the tools a run adds through its run options (with
    /// <see cref="ReceivingAgentCapabilities.UseRunTools"/> a run-only tool counts). The block is the one the provider
    /// gives on the agent's options, byte for byte, read by the agent's own rules (no history under a conversation ID,
    /// per-service-call persistence, or a continued background response, which gets no injection either). The agent's
    /// <see cref="InMemoryChatHistoryProvider"/> is read without side effects, as stored: a reducer that runs before
    /// retrieval is not run again, so if it would drop the earlier turn the join still reads it. Any other history
    /// provider is asked as the agent asks it, once more per run, bounded at 500 milliseconds; a read that fails or times
    /// out joins nothing and adds no failure to the run. Messages and tools another context provider on the agent's
    /// options adds are not seen here; an inner agent that is not a
    /// <see cref="ChatClientAgent"/> offers the caller's messages only, and the tools it reports through
    /// <see cref="AIAgent.GetService"/> as <c>ChatOptions</c>. Giving the agent the provider on its options as well, as
    /// below, adds nothing here, and the agent still gets exactly one block:
    /// </para>
    /// <code>
    /// AIAgent agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions { AIContextProviders = [services.GetAgentExperienceContextProvider()] })
    ///     .AsBuilder()
    ///     .UseAgentExperience(services)
    ///     .Build();
    /// </code>
    /// <para>
    /// <see cref="AgentExperienceOptions.InjectWithUseAgentExperience"/> set to <see langword="false"/> turns the
    /// agent-level injection off, so an agent without the provider on its options is captured but not injected, as
    /// before this call wired injection.
    /// </para>
    /// <para>
    /// For each invocation the identity is resolved first; with none, the invocation is neither captured nor injected.
    /// The run is described by the task identifier (<see cref="AgentExperienceOptions.TaskId"/>, else the agent's
    /// name), the identity's scope, and the user's own words (<see cref="ExperienceRunContext.DerivedTaskText"/>,
    /// stored as written). Nothing here throws into the agent: failures go to
    /// <see cref="ExperienceCaptureOptions.OnCaptureFailure"/>, set through <see cref="AgentExperienceOptions.Capture"/>.
    /// The capture is disposed with the container.
    /// </para>
    /// <para>
    /// The container holds one context provider, so every agent built this way gets the same injection configuration;
    /// for per-agent injection settings, construct an <see cref="ExperienceContextProvider"/> per agent (the explicit
    /// wiring).
    /// </para>
    /// </remarks>
    /// <param name="builder">The agent builder.</param>
    /// <param name="services">The service provider built from the collection <c>AddAgentExperience</c> was called on.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <c>AddAgentExperience</c> was not called, no storage was chosen, or
    /// <see cref="AgentExperienceOptions.UseDevelopmentIdentity"/> is used outside a development or test environment.
    /// The context provider is resolved only when injection is wired at the agent level, when the agent is built, so a
    /// failure to resolve it surfaces from <see cref="AIAgentBuilder.Build"/>.
    /// </exception>
    public static AIAgentBuilder UseAgentExperience(this AIAgentBuilder builder, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(services);

        var registration = AgentExperienceRegistration.From(services);
        var options = registration.CreateCaptureOptions(services);
        builder.UseExperienceCapture(services.GetRequiredService<IExperienceCaptureService>(), options, out var lifetime);

        if (registration.Options.InjectWithUseAgentExperience)
        {
            // Inside capture, so injection runs on the flow capture resolved the identity on, and reuses it. The
            // provider is resolved only when it is wired, at build time.
            builder.Use(inner => HoldsExperienceProvider(inner)
                ? inner
                : WithInjection(inner, services.GetAgentExperienceContextProvider()));
        }

        // Disposed with the container, which stops this agent's open-run timers and any late finalization.
        services.GetRequiredService<AgentExperienceLifetimes>().Add(lifetime);
        return builder;
    }

    /// <summary>
    /// Whether <paramref name="agent"/> already injects: its chat client agent holds any
    /// <see cref="ExperienceContextProvider"/> on its options (the container's or a hand-built one, deliberately), or an
    /// earlier <c>UseAgentExperience</c> on the same pipeline already wired one at the agent level. Adding another
    /// would put two blocks in one request.
    /// </summary>
    internal static bool HoldsExperienceProvider(AIAgent agent) =>
        agent.GetService<ExperienceMessageContextProvider>() is not null
        || agent.GetService<ChatClientAgent>()?.AIContextProviders?.Any(provider => provider is ExperienceContextProvider) == true;

    /// <summary><paramref name="inner"/> with <paramref name="provider"/> wired at the agent level, through MAF's own extension.</summary>
    private static AIAgent WithInjection(AIAgent inner, ExperienceContextProvider provider)
    {
        var adapter = new ExperienceMessageContextProvider(provider);
        return new ExperienceInjectionAgent(new AIAgentBuilder(inner).UseAIContextProviders(adapter).Build(), adapter);
    }

    /// <summary>
    /// Marks the agent-level injection, so a later <c>UseAgentExperience</c> on the same pipeline finds it through
    /// <see cref="AIAgent.GetService"/> and adds no second one. Forwards everything else unchanged.
    /// </summary>
    private sealed class ExperienceInjectionAgent(AIAgent inner, ExperienceMessageContextProvider adapter) : DelegatingAIAgent(inner)
    {
        public override object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType == typeof(ExperienceMessageContextProvider) ? adapter : base.GetService(serviceType, serviceKey);
    }
}

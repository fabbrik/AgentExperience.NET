using AgentExperience.MicrosoftAgentFramework.ReflectiveExecution;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers and applies reflective execution (experimental): an optional loop that evaluates an attempt, proposes a
/// corrected strategy and retries within hard budgets and only with the host's authorization. See
/// <c>docs/guide/reflective-execution.md</c>.
/// </summary>
public static class ReflectiveExecutionExtensions
{
    /// <summary>
    /// Registers the options. Call it with <c>AddAgentExperience</c>, before the agent is built. Nothing retries until
    /// <see cref="AgentExperience.Core.ReflectiveExecution.ReflectionExecutionOptions.Enabled"/> is set, an evaluator is
    /// given and the <see cref="AgentExperience.Core.ReflectiveExecution.IRetryAuthorizer"/> allows it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the options; called once, here.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddReflectiveExecution(this IServiceCollection services, Action<ReflectiveExecutionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new ReflectiveExecutionOptions();
        configure?.Invoke(options);
        options.Limits.Validate();
        services.TryAddSingleton(new ReflectiveExecutionRegistration(options));
        return services;
    }

    /// <summary>
    /// Wraps the agent with the reflection loop. It must be the <b>outermost</b> layer: call it first on the builder,
    /// before <c>UseAgentExperience(services)</c>, because every attempt must be one captured invocation. With it
    /// disabled the wrapper is a pass-through.
    /// </summary>
    /// <param name="builder">The agent builder.</param>
    /// <param name="services">The provider built from the collection <c>AddReflectiveExecution</c> was called on.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException"><c>AddReflectiveExecution</c> was not called.</exception>
    public static AIAgentBuilder UseReflectiveExecution(this AIAgentBuilder builder, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(services);
        var registration = services.GetService(typeof(ReflectiveExecutionRegistration)) as ReflectiveExecutionRegistration
            ?? throw new InvalidOperationException("AddReflectiveExecution was not called on the service collection.");
        return builder.Use(inner => new ReflectiveExecutionAgent(inner, registration));
    }
}

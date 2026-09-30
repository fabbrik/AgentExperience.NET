using AgentExperience.Core.Reflections;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.MicrosoftAgentFramework.Reflections;

/// <summary>Registers the optional <see cref="ChatClientExperienceReflector"/>.</summary>
public static class ChatClientReflectorServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="ChatClientExperienceReflector"/> over a registered <see cref="IChatClient"/> as the
    /// <see cref="IExperienceReflector"/>, a singleton.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Opt-in.</b> Nothing registers this reflector by default: without this call,
    /// <c>AddAgentExperienceCore</c> registers <see cref="DefaultExperienceReflector"/>. With it -- before or
    /// after <c>AddAgentExperienceCore</c>, which only adds a reflector when none is registered -- every
    /// finalized, verified run is reflected on by the model.
    /// </para>
    /// <para>
    /// <b>Nothing is evicted silently.</b> A registered <see cref="DefaultExperienceReflector"/> is replaced. Any
    /// other unkeyed <see cref="IExperienceReflector"/> registration -- the host's own, or an earlier call to this
    /// method -- makes this call throw, unless <paramref name="replaceExisting"/> is <see langword="true"/>. An
    /// <see cref="IExperienceReflector"/> the host registers <em>after</em> this call wins, as the last
    /// registration always does. Keyed reflector registrations are left alone.
    /// </para>
    /// <para>
    /// <b>Privacy.</b> This sends sanitized captured run content to the model provider behind the
    /// <see cref="IChatClient"/>. See <see cref="ChatClientExperienceReflector"/>.
    /// </para>
    /// <para>
    /// <b>The chat client.</b> The reflector is a singleton and resolves the <see cref="IChatClient"/> once, from
    /// the root provider, when it is itself first resolved: keyed by <paramref name="chatClientServiceKey"/> when
    /// one is given, with no fallback to the unkeyed registration, and unkeyed otherwise. A scoped
    /// <see cref="IChatClient"/> registration is refused -- here when it is already registered, or when the
    /// reflector is resolved -- because a singleton would capture one scope's instance. Resolving fails when no
    /// matching client is registered.
    /// </para>
    /// <para>The options are configured and checked now, so a bad value fails here rather than at first use.</para>
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Adjusts the reflector's options; <see langword="null"/> keeps the defaults.</param>
    /// <param name="chatClientServiceKey">The service key of a keyed <see cref="IChatClient"/> registration, or <see langword="null"/> for the unkeyed one.</param>
    /// <param name="replaceExisting"><see langword="true"/> to replace an unkeyed <see cref="IExperienceReflector"/> registration other than the default one.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The configured options are invalid.</exception>
    /// <exception cref="InvalidOperationException">Another reflector is registered and <paramref name="replaceExisting"/> is <see langword="false"/>, or the matching <see cref="IChatClient"/> is registered as scoped.</exception>
    public static IServiceCollection AddAgentExperienceChatClientReflector(
        this IServiceCollection services,
        Action<ChatClientExperienceReflectorOptions>? configure = null,
        object? chatClientServiceKey = null,
        bool replaceExisting = false)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ChatClientExperienceReflectorOptions();
        configure?.Invoke(options);
        options = options.Snapshot();

        var existing = services.Where(d => d.ServiceType == typeof(IExperienceReflector) && !d.IsKeyedService).ToList();
        if (!replaceExisting && existing.Any(d => d.ImplementationType != typeof(DefaultExperienceReflector)))
        {
            throw new InvalidOperationException(
                "An IExperienceReflector other than DefaultExperienceReflector is already registered. Pass replaceExisting: true to replace it with the model-backed reflector.");
        }

        EnsureNotScoped(services, chatClientServiceKey);

        foreach (var descriptor in existing)
        {
            services.Remove(descriptor);
        }

        services.AddSingleton<IExperienceReflector>(provider =>
        {
            EnsureNotScoped(services, chatClientServiceKey);
            return new ChatClientExperienceReflector(
                chatClientServiceKey is null
                    ? provider.GetRequiredService<IChatClient>()
                    : provider.GetRequiredKeyedService<IChatClient>(chatClientServiceKey),
                options);
        });

        return services;
    }

    /// <summary>Throws when the <see cref="IChatClient"/> registration the reflector would resolve is scoped.</summary>
    private static void EnsureNotScoped(IServiceCollection services, object? serviceKey)
    {
        var registration = services.LastOrDefault(d =>
            d.ServiceType == typeof(IChatClient)
            && (serviceKey is null ? !d.IsKeyedService : d.IsKeyedService && Equals(d.ServiceKey, serviceKey)));

        if (registration?.Lifetime == ServiceLifetime.Scoped)
        {
            throw new InvalidOperationException(
                "The IChatClient the model-backed reflector would use is registered as scoped; the reflector is a singleton and would capture one scope's client. Register the IChatClient as a singleton (or transient), or give the reflector a keyed singleton client.");
        }
    }
}

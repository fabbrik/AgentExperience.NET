using AgentExperience.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AgentExperience.Storage.InMemory.DependencyInjection;

/// <summary>
/// Registers the in-memory stores in a <see cref="IServiceCollection"/>. <b>For development and tests only</b>: data
/// is lost when the process ends, and none of the PostgreSQL guarantees apply (append-only enforcement, erasure
/// reach, backups, the two database roles, crypto-shredding).
/// </summary>
public static class AgentExperienceInMemoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="InMemoryExperienceRecordStore"/> as the singleton <see cref="IExperienceRecordStore"/>,
    /// <see cref="InMemoryExperienceCandidateSource"/> over that same store as the singleton
    /// <see cref="IExperienceCandidateSource"/>, and <see cref="InMemoryExperienceReuseFeedbackStore"/> as the singleton
    /// <see cref="IExperienceReuseFeedbackStore"/>. <b>For development and tests only.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It runs only in a development or test environment.</b> The environment is allowed when its name is
    /// <c>Development</c>, <c>Test</c> or <c>Testing</c> (case-insensitive); every other name, a blank one and
    /// <c>Staging</c> included, is refused unless <see cref="InMemoryStorageOptions.AllowProductionEnvironment"/> is
    /// set. The name is the registered <see cref="IHostEnvironment"/>'s; with none registered (a plain console app, a
    /// test) it is the <c>DOTNET_ENVIRONMENT</c> variable, else <c>ASPNETCORE_ENVIRONMENT</c>, and with neither set
    /// there is nothing to check. A refused environment is caught twice: a hosted service this registers throws from
    /// <see cref="IHostedService.StartAsync"/>, so a Generic Host refuses to start, and resolving any of the stores
    /// throws <see cref="InvalidOperationException"/>, for a container used without a host. Constructing the stores
    /// directly is not guarded.
    /// </para>
    /// <para>
    /// <b>It will not mix with another record store.</b> The candidate source always searches the in-memory record
    /// store, so registering this after a different <see cref="IExperienceRecordStore"/> would leave a source that
    /// finds nothing the host writes; that throws here instead. The candidate source and the feedback store are added
    /// with <c>TryAdd</c>, so ones the host registered first are kept. Calling this again with no
    /// <paramref name="configure"/> changes nothing; calling it again with one throws, because the first call's options
    /// are already in force.
    /// </para>
    /// <para>
    /// Sharing grants, the grant access log, the embedding index, deletion, retention and encryption are not
    /// provided.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Sets <see cref="InMemoryStorageOptions"/>; called once, here.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A different <see cref="IExperienceRecordStore"/> is already registered, or this was already called and
    /// <paramref name="configure"/> is set.
    /// </exception>
    public static IServiceCollection AddAgentExperienceInMemoryStorageForDevelopment(
        this IServiceCollection services,
        Action<InMemoryStorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(InMemoryStorageRegistration)))
        {
            if (configure is not null)
            {
                throw new InvalidOperationException(
                    $"{nameof(AddAgentExperienceInMemoryStorageForDevelopment)} was already called, so its options are already set; "
                    + "pass the options delegate to the first call only.");
            }

            return services;
        }

        if (services.Any(descriptor => descriptor.ServiceType == typeof(IExperienceRecordStore)))
        {
            throw new InvalidOperationException(
                $"An {nameof(IExperienceRecordStore)} is already registered. {nameof(AddAgentExperienceInMemoryStorageForDevelopment)} "
                + "registers its own record store, which its candidate source searches; beside another record store that source "
                + "would find nothing the host writes. Register one storage package, not both.");
        }

        var options = new InMemoryStorageOptions();
        configure?.Invoke(options);
        var registration = new InMemoryStorageRegistration(options);

        services.AddSingleton(registration);
        services.AddSingleton<IHostedService>(provider => new InMemoryStorageEnvironmentCheck(registration, provider));

        services.TryAddSingleton(provider =>
        {
            registration.Check(provider);
            return new InMemoryExperienceRecordStore(provider.GetService<TimeProvider>());
        });

        services.TryAddSingleton<IExperienceRecordStore>(provider =>
        {
            registration.Check(provider);
            return provider.GetRequiredService<InMemoryExperienceRecordStore>();
        });

        services.TryAddSingleton<IExperienceCandidateSource>(provider =>
        {
            registration.Check(provider);
            return new InMemoryExperienceCandidateSource(provider.GetRequiredService<InMemoryExperienceRecordStore>());
        });

        services.TryAddSingleton<IExperienceReuseFeedbackStore>(provider =>
        {
            registration.Check(provider);
            return new InMemoryExperienceReuseFeedbackStore();
        });

        return services;
    }
}

/// <summary>
/// The options one registration was made with, and the environment check they govern. Registered as a service so a
/// second registration can tell it is one.
/// </summary>
internal sealed class InMemoryStorageRegistration(InMemoryStorageOptions options)
{
    /// <summary>The environment names the stores run in without the override. Compared case-insensitively.</summary>
    internal static readonly IReadOnlyList<string> AllowedEnvironments = ["Development", "Test", "Testing"];

    /// <summary>Throws when the environment is not a development or test one and the override is off.</summary>
    public void Check(IServiceProvider provider)
    {
        if (options.AllowProductionEnvironment)
        {
            return;
        }

        string? name;
        string source;
        if (provider.GetService<IHostEnvironment>() is { } environment)
        {
            name = environment.EnvironmentName;
            source = "the host environment";
        }
        else if (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") is { } dotnet)
        {
            name = dotnet;
            source = "DOTNET_ENVIRONMENT";
        }
        else if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") is { } aspnetcore)
        {
            name = aspnetcore;
            source = "ASPNETCORE_ENVIRONMENT";
        }
        else
        {
            // No host and no environment variable: a plain console app or a test, with nothing to check.
            return;
        }

        if (name is not null && AllowedEnvironments.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            "AgentExperience.Storage.InMemory is for development and tests only: its data is lost when the process ends, "
            + $"and none of the PostgreSQL guarantees apply. It runs only when the environment is {string.Join(", ", AllowedEnvironments)}, "
            + $"and {source} is '{name}'. Register a durable store (AgentExperience.Storage.Postgres), or, only if losing "
            + $"everything on restart is acceptable, set {nameof(InMemoryStorageOptions)}.{nameof(InMemoryStorageOptions.AllowProductionEnvironment)} "
            + "to true in AddAgentExperienceInMemoryStorageForDevelopment.");
    }
}

/// <summary>
/// Refuses to let a Generic Host start in an environment the in-memory stores may not run in, rather than waiting for
/// the first request to resolve a store.
/// </summary>
internal sealed class InMemoryStorageEnvironmentCheck(InMemoryStorageRegistration registration, IServiceProvider provider) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        registration.Check(provider);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

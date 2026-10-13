using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Shared source (story 21.1): the one rule that decides whether the process runs in a development or test environment.
// The in-memory store's registration and the MAF adapter's development identity both link this file, so the two
// development-only conveniences refuse exactly the same environments. Each project defines exactly one of the
// constants below, which puts the type in that project's own namespace.
#if AGENTEXPERIENCE_INMEMORY && AGENTEXPERIENCE_MAF
#error Define exactly one of AGENTEXPERIENCE_INMEMORY and AGENTEXPERIENCE_MAF.
#elif AGENTEXPERIENCE_INMEMORY
namespace AgentExperience.Storage.InMemory.DependencyInjection;
#elif AGENTEXPERIENCE_MAF
namespace AgentExperience.MicrosoftAgentFramework;
#else
#error Define AGENTEXPERIENCE_INMEMORY or AGENTEXPERIENCE_MAF to select the namespace of the shared environment rule.
#endif

/// <summary>
/// Whether the process runs in a development or test environment. The environment is allowed when its name is
/// <c>Development</c>, <c>Test</c> or <c>Testing</c> (case-insensitive); every other name, a blank one and
/// <c>Staging</c> included, is not. The name is the registered <see cref="IHostEnvironment"/>'s; with none registered
/// (a plain console app, a test) it is the <c>DOTNET_ENVIRONMENT</c> variable, else <c>ASPNETCORE_ENVIRONMENT</c>,
/// and with neither set there is nothing to check, so it is allowed.
/// </summary>
internal static class DevelopmentEnvironment
{
    /// <summary>The environment names allowed. Compared case-insensitively.</summary>
    internal static readonly IReadOnlyList<string> AllowedEnvironments = ["Development", "Test", "Testing"];

    /// <summary>
    /// Whether the environment <paramref name="provider"/> runs in is allowed. When it is not, <paramref name="name"/>
    /// is the environment's name and <paramref name="source"/> says where it was read from, for the refusal's message.
    /// </summary>
    internal static bool IsAllowed(IServiceProvider provider, out string? name, out string source)
    {
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
            name = null;
            source = "nothing";
            return true;
        }

        return name is not null && AllowedEnvironments.Contains(name, StringComparer.OrdinalIgnoreCase);
    }
}

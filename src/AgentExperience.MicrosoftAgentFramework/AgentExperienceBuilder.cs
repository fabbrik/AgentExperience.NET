using AgentExperience.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.MicrosoftAgentFramework;

/// <summary>
/// What <see cref="AgentExperienceServiceCollectionExtensions.AddAgentExperience"/> returns: choose the storage on it,
/// with <c>.UseInMemoryStorageForDevelopment()</c> (<c>AgentExperience.Storage.InMemory</c>) or
/// <c>.UsePostgres(connectionString)</c> (<c>AgentExperience.Storage.Postgres</c>), exactly once.
/// </summary>
public interface IAgentExperienceBuilder : IAgentExperienceBuilder<IServiceCollection>
{
}

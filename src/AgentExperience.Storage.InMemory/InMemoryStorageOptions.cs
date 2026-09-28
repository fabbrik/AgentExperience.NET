namespace AgentExperience.Storage.InMemory;

/// <summary>
/// Options for
/// <see cref="DependencyInjection.AgentExperienceInMemoryServiceCollectionExtensions.AddAgentExperienceInMemoryStorageForDevelopment"/>.
/// The in-memory stores are <b>for development and tests only</b>: data is lost when the process ends, and none of
/// the PostgreSQL guarantees apply (append-only enforcement, erasure reach, backups, the two database roles,
/// crypto-shredding).
/// </summary>
public sealed class InMemoryStorageOptions
{
    /// <summary>
    /// Whether the stores may run in an environment other than <c>Development</c>, <c>Test</c> or <c>Testing</c>:
    /// Production, Staging, a custom name or a blank one. Off by default, and then the host refuses to start there and
    /// resolving any of the stores throws <see cref="InvalidOperationException"/>. Turn it on only for a deliberate
    /// reason, such as a demo deployed under the Production environment name, knowing that everything it stores is
    /// lost on restart.
    /// </summary>
    public bool AllowProductionEnvironment { get; set; }
}

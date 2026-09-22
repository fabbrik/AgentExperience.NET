using AgentExperience.Storage.Postgres.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Resolves what
/// <see cref="AgentExperiencePostgresServiceCollectionExtensions.AddAgentExperiencePostgresStore(IServiceCollection)"/>
/// registers out of a real container, so deleting the registration fails here rather than only at a
/// host's startup. No database is touched: registering a store does not open a connection.
/// </summary>
public class PostgresServiceRegistrationTests
{
    [Fact]
    public void The_store_is_resolved_from_a_data_source_in_the_container()
    {
        using var dataSource = TestRecords.Unreachable();

        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddAgentExperiencePostgresStore();

        using var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<IExperienceRecordStore>();
        Assert.IsType<PostgresExperienceRecordStore>(store);
        Assert.Same(store, provider.GetRequiredService<IExperienceRecordStore>()); // singleton
    }

    [Fact]
    public void The_overload_taking_a_data_source_needs_nothing_else_in_the_container()
    {
        using var dataSource = TestRecords.Unreachable();

        var services = new ServiceCollection();
        services.AddAgentExperiencePostgresStore(dataSource);

        using var provider = services.BuildServiceProvider();

        Assert.IsType<PostgresExperienceRecordStore>(provider.GetRequiredService<IExperienceRecordStore>());
    }

    [Fact]
    public void A_host_store_registered_first_wins()
    {
        using var dataSource = TestRecords.Unreachable();
        var hostStore = new PostgresExperienceRecordStore(dataSource);

        var services = new ServiceCollection();
        services.AddSingleton<IExperienceRecordStore>(hostStore);
        services.AddAgentExperiencePostgresStore(dataSource);

        using var provider = services.BuildServiceProvider();

        Assert.Same(hostStore, provider.GetRequiredService<IExperienceRecordStore>());
    }

    [Fact]
    public void The_candidate_source_is_resolved_independently_of_the_store()
    {
        using var dataSource = TestRecords.Unreachable();

        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddAgentExperiencePostgresCandidateSource();

        using var provider = services.BuildServiceProvider();

        // Two independent ports: a host that only searches never has to register the writer.
        var source = provider.GetRequiredService<IExperienceCandidateSource>();
        Assert.IsType<PostgresExperienceCandidateSource>(source);
        Assert.Same(source, provider.GetRequiredService<IExperienceCandidateSource>()); // singleton
        Assert.Null(provider.GetService<IExperienceRecordStore>());
    }

    [Fact]
    public void The_candidate_source_overload_taking_a_data_source_needs_nothing_else_in_the_container()
    {
        using var dataSource = TestRecords.Unreachable();
        var hostSource = new PostgresExperienceCandidateSource(dataSource);

        var services = new ServiceCollection();
        services.AddSingleton<IExperienceCandidateSource>(hostSource);
        services.AddAgentExperiencePostgresCandidateSource(dataSource);

        using var provider = services.BuildServiceProvider();

        Assert.Same(hostSource, provider.GetRequiredService<IExperienceCandidateSource>()); // registered first, so TryAdd keeps it
    }

    [Fact]
    public void Null_arguments_throw()
    {
        using var dataSource = TestRecords.Unreachable();

        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddAgentExperiencePostgresStore());
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddAgentExperiencePostgresStore(dataSource));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddAgentExperiencePostgresStore((NpgsqlDataSource)null!));
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddAgentExperiencePostgresCandidateSource());
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddAgentExperiencePostgresCandidateSource(dataSource));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddAgentExperiencePostgresCandidateSource((NpgsqlDataSource)null!));
    }
}

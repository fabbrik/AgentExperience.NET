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
    public void The_store_picks_up_a_registered_access_log_whichever_order_they_are_registered_in()
    {
        using var dataSource = TestRecords.Unreachable();

        var services = new ServiceCollection();
        services.AddSingleton(dataSource);

        // Deliberately before the store: the store is built from the container when it is first
        // resolved, so a host must not have to know which line comes first.
        services.AddAgentExperiencePostgresGrantAccessLog(_ => { }, ExperienceGrantAuditingMode.Required);
        services.AddAgentExperiencePostgresStore();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<PostgresExperienceGrantAccessLog>(provider.GetRequiredService<IExperienceGrantAccessLog>());

        var auditing = provider.GetRequiredService<ExperienceGrantAuditing>();
        Assert.Equal(ExperienceGrantAuditingMode.Required, auditing.Mode);
        Assert.Same(provider.GetRequiredService<IExperienceGrantAccessLog>(), auditing.Log);
        Assert.IsType<PostgresExperienceRecordStore>(provider.GetRequiredService<IExperienceRecordStore>());
    }

    [Fact]
    public void Auditing_is_off_unless_a_host_wires_it()
    {
        using var dataSource = TestRecords.Unreachable();

        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddAgentExperiencePostgresStore();

        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<ExperienceGrantAuditing>());
        Assert.Null(provider.GetService<IExperienceGrantAccessLog>());
        Assert.IsType<PostgresExperienceRecordStore>(provider.GetRequiredService<IExperienceRecordStore>());
    }

    [Fact]
    public void The_grant_store_takes_the_hosts_lifetime_policy_and_defaults_to_ninety_days()
    {
        using var dataSource = TestRecords.Unreachable();

        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddAgentExperiencePostgresGrantStore(new PostgresExperienceGrantPolicy(TimeSpan.FromDays(30)));

        using var provider = services.BuildServiceProvider();

        var grants = Assert.IsType<PostgresExperienceGrantStore>(provider.GetRequiredService<IExperienceGrantStore>());
        Assert.Equal(TimeSpan.FromDays(30), grants.Policy.MaxLifetime);

        // Registering nothing is still a policy: there is no unbounded option.
        Assert.Equal(TimeSpan.FromDays(90), new PostgresExperienceGrantStore(dataSource).Policy.MaxLifetime);
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
    public void The_grant_store_is_resolved_independently_of_the_record_store()
    {
        using var dataSource = TestRecords.Unreachable();

        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddAgentExperiencePostgresGrantStore();

        using var provider = services.BuildServiceProvider();

        // Another independent port: administering sharing is opt-in, and the reads that honour grants
        // do so in SQL whether or not a host ever registers this.
        var grants = provider.GetRequiredService<IExperienceGrantStore>();
        Assert.IsType<PostgresExperienceGrantStore>(grants);
        Assert.Same(grants, provider.GetRequiredService<IExperienceGrantStore>()); // singleton
        Assert.Null(provider.GetService<IExperienceRecordStore>());
    }

    [Fact]
    public void The_grant_store_overload_taking_a_data_source_needs_nothing_else_in_the_container()
    {
        using var dataSource = TestRecords.Unreachable();
        var hostGrants = new PostgresExperienceGrantStore(dataSource);

        var services = new ServiceCollection();
        services.AddSingleton<IExperienceGrantStore>(hostGrants);
        services.AddAgentExperiencePostgresGrantStore(dataSource);

        using var provider = services.BuildServiceProvider();

        Assert.Same(hostGrants, provider.GetRequiredService<IExperienceGrantStore>()); // registered first, so TryAdd keeps it
    }

    [Fact]
    public void The_reuse_feedback_ledger_is_resolved_independently_of_the_record_store()
    {
        using var dataSource = TestRecords.Unreachable();

        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddAgentExperiencePostgresReuseFeedbackStore();

        using var provider = services.BuildServiceProvider();

        // Recording reuse feedback is opt-in: a host that never does it never needs the ledger, and a
        // host that does still registers the record store separately for the confidence path.
        var ledger = provider.GetRequiredService<IExperienceReuseFeedbackStore>();
        Assert.IsType<PostgresExperienceReuseFeedbackStore>(ledger);
        Assert.Same(ledger, provider.GetRequiredService<IExperienceReuseFeedbackStore>()); // singleton
        Assert.Null(provider.GetService<IExperienceRecordStore>());
    }

    [Fact]
    public void The_reuse_feedback_overload_taking_a_data_source_needs_nothing_else_in_the_container()
    {
        using var dataSource = TestRecords.Unreachable();
        var hostLedger = new PostgresExperienceReuseFeedbackStore(dataSource);

        var services = new ServiceCollection();
        services.AddSingleton<IExperienceReuseFeedbackStore>(hostLedger);
        services.AddAgentExperiencePostgresReuseFeedbackStore(dataSource);

        using var provider = services.BuildServiceProvider();

        Assert.Same(hostLedger, provider.GetRequiredService<IExperienceReuseFeedbackStore>()); // registered first, so TryAdd keeps it
    }

    [Fact]
    public void Null_arguments_throw()
    {
        using var dataSource = TestRecords.Unreachable();

        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddAgentExperiencePostgresReuseFeedbackStore());
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddAgentExperiencePostgresReuseFeedbackStore(dataSource));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddAgentExperiencePostgresReuseFeedbackStore((NpgsqlDataSource)null!));
        Assert.Throws<ArgumentNullException>(() => new PostgresExperienceReuseFeedbackStore(null!));

        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddAgentExperiencePostgresGrantStore());
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddAgentExperiencePostgresGrantStore(dataSource));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddAgentExperiencePostgresGrantStore((NpgsqlDataSource)null!));
        Assert.Throws<ArgumentNullException>(() => new PostgresExperienceGrantStore(null!));

        Assert.Throws<ArgumentNullException>(() =>
            ((IServiceCollection)null!).AddAgentExperiencePostgresGrantAccessLog(_ => { }));
        Assert.Throws<ArgumentNullException>(() =>
            ((IServiceCollection)null!).AddAgentExperiencePostgresGrantAccessLog(dataSource, _ => { }));
        Assert.Throws<ArgumentNullException>(() =>
            new ServiceCollection().AddAgentExperiencePostgresGrantAccessLog(null!));
        Assert.Throws<ArgumentNullException>(() =>
            new ServiceCollection().AddAgentExperiencePostgresGrantAccessLog(dataSource, null!));
        Assert.Throws<ArgumentNullException>(() =>
            new ServiceCollection().AddAgentExperiencePostgresGrantAccessLog((NpgsqlDataSource)null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => new PostgresExperienceGrantAccessLog(null!));

        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddAgentExperiencePostgresStore());
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddAgentExperiencePostgresStore(dataSource));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddAgentExperiencePostgresStore((NpgsqlDataSource)null!));
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddAgentExperiencePostgresCandidateSource());
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddAgentExperiencePostgresCandidateSource(dataSource));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddAgentExperiencePostgresCandidateSource((NpgsqlDataSource)null!));
    }
}

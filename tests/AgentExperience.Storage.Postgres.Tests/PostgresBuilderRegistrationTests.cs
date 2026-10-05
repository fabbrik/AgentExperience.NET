using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// <c>UsePostgres</c> on the one-call setup's builder registers the data source, the record store and the candidate
/// source, out of a real container. No database is touched: registering a store does not open a connection.
/// </summary>
public sealed class PostgresBuilderRegistrationTests
{
    private const string ConnectionString = "Host=127.0.0.1;Port=1;Database=none;Username=app;Password=unused";

    [Fact]
    public void A_connection_string_registers_a_container_owned_data_source_and_the_stores_over_it()
    {
        var builder = new Builder();

        Assert.Same(builder, builder.UsePostgres(ConnectionString));

        using var provider = builder.Services.BuildServiceProvider();
        Assert.Contains("Username=app", provider.GetRequiredService<NpgsqlDataSource>().ConnectionString, StringComparison.Ordinal);
        Assert.IsType<PostgresExperienceRecordStore>(provider.GetRequiredService<IExperienceRecordStore>());
        Assert.IsType<PostgresExperienceCandidateSource>(provider.GetRequiredService<IExperienceCandidateSource>());

        // Registered through a factory, not as an instance, so the container that created it disposes it.
        var registration = Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(NpgsqlDataSource));
        Assert.NotNull(registration.ImplementationFactory);
        Assert.Null(registration.ImplementationInstance);
    }

    [Fact]
    public void A_data_source_is_used_as_given_and_left_to_its_owner()
    {
        using var dataSource = TestRecords.Unreachable();
        var builder = new Builder();

        builder.UsePostgres(dataSource);

        using (var provider = builder.Services.BuildServiceProvider())
        {
            Assert.Same(dataSource, provider.GetRequiredService<NpgsqlDataSource>());
            Assert.IsType<PostgresExperienceRecordStore>(provider.GetRequiredService<IExperienceRecordStore>());
            Assert.IsType<PostgresExperienceCandidateSource>(provider.GetRequiredService<IExperienceCandidateSource>());
        }

        using var connection = dataSource.CreateConnection();
    }

    [Fact]
    public void A_connection_string_beside_a_registered_data_source_is_refused_rather_than_ignored()
    {
        using var dataSource = TestRecords.Unreachable();
        var builder = new Builder();
        builder.Services.AddSingleton(dataSource);

        var refused = Assert.Throws<InvalidOperationException>(() => builder.UsePostgres(ConnectionString));
        Assert.Contains("UsePostgres(dataSource)", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Storage_is_chosen_once()
    {
        using var dataSource = TestRecords.Unreachable();

        var twice = new Builder();
        twice.UsePostgres(ConnectionString);
        Assert.Contains("already chosen", Assert.Throws<InvalidOperationException>(() => twice.UsePostgres(ConnectionString)).Message, StringComparison.Ordinal);
        Assert.Contains("already chosen", Assert.Throws<InvalidOperationException>(() => twice.UsePostgres(dataSource)).Message, StringComparison.Ordinal);

        // A record store already registered stands in for UseInMemoryStorageForDevelopment, which this project does not reference.
        var afterAnother = new Builder();
        afterAnother.Services.AddSingleton<IExperienceRecordStore>(new PostgresExperienceRecordStore(dataSource));
        Assert.Throws<InvalidOperationException>(() => afterAnother.UsePostgres(dataSource));
    }

    [Fact]
    public void A_data_source_beside_a_different_registered_one_is_refused_and_the_same_one_is_accepted()
    {
        using var registered = TestRecords.Unreachable();
        using var other = TestRecords.Unreachable();

        var different = new Builder();
        different.Services.AddSingleton(registered);
        Assert.Throws<InvalidOperationException>(() => different.UsePostgres(other));

        var same = new Builder();
        same.Services.AddSingleton(registered);
        same.UsePostgres(registered);
        Assert.Single(same.Services, descriptor => descriptor.ServiceType == typeof(NpgsqlDataSource));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Host=x;NotAKeyword=1")]
    public void A_blank_or_malformed_connection_string_is_refused_at_registration(string connectionString)
    {
        Assert.ThrowsAny<ArgumentException>(() => new Builder().UsePostgres(connectionString));
    }

    /// <summary>Stands in for the MAF adapter's builder, which this package never references.</summary>
    private sealed class Builder : IAgentExperienceBuilder<IServiceCollection>
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
    }
}

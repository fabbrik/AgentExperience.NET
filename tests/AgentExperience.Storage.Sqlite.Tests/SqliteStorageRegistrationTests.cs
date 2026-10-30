using AgentExperience.Storage.Sqlite;
using AgentExperience.Storage.Sqlite.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using static AgentExperience.Storage.Conformance.ConformanceData;

namespace AgentExperience.Storage.Sqlite.Tests;

/// <summary>Story 28.7: the registration resolves every SQLite port from a real container, and refuses a second storage.</summary>
public sealed class SqliteStorageRegistrationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"agentexperience-sqlite-{Guid.NewGuid():N}.db");

    public void Dispose() => SqliteTestDatabase.Delete(_path);

    [Fact]
    public async Task Every_port_resolves_over_one_file_and_first_use_migration_is_opt_in()
    {
        using var provider = new ServiceCollection()
            .AddAgentExperienceSqliteStorage(_path, options => options.MigrateSchemaOnFirstUse = true)
            .BuildServiceProvider();

        var store = provider.GetRequiredService<IExperienceRecordStore>();
        Assert.IsType<SqliteExperienceRecordStore>(store);
        Assert.Same(store, provider.GetRequiredService<SqliteExperienceRecordStore>());
        Assert.IsType<SqliteExperienceCandidateSource>(provider.GetRequiredService<IExperienceCandidateSource>());
        Assert.IsType<SqliteExperienceSignatureSource>(provider.GetRequiredService<IExperienceSignatureSource>());
        Assert.IsType<SqliteExperienceReuseFeedbackStore>(provider.GetRequiredService<IExperienceReuseFeedbackStore>());

        var tenant = NewTenant();
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(Authorize(tenant), Record(Scope(tenant)), CancellationToken.None)).Outcome);
        Assert.Equal(Enumerable.Range(1, ExperienceSqliteSchema.CurrentVersion), await ExperienceSqliteSchema.AppliedVersionsAsync(_path));
    }

    [Fact]
    public async Task Without_the_opt_in_nothing_is_migrated()
    {
        using var provider = new ServiceCollection().AddAgentExperienceSqliteStorage(_path).BuildServiceProvider();

        _ = provider.GetRequiredService<IExperienceRecordStore>();

        Assert.Empty(await ExperienceSqliteSchema.AppliedVersionsAsync(_path));
    }

    [Fact]
    public void Registering_beside_another_record_store_throws()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IExperienceRecordStore>(new SqliteExperienceRecordStore(ExperienceSqliteSchema.ConnectionStringFor(_path)));

        Assert.Throws<InvalidOperationException>(() => services.AddAgentExperienceSqliteStorage(_path));
    }

    [Fact]
    public void Registering_twice_with_the_same_file_changes_nothing_and_with_another_throws()
    {
        var services = new ServiceCollection().AddAgentExperienceSqliteStorage(_path);
        var count = services.Count;

        services.AddAgentExperienceSqliteStorage(_path);

        Assert.Equal(count, services.Count);
        Assert.Throws<InvalidOperationException>(() => services.AddAgentExperienceSqliteStorage(_path + ".other"));
    }
}

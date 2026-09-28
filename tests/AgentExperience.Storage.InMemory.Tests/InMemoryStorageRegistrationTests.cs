using AgentExperience.Storage.InMemory.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using static AgentExperience.Storage.Conformance.ConformanceData;

namespace AgentExperience.Storage.InMemory.Tests;

/// <summary>
/// Story 11.2: the one registration, resolved from a real container, and its environment guard. Every test here
/// registers an <see cref="IHostEnvironment"/>, so none reads the process environment variables (those tests are in
/// <see cref="InMemoryStorageEnvironmentVariableTests"/>, which run alone).
/// </summary>
public sealed class InMemoryStorageRegistrationTests
{
    private static readonly Type[] Stores =
        [typeof(IExperienceRecordStore), typeof(IExperienceCandidateSource), typeof(IExperienceReuseFeedbackStore), typeof(InMemoryExperienceRecordStore)];

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("production")]
    [InlineData("QA")]
    [InlineData("Developer")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Outside_the_allow_list_the_host_refuses_to_start_and_every_store_refuses_to_resolve(string environment)
    {
        using var provider = Build(environment);

        var check = Assert.Single(provider.GetServices<IHostedService>());
        var startRefused = await Assert.ThrowsAsync<InvalidOperationException>(() => check.StartAsync(CancellationToken.None));
        Assert.Contains(nameof(InMemoryStorageOptions.AllowProductionEnvironment), startRefused.Message, StringComparison.Ordinal);

        foreach (var port in Stores)
        {
            var refused = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService(port));
            Assert.Contains(nameof(InMemoryStorageOptions.AllowProductionEnvironment), refused.Message, StringComparison.Ordinal);

            // Not cached as a success: asking again is refused again.
            Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService(port));
        }
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Test")]
    [InlineData("Testing")]
    [InlineData("development")]
    [InlineData("TESTING")]
    public async Task In_a_development_or_test_environment_the_host_starts_and_the_stores_resolve(string environment)
    {
        using var provider = Build(environment);

        await Assert.Single(provider.GetServices<IHostedService>()).StartAsync(CancellationToken.None);
        AssertAllResolve(provider);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task The_explicit_override_lets_the_host_start_and_the_stores_resolve_anywhere(string environment)
    {
        using var provider = Build(environment, options => options.AllowProductionEnvironment = true);

        await Assert.Single(provider.GetServices<IHostedService>()).StartAsync(CancellationToken.None);
        AssertAllResolve(provider);
    }

    [Fact]
    public void The_override_is_off_by_default()
    {
        Assert.False(new InMemoryStorageOptions().AllowProductionEnvironment);
    }

    [Fact]
    public async Task The_candidate_source_searches_the_registered_record_store_and_every_port_is_a_singleton()
    {
        using var provider = Build(Environments.Development);
        var store = provider.GetRequiredService<IExperienceRecordStore>();
        var source = provider.GetRequiredService<IExperienceCandidateSource>();

        Assert.Same(provider.GetRequiredService<InMemoryExperienceRecordStore>(), store);
        Assert.Same(store, provider.GetRequiredService<IExperienceRecordStore>());
        Assert.Same(source, provider.GetRequiredService<IExperienceCandidateSource>());
        Assert.Same(provider.GetRequiredService<IExperienceReuseFeedbackStore>(), provider.GetRequiredService<IExperienceReuseFeedbackStore>());

        var tenant = NewTenant();
        var record = Record(Scope(tenant), ExperienceStatus.Validated, summary: "Resolve a refund");
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(Authorize(tenant), record, CancellationToken.None)).Outcome);

        var found = await source.SearchAsync(
            Authorize(tenant), new ExperienceCandidateQuery(record.Scope, "refund", ExperienceStatuses.EligibleForReuse, 0d), CancellationToken.None);

        Assert.Equal(record.ExperienceId, Assert.Single(found.Candidates).Record.ExperienceId);
    }

    [Fact]
    public void A_candidate_source_or_feedback_store_the_host_registered_first_is_kept()
    {
        var own = new InMemoryExperienceReuseFeedbackStore();
        using var provider = new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new FakeHostEnvironment(Environments.Development))
            .AddSingleton<IExperienceReuseFeedbackStore>(own)
            .AddAgentExperienceInMemoryStorageForDevelopment()
            .BuildServiceProvider();

        Assert.Same(own, provider.GetRequiredService<IExperienceReuseFeedbackStore>());
    }

    [Fact]
    public void Registering_beside_another_record_store_is_refused_at_registration()
    {
        var services = new ServiceCollection().AddSingleton<IExperienceRecordStore>(new InMemoryExperienceRecordStore());

        var refused = Assert.Throws<InvalidOperationException>(() => services.AddAgentExperienceInMemoryStorageForDevelopment());

        Assert.Contains(nameof(IExperienceRecordStore), refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_call_with_no_options_changes_nothing_and_one_with_options_is_refused()
    {
        var services = new ServiceCollection().AddAgentExperienceInMemoryStorageForDevelopment();
        var registered = services.Count;

        services.AddAgentExperienceInMemoryStorageForDevelopment();
        Assert.Equal(registered, services.Count);

        Assert.Throws<InvalidOperationException>(() => services.AddAgentExperienceInMemoryStorageForDevelopment(options => options.AllowProductionEnvironment = true));
        Assert.Equal(registered, services.Count);
    }

    [Fact]
    public async Task A_registered_TimeProvider_is_the_clock_commits_are_stamped_with()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        using var provider = new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new FakeHostEnvironment(Environments.Development))
            .AddSingleton<TimeProvider>(clock)
            .AddAgentExperienceInMemoryStorageForDevelopment()
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IExperienceRecordStore>();
        var tenant = NewTenant();
        var record = Record(Scope(tenant));
        await store.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        await store.CommitLifecycleEventAsync(
            Authorize(tenant), record.Scope, Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0), CancellationToken.None);

        var stored = await store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None);
        Assert.Equal(clock.GetUtcNow(), stored.Record!.UpdatedAt);
    }

    [Fact]
    public void A_null_service_collection_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => AgentExperienceInMemoryServiceCollectionExtensions.AddAgentExperienceInMemoryStorageForDevelopment(null!));
    }

    private static ServiceProvider Build(string environment, Action<InMemoryStorageOptions>? configure = null) =>
        new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new FakeHostEnvironment(environment))
            .AddAgentExperienceInMemoryStorageForDevelopment(configure)
            .BuildServiceProvider();

    internal static void AssertAllResolve(IServiceProvider provider)
    {
        Assert.IsType<InMemoryExperienceRecordStore>(provider.GetRequiredService<IExperienceRecordStore>());
        Assert.IsType<InMemoryExperienceCandidateSource>(provider.GetRequiredService<IExperienceCandidateSource>());
        Assert.IsType<InMemoryExperienceReuseFeedbackStore>(provider.GetRequiredService<IExperienceReuseFeedbackStore>());
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "in-memory-tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>Runs the tests that change the process's environment variables alone, never beside another test.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment variables";
}

/// <summary>
/// Story 11.2: with no <see cref="IHostEnvironment"/> registered, the guard reads <c>DOTNET_ENVIRONMENT</c>, then
/// <c>ASPNETCORE_ENVIRONMENT</c>; with neither set there is nothing to check. Each test sets both variables and restores
/// them afterwards.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class InMemoryStorageEnvironmentVariableTests : IDisposable
{
    private const string Dotnet = "DOTNET_ENVIRONMENT";
    private const string AspNetCore = "ASPNETCORE_ENVIRONMENT";

    private readonly string? _dotnet = Environment.GetEnvironmentVariable(Dotnet);
    private readonly string? _aspNetCore = Environment.GetEnvironmentVariable(AspNetCore);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Dotnet, _dotnet);
        Environment.SetEnvironmentVariable(AspNetCore, _aspNetCore);
    }

    [Fact]
    public async Task With_no_host_environment_and_neither_variable_set_there_is_nothing_to_check()
    {
        Set(dotnet: null, aspNetCore: null);
        using var provider = Build();

        await Assert.Single(provider.GetServices<IHostedService>()).StartAsync(CancellationToken.None);
        InMemoryStorageRegistrationTests.AssertAllResolve(provider);
    }

    [Theory]
    [InlineData("Development", null)]
    [InlineData("testing", "Production")] // DOTNET_ENVIRONMENT is read first.
    [InlineData(null, "Test")]
    public async Task An_allow_listed_variable_lets_the_stores_run(string? dotnet, string? aspNetCore)
    {
        Set(dotnet, aspNetCore);
        using var provider = Build();

        await Assert.Single(provider.GetServices<IHostedService>()).StartAsync(CancellationToken.None);
        InMemoryStorageRegistrationTests.AssertAllResolve(provider);
    }

    [Theory]
    [InlineData("Production", null)]
    [InlineData("Staging", "Development")] // DOTNET_ENVIRONMENT is read first.
    [InlineData(null, "Production")]
    [InlineData(null, "Custom")]
    public async Task Any_other_value_is_refused(string? dotnet, string? aspNetCore)
    {
        Set(dotnet, aspNetCore);
        using var provider = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Assert.Single(provider.GetServices<IHostedService>()).StartAsync(CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IExperienceRecordStore>());
    }

    [Fact]
    public void The_override_applies_to_a_refused_variable_too()
    {
        Set(dotnet: "Production", aspNetCore: null);
        using var provider = new ServiceCollection()
            .AddAgentExperienceInMemoryStorageForDevelopment(options => options.AllowProductionEnvironment = true)
            .BuildServiceProvider();

        InMemoryStorageRegistrationTests.AssertAllResolve(provider);
    }

    private static ServiceProvider Build() =>
        new ServiceCollection().AddAgentExperienceInMemoryStorageForDevelopment().BuildServiceProvider();

    private static void Set(string? dotnet, string? aspNetCore)
    {
        Environment.SetEnvironmentVariable(Dotnet, dotnet);
        Environment.SetEnvironmentVariable(AspNetCore, aspNetCore);
    }
}

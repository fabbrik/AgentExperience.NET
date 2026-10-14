using AgentExperience.Core.Finalization;
using AgentExperience.Core.Indexing;
using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.Tests.Shared;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace AgentExperience.Storage.Postgres.Vectors.Tests;

/// <summary>
/// Semantic retrieval through the one-call setup over PostgreSQL: <c>UsePostgres</c> stays text-only (and says so once)
/// when the container has an embedding generator, <c>.WithPostgresVectors()</c> makes it hybrid, and a database without
/// the vector schema still answers from text, observably. The generator is <see cref="ConceptEmbeddingGenerator"/>, a
/// deterministic test double.
/// </summary>
[Collection(VectorsCollection.Name)]
public sealed class OneCallSetupVectorsTests(VectorsFixture fixture)
{
    private const string Original = "Reconcile the ledger for invoice 7731 and close it";

    /// <summary>The same concepts, and no word (or word stem) in common with the stored lesson.</summary>
    private const string Paraphrase = "Balance those books; finalize that bill";

    [Fact]
    public async Task WithPostgresVectors_and_the_vector_schema_find_a_paraphrase_by_meaning()
    {
        var host = new Host(fixture.DataSource) { Vectors = true };
        await using var services = host.Build();
        var agent = Agent(services);

        await agent.RunAsync(Original);
        await agent.RunAsync(Paraphrase);

        Assert.Empty(host.Failures);
        Assert.All(host.Finalized, finalized => Assert.Equal(ExperienceIndexingOutcome.Indexed, finalized.Indexing!.Outcome));
        var stored = host.Finalized[0];
        Assert.Equal(FinalizationOutcome.Validated, stored.Outcome);
        Assert.Equal(InjectionOutcome.Injected, host.Injections[1].Outcome);
        Assert.False(host.Injections[1].TextOnly);
        Assert.Equal([stored.ExperienceId!.Value], host.Injections[1].InjectedExperienceIds);
        Assert.IsType<PostgresExperienceEmbeddingIndex>(services.GetRequiredService<IExperienceEmbeddingIndex>());
    }

    [Fact]
    public async Task UsePostgres_without_WithPostgresVectors_stays_text_only_and_reports_it_once()
    {
        var host = new Host(fixture.DataSource);
        await using var services = host.Build();
        var agent = Agent(services);
        _ = Agent(services);

        await agent.RunAsync(Original);
        await agent.RunAsync(Paraphrase);
        await agent.RunAsync(Original);

        var report = Assert.Single(host.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Embeddings, report.Stage);
        Assert.Contains("WithPostgresVectors", report.Reason, StringComparison.Ordinal);
        Assert.Null(host.Finalized[0].Indexing);
        Assert.Equal(InjectionOutcome.NothingToInject, host.Injections[1].Outcome);
        Assert.Equal(TextOnlyReason.NotConfigured, host.Injections[1].VectorFallback!.Reason);
        Assert.Equal(InjectionOutcome.Injected, host.Injections[2].Outcome);
        Assert.Equal(0, host.Generator.Calls);
        Assert.Null(services.GetService<IExperienceEmbeddingIndex>());
    }

    [Fact]
    public async Task Without_the_vector_schema_retrieval_falls_back_to_text_observably_and_no_run_fails()
    {
        var (owner, app, appRole) = await fixture.CreateDatabaseAsync("nvec");
        await using (owner)
        await using (app)
        {
            await ExperienceSchemaMigrator.MigrateAsync(owner, CancellationToken.None);
            await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(owner, new ExperienceApplicationRoleOptions(appRole), CancellationToken.None);

            var host = new Host(app) { Vectors = true };
            await using var services = host.Build();
            var agent = Agent(services);

            await agent.RunAsync(Original);
            await agent.RunAsync(Original);

            Assert.Empty(host.Failures);
            Assert.Equal(FinalizationOutcome.Validated, host.Finalized[0].Outcome);
            Assert.NotEqual(ExperienceIndexingOutcome.Indexed, host.Finalized[0].Indexing!.Outcome);
            Assert.Equal(InjectionOutcome.Injected, host.Injections[1].Outcome);
            Assert.Equal(TextOnlyReason.VectorSearchFailed, host.Injections[1].VectorFallback!.Reason);
        }
    }

    [Fact]
    public async Task Embeddings_Required_with_a_generator_but_no_WithPostgresVectors_refuses()
    {
        var host = new Host(fixture.DataSource) { Embeddings = AgentExperienceEmbeddings.Required };
        await using var services = host.Build();

        var thrown = Assert.Throws<InvalidOperationException>(() => Agent(services));
        Assert.Contains("WithPostgresVectors", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithPostgresVectors_before_UsePostgres_throws()
    {
        var services = new ServiceCollection();
        var builder = services.AddAgentExperience(options => options.UseDevelopmentIdentity());

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.WithPostgresVectors());
        Assert.Contains("UsePostgres", thrown.Message, StringComparison.Ordinal);
    }

    private static AIAgent Agent(IServiceProvider services) =>
        new ChatClientAgent(new DoneModel()).AsBuilder().UseAgentExperience(services).Build();

    private sealed class Host(NpgsqlDataSource dataSource)
    {
        public bool Vectors { get; init; }

        public AgentExperienceEmbeddings Embeddings { get; init; } = AgentExperienceEmbeddings.Auto;

        public ConceptEmbeddingGenerator Generator { get; } = new();

        public List<ExperienceCaptureFailure> Failures { get; } = [];

        public List<FinalizeExperienceResult> Finalized { get; } = [];

        public List<ExperienceInjectionResult> Injections { get; } = [];

        public ServiceProvider Build()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IHostEnvironment>(new DevelopmentEnvironment());
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(Generator);
            var builder = services
                .AddAgentExperience(options =>
                {
                    // A tenant of its own: the fixture's database is shared by every test in the collection.
                    options.UseDevelopmentIdentity("tenant-" + Guid.NewGuid().ToString("N"));
                    options.Embeddings = Embeddings;
                    options.Verify = ExperienceVerifiers.Check("done", (_, _) => ValueTask.FromResult<bool?>(true));
                    options.Injection = settings => settings.OnContextInjected = Injections.Add;
                    options.Capture = capture =>
                    {
                        capture.OnCaptureFailure = Failures.Add;
                        capture.OnRunFinalized = Finalized.Add;
                    };
                })
                .UsePostgres(dataSource);
            if (Vectors)
            {
                builder.WithPostgresVectors();
            }

            return services.BuildServiceProvider();
        }
    }

    private sealed class DoneModel : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class DevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "vectors-tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

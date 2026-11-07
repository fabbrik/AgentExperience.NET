using AgentExperience.Core.Finalization;
using AgentExperience.Core.Indexing;
using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.Tests.Shared;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// Semantic retrieval through the one-call setup: a registered <c>Microsoft.Extensions.AI</c> embedding generator is
/// picked up, each stored lesson is embedded into the in-memory index, and a later task that shares no indexed word with
/// it still gets it. Every generator here is <see cref="ConceptEmbeddingGenerator"/>, a deterministic test double: these
/// tests prove the wiring, never retrieval quality.
/// </summary>
public sealed class AgentExperienceSemanticRetrievalTests
{
    /// <summary>The first task: what the lesson is stored under.</summary>
    private const string Original = "Reconcile the ledger for invoice 7731 and close it";

    /// <summary>The same concepts in other words: no term in common with the stored task ID, summary or lesson.</summary>
    private const string Paraphrase = "Balance those books; finalize that bill";

    /// <summary>An unrelated lesson: below the similarity floor for the paraphrase.</summary>
    private const string Unrelated = "Release the stuck refund held by a mutex";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_registered_generator_lets_a_paraphrase_sharing_no_term_find_the_lesson(bool registeredBeforeSetup)
    {
        var generator = new ConceptEmbeddingGenerator();
        var host = new Host { Generator = generator, GeneratorBeforeSetup = registeredBeforeSetup };
        await using var services = host.Build();
        var agent = Agent(services);

        await agent.RunAsync(Unrelated);
        await agent.RunAsync(Original);
        await agent.RunAsync(Paraphrase);

        Assert.Empty(host.Failures);
        Assert.All(host.Finalized, finalized =>
        {
            Assert.Equal(FinalizationOutcome.Validated, finalized.Outcome);
            Assert.Equal(ExperienceIndexingOutcome.Indexed, finalized.Indexing!.Outcome);
        });

        // Only the lesson: the unrelated one is below the similarity floor.
        var injection = host.Injections[2];
        Assert.Equal(InjectionOutcome.Injected, injection.Outcome);
        Assert.False(injection.TextOnly);
        Assert.Equal([host.Finalized[1].ExperienceId!.Value], injection.InjectedExperienceIds);

        // The text channel alone finds nothing for the paraphrase: the vector channel is what found it.
        var textOnly = new Host { Generator = new ConceptEmbeddingGenerator(), Embeddings = AgentExperienceEmbeddings.Off };
        await using (var textServices = textOnly.Build())
        {
            var textAgent = Agent(textServices);
            await textAgent.RunAsync(Original);
            await textAgent.RunAsync(Paraphrase);
            Assert.Equal(InjectionOutcome.NothingToInject, textOnly.Injections[1].Outcome);
            Assert.Equal(TextOnlyReason.NotConfigured, textOnly.Injections[1].VectorFallback!.Reason);
        }
    }

    [Fact]
    public async Task An_unrelated_lesson_is_not_injected_unless_the_floor_is_turned_off()
    {
        async Task<ExperienceInjectionResult> SecondRun(double floor)
        {
            var host = new Host { Generator = new ConceptEmbeddingGenerator(), Configure = options => options.MinimumSemanticSimilarity = floor };
            await using var services = host.Build();
            var agent = Agent(services);
            await agent.RunAsync(Unrelated);
            await agent.RunAsync(Paraphrase);
            Assert.Empty(host.Failures);
            return host.Injections[1];
        }

        var floored = await SecondRun(AgentExperienceOptions.DefaultMinimumSemanticSimilarity);
        Assert.Equal(InjectionOutcome.NothingToInject, floored.Outcome);
        Assert.False(floored.TextOnly);

        // Without a floor, the nearest lesson is shown however unrelated: what the floor exists to stop.
        Assert.Equal(InjectionOutcome.Injected, (await SecondRun(-1d)).Outcome);
    }

    [Fact]
    public void The_similarity_floor_is_a_cosine_in_minus_one_to_one()
    {
        Assert.Equal(0.75, AgentExperienceEmbeddingsRuntime.MinimumRelevance(0.5));
        Assert.Equal(0d, AgentExperienceEmbeddingsRuntime.MinimumRelevance(-1d));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddAgentExperience(options =>
        {
            options.UseDevelopmentIdentity();
            options.MinimumSemanticSimilarity = 1.5;
        }));
    }

    [Fact]
    public async Task Without_a_generator_nothing_changes_and_nothing_is_registered_or_reported()
    {
        var host = new Host();
        await using var services = host.Build();
        var agent = Agent(services);

        await agent.RunAsync(Original);
        await agent.RunAsync(Original);

        Assert.Empty(host.Failures);
        Assert.Null(host.Finalized[0].Indexing);
        Assert.Equal(InjectionOutcome.Injected, host.Injections[1].Outcome);
        Assert.Equal(TextOnlyReason.NotConfigured, host.Injections[1].VectorFallback!.Reason);

        // Nothing the setup registers can resolve to null: it registers neither.
        var isService = services.GetRequiredService<IServiceProviderIsService>();
        Assert.False(isService.IsService(typeof(IExperienceEmbeddingGenerator)));
        Assert.False(isService.IsService(typeof(ExperienceIndexingService)));
    }

    [Fact]
    public async Task A_generator_that_throws_leaves_each_call_text_only_and_never_fails_a_run()
    {
        var generator = new ConceptEmbeddingGenerator { Fail = true };
        var host = new Host { Generator = generator };
        await using var services = host.Build();
        var agent = Agent(services);

        await agent.RunAsync(Original);
        await agent.RunAsync(Original);

        Assert.Empty(host.Failures);
        Assert.Equal(FinalizationOutcome.Validated, host.Finalized[0].Outcome);
        Assert.Equal(ExperienceIndexingOutcome.ProviderFailed, host.Finalized[0].Indexing!.Outcome);
        var injection = host.Injections[1];
        Assert.Equal(InjectionOutcome.Injected, injection.Outcome);
        Assert.Equal([host.Finalized[0].ExperienceId!.Value], injection.InjectedExperienceIds);
        Assert.Equal(TextOnlyReason.ProviderUnavailable, injection.VectorFallback!.Reason);
        Assert.True(generator.Calls > 0);
    }

    [Fact]
    public async Task Embeddings_Off_ignores_every_registered_generator_even_the_hosts_own()
    {
        var generator = new ConceptEmbeddingGenerator();
        var own = new CountingGenerator(new ConceptEmbeddingGenerator());
        var host = new Host
        {
            Generator = generator,
            Embeddings = AgentExperienceEmbeddings.Off,
            Before = services => services.AddSingleton<IExperienceEmbeddingGenerator>(own),
        };
        await using var services = host.Build();
        var agent = Agent(services);

        await agent.RunAsync(Original);
        await agent.RunAsync(Paraphrase);

        Assert.Empty(host.Failures);
        Assert.Null(host.Finalized[0].Indexing);
        Assert.Equal(TextOnlyReason.NotConfigured, host.Injections[1].VectorFallback!.Reason);
        Assert.Equal(0, generator.Calls);
        Assert.Equal(0, own.Calls);
    }

    [Fact]
    public async Task Embeddings_Required_without_a_generator_refuses_at_resolution()
    {
        var host = new Host { Embeddings = AgentExperienceEmbeddings.Required };
        await using var services = host.Build();

        var built = Assert.Throws<InvalidOperationException>(() => Agent(services));
        Assert.Contains("Required", built.Message, StringComparison.Ordinal);
        Assert.Contains("IEmbeddingGenerator", built.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(services.GetAgentExperienceContextProvider);
    }

    [Fact]
    public async Task Embeddings_Required_with_no_storage_names_the_storage_choices()
    {
        var host = new Host { Embeddings = AgentExperienceEmbeddings.Required, Generator = new ConceptEmbeddingGenerator(), NoStorage = true };
        await using var services = host.Build();

        var built = Assert.Throws<InvalidOperationException>(() => Agent(services));
        Assert.Contains("UseInMemoryStorageForDevelopment", built.Message, StringComparison.Ordinal);
        Assert.Contains("UsePostgres", built.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Embeddings_Required_with_a_usable_generator_runs_semantically()
    {
        var host = new Host { Generator = new ConceptEmbeddingGenerator(), Embeddings = AgentExperienceEmbeddings.Required };
        await using var services = host.Build();
        var agent = Agent(services);

        await agent.RunAsync(Original);
        await agent.RunAsync(Paraphrase);

        Assert.Empty(host.Failures);
        Assert.Equal(InjectionOutcome.Injected, host.Injections[1].Outcome);
        Assert.False(host.Injections[1].TextOnly);
    }

    [Fact]
    public async Task A_generator_that_reports_no_model_is_text_only_and_reported_once()
    {
        var host = new Host { Generator = new ConceptEmbeddingGenerator(reportMetadata: false) };
        await using var services = host.Build();

        // Two agents on one container: one report.
        var agent = Agent(services);
        _ = Agent(services);
        await agent.RunAsync(Original);
        await agent.RunAsync(Original);

        var failure = Assert.Single(host.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Embeddings, failure.Stage);
        Assert.Null(failure.RunId);
        Assert.Contains("model ID", failure.Reason, StringComparison.Ordinal);
        Assert.IsType<ArgumentException>(failure.Exception);
        Assert.Equal(InjectionOutcome.Injected, host.Injections[1].Outcome);
        Assert.Equal(TextOnlyReason.NotConfigured, host.Injections[1].VectorFallback!.Reason);

        var required = new Host { Generator = new ConceptEmbeddingGenerator(reportMetadata: false), Embeddings = AgentExperienceEmbeddings.Required };
        await using var requiredServices = required.Build();
        Assert.Throws<InvalidOperationException>(() => Agent(requiredServices));
    }

    [Fact]
    public async Task The_report_waits_for_an_agent_built_with_a_failure_callback()
    {
        var host = new Host { Generator = new ConceptEmbeddingGenerator(reportMetadata: false), ReportFailures = false };
        await using var services = host.Build();

        _ = Agent(services);
        host.ReportFailures = true;
        _ = Agent(services);
        _ = Agent(services);

        Assert.Equal(ExperienceCaptureFailureStage.Embeddings, Assert.Single(host.Failures).Stage);
    }

    [Fact]
    public async Task A_generator_whose_resolution_throws_is_reported_and_text_only_and_decided_again_next_time()
    {
        var attempts = 0;
        var generator = new ConceptEmbeddingGenerator();
        var host = new Host
        {
            After = services => services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_ =>
                Interlocked.Increment(ref attempts) == 1 ? throw new InvalidOperationException("the provider client failed to start") : generator),
        };
        await using var services = host.Build();

        var agent = Agent(services);
        await agent.RunAsync(Original);
        await agent.RunAsync(Original);

        var failure = Assert.Single(host.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Embeddings, failure.Stage);
        Assert.IsType<InvalidOperationException>(failure.Exception);
        Assert.Equal(InjectionOutcome.Injected, host.Injections[1].Outcome);

        // The failed resolution is not kept: it is decided again, and the generator now resolves, with no second report.
        _ = Agent(services);
        Assert.True(services.GetRequiredService<AgentExperienceEmbeddingsRuntime>().Decide().Active);
        Assert.Single(host.Failures);

        var required = new Host
        {
            Embeddings = AgentExperienceEmbeddings.Required,
            After = services => services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_ => throw new InvalidOperationException("down")),
        };
        await using var requiredServices = required.Build();
        var thrown = Assert.Throws<InvalidOperationException>(() => Agent(requiredServices));
        Assert.Equal("down", thrown.InnerException!.Message);
    }

    [Fact]
    public async Task An_index_whose_resolution_throws_is_reported_and_text_only_and_Required_refuses()
    {
        var host = new Host
        {
            Generator = new ConceptEmbeddingGenerator(),
            Before = services => services.AddSingleton<IExperienceEmbeddingIndex>(_ => throw new InvalidOperationException("index down")),
        };
        await using var services = host.Build();

        var agent = Agent(services);
        await agent.RunAsync(Original);
        await agent.RunAsync(Original);

        var failure = Assert.Single(host.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Embeddings, failure.Stage);
        Assert.Equal("index down", failure.Exception!.Message);
        Assert.Equal(InjectionOutcome.Injected, host.Injections[1].Outcome);
        Assert.Equal(TextOnlyReason.NotConfigured, host.Injections[1].VectorFallback!.Reason);

        var required = new Host
        {
            Generator = new ConceptEmbeddingGenerator(),
            Embeddings = AgentExperienceEmbeddings.Required,
            Before = services => services.AddSingleton<IExperienceEmbeddingIndex>(_ => throw new InvalidOperationException("index down")),
        };
        await using var requiredServices = required.Build();
        Assert.Equal("index down", Assert.Throws<InvalidOperationException>(() => Agent(requiredServices)).InnerException!.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_IExperienceEmbeddingGenerator_the_host_registered_is_the_one_used(bool beforeSetup)
    {
        var own = new CountingGenerator(new ConceptEmbeddingGenerator());
        var meai = new ConceptEmbeddingGenerator();
        Action<IServiceCollection> register = services => services.AddSingleton<IExperienceEmbeddingGenerator>(own);
        var host = new Host { Generator = meai, Before = beforeSetup ? register : null, After = beforeSetup ? null : register };
        await using var services = host.Build();
        var agent = Agent(services);

        await agent.RunAsync(Original);
        await agent.RunAsync(Paraphrase);

        Assert.True(own.Calls >= 2);
        Assert.Equal(0, meai.Calls);
        Assert.Equal(InjectionOutcome.Injected, host.Injections[1].Outcome);
        Assert.False(host.Injections[1].TextOnly);
    }

    [Fact]
    public async Task A_host_IExperienceEmbeddingGenerator_without_a_model_ID_is_reported_and_not_used()
    {
        var own = new CountingGenerator(new ConceptEmbeddingGenerator(), modelId: " ");
        var host = new Host { Before = services => services.AddSingleton<IExperienceEmbeddingGenerator>(own) };
        await using var services = host.Build();
        var agent = Agent(services);

        await agent.RunAsync(Original);

        var failure = Assert.Single(host.Failures);
        Assert.Contains("model ID", failure.Reason, StringComparison.Ordinal);
        Assert.Equal(0, own.Calls);
    }

    [Fact]
    public async Task An_index_and_an_indexing_service_the_host_registered_are_kept()
    {
        var index = new RecordingIndex();
        var indexing = new ExperienceIndexingService(index, new CountingGenerator(new ConceptEmbeddingGenerator()));
        var host = new Host
        {
            Generator = new ConceptEmbeddingGenerator(),
            Before = services => services.AddSingleton<IExperienceEmbeddingIndex>(index).AddSingleton(indexing),
        };
        await using var services = host.Build();
        var agent = Agent(services);

        await agent.RunAsync(Original);
        await agent.RunAsync(Paraphrase);

        Assert.Same(index, services.GetRequiredService<IExperienceEmbeddingIndex>());
        Assert.Same(indexing, services.GetRequiredService<ExperienceIndexingService>());
        Assert.True(index.Scans > 0);
        Assert.True(index.Searches > 0);
    }

    [Fact]
    public async Task A_keyed_generator_is_used_only_through_EmbeddingGeneratorKey()
    {
        var keyedOnly = new Host { After = services => services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>("embeddings", new ConceptEmbeddingGenerator()) };
        await using (var services = keyedOnly.Build())
        {
            var agent = Agent(services);
            await agent.RunAsync(Original);
            await agent.RunAsync(Paraphrase);
            Assert.Empty(keyedOnly.Failures);
            Assert.Equal(TextOnlyReason.NotConfigured, keyedOnly.Injections[1].VectorFallback!.Reason);
        }

        var keyed = new Host
        {
            Configure = options => options.EmbeddingGeneratorKey = "embeddings",
            After = services => services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>("embeddings", new ConceptEmbeddingGenerator()),
        };
        await using (var services = keyed.Build())
        {
            var agent = Agent(services);
            await agent.RunAsync(Original);
            await agent.RunAsync(Paraphrase);
            Assert.Empty(keyed.Failures);
            Assert.Equal(InjectionOutcome.Injected, keyed.Injections[1].Outcome);
            Assert.False(keyed.Injections[1].TextOnly);
        }

        var missing = new Host { Configure = options => options.EmbeddingGeneratorKey = "nothing-here" };
        await using (var services = missing.Build())
        {
            _ = Agent(services);
            Assert.Contains(nameof(AgentExperienceOptions.EmbeddingGeneratorKey), Assert.Single(missing.Failures).Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Two_containers_from_one_collection_decide_and_report_each_for_itself()
    {
        var host = new Host { Generator = new ConceptEmbeddingGenerator(reportMetadata: false) };
        var collection = host.Collection();
        await using var first = collection.BuildServiceProvider();
        await using var second = collection.BuildServiceProvider();

        _ = Agent(first);
        _ = Agent(second);
        _ = Agent(second);

        Assert.Equal(2, host.Failures.Count);
        Assert.NotSame(first.GetRequiredService<AgentExperienceEmbeddingsRuntime>(), second.GetRequiredService<AgentExperienceEmbeddingsRuntime>());
    }

    private static AIAgent Agent(IServiceProvider services) =>
        new ChatClientAgent(new AgentExperienceFiveLineSetupTests.Model()).AsBuilder().UseAgentExperience(services).Build();

    /// <summary>The five-line setup with a generator, and everything the library reported back.</summary>
    private sealed class Host
    {
        public ConceptEmbeddingGenerator? Generator { get; init; }

        public bool GeneratorBeforeSetup { get; init; }

        public AgentExperienceEmbeddings Embeddings { get; init; } = AgentExperienceEmbeddings.Auto;

        public Action<AgentExperienceOptions>? Configure { get; init; }

        public Action<IServiceCollection>? Before { get; init; }

        public Action<IServiceCollection>? After { get; init; }

        public bool NoStorage { get; init; }

        /// <summary>Whether the capture hook sets <c>OnCaptureFailure</c> for the next agent built.</summary>
        public bool ReportFailures { get; set; } = true;

        public List<ExperienceCaptureFailure> Failures { get; } = [];

        public List<FinalizeExperienceResult> Finalized { get; } = [];

        public List<ExperienceInjectionResult> Injections { get; } = [];

        public ServiceProvider Build() => Collection().BuildServiceProvider();

        public ServiceCollection Collection()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IHostEnvironment>(new AgentExperienceFiveLineSetupTests.FakeHostEnvironment(Environments.Development));
            // A keyed service in the collection must not trip the storage choice up.
            services.AddKeyedSingleton<IChatClient>("unrelated", new AgentExperienceFiveLineSetupTests.Model());
            Before?.Invoke(services);
            if (Generator is not null && GeneratorBeforeSetup)
            {
                services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(Generator);
            }

            var builder = services.AddAgentExperience(options =>
            {
                options.UseDevelopmentIdentity();
                options.Embeddings = Embeddings;
                // No real timer fires: a loaded machine must not time out a retrieval or an indexing call that would have finished.
                options.TimeProvider = NeverFiringTimeProvider.Instance;
                options.Verify = ExperienceVerifiers.Check("done", (_, _) => ValueTask.FromResult<bool?>(true));
                options.Injection = settings => settings.OnContextInjected = Injections.Add;
                options.Capture = capture =>
                {
                    if (ReportFailures)
                    {
                        capture.OnCaptureFailure = failure =>
                        {
                            lock (Failures)
                            {
                                Failures.Add(failure);
                            }
                        };
                    }

                    capture.OnRunFinalized = Finalized.Add;
                };
                Configure?.Invoke(options);
            });
            if (!NoStorage)
            {
                builder.UseInMemoryStorageForDevelopment();
            }

            if (Generator is not null && !GeneratorBeforeSetup)
            {
                services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(Generator);
            }

            After?.Invoke(services);
            return services;
        }
    }

    /// <summary>A host's own <see cref="IExperienceEmbeddingGenerator"/>, over the concept generator, counting calls.</summary>
    private sealed class CountingGenerator(ConceptEmbeddingGenerator inner, string? modelId = null) : IExperienceEmbeddingGenerator
    {
        private int _calls;

        public string ModelId => modelId ?? "host-" + ConceptEmbeddingGenerator.ModelId;

        public int Dimension => inner.Dimension;

        public int Calls => Volatile.Read(ref _calls);

        public Task<ReadOnlyMemory<float>> GenerateAsync(string text, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<ReadOnlyMemory<float>>(inner.Embed(text));
        }
    }

    /// <summary>A host's own index: counts scans and searches, stores nothing, finds nothing.</summary>
    private sealed class RecordingIndex : IExperienceEmbeddingIndex
    {
        private int _scans;
        private int _searches;

        public int Scans => Volatile.Read(ref _scans);

        public int Searches => Volatile.Read(ref _searches);

        public Task<ExperienceIndexWriteResult> WriteAsync(AuthorizationContext authorization, ExperienceIndexWrite write, CancellationToken cancellationToken) =>
            Task.FromResult(new ExperienceIndexWriteResult(ExperienceIndexOutcome.Written, 0, []));

        public Task<ExperienceIndexScanResult> ScanAsync(AuthorizationContext authorization, ExperienceIndexScan scan, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _scans);
            return Task.FromResult(new ExperienceIndexScanResult(ExperienceStoreOutcome.Found, [], []));
        }

        public Task<ExperienceVectorSearchResult> SearchAsync(AuthorizationContext authorization, ExperienceVectorQuery query, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _searches);
            return Task.FromResult(new ExperienceVectorSearchResult(ExperienceVectorSearchOutcome.Found, [], []));
        }

        public Task<ExperienceIndexRemoveResult> RemoveAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, CancellationToken cancellationToken) =>
            Task.FromResult(new ExperienceIndexRemoveResult(ExperienceIndexRemoveOutcome.NotIndexed, []));
    }
}

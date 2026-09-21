using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AgentExperience.Storage.Postgres.Vectors.Tests;

/// <summary>
/// Everything about this package that needs no database: what its own migration script may and may not
/// contain, and the <see cref="AiExperienceEmbeddingGenerator"/> bridge -- the only in-repo path to a
/// real model provider -- driven over a stub <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>.
/// </summary>
public class OfflineVectorsTests
{
    // ---------------------------------------------------------------- the script this package owns

    [Fact]
    public void The_embedding_script_is_owned_here_and_not_by_the_base_adapter()
    {
        // The whole point of the split: a host that never enables the vector channel never runs
        // CREATE EXTENSION vector, which needs a superuser.
        Assert.Equal([ExperienceVectorSchema.EmbeddingsScriptName], ExperienceVectorSchema.ScriptNames);
        Assert.DoesNotContain(
            ExperienceVectorSchema.EmbeddingsScriptName,
            PostgresExperienceRecordSchema.ScriptNames,
            StringComparer.Ordinal);
        Assert.Throws<ArgumentException>(() => ExperienceVectorSchema.GetScript("9999_missing.sql"));
        Assert.Throws<ArgumentException>(() => PostgresExperienceRecordSchema.GetScript(ExperienceVectorSchema.EmbeddingsScriptName));
    }

    [Fact]
    public void Embedded_migration_resources_match_the_declared_script_names()
    {
        var embedded = typeof(ExperienceVectorSchema).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ExperienceVectorSchema.ResourcePrefix, StringComparison.Ordinal)
                && name.EndsWith(".sql", StringComparison.Ordinal))
            .Select(name => name[ExperienceVectorSchema.ResourcePrefix.Length..])
            .Order(StringComparer.Ordinal);

        Assert.Equal(ExperienceVectorSchema.ScriptNames.Order(StringComparer.Ordinal), embedded);
    }

    [Fact]
    public void The_embedding_script_only_adds_derived_write_artifacts()
    {
        var script = ExperienceVectorSchema.GetScript(ExperienceVectorSchema.EmbeddingsScriptName);

        Assert.Contains("CREATE EXTENSION IF NOT EXISTS vector", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS agent_experience.experience_embeddings", script, StringComparison.Ordinal);

        // What an embedding *is*, stored separately from lifecycle state.
        Assert.Contains("model_id text NOT NULL", script, StringComparison.Ordinal);
        Assert.Contains("dimension integer NOT NULL", script, StringComparison.Ordinal);
        Assert.Contains("content_hash text NOT NULL", script, StringComparison.Ordinal);
        Assert.Contains("source_revision bigint NOT NULL", script, StringComparison.Ordinal);

        // Unconstrained, with the dimension carried in its own column and checked against the vector, so
        // the search's embedding::vector(n) cast can never meet a row that disagrees.
        Assert.Contains("embedding vector NOT NULL", script, StringComparison.Ordinal);
        Assert.DoesNotContain("embedding vector(", script, StringComparison.Ordinal);
        Assert.Contains("CHECK (vector_dims(embedding) = dimension)", script, StringComparison.Ordinal);

        var statements = string.Join(
            '\n',
            script.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

        // An embedding may never outlive the record it describes.
        Assert.Contains("ON DELETE CASCADE", statements, StringComparison.Ordinal);

        // Append-only: it adds its own table and rewrites nothing the base adapter created.
        Assert.DoesNotContain("DROP", statements, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE", statements, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lifecycle_events", statements, StringComparison.Ordinal);

        // The approximate-nearest-neighbour index needs a dimension this script does not have, so it is
        // deliberately absent and created by an explicit adapter call.
        Assert.DoesNotContain("hnsw", statements, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ivfflat", statements, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_vectors_migrator_rejects_a_null_data_source()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ExperienceVectorSchemaMigrator.MigrateAsync(null!, CancellationToken.None));
    }

    // ---------------------------------------------------------------- the model-provider bridge

    [Fact]
    public void The_generator_takes_its_model_and_dimension_from_the_providers_metadata()
    {
        var bridge = new AiExperienceEmbeddingGenerator(new StubEmbeddingGenerator("text-embed-3", 6));

        Assert.Equal("text-embed-3", bridge.ModelId);
        Assert.Equal(6, bridge.Dimension);
    }

    [Fact]
    public void An_explicit_model_and_dimension_stand_in_for_metadata_the_provider_does_not_report()
    {
        var bridge = new AiExperienceEmbeddingGenerator(
            new StubEmbeddingGenerator(modelId: null, dimensions: null),
            modelId: "host-chosen",
            dimension: 3);

        Assert.Equal("host-chosen", bridge.ModelId);
        Assert.Equal(3, bridge.Dimension);
    }

    [Fact]
    public void An_argument_that_contradicts_the_provider_is_rejected_at_construction()
    {
        // Stamping vectors with a model the provider did not produce them under is exactly how two
        // incomparable sets come to look comparable -- the one thing the descriptor exists to prevent.
        var provider = new StubEmbeddingGenerator("text-embed-3", 6);

        Assert.Throws<ArgumentException>(() => new AiExperienceEmbeddingGenerator(provider, modelId: "something-else"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiExperienceEmbeddingGenerator(provider, dimension: 4));
    }

    [Fact]
    public void A_provider_that_reports_nothing_and_is_told_nothing_fails_at_construction_not_mid_query()
    {
        var provider = new StubEmbeddingGenerator(modelId: null, dimensions: null);

        Assert.Throws<ArgumentNullException>(() => new AiExperienceEmbeddingGenerator(null!));
        Assert.Throws<ArgumentException>(() => new AiExperienceEmbeddingGenerator(provider));
        Assert.Throws<ArgumentException>(() => new AiExperienceEmbeddingGenerator(provider, modelId: "  ", dimension: 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiExperienceEmbeddingGenerator(provider, modelId: "m"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiExperienceEmbeddingGenerator(provider, modelId: "m", dimension: 0));
    }

    [Fact]
    public async Task Every_request_tells_the_provider_which_model_and_width_to_answer_with()
    {
        // Otherwise a host that named the model would get vectors from the provider's own default,
        // stamped with the name it asked for.
        var provider = new StubEmbeddingGenerator("text-embed-3", 6);
        var bridge = new AiExperienceEmbeddingGenerator(provider);

        var vector = await bridge.GenerateAsync("refund ticket", CancellationToken.None);

        Assert.Equal(6, vector.Length);
        Assert.Equal("text-embed-3", provider.LastOptions!.ModelId);
        Assert.Equal(6, provider.LastOptions.Dimensions);
        Assert.Equal(["refund ticket"], provider.Requests);
    }

    [Fact]
    public async Task A_provider_that_answers_with_the_wrong_width_or_with_nothing_is_rejected()
    {
        var wrongWidth = new AiExperienceEmbeddingGenerator(new StubEmbeddingGenerator("m", 6) { ReturnDimension = 5 });
        var empty = new AiExperienceEmbeddingGenerator(new StubEmbeddingGenerator("m", 6) { ReturnNothing = true });

        await Assert.ThrowsAsync<InvalidOperationException>(() => wrongWidth.GenerateAsync("text", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => empty.GenerateAsync("text", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => new AiExperienceEmbeddingGenerator(new StubEmbeddingGenerator("m", 6)).GenerateAsync(null!, CancellationToken.None));
    }

    [Fact]
    public void AddAgentExperienceEmbeddingGenerator_resolves_the_bridge_over_a_registered_provider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new StubEmbeddingGenerator("text-embed-3", 6));
        DependencyInjection.AgentExperiencePostgresVectorsServiceCollectionExtensions
            .AddAgentExperienceEmbeddingGenerator(services);

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IExperienceEmbeddingGenerator>();

        Assert.IsType<AiExperienceEmbeddingGenerator>(resolved);
        Assert.Equal("text-embed-3", resolved.ModelId);
        Assert.Equal(6, resolved.Dimension);
        Assert.Same(resolved, provider.GetRequiredService<IExperienceEmbeddingGenerator>());
    }

    [Fact]
    public void AddAgentExperiencePostgresEmbeddingIndex_registers_the_index_over_a_host_owned_data_source()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=nobody;Password=nothing;Database=none");
        var services = new ServiceCollection();
        DependencyInjection.AgentExperiencePostgresVectorsServiceCollectionExtensions
            .AddAgentExperiencePostgresEmbeddingIndex(services, dataSource);

        using var provider = services.BuildServiceProvider();

        Assert.IsType<PostgresExperienceEmbeddingIndex>(provider.GetRequiredService<IExperienceEmbeddingIndex>());
    }

    /// <summary>
    /// A stub <c>Microsoft.Extensions.AI</c> generator: it reports whatever metadata the test wants,
    /// records the options it was called with, and answers deterministically. No provider, no network.
    /// </summary>
    private sealed class StubEmbeddingGenerator(string? modelId, int? dimensions)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly EmbeddingGeneratorMetadata _metadata = new("stub", providerUri: null, modelId, dimensions);

        public List<string> Requests { get; } = [];

        public EmbeddingGenerationOptions? LastOptions { get; private set; }

        /// <summary>When set, the answer has this many components instead of the requested width.</summary>
        public int? ReturnDimension { get; init; }

        /// <summary>When set, the answer holds no embedding at all.</summary>
        public bool ReturnNothing { get; init; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.AddRange(values);
            LastOptions = options;

            var generated = new GeneratedEmbeddings<Embedding<float>>();
            if (!ReturnNothing)
            {
                var width = ReturnDimension ?? options?.Dimensions ?? dimensions ?? 1;
                generated.Add(new Embedding<float>(new float[width]));
            }

            return Task.FromResult(generated);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata : null;

        public void Dispose()
        {
        }
    }
}

using AgentExperience.Abstractions;
using Microsoft.Extensions.AI;

namespace AgentExperience.Storage.Postgres.Vectors;

/// <summary>
/// Adapts a <c>Microsoft.Extensions.AI</c> <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> to
/// AgentExperience's own domain-typed <see cref="IExperienceEmbeddingGenerator"/>. It exists so the
/// model-provider abstraction stops here, at the adapter edge: neither
/// <c>AgentExperience.Abstractions</c> nor <c>AgentExperience.Core</c> ever references
/// <c>Microsoft.Extensions.AI</c>, and a host can swap this for an in-process, deterministic, or
/// bespoke generator without either of them noticing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The model ID and the dimension are fixed at construction.</b> They have to be: the content hash
/// covers the model ID, so "the same text under the same model" must be recognizable <em>before</em>
/// any provider call is made. They are read from the generator's
/// <see cref="EmbeddingGeneratorMetadata"/> unless the caller states them, and a generator that
/// reports neither is rejected here -- at startup -- rather than producing vectors nobody can decide
/// the comparability of later.
/// </para>
/// <para>
/// <b>It validates what the provider returned.</b> A response with no embedding, or one whose width
/// is not <see cref="Dimension"/>, throws rather than being stored: a descriptor that disagrees with
/// its own vector would make every later comparison against it unsound. The caller treats that throw
/// as a retryable provider failure, exactly like a timeout.
/// </para>
/// </remarks>
public sealed class AiExperienceEmbeddingGenerator : IExperienceEmbeddingGenerator
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly EmbeddingGenerationOptions _options;

    /// <summary>
    /// Wraps <paramref name="generator"/>, taking its model ID and dimension from the arguments when
    /// given and otherwise from the generator's own metadata.
    /// </summary>
    /// <param name="generator">The underlying embedding generator. Its lifetime belongs to the host; this adapter never disposes it.</param>
    /// <param name="modelId">Optional. The model identifier to stamp on every embedding, and to ask the provider for. Defaults to <see cref="EmbeddingGeneratorMetadata.DefaultModelId"/>, and may not contradict it when it is reported.</param>
    /// <param name="dimension">Optional. The vector width to require, and to ask the provider for. Defaults to <see cref="EmbeddingGeneratorMetadata.DefaultModelDimensions"/>, and may not contradict it when it is reported.</param>
    /// <exception cref="ArgumentNullException"><paramref name="generator"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="modelId"/> is blank, contradicts the generator's reported model, or no model ID is available from either source.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dimension"/> is outside 1..<see cref="ExperienceEmbeddingDescriptor.MaxDimension"/>, contradicts the generator's reported dimension, or no dimension is available from either source.</exception>
    public AiExperienceEmbeddingGenerator(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        string? modelId = null,
        int? dimension = null)
    {
        ArgumentNullException.ThrowIfNull(generator);
        _generator = generator;

        var metadata = generator.GetService<EmbeddingGeneratorMetadata>();

        var effectiveModelId = modelId ?? metadata?.DefaultModelId;
        if (string.IsNullOrWhiteSpace(effectiveModelId))
        {
            throw new ArgumentException(
                "The embedding model ID must be supplied, or reported by the generator's EmbeddingGeneratorMetadata: " +
                "it is part of every stored embedding's content hash and decides which vectors are comparable.",
                nameof(modelId));
        }

        if (effectiveModelId.Length > ExperienceEmbeddingDescriptor.MaxModelIdLength)
        {
            throw new ArgumentException(
                $"The embedding model ID must be at most {ExperienceEmbeddingDescriptor.MaxModelIdLength} characters.",
                nameof(modelId));
        }

        var effectiveDimension = dimension ?? metadata?.DefaultModelDimensions;
        if (effectiveDimension is not (>= 1 and <= ExperienceEmbeddingDescriptor.MaxDimension))
        {
            throw new ArgumentOutOfRangeException(
                nameof(dimension),
                effectiveDimension,
                "The embedding dimension must be supplied, or reported by the generator's EmbeddingGeneratorMetadata, " +
                $"and must be between 1 and {ExperienceEmbeddingDescriptor.MaxDimension}.");
        }

        // An override that contradicts what the generator actually runs is the one failure the
        // descriptor exists to prevent: vectors would be stamped with a model ID the provider never
        // used, so two genuinely incomparable sets would look comparable. Rejected at wiring time.
        if (metadata?.DefaultModelId is { Length: > 0 } reportedModelId
            && !string.Equals(reportedModelId, effectiveModelId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The generator reports model '{reportedModelId}', but '{effectiveModelId}' was supplied. Stamping vectors " +
                "with a model ID the provider did not produce them under would make incomparable vectors look comparable.",
                nameof(modelId));
        }

        if (metadata?.DefaultModelDimensions is { } reportedDimension && reportedDimension != effectiveDimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dimension),
                effectiveDimension,
                $"The generator reports {reportedDimension} dimensions, but {effectiveDimension} was supplied.");
        }

        ModelId = effectiveModelId;
        Dimension = effectiveDimension.Value;

        // The resolved model and dimension are what the provider is actually asked for, not merely what
        // the stored descriptor claims -- otherwise a request would silently run on the provider's own
        // default while being stamped with something else.
        _options = new EmbeddingGenerationOptions { ModelId = ModelId, Dimensions = Dimension };
    }

    /// <inheritdoc />
    public string ModelId { get; }

    /// <inheritdoc />
    public int Dimension { get; }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The generator returned no embedding, or one of the wrong width.</exception>
    public async Task<ReadOnlyMemory<float>> GenerateAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);

        var generated = await _generator
            .GenerateAsync([text], _options, cancellationToken)
            .ConfigureAwait(false);

        if (generated is not { Count: > 0 })
        {
            throw new InvalidOperationException("The embedding generator returned no embedding for the requested text.");
        }

        var vector = generated[0].Vector;
        return vector.Length == Dimension
            ? vector
            : throw new InvalidOperationException(
                $"The embedding generator returned a {vector.Length}-component vector where {Dimension} were declared; " +
                "a stored descriptor must never disagree with its own vector.");
    }
}

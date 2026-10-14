using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;

namespace AgentExperience.Tests.Shared;

/// <summary>
/// A deterministic <c>Microsoft.Extensions.AI</c> embedding generator for tests only (story 21.3): it maps each word to
/// a concept axis through a small synonym table, so two texts that share no word but name the same concepts point the
/// same way, and every other word to a faintly weighted hashed axis. It exercises the semantic retrieval plumbing with
/// no network and no key. It knows nothing about meaning beyond its table, so any number measured with it says how the
/// plumbing behaves, never how good semantic retrieval is.
/// </summary>
public sealed class ConceptEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>The model ID the generator reports.</summary>
    public const string ModelId = "concept-embed-test-v1";

    /// <summary>The default synonym table: each line is one concept.</summary>
    public static readonly IReadOnlyList<string[]> DefaultConcepts =
    [
        ["reconcile", "balance"],
        ["ledger", "books", "accounts"],
        ["invoice", "bill"],
        ["close", "finish", "finalize"],
        ["refund", "reimbursement", "repayment"],
        ["lock", "mutex", "latch"],
    ];

    /// <summary>How many hashed axes words outside the table share.</summary>
    private const int HashedAxes = 8;

    /// <summary>The weight of a word outside the table: present, but far weaker than a concept.</summary>
    private const float HashedWeight = 0.05f;

    private readonly Dictionary<string, int> _concepts = new(StringComparer.Ordinal);
    private readonly EmbeddingGeneratorMetadata _metadata;
    private int _calls;

    /// <summary>Creates a generator over <paramref name="concepts"/>, or <see cref="DefaultConcepts"/>.</summary>
    /// <param name="concepts">The synonym table.</param>
    /// <param name="reportMetadata">Whether <see cref="GetService"/> reports the model ID and dimension. A generator that does not cannot be adapted.</param>
    public ConceptEmbeddingGenerator(IReadOnlyList<string[]>? concepts = null, bool reportMetadata = true)
    {
        concepts ??= DefaultConcepts;
        for (var axis = 0; axis < concepts.Count; axis++)
        {
            foreach (var word in concepts[axis])
            {
                _concepts[word] = axis;
            }
        }

        Dimension = concepts.Count + HashedAxes;
        _metadata = reportMetadata
            ? new EmbeddingGeneratorMetadata("concept-test", defaultModelId: ModelId, defaultModelDimensions: Dimension)
            : new EmbeddingGeneratorMetadata("concept-test");
    }

    /// <summary>The vector width.</summary>
    public int Dimension { get; }

    /// <summary>When set, every call throws <see cref="HttpRequestException"/>, as an unreachable provider does.</summary>
    public bool Fail { get; set; }

    /// <summary>How many generation calls were made, failed ones included.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <inheritdoc />
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        cancellationToken.ThrowIfCancellationRequested();
        if (Fail)
        {
            throw new HttpRequestException("The test embedding provider is unreachable.");
        }

        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(value => new Embedding<float>(Embed(value)) { ModelId = ModelId })));
    }

    /// <summary>The vector for <paramref name="text"/>.</summary>
    public float[] Embed(string text)
    {
        var vector = new float[Dimension];
        foreach (var word in Words(text))
        {
            if (_concepts.TryGetValue(word, out var axis))
            {
                vector[axis] += 1f;
            }
            else
            {
                vector[Dimension - HashedAxes + (int)(Fnv(word) % HashedAxes)] += HashedWeight;
            }
        }

        return vector;
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata
        : serviceKey is null && serviceType.IsInstanceOfType(this) ? this
        : null;

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private static IEnumerable<string> Words(string text)
    {
        var word = new StringBuilder();
        foreach (var character in text.ToLower(CultureInfo.InvariantCulture))
        {
            if (char.IsLetterOrDigit(character))
            {
                word.Append(character);
                continue;
            }

            if (word.Length > 0)
            {
                yield return word.ToString();
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            yield return word.ToString();
        }
    }

    private static uint Fnv(string word)
    {
        var hash = 2166136261u;
        foreach (var character in word)
        {
            hash = (hash ^ character) * 16777619u;
        }

        return hash;
    }
}

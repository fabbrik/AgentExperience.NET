using AgentExperience.Abstractions;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Indexing;
using AgentExperience.Core.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.MicrosoftAgentFramework;

/// <summary>
/// What the one-call setup does about embeddings, in one container: registered as a singleton, so two containers built
/// from one service collection decide, and report, each for itself. It hands Core the vector channel and the indexing
/// service through Core's own constructors, so no registration the setup adds ever resolves to nothing.
/// </summary>
/// <remarks>
/// A decision is kept once it is made, unless it came from a resolution that threw: that one lasts for the agent being
/// built and is made again for the next agent built, since a provider that failed to construct may construct the next
/// time. Core's services already built from a failed decision keep the text-only wiring they were built with.
/// </remarks>
internal sealed class AgentExperienceEmbeddingsRuntime(AgentExperienceOptions options, IServiceProvider provider)
{
    private readonly object _gate = new();
    private EmbeddingsDecision? _decided;
    private int _reported;

    /// <summary>
    /// The decision, made now unless one was kept. Under <see cref="AgentExperienceEmbeddings.Required"/>, anything but
    /// semantic retrieval throws <see cref="InvalidOperationException"/>, with the failure that caused it as the inner
    /// exception.
    /// </summary>
    public EmbeddingsDecision Decide()
    {
        var decision = DecideOrKeep();
        if (options.Embeddings == AgentExperienceEmbeddings.Required && !decision.Active)
        {
            throw new InvalidOperationException(
                $"{nameof(AgentExperienceOptions)}.{nameof(AgentExperienceOptions.Embeddings)} is {nameof(AgentExperienceEmbeddings.Required)}, but "
                + (decision.Problem ?? "no Microsoft.Extensions.AI IEmbeddingGenerator<string, Embedding<float>> is registered in the container.")
                + $" Register a generator that reports its model ID and dimension, choose a storage with an embedding index, or set {nameof(AgentExperienceOptions.Embeddings)} "
                + $"to {nameof(AgentExperienceEmbeddings.Auto)}.",
                decision.Exception);
        }

        return decision;
    }

    /// <summary>The vector channel Core's retrieval service is built with: both halves and the floor, or neither.</summary>
    public RetrievalVectorChannel VectorChannel() =>
        Decide() is { Active: true } decision
            ? new RetrievalVectorChannel(decision.Index, decision.Generator, MinimumRelevance(options.MinimumSemanticSimilarity))
            : new RetrievalVectorChannel(null, null, null);

    /// <summary>The indexing service finalization embeds each committed record with, or none (text-only).</summary>
    public ExperienceIndexingService? Indexing() =>
        Decide() is { Active: true } decision
            ? new ExperienceIndexingService(decision.Index!, decision.Generator!, provider.GetService<RetrievalPolicy>())
            : null;

    /// <summary>
    /// Reports, once per container, a generator the setup could not use, through the capture options' failure callback.
    /// Only a callback that is there consumes the report, so a later agent with one still gets it.
    /// </summary>
    public void Report(ExperienceCaptureOptions capture)
    {
        var decision = DecideOrKeep();
        try
        {
            if (options.Embeddings != AgentExperienceEmbeddings.Auto
                || capture.OnCaptureFailure is not { } report
                || decision.Problem is not { } problem
                || Interlocked.Exchange(ref _reported, 1) != 0)
            {
                return;
            }

            report(new ExperienceCaptureFailure(ExperienceCaptureFailureStage.Embeddings, RunId: null, "Semantic retrieval is off: " + problem, decision.Exception));
        }
        catch (Exception)
        {
            // Like every capture failure callback: a throwing callback changes nothing.
        }
        finally
        {
            // A decision from a resolution that threw lasts for this agent's build only: the next build decides again.
            if (decision.Transient)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_decided, decision))
                    {
                        _decided = null;
                    }
                }
            }
        }
    }

    /// <summary>A minimum cosine similarity in [-1, 1] as the vector channel's relevance, <c>(1 + cosine) / 2</c>.</summary>
    internal static double MinimumRelevance(double cosine) => Math.Clamp((1d + cosine) / 2d, 0d, 1d);

    private EmbeddingsDecision DecideOrKeep()
    {
        lock (_gate)
        {
            if (_decided is { } kept)
            {
                return kept;
            }

            // Kept, even when a resolution threw: everything built for one agent sees one decision. A failed one is
            // dropped when that build ends (Report), so the next build decides again.
            return _decided = Evaluate();
        }
    }

    private EmbeddingsDecision Evaluate()
    {
        if (options.Embeddings == AgentExperienceEmbeddings.Off)
        {
            return EmbeddingsDecision.None;
        }

        var key = options.EmbeddingGeneratorKey;
        IExperienceEmbeddingGenerator? generator = null;

        // A generator the host registered itself is used as it is, once it says what it is, by the adapter's own rule.
        if (key is null)
        {
            try
            {
                generator = provider.GetService<IExperienceEmbeddingGenerator>();
            }
            catch (Exception ex)
            {
                return EmbeddingsDecision.Failed($"resolving the registered IExperienceEmbeddingGenerator threw {ex.GetType().FullName}.", ex);
            }

            if (generator is not null)
            {
                try
                {
                    AiExperienceEmbeddingGenerator.CheckIdentity(generator.ModelId, generator.Dimension);
                }
                catch (ArgumentException ex)
                {
                    return EmbeddingsDecision.Unusable("the registered IExperienceEmbeddingGenerator cannot be used for semantic retrieval: " + ex.Message, ex);
                }
            }
        }

        if (generator is null)
        {
            var name = key is null ? "IEmbeddingGenerator<string, Embedding<float>>" : "IEmbeddingGenerator<string, Embedding<float>> registered under the configured key";
            IEmbeddingGenerator<string, Embedding<float>>? registered;
            try
            {
                registered = key is null
                    ? provider.GetService<IEmbeddingGenerator<string, Embedding<float>>>()
                    : provider.GetKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(key);
            }
            catch (Exception ex)
            {
                return EmbeddingsDecision.Failed($"resolving the registered {name} threw {ex.GetType().FullName}.", ex);
            }

            if (registered is null)
            {
                // No generator is no problem; a key that names none is a configuration mistake worth saying.
                return key is null
                    ? EmbeddingsDecision.None
                    : EmbeddingsDecision.Unusable(
                        $"{nameof(AgentExperienceOptions)}.{nameof(AgentExperienceOptions.EmbeddingGeneratorKey)} is set, but no {name} is registered.", null);
            }

            try
            {
                generator = new AiExperienceEmbeddingGenerator(registered);
            }
            catch (ArgumentException ex)
            {
                return EmbeddingsDecision.Unusable($"the registered {name} cannot be used for semantic retrieval: " + ex.Message, ex);
            }
        }

        IExperienceEmbeddingIndex? index;
        try
        {
            index = provider.GetService<IExperienceEmbeddingIndex>();
        }
        catch (Exception ex)
        {
            return EmbeddingsDecision.Failed($"resolving the registered IExperienceEmbeddingIndex threw {ex.GetType().FullName}.", ex);
        }

        return index is null
            ? EmbeddingsDecision.Unusable(
                "an embedding generator is registered, but the storage has no embedding index, so retrieval stays text-only. "
                + "With PostgreSQL, add .WithPostgresVectors() after UsePostgres(...) (package AgentExperience.Storage.Postgres.Vectors) "
                + "and apply its vector schema; UseInMemoryStorageForDevelopment() has an index built in.",
                null)
            : new EmbeddingsDecision(Active: true, index, generator, Problem: null, Exception: null, Transient: false);
    }
}

/// <summary>
/// One container's decision about embeddings. <see cref="Active"/> means semantic retrieval, with both
/// <see cref="Index"/> and <see cref="Generator"/>. <see cref="Problem"/> says why a generator that is there is not used;
/// <see cref="Transient"/> marks a decision that came from a resolution that threw, which is not kept.
/// </summary>
internal sealed record EmbeddingsDecision(
    bool Active,
    IExperienceEmbeddingIndex? Index,
    IExperienceEmbeddingGenerator? Generator,
    string? Problem,
    Exception? Exception,
    bool Transient)
{
    /// <summary>No generator, or <see cref="AgentExperienceEmbeddings.Off"/>: text-only, with nothing to report.</summary>
    public static EmbeddingsDecision None { get; } = new(false, null, null, null, null, false);

    /// <summary>A generator is there but cannot be used: text-only, reported once.</summary>
    public static EmbeddingsDecision Unusable(string problem, Exception? exception) => new(false, null, null, problem, exception, false);

    /// <summary>A resolution threw: text-only, reported, and decided again next time.</summary>
    public static EmbeddingsDecision Failed(string problem, Exception exception) => new(false, null, null, problem, exception, true);
}

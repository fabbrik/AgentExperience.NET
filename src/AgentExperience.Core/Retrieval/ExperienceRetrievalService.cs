using AgentExperience.Abstractions;
using AgentExperience.Core.Diagnostics;

namespace AgentExperience.Core.Retrieval;

/// <summary>
/// The single Core call that finds experience applicable to a task: it asks an
/// <see cref="IExperienceCandidateSource"/> for scope-, status- and confidence-filtered candidates
/// matched on task text, optionally asks an <see cref="IExperienceEmbeddingIndex"/> for the same
/// thing matched on meaning, merges the two, decides the remaining eligibility itself, and ranks what
/// survives with weights and components it reports back in full.
/// </summary>
/// <remarks>
/// <para>
/// <b>Filter, then rank.</b> Nothing is ever scored before it is known to be reusable. The database
/// decides scope, status, and the confidence floor -- identically for both channels; Core decides
/// expiry and environment compatibility, because both depend on the clock and on the request rather
/// than on stored state alone. Only <see cref="ExperienceStatus.Validated"/> and
/// <see cref="ExperienceStatus.Reinforced"/> records are ever eligible -- a
/// <see cref="ExperienceStatus.Candidate"/>, <see cref="ExperienceStatus.Quarantined"/>,
/// <see cref="ExperienceStatus.Contested"/>, <see cref="ExperienceStatus.Stale"/>,
/// <see cref="ExperienceStatus.Superseded"/>, or <see cref="ExperienceStatus.Revoked"/> record is
/// dropped whatever its text or vector match.
/// </para>
/// <para>
/// <b>Two channels, one answer.</b> When an embedding index and an embedding generator are both
/// wired in, the task text is also embedded and searched as a vector, inside the same timeout and
/// under the same candidate ceiling. The two candidate lists are then deduplicated by
/// <see cref="ExperienceRecord.ExperienceId"/>, and a record found by both keeps the <em>higher</em>
/// of its two normalized relevances. Ranking then runs once, over the merged list, with the same five
/// weights as before: there is no sixth axis and no "found by both" bonus.
/// </para>
/// <para>
/// <b>An embedding never decides anything.</b> It can only make a record a candidate; eligibility,
/// status, and confidence are untouched by it. And when the vector channel cannot be trusted -- no
/// provider, a provider that failed or timed out on its own, or stored vectors from another model or
/// another dimension -- the result is an explicit text-only answer carrying <c>VectorFallback</c> with
/// the reason, and the text candidates still come back. No incompatible comparison is ever attempted,
/// and nothing the vector channel does can turn a good text answer into a failure: only cancellation
/// of the <em>caller's own</em> token ever escapes it.
/// </para>
/// <para>
/// <b>The candidate ceiling is a real recall limit.</b> Each channel returns at most
/// <see cref="RetrievalPolicy.CandidateLimit"/> candidates, ordered by its own relevance, and ranking
/// only ever sees those. So a record with a weaker match but strong confidence, recency, or status is
/// not ranked at all once that many stronger matches exist in both channels -- the weighting can only
/// reorder what the ceiling let through. When either channel reached its ceiling the result says so
/// (<c>Truncated</c>); the records beyond it are not in the exclusion list either, because no
/// eligibility check ever looked at them. Raise the ceiling, or narrow the task text, when that
/// matters.
/// </para>
/// <para>
/// <b>Bounded, and a timeout is not an error.</b> The whole call is bounded by
/// <see cref="RetrievalPolicy.Timeout"/>, measured with the injected <see cref="TimeProvider"/>.
/// Exceeding it returns an empty result carrying <see cref="ExperienceRetrievalResult.TimedOut"/> and
/// the request's correlation ID -- never an exception -- so an agent whose memory is slow simply runs
/// without it. Caller cancellation is different in kind and propagates as an unwrapped
/// <see cref="OperationCanceledException"/>; the timeout is reported before the inner token is
/// cancelled, so a token-honouring source cannot race a cancellation ahead of the timeout report.
/// </para>
/// <para>
/// <b>Fail-closed.</b> An authorization mismatch, a source failure inside the timeout, a candidate
/// that cannot be read, and a candidate returned outside the requested scope all produce an
/// <em>empty</em> result rather than an unfiltered one. Retrieval never answers with experience it
/// could not fully check.
/// </para>
/// <para>
/// <b>Sharing grants are not decided here.</b> A candidate may belong to a sibling scope inside the
/// same tenant, application, and project, because an adapter's query predicate found an active grant
/// permitting the requesting scope to read it. This service does not look for grants and cannot
/// create one: it believes the channel's <see cref="ExperienceCandidate.SharedByGrant"/> flag, which
/// only the layer that applied the predicate can set, and passes it through on
/// <see cref="RankedExperience.SharedByGrant"/>. Such a record is otherwise treated exactly like an
/// owned one -- the same eligibility rules, the same ranking, the same limits -- and the scope guard
/// stays strict equality for every candidate that does <em>not</em> carry the flag.
/// </para>
/// <para>
/// This service does not build an injectable payload: it returns ranked records and the evidence for
/// their ranking, and what a host does with them is a separate decision. It also never
/// <em>writes</em> an embedding -- producing and storing them is
/// <see cref="AgentExperience.Core.Indexing.ExperienceIndexingService"/>'s job, and happens after a
/// record is already committed.
/// </para>
/// </remarks>
public sealed class ExperienceRetrievalService
{
    /// <summary>
    /// The only statuses a record may be in and still be returned. This is the eligibility rule, not
    /// a default: a record in any other status is never injectable, whatever its text match or
    /// confidence.
    /// </summary>
    public static IReadOnlyList<ExperienceStatus> EligibleStatuses => ExperienceStatuses.EligibleForReuse;

    /// <summary>The status component's value for a <see cref="ExperienceStatus.Validated"/> record.</summary>
    public const double ValidatedStatusScore = 0.5;

    /// <summary>
    /// The status component's value for a <see cref="ExperienceStatus.Reinforced"/> record: reuse was
    /// observed to succeed again, which is the strongest evidence this axis can carry.
    /// </summary>
    public const double ReinforcedStatusScore = 1.0;

    /// <summary>
    /// The environment component's value for a record that satisfied the request's required
    /// attributes -- which, by construction, every ranked record did, since a mismatch excludes the
    /// record before ranking. It is reported anyway, with its effective weight, so the score a host
    /// sees always adds up from every documented axis.
    /// </summary>
    public const double CompatibleEnvironmentScore = 1.0;

    private static readonly IReadOnlyList<RankedExperience> NoRecords = [];

    private static readonly IReadOnlyList<ExcludedExperience> NoExclusions = [];

    /// <summary>
    /// The text-only signal for a deployment that has no vector channel at all. It is a statement
    /// about the wiring, not about this call, so it is a single shared instance.
    /// </summary>
    private static readonly VectorChannelFallback NotConfigured = new(
        TextOnlyReason.NotConfigured,
        "No embedding index or embedding generator is registered, so this retrieval has no vector channel.",
        Exception: null);

    private readonly IExperienceCandidateSource _candidateSource;
    private readonly RetrievalPolicy _policy;
    private readonly RankingWeights _weights;
    private readonly TimeProvider _timeProvider;
    private readonly IExperienceEmbeddingIndex? _embeddingIndex;
    private readonly IExperienceEmbeddingGenerator? _embeddingGenerator;

    /// <summary>
    /// Creates a text-only retrieval service over a candidate source, its policy, its weights, and the
    /// clock it measures with. Every result it produces is flagged
    /// <see cref="ExperienceRetrievalResult.TextOnly"/> with
    /// <see cref="TextOnlyReason.NotConfigured"/>.
    /// </summary>
    /// <param name="candidateSource">Where scope-, status- and confidence-filtered text matches come from.</param>
    /// <param name="policy">The timeout, confidence floor, expiry, recency half-life, and candidate bound.</param>
    /// <param name="weights">The weights applied to each normalized ranking component.</param>
    /// <param name="timeProvider">The clock the timeout, expiry, and recency are measured with.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ExperienceRetrievalService(
        IExperienceCandidateSource candidateSource,
        RetrievalPolicy policy,
        RankingWeights weights,
        TimeProvider timeProvider)
        : this(candidateSource, policy, weights, timeProvider, embeddingIndex: null, embeddingGenerator: null)
    {
    }

    /// <summary>
    /// Creates a retrieval service with an optional vector channel alongside the text one. The vector
    /// channel is active only when <em>both</em> <paramref name="embeddingIndex"/> and
    /// <paramref name="embeddingGenerator"/> are supplied: one without the other cannot produce a
    /// comparison, so it is treated as no vector channel at all rather than as a failure on every
    /// call.
    /// </summary>
    /// <param name="candidateSource">Where scope-, status- and confidence-filtered text matches come from.</param>
    /// <param name="policy">The timeout, confidence floor, expiry, recency half-life, and candidate bound. Both channels run under it.</param>
    /// <param name="weights">The weights applied to each normalized ranking component. Unchanged by hybrid retrieval: still five.</param>
    /// <param name="timeProvider">The clock the timeout, expiry, and recency are measured with.</param>
    /// <param name="embeddingIndex">Optional. Where scope-, status- and confidence-filtered vector matches come from.</param>
    /// <param name="embeddingGenerator">Optional. What turns the request's task text into a query vector.</param>
    /// <exception cref="ArgumentNullException">Any non-optional argument is <see langword="null"/>.</exception>
    public ExperienceRetrievalService(
        IExperienceCandidateSource candidateSource,
        RetrievalPolicy policy,
        RankingWeights weights,
        TimeProvider timeProvider,
        IExperienceEmbeddingIndex? embeddingIndex,
        IExperienceEmbeddingGenerator? embeddingGenerator)
    {
        ArgumentNullException.ThrowIfNull(candidateSource);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _candidateSource = candidateSource;
        _policy = policy;
        _weights = weights;
        _timeProvider = timeProvider;
        _embeddingIndex = embeddingIndex;
        _embeddingGenerator = embeddingGenerator;
    }

    /// <summary>The policy this service runs under.</summary>
    public RetrievalPolicy Policy => _policy;

    /// <summary>The weights this service ranks with.</summary>
    public RankingWeights Weights => _weights;

    /// <summary>
    /// Whether this service has a vector channel at all. <see langword="false"/> means every result
    /// is text-only for <see cref="TextOnlyReason.NotConfigured"/>; <see langword="true"/> means the
    /// channel is wired, not that it will succeed on any given call.
    /// </summary>
    public bool HybridEnabled => _embeddingIndex is not null && _embeddingGenerator is not null;

    /// <summary>
    /// Retrieves the experience that applies to <paramref name="request"/>, ranked, or an empty
    /// result when it is denied, times out, or fails.
    /// </summary>
    /// <param name="request">The scope, task text, required environment attributes, and correlation ID to retrieve for.</param>
    /// <param name="cancellationToken">Cancels the operation. Cancellation is not an expected condition and propagates unwrapped, distinct from the timeout fallback.</param>
    /// <returns>A result that always says what happened; it is empty unless <see cref="RetrievalOutcome.Completed"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/>, or its <see cref="RetrieveExperienceRequest.Authorization"/> or <see cref="RetrieveExperienceRequest.Scope"/>, is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><see cref="RetrieveExperienceRequest.TaskText"/> is blank or longer than <see cref="ExperienceCandidateQuery.MaxTaskTextLength"/>, or <see cref="RetrieveExperienceRequest.Limit"/> is not strictly positive or exceeds <see cref="RetrievalPolicy.CandidateLimit"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<ExperienceRetrievalResult> RetrieveAsync(
        RetrieveExperienceRequest request,
        CancellationToken cancellationToken = default)
    {
        using var operation = ExperienceDiagnostics.Start(ExperienceOperationNames.Retrieve, cancellationToken);

        ExperienceRetrievalResult result;
        try
        {
            // Read from the request, not from the result, so it really is echoed on every outcome -- a
            // timeout and a thrown retrieval included. A retrieval that threw is precisely the one an
            // operator needs to tie back to the invocation that asked for it, and it has no result to
            // read the identifier off. Omitted rather than written as an empty string when the host
            // supplied none: an absent attribute and a blank one do not mean the same thing to a query.
            ArgumentNullException.ThrowIfNull(request);
            ExperienceDiagnostics.Tag(operation, ExperienceDiagnostics.CorrelationIdAttribute, request.CorrelationId);

            result = await RetrieveCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ExperienceDiagnostics.Faulted(operation, ExperienceOperationNames.Retrieve, ex);
            throw;
        }

        // The task text this searched on, the records it ranked, and their lessons are never
        // telemetry values -- only the bounded outcome is.
        ExperienceDiagnostics.Succeeded(operation, ExperienceOperationNames.Retrieve, result.Outcome.ToString());
        return result;
    }

    /// <summary>The body of <see cref="RetrieveAsync"/>, unchanged by instrumentation: it neither reads nor writes a span.</summary>
    private async Task<ExperienceRetrievalResult> RetrieveCoreAsync(
        RetrieveExperienceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Authorization, $"{nameof(request)}.{nameof(request.Authorization)}");
        ArgumentNullException.ThrowIfNull(request.Scope, $"{nameof(request)}.{nameof(request.Scope)}");

        if (string.IsNullOrWhiteSpace(request.TaskText))
        {
            throw new ArgumentException("TaskText must be set to the task text to match experience against.", nameof(request));
        }

        if (request.TaskText.Length > ExperienceCandidateQuery.MaxTaskTextLength)
        {
            throw new ArgumentException(
                $"TaskText must be at most {ExperienceCandidateQuery.MaxTaskTextLength} characters.",
                nameof(request));
        }

        if (request.Limit is <= 0)
        {
            throw new ArgumentException("Limit must be strictly positive when specified.", nameof(request));
        }

        if (request.Limit > _policy.CandidateLimit)
        {
            // Rejected rather than quietly capped: asking for more than the search will ever consider
            // is a configuration mistake, and silently returning fewer would hide it.
            throw new ArgumentException(
                $"Limit must be at most the policy's candidate limit ({_policy.CandidateLimit}); a larger limit could never be satisfied.",
                nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var startedAt = _timeProvider.GetTimestamp();
        var unrestricted = request.RequiredEnvironmentAttributes is null or { Count: 0 };

        // Authorization first, and fail-closed: a request scope outside the host's authorization ends
        // here, so no search is issued at all and nothing about foreign scopes is observable.
        if (!request.Authorization.Permits(request.Scope))
        {
            // Denied before either channel is touched, so neither the database nor the embedding
            // provider ever sees a request outside the host's authorization.
            return Empty(RetrievalOutcome.Denied, request, unrestricted, startedAt, failure: null, EndedEarly());
        }

        // One more than the ceiling: the extra candidate is never ranked, it only distinguishes "exactly
        // at the ceiling" from "more existed", which is what the result's Truncated flag reports.
        var query = new ExperienceCandidateQuery(
            request.Scope,
            request.TaskText,
            EligibleStatuses,
            _policy.MinimumConfidence,
            _policy.CandidateLimit + 1,
            // Carried only so a channel that audits what it discloses can tie a delivered record back
            // to the invocation that asked for it. Nothing in retrieval reads it.
            request.CorrelationId);

        // Cancelled only after a timeout has been reported (it carries no timer of its own), so a
        // token-honouring source can never race a cancellation failure ahead of the timeout report.
        var inner = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Task.Run also bounds a source that blocks or throws synchronously. Both channels start here
        // and run concurrently, so the one timeout below bounds the pair rather than each in turn.
        var work = Task.Run(() => SearchChannelsAsync(request, query, inner.Token), CancellationToken.None);

        // Set once the abandoned-search path has taken ownership of disposing the token source; every
        // other exit -- returned, thrown, or an unexpected failure from WaitAsync or Rank -- disposes it
        // here, so a long-lived caller token never accumulates registrations.
        var abandoned = false;
        try
        {
            ChannelOutcome outcome;
            try
            {
                outcome = await work.WaitAsync(_policy.Timeout, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Report first, so a token-honouring source's cancellation cannot be reported in its place.
                abandoned = true;
                Abandon(work, inner);
                return Empty(RetrievalOutcome.TimedOut, request, unrestricted, startedAt, failure: null, EndedEarly());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller cancelled. Distinct from the timeout in kind and in reporting: it propagates.
                abandoned = true;
                Abandon(work, inner);
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // Neither the caller nor the timeout: a channel cancelled for its own reasons. Fail-closed.
                abandoned = true;
                Abandon(work, inner);
                return Empty(
                    RetrievalOutcome.Failed,
                    request,
                    unrestricted,
                    startedAt,
                    new RetrievalFailure("A retrieval channel cancelled the search for its own reasons.", ex),
                    EndedEarly());
            }

            if (outcome.Text.Failure is { } failure)
            {
                // The text channel is the one that can end the call: it is the channel every
                // deployment has, and answering from vectors alone would be an unfiltered-by-text
                // result the caller never asked for.
                return Empty(RetrievalOutcome.Failed, request, unrestricted, startedAt, failure, outcome.Vector.Fallback);
            }

            return Rank(request, outcome, unrestricted, startedAt);
        }
        finally
        {
            if (!abandoned)
            {
                inner.Dispose();
            }
        }
    }

    /// <summary>
    /// Runs both channels concurrently under the caller's one bound, and never lets the vector
    /// channel's trouble become the call's. Cancellation from either escapes, for the caller to
    /// classify as a timeout, a caller cancellation, or a channel cancelling for its own reasons.
    /// </summary>
    private async Task<ChannelOutcome> SearchChannelsAsync(
        RetrieveExperienceRequest request,
        ExperienceCandidateQuery query,
        CancellationToken cancellationToken)
    {
        var text = SearchAsync(request, query, cancellationToken);
        var vector = VectorSearchAsync(request, cancellationToken);

        // Awaited together rather than in sequence: the policy's timeout bounds the pair, so running
        // them one after the other would halve the budget each actually gets.
        await Task.WhenAll(text, vector).ConfigureAwait(false);
        return new ChannelOutcome(await text.ConfigureAwait(false), await vector.ConfigureAwait(false));
    }

    /// <summary>
    /// Embeds the request's task text and searches the index with it, turning every expected condition
    /// and every non-cancellation failure into an explicit text-only fallback rather than a failure.
    /// The text channel's candidates are never lost to something that went wrong here.
    /// </summary>
    private async Task<VectorOutcome> VectorSearchAsync(
        RetrieveExperienceRequest request,
        CancellationToken cancellationToken)
    {
        if (_embeddingIndex is null || _embeddingGenerator is null)
        {
            return VectorOutcome.FellBack(NotConfigured);
        }

        // The vector channel may never take the text channel's answer down with it, and that includes
        // cancellation it did not receive from the caller. An HttpClient request timeout surfaces as a
        // TaskCanceledException with the caller's token untouched, so a merely slow embedding provider
        // would otherwise turn every retrieval into a Failed result with no records at all.
        bool CallerCancelled() => cancellationToken.IsCancellationRequested;

        string modelId;
        int dimension;
        ReadOnlyMemory<float> vector;
        try
        {
            modelId = _embeddingGenerator.ModelId;
            dimension = _embeddingGenerator.Dimension;
            vector = await _embeddingGenerator.GenerateAsync(request.TaskText, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (CallerCancelled())
        {
            throw;
        }
        catch (Exception ex)
        {
            // Including a provider-side timeout, which arrives here as an OperationCanceledException
            // the caller never asked for.
            return VectorOutcome.FellBack(new VectorChannelFallback(
                TextOnlyReason.ProviderUnavailable,
                $"The embedding provider threw {ex.GetType().FullName}, so no query vector was produced.",
                ex));
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            return VectorOutcome.FellBack(new VectorChannelFallback(
                TextOnlyReason.ProviderUnavailable,
                "The embedding provider reported no model ID, so no stored vector could be known to be comparable.",
                Exception: null));
        }

        if (vector.Length == 0 || vector.Length != dimension)
        {
            // A vector that does not match the width the provider declared cannot be compared with
            // anything stored under that declaration, so nothing is sent to the index at all.
            return VectorOutcome.FellBack(new VectorChannelFallback(
                TextOnlyReason.ProviderUnavailable,
                $"The embedding provider returned a {vector.Length}-component query vector where {dimension} were declared.",
                Exception: null));
        }

        if (!IsFinite(vector))
        {
            // A non-finite component makes every distance computed against it meaningless, and pgvector
            // would reject it at the server. Fall back here rather than spend a database round trip.
            return VectorOutcome.FellBack(new VectorChannelFallback(
                TextOnlyReason.ProviderUnavailable,
                "The embedding provider returned a query vector with a non-finite component, so no comparison is possible.",
                Exception: null));
        }

        ExperienceVectorSearchResult result;
        try
        {
            result = await _embeddingIndex
                .SearchAsync(
                    request.Authorization,
                    new ExperienceVectorQuery(
                        request.Scope,
                        modelId,
                        vector,
                        EligibleStatuses,
                        _policy.MinimumConfidence,
                        // The same ceiling-plus-one probe the text channel uses, so either channel
                        // reaching the ceiling is visible as truncation.
                        _policy.CandidateLimit + 1),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (CallerCancelled())
        {
            throw;
        }
        catch (Exception ex)
        {
            return VectorOutcome.FellBack(new VectorChannelFallback(
                TextOnlyReason.VectorSearchFailed,
                $"The embedding index threw {ex.GetType().FullName} while searching stored vectors.",
                ex));
        }

        if (result is null)
        {
            return VectorOutcome.FellBack(new VectorChannelFallback(
                TextOnlyReason.VectorSearchFailed,
                "The embedding index returned no result at all.",
                Exception: null));
        }

        return result.Outcome switch
        {
            ExperienceVectorSearchOutcome.Found when result.Candidates is not null =>
                VectorOutcome.Succeeded(result.Candidates),
            ExperienceVectorSearchOutcome.Found => VectorOutcome.FellBack(new VectorChannelFallback(
                TextOnlyReason.VectorSearchFailed,
                "The embedding index reported matches but returned no candidate list.",
                Exception: null)),
            ExperienceVectorSearchOutcome.ModelMismatch => VectorOutcome.FellBack(new VectorChannelFallback(
                TextOnlyReason.ModelMismatch,
                "Every embedding stored in this scope came from a different model than the query vector; no comparison was attempted.",
                Exception: null)),
            ExperienceVectorSearchOutcome.DimensionMismatch => VectorOutcome.FellBack(new VectorChannelFallback(
                TextOnlyReason.DimensionMismatch,
                "Every embedding stored in this scope is a different width than the query vector; no comparison was attempted.",
                Exception: null)),
            _ => VectorOutcome.FellBack(new VectorChannelFallback(
                TextOnlyReason.VectorSearchFailed,
                $"The embedding index returned '{result.Outcome}' rather than '{ExperienceVectorSearchOutcome.Found}'.",
                Exception: null)),
        };
    }

    /// <summary>
    /// Runs the text search and turns every expected condition and every non-cancellation failure into
    /// a <see cref="SearchOutcome"/>. Cancellation alone escapes, for the caller to classify.
    /// </summary>
    private async Task<SearchOutcome> SearchAsync(
        RetrieveExperienceRequest request,
        ExperienceCandidateQuery query,
        CancellationToken cancellationToken)
    {
        ExperienceCandidateSearchResult result;
        try
        {
            result = await _candidateSource.SearchAsync(request.Authorization, query, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ExperienceStoreException ex)
        {
            return SearchOutcome.Failed(new RetrievalFailure("The candidate source failed to search stored experience.", ex));
        }
        catch (Exception ex)
        {
            return SearchOutcome.Failed(new RetrievalFailure($"The candidate source threw {ex.GetType().FullName}.", ex));
        }

        if (result is null)
        {
            return SearchOutcome.Failed(new RetrievalFailure("The candidate source returned no result at all.", Exception: null));
        }

        if (result.Outcome != ExperienceStoreOutcome.Found)
        {
            // Denied or Invalid from the source is still a refusal to answer, not an empty answer.
            // Deleted is not a search outcome -- a search never returns a tombstone, it leaves it out --
            // so a source answering it broke its contract and is reported like any other refusal.
            return SearchOutcome.Failed(new RetrievalFailure(
                $"The candidate source returned '{result.Outcome}' rather than '{ExperienceStoreOutcome.Found}'.",
                Exception: null));
        }

        return result.Candidates is null
            ? SearchOutcome.Failed(new RetrievalFailure("The candidate source reported matches but returned no candidate list.", Exception: null))
            : SearchOutcome.Succeeded(result.Candidates);
    }

    /// <summary>
    /// Merges the two channels, applies the eligibility checks Core owns, scores what survives, and
    /// orders the result. Any candidate that cannot be fully checked -- unreadable, or outside the
    /// requested scope -- makes the whole result empty rather than partly filtered.
    /// </summary>
    private ExperienceRetrievalResult Rank(
        RetrieveExperienceRequest request,
        ChannelOutcome channels,
        bool unrestricted,
        long startedAt)
    {
        var now = _timeProvider.GetUtcNow();
        var required = request.RequiredEnvironmentAttributes;

        // Each channel was asked for one candidate past the ceiling: its presence means more matched
        // than were considered, and it is dropped rather than ranked, so the ceiling still holds for
        // each channel separately.
        var merged = new List<ExperienceCandidate>();
        var positions = new Dictionary<Guid, int>();
        var truncated = false;

        if (Absorb(request, channels.Text.Candidates, "candidate source", merged, positions, ref truncated) is { } textFailure)
        {
            return Empty(RetrievalOutcome.Failed, request, unrestricted, startedAt, textFailure, channels.Vector.Fallback);
        }

        if (Absorb(request, channels.Vector.Candidates, "embedding index", merged, positions, ref truncated) is { } vectorFailure)
        {
            // A vector channel that answered with something unverifiable is treated exactly like a
            // text one that did: fail-closed. Silently dropping it would mean returning a result
            // built partly on an answer we just decided we could not check.
            return Empty(RetrievalOutcome.Failed, request, unrestricted, startedAt, vectorFailure, channels.Vector.Fallback);
        }

        var ranked = new List<(RankedExperience Ranked, string TieBreak)>(merged.Count);
        var excluded = new List<ExcludedExperience>();

        foreach (var candidate in merged)
        {
            var record = candidate.Record;

            if (!EligibleStatuses.Contains(record.Status))
            {
                excluded.Add(new ExcludedExperience(record.ExperienceId, RetrievalExclusionReason.IneligibleStatus));
                continue;
            }

            if (_policy.MaxAge is { } maxAge && now - record.UpdatedAt > maxAge)
            {
                excluded.Add(new ExcludedExperience(record.ExperienceId, RetrievalExclusionReason.Expired));
                continue;
            }

            if (!unrestricted && !EnvironmentMatches(required!, record.Environment.Metadata))
            {
                excluded.Add(new ExcludedExperience(record.ExperienceId, RetrievalExclusionReason.EnvironmentMismatch));
                continue;
            }

            ranked.Add((Score(record, candidate, now), record.ExperienceId.ToString("D")));
        }

        // Ties sort by ExperienceId ascending and ordinal, so the order is total and stable rather than
        // whatever order the database happened to return equally-scored rows in.
        ranked.Sort(static (left, right) =>
        {
            var byScore = right.Ranked.Score.CompareTo(left.Ranked.Score);
            return byScore != 0 ? byScore : string.CompareOrdinal(left.TieBreak, right.TieBreak);
        });

        var limit = request.Limit ?? _policy.CandidateLimit;
        var records = ranked.Take(limit).Select(entry => entry.Ranked).ToArray();

        return new ExperienceRetrievalResult(
            RetrievalOutcome.Completed,
            records,
            excluded,
            truncated,
            unrestricted,
            request.CorrelationId,
            _timeProvider.GetElapsedTime(startedAt),
            Failure: null,
            channels.Vector.Fallback);
    }

    /// <summary>
    /// Validates one channel's candidates and folds them into the merged list. Returns the failure
    /// that makes the whole result empty, or <see langword="null"/> when the channel's answer was
    /// entirely checkable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A record already contributed by the other channel is not added twice: it keeps whichever of the
    /// two normalized relevances is higher, and its position in the merged list does not move. That
    /// is the whole merge rule -- being found twice is not itself evidence of anything, so it earns no
    /// bonus and adds no sixth ranking axis.
    /// </para>
    /// <para>
    /// A duplicate <em>within</em> one channel is a different matter and is fail-closed: a channel
    /// that returned the same record twice cannot be trusted for the rest of its answer either.
    /// </para>
    /// </remarks>
    private RetrievalFailure? Absorb(
        RetrieveExperienceRequest request,
        IReadOnlyList<ExperienceCandidate> candidates,
        string channel,
        List<ExperienceCandidate> merged,
        Dictionary<Guid, int> positions,
        ref bool truncated)
    {
        if (candidates.Count > _policy.CandidateLimit)
        {
            truncated = true;
        }

        var considered = Math.Min(candidates.Count, _policy.CandidateLimit);
        var seen = new HashSet<Guid>(considered);

        for (var index = 0; index < considered; index++)
        {
            var candidate = candidates[index];
            if (candidate?.Record is not { } record || record.Environment?.Metadata is null || record.Scope is null)
            {
                return new RetrievalFailure(
                    $"A candidate from the {channel} could not be read, so the result would have been unfiltered.",
                    Exception: null);
            }

            // Strict by default: a record must be the requester's own. The only exception is one the
            // channel itself declared shared, and even then it must lie inside the boundary a grant can
            // never cross. A channel that returns a foreign record without saying so -- a third-party
            // adapter, or a regression in our own predicate -- is still caught here, and a channel that
            // claims sharing cannot use the claim to cross a tenant, application, or project.
            var inScope = candidate.SharedByGrant
                ? record.Scope.SharesGrantBoundary(request.Scope)
                : record.Scope == request.Scope;

            if (!inScope)
            {
                // The channel answered outside the exact request scope. Nothing it returned can be
                // trusted to be in scope, so none of it is returned.
                return new RetrievalFailure($"A candidate was returned by the {channel} outside the requested scope.", Exception: null);
            }

            if (!seen.Add(record.ExperienceId))
            {
                // The same record twice would be scored twice and ordered arbitrarily against itself,
                // so the ranking would no longer be total.
                return new RetrievalFailure($"The {channel} returned the same record more than once.", Exception: null);
            }

            if (positions.TryGetValue(record.ExperienceId, out var existing))
            {
                // Only the relevance is merged, never the record: the two channels read the record at
                // different instants, and adopting the other snapshot would let the expiry, status, and
                // environment checks below be decided on the staler of the two.
                var kept = merged[existing];
                if (Normalize(candidate.Relevance) > Normalize(kept.Relevance))
                {
                    merged[existing] = kept with { Relevance = candidate.Relevance };
                }

                continue;
            }

            positions[record.ExperienceId] = merged.Count;
            merged.Add(candidate);
        }

        return null;
    }

    /// <summary>
    /// Every required attribute must be present on the record and equal ordinally. A missing key
    /// excludes the record: an unstated environment is not a matching one.
    /// </summary>
    private static bool EnvironmentMatches(IReadOnlyDictionary<string, string> required, IReadOnlyDictionary<string, string> metadata)
    {
        foreach (var (key, value) in required)
        {
            if (!metadata.TryGetValue(key, out var stored) || !string.Equals(stored, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Scores one eligible record. Every component is normalized to [0, 1] and reported with the
    /// weight applied to it, so the total is always reproducible from what the result carries.
    /// </summary>
    private RankedExperience Score(ExperienceRecord record, ExperienceCandidate candidate, DateTimeOffset now)
    {
        RankingComponent[] components =
        [
            new(RankingComponentKind.Relevance, Normalize(candidate.Relevance), _weights.Relevance),
            new(RankingComponentKind.Confidence, Normalize(record.ReuseConfidence), _weights.Confidence),
            new(RankingComponentKind.Recency, Recency(record.UpdatedAt, now), _weights.Recency),
            new(RankingComponentKind.Status, StatusScore(record.Status), _weights.Status),
            new(RankingComponentKind.EnvironmentCompatibility, CompatibleEnvironmentScore, _weights.EnvironmentCompatibility),
        ];

        var score = 0d;
        foreach (var component in components)
        {
            score += component.Contribution;
        }

        // Passed through, never decided here: only the adapter that applied the scope predicate knows
        // whether a grant was what admitted this record.
        return new RankedExperience(record, score, components, candidate.SharedByGrant);
    }

    /// <summary>
    /// Exponential decay with the policy's half-life: 1 for a record updated now (or, with clock skew,
    /// in the future), 0.5 at one half-life, and always inside (0, 1].
    /// </summary>
    private double Recency(DateTimeOffset updatedAt, DateTimeOffset now)
    {
        var age = now - updatedAt;
        if (age <= TimeSpan.Zero)
        {
            return 1d;
        }

        return Normalize(Math.Pow(2d, -age.TotalSeconds / _policy.RecencyHalfLife.TotalSeconds));
    }

    private static double StatusScore(ExperienceStatus status) => status switch
    {
        ExperienceStatus.Reinforced => ReinforcedStatusScore,
        ExperienceStatus.Validated => ValidatedStatusScore,
        // Unreachable: anything else was excluded before ranking. Scored 0 rather than assumed.
        _ => 0d,
    };

    /// <summary>
    /// Clamps a component into [0, 1]. A NaN is scored 0: left alone it would poison every comparison
    /// against the record and make the ordering non-total.
    /// </summary>
    private static double Normalize(double value) => double.IsNaN(value) ? 0d : Math.Clamp(value, 0d, 1d);

    /// <summary>Whether every component is a real number. A NaN or an infinity makes every distance computed against the vector meaningless.</summary>
    private static bool IsFinite(ReadOnlyMemory<float> vector)
    {
        foreach (var component in vector.Span)
        {
            if (!float.IsFinite(component))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Hands an abandoned search off to run itself down in the background: cancel its token, then --
    /// once both the search and the cancellation have actually finished -- observe any exception it
    /// faulted with and dispose the token source.
    /// </summary>
    /// <remarks>
    /// The cancellation deliberately does not run on the caller's thread. A cancellation callback can
    /// be arbitrarily slow -- Npgsql's opens a <em>new</em> connection to the server to cancel the
    /// running statement -- so cancelling inline would let the call overrun the very timeout it is in
    /// the middle of reporting, exactly when the bound matters most. Disposal waits for both tasks,
    /// because disposing earlier would tear the token out from under a search or a callback still
    /// reading it.
    /// </remarks>
    private static void Abandon(Task task, CancellationTokenSource source)
    {
        var cancelling = Task.Run(
            async () =>
            {
                try
                {
                    await source.CancelAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A throwing cancellation callback must never affect the retrieval call's own result.
                }
            },
            CancellationToken.None);

        _ = Task.WhenAll(task, cancelling).ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                _ = task.Exception;
                source.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private ExperienceRetrievalResult Empty(
        RetrievalOutcome outcome,
        RetrieveExperienceRequest request,
        bool unrestricted,
        long startedAt,
        RetrievalFailure? failure,
        VectorChannelFallback? vectorFallback) => new(
            outcome,
            NoRecords,
            NoExclusions,
            // Nothing was ranked, so nothing was cut: a denied, timed-out, or failed result is empty for
            // its own reason, never because a ceiling was reached.
            Truncated: false,
            unrestricted,
            request.CorrelationId,
            _timeProvider.GetElapsedTime(startedAt),
            failure,
            vectorFallback);

    /// <summary>
    /// The vector signal for a call that ended before either channel could contribute: the standing
    /// fact that this deployment has no vector channel, when that is so, and otherwise nothing -- the
    /// outcome itself already says why the result is empty.
    /// </summary>
    private VectorChannelFallback? EndedEarly() => HybridEnabled ? null : NotConfigured;

    /// <summary>What the bounded text search produced: either candidates, or the failure that ended it.</summary>
    private readonly record struct SearchOutcome(IReadOnlyList<ExperienceCandidate> Candidates, RetrievalFailure? Failure)
    {
        public static SearchOutcome Succeeded(IReadOnlyList<ExperienceCandidate> candidates) => new(candidates, null);

        public static SearchOutcome Failed(RetrievalFailure failure) => new([], failure);
    }

    /// <summary>
    /// What the bounded vector search produced. Unlike the text channel it has no failure: everything
    /// that can go wrong here is a fallback, because the text channel's answer must survive it.
    /// </summary>
    private readonly record struct VectorOutcome(IReadOnlyList<ExperienceCandidate> Candidates, VectorChannelFallback? Fallback)
    {
        public static VectorOutcome Succeeded(IReadOnlyList<ExperienceCandidate> candidates) => new(candidates, null);

        public static VectorOutcome FellBack(VectorChannelFallback fallback) => new([], fallback);
    }

    /// <summary>Both channels' answers, produced together inside the one timeout.</summary>
    private readonly record struct ChannelOutcome(SearchOutcome Text, VectorOutcome Vector);
}

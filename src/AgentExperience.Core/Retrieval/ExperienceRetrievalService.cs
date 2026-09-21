using AgentExperience.Abstractions;

namespace AgentExperience.Core.Retrieval;

/// <summary>
/// The single Core call that finds experience applicable to a task: it asks an
/// <see cref="IExperienceCandidateSource"/> for scope-, status- and confidence-filtered candidates
/// matched on task text, decides the remaining eligibility itself, and ranks what survives with
/// weights and components it reports back in full.
/// </summary>
/// <remarks>
/// <para>
/// <b>Filter, then rank.</b> Nothing is ever scored before it is known to be reusable. The database
/// decides scope, status, and the confidence floor; Core decides expiry and environment
/// compatibility, because both depend on the clock and on the request rather than on stored state
/// alone. Only <see cref="ExperienceStatus.Validated"/> and <see cref="ExperienceStatus.Reinforced"/>
/// records are ever eligible -- a <see cref="ExperienceStatus.Candidate"/>,
/// <see cref="ExperienceStatus.Quarantined"/>, <see cref="ExperienceStatus.Contested"/>,
/// <see cref="ExperienceStatus.Stale"/>, <see cref="ExperienceStatus.Superseded"/>, or
/// <see cref="ExperienceStatus.Revoked"/> record is dropped whatever its text match.
/// </para>
/// <para>
/// <b>The candidate ceiling is a real recall limit.</b> The search returns at most
/// <see cref="RetrievalPolicy.CandidateLimit"/> candidates, ordered by <em>text</em> relevance, and
/// ranking only ever sees those. So a record with a weaker text match but strong confidence, recency,
/// or status is not ranked at all once that many stronger text matches exist -- the weighting can only
/// reorder what the ceiling let through. When the ceiling was reached the result says so
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
/// This service neither generates nor queries embeddings, and it does not build an injectable
/// payload: it returns ranked records and the evidence for their ranking, and what a host does with
/// them is a separate decision.
/// </para>
/// </remarks>
public sealed class ExperienceRetrievalService
{
    /// <summary>
    /// The only statuses a record may be in and still be returned. This is the eligibility rule, not
    /// a default: a record in any other status is never injectable, whatever its text match or
    /// confidence.
    /// </summary>
    public static IReadOnlyList<ExperienceStatus> EligibleStatuses { get; } =
        [ExperienceStatus.Validated, ExperienceStatus.Reinforced];

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

    private readonly IExperienceCandidateSource _candidateSource;
    private readonly RetrievalPolicy _policy;
    private readonly RankingWeights _weights;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a retrieval service over a candidate source, its policy, its weights, and the clock it measures with.</summary>
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
    {
        ArgumentNullException.ThrowIfNull(candidateSource);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _candidateSource = candidateSource;
        _policy = policy;
        _weights = weights;
        _timeProvider = timeProvider;
    }

    /// <summary>The policy this service runs under.</summary>
    public RetrievalPolicy Policy => _policy;

    /// <summary>The weights this service ranks with.</summary>
    public RankingWeights Weights => _weights;

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
            return Empty(RetrievalOutcome.Denied, request, unrestricted, startedAt, failure: null);
        }

        // One more than the ceiling: the extra candidate is never ranked, it only distinguishes "exactly
        // at the ceiling" from "more existed", which is what the result's Truncated flag reports.
        var query = new ExperienceCandidateQuery(
            request.Scope,
            request.TaskText,
            EligibleStatuses,
            _policy.MinimumConfidence,
            _policy.CandidateLimit + 1);

        // Cancelled only after a timeout has been reported (it carries no timer of its own), so a
        // token-honouring source can never race a cancellation failure ahead of the timeout report.
        var inner = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Task.Run also bounds a source that blocks or throws synchronously.
        var work = Task.Run(() => SearchAsync(request, query, inner.Token), CancellationToken.None);

        // Set once the abandoned-search path has taken ownership of disposing the token source; every
        // other exit -- returned, thrown, or an unexpected failure from WaitAsync or Rank -- disposes it
        // here, so a long-lived caller token never accumulates registrations.
        var abandoned = false;
        try
        {
            SearchOutcome outcome;
            try
            {
                outcome = await work.WaitAsync(_policy.Timeout, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Report first, so a token-honouring source's cancellation cannot be reported in its place.
                abandoned = true;
                Abandon(work, inner);
                return Empty(RetrievalOutcome.TimedOut, request, unrestricted, startedAt, failure: null);
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
                // Neither the caller nor the timeout: the source cancelled for its own reasons. Fail-closed.
                abandoned = true;
                Abandon(work, inner);
                return Empty(
                    RetrievalOutcome.Failed,
                    request,
                    unrestricted,
                    startedAt,
                    new RetrievalFailure("The candidate source cancelled the search for its own reasons.", ex));
            }

            if (outcome.Failure is { } failure)
            {
                return Empty(RetrievalOutcome.Failed, request, unrestricted, startedAt, failure);
            }

            return Rank(request, outcome.Candidates, unrestricted, startedAt);
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
    /// Runs the search and turns every expected condition and every non-cancellation failure into a
    /// <see cref="SearchOutcome"/>. Cancellation alone escapes, for the caller to classify.
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
            return SearchOutcome.Failed(new RetrievalFailure(
                $"The candidate source returned '{result.Outcome}' rather than '{ExperienceStoreOutcome.Found}'.",
                Exception: null));
        }

        return result.Candidates is null
            ? SearchOutcome.Failed(new RetrievalFailure("The candidate source reported matches but returned no candidate list.", Exception: null))
            : SearchOutcome.Succeeded(result.Candidates);
    }

    /// <summary>
    /// Applies the eligibility checks Core owns, scores what survives, and orders the result. Any
    /// candidate that cannot be fully checked -- unreadable, or outside the requested scope -- makes
    /// the whole result empty rather than partly filtered.
    /// </summary>
    private ExperienceRetrievalResult Rank(
        RetrieveExperienceRequest request,
        IReadOnlyList<ExperienceCandidate> candidates,
        bool unrestricted,
        long startedAt)
    {
        var now = _timeProvider.GetUtcNow();
        var required = request.RequiredEnvironmentAttributes;

        // The search was asked for one candidate past the ceiling: its presence means more matched than
        // were considered, and it is dropped rather than ranked, so the ceiling still holds.
        var truncated = candidates.Count > _policy.CandidateLimit;
        var considered = truncated ? _policy.CandidateLimit : candidates.Count;

        var ranked = new List<(RankedExperience Ranked, string TieBreak)>(considered);
        var excluded = new List<ExcludedExperience>();
        var seen = new HashSet<Guid>(considered);

        for (var index = 0; index < considered; index++)
        {
            var candidate = candidates[index];
            if (candidate?.Record is not { } record || record.Environment?.Metadata is null || record.Scope is null)
            {
                return Empty(
                    RetrievalOutcome.Failed,
                    request,
                    unrestricted,
                    startedAt,
                    new RetrievalFailure("A candidate could not be read, so the result would have been unfiltered.", Exception: null));
            }

            if (record.Scope != request.Scope)
            {
                // The source answered outside the exact request scope. Nothing it returned can be
                // trusted to be in scope, so none of it is returned.
                return Empty(
                    RetrievalOutcome.Failed,
                    request,
                    unrestricted,
                    startedAt,
                    new RetrievalFailure("A candidate was returned outside the requested scope.", Exception: null));
            }

            if (!seen.Add(record.ExperienceId))
            {
                // The same record twice would be scored twice and ordered arbitrarily against itself, so
                // the ranking would no longer be total. A source that did that cannot be trusted for the
                // rest of its answer either.
                return Empty(
                    RetrievalOutcome.Failed,
                    request,
                    unrestricted,
                    startedAt,
                    new RetrievalFailure("The candidate source returned the same record more than once.", Exception: null));
            }

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

            ranked.Add((Score(record, candidate.Relevance, now), record.ExperienceId.ToString("D")));
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
            Failure: null);
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
    private RankedExperience Score(ExperienceRecord record, double relevance, DateTimeOffset now)
    {
        RankingComponent[] components =
        [
            new(RankingComponentKind.Relevance, Normalize(relevance), _weights.Relevance),
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

        return new RankedExperience(record, score, components);
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
        RetrievalFailure? failure) => new(
            outcome,
            NoRecords,
            NoExclusions,
            // Nothing was ranked, so nothing was cut: a denied, timed-out, or failed result is empty for
            // its own reason, never because a ceiling was reached.
            Truncated: false,
            unrestricted,
            request.CorrelationId,
            _timeProvider.GetElapsedTime(startedAt),
            failure);

    /// <summary>What the bounded search produced: either candidates, or the failure that ended it.</summary>
    private readonly record struct SearchOutcome(IReadOnlyList<ExperienceCandidate> Candidates, RetrievalFailure? Failure)
    {
        public static SearchOutcome Succeeded(IReadOnlyList<ExperienceCandidate> candidates) => new(candidates, null);

        public static SearchOutcome Failed(RetrievalFailure failure) => new([], failure);
    }
}

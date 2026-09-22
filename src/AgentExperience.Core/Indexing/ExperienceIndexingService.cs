using AgentExperience.Abstractions;
using AgentExperience.Core.Retrieval;

namespace AgentExperience.Core.Indexing;

/// <summary>
/// The single Core call that gives a stored Experience Record a vector: it reads what the record's
/// sanitized retrieval summary currently is, embeds it through a replaceable provider port, and asks
/// the index to store the result conditionally on the record still being exactly where and what it
/// was read as.
/// </summary>
/// <remarks>
/// <para>
/// <b>Embeddings are derived data, and this is why nothing here can break a record.</b> Indexing runs
/// strictly after the canonical commit, never inside it. Every failure -- an unavailable provider, a
/// rejected write, an unreachable index -- comes back as a structured result, and the record stays
/// committed, durable, and text-searchable either way. Nothing this service does can change a
/// record's status, revision, confidence, or eligibility.
/// </para>
/// <para>
/// <b>Only the sanitized retrieval summary is ever sent to a provider.</b> That is the task ID, the
/// sanitized task summary, and the reflection's lesson -- exactly the three fields the text index
/// analyzes (<see cref="ExperienceRetrievalSummary"/>). Attempts, tool calls, evidence, provenance,
/// and environment metadata never leave the database. The summary is read from the index's own scan
/// rather than from a record the caller happens to be holding, so the text that is embedded is the
/// text that is really stored, at the revision it is really stored at.
/// </para>
/// <para>
/// <b>Unchanged means free.</b> The content hash covers the model ID and the normalized summary, so a
/// record whose stored vector already came from this model and this text is reported
/// <see cref="ExperienceIndexingOutcome.Skipped"/> <em>before</em> any provider call. That is what
/// makes a re-index pass idempotent: running it twice over unchanged records costs two reads and
/// nothing else.
/// </para>
/// <para>
/// <b>Writes are conditional, and this service never forces one.</b> The descriptor it sends carries
/// the revision the summary was read at, and the index applies the write only while the record is
/// still at that revision. A record that moved is <see cref="ExperienceIndexingOutcome.Stale"/> and a
/// record that is gone is <see cref="ExperienceIndexingOutcome.Missing"/> -- never a retry loop that
/// eventually overwrites newer state, and never a row for a record that no longer exists.
/// </para>
/// <para>
/// <b>Only records a search could return are ever embedded.</b> Both the scan and the post-commit hook
/// apply the same status filter and confidence floor the vector search applies, so a
/// <see cref="ExperienceStatus.Quarantined"/>, <see cref="ExperienceStatus.Revoked"/>,
/// <see cref="ExperienceStatus.Superseded"/>, or <see cref="ExperienceStatus.Candidate"/> record's
/// summary and lesson never leave the database for a third-party provider -- its vector could never be
/// returned anyway.
/// </para>
/// <para>
/// <b>Only the caller's cancellation escapes.</b> An <see cref="OperationCanceledException"/> observed
/// while <em>the caller's own token</em> is cancelled propagates, so a cancelled pass is never reported
/// as a completed one. A port that cancels for its own reasons does not: that is the ordinary shape of
/// a client-side request timeout, and one slow record must not abandon a whole pass, so it is reported
/// as <see cref="ExperienceIndexingOutcome.ProviderFailed"/> like any other provider failure.
/// </para>
/// </remarks>
public sealed class ExperienceIndexingService
{
    private static readonly IReadOnlyList<StoreValidationError> NoErrors = [];

    private static readonly IReadOnlyList<ExperienceIndexingResult> NoRecords = [];

    private readonly IExperienceEmbeddingIndex _index;
    private readonly IExperienceEmbeddingGenerator _generator;

    /// <summary>Creates an indexing service over the embedding index and the provider that produces vectors.</summary>
    /// <param name="index">Where vectors are stored, listed, and searched.</param>
    /// <param name="generator">The replaceable provider port. Its model ID and dimension are read once, here.</param>
    /// <param name="policy">
    /// Optional. The retrieval policy whose <see cref="RetrievalPolicy.MinimumConfidence"/> decides
    /// which records are worth embedding -- deliberately the same object retrieval runs under, because
    /// a record the search would never return must never be sent to a provider. Defaults to
    /// <see cref="RetrievalPolicy.Default"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> or <paramref name="generator"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="generator"/> reports a blank or over-long model ID.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="generator"/> reports a dimension outside 1..<see cref="ExperienceEmbeddingDescriptor.MaxDimension"/>.</exception>
    public ExperienceIndexingService(
        IExperienceEmbeddingIndex index,
        IExperienceEmbeddingGenerator generator,
        RetrievalPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(generator);

        // Read once, at construction: the content hash covers the model ID, so both have to be stable
        // and known before any provider call. A generator that cannot say what it is fails here,
        // at wiring time, rather than producing vectors nobody can decide the comparability of.
        ModelId = generator.ModelId;
        if (string.IsNullOrWhiteSpace(ModelId))
        {
            throw new ArgumentException(
                "The embedding generator must report a non-blank model ID: it is part of every stored embedding's " +
                "content hash and decides which vectors are comparable.",
                nameof(generator));
        }

        if (ModelId.Length > ExperienceEmbeddingDescriptor.MaxModelIdLength)
        {
            throw new ArgumentException(
                $"The embedding generator's model ID must be at most {ExperienceEmbeddingDescriptor.MaxModelIdLength} characters.",
                nameof(generator));
        }

        Dimension = generator.Dimension;
        if (Dimension is < 1 or > ExperienceEmbeddingDescriptor.MaxDimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generator),
                Dimension,
                $"The embedding generator's dimension must be between 1 and {ExperienceEmbeddingDescriptor.MaxDimension}.");
        }

        _index = index;
        _generator = generator;
        MinimumConfidence = (policy ?? RetrievalPolicy.Default).MinimumConfidence;
    }

    /// <summary>
    /// The only statuses whose records are ever embedded: exactly the ones a vector search can return
    /// (<see cref="ExperienceRetrievalService.EligibleStatuses"/>). A
    /// <see cref="ExperienceStatus.Quarantined"/>, <see cref="ExperienceStatus.Revoked"/>,
    /// <see cref="ExperienceStatus.Superseded"/>, or <see cref="ExperienceStatus.Candidate"/> record's
    /// summary and lesson are never handed to a provider, because its vector could never be returned.
    /// </summary>
    public static IReadOnlyList<ExperienceStatus> IndexableStatuses => ExperienceRetrievalService.EligibleStatuses;

    /// <summary>The model every vector this service writes is stamped with, and compared under at query time.</summary>
    public string ModelId { get; }

    /// <summary>The width of every vector this service writes. A provider that returns any other width is rejected.</summary>
    public int Dimension { get; }

    /// <summary>The reuse-confidence floor below which a record is not worth embedding, because the search would not return it either.</summary>
    public double MinimumConfidence { get; }

    /// <summary>
    /// Whether a record in this state could ever be returned by a vector search, and is therefore worth
    /// sending to an embedding provider. Callers that already hold a record -- the post-commit hook,
    /// for one -- check this before any provider call rather than discovering it from an empty scan.
    /// </summary>
    /// <param name="status">The record's lifecycle status.</param>
    /// <param name="reuseConfidence">The record's reuse confidence.</param>
    /// <returns><see langword="true"/> when the record is worth embedding.</returns>
    public bool IsIndexable(ExperienceStatus status, double reuseConfidence) =>
        IndexableStatuses.Contains(status) && reuseConfidence >= MinimumConfidence;

    /// <summary>
    /// Indexes one record: reads its current summary and revision, skips it when this model already
    /// embedded exactly that text, and otherwise embeds it and writes the vector conditionally.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The exact scope the record must lie in. Never treated as authority.</param>
    /// <param name="experienceId">The record to index.</param>
    /// <param name="cancellationToken">Cancels the operation. Cancellation is not an expected condition and propagates unwrapped.</param>
    /// <returns>
    /// A structured result; never an exception for an expected condition. A record the index does not
    /// list is <see cref="ExperienceIndexingOutcome.Missing"/>, which covers all three ways that can
    /// happen: it was deleted, it is in another scope, or its status or confidence means a search could
    /// never return it. A caller that already holds the record -- the post-commit hook -- distinguishes
    /// the last case itself with <see cref="IsIndexable"/> before calling.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="authorization"/> or <paramref name="scope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="experienceId"/> is <see cref="Guid.Empty"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled, or a port cancelled.</exception>
    public async Task<ExperienceIndexingResult> IndexAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);
        if (experienceId == Guid.Empty)
        {
            throw new ArgumentException("ExperienceId must not be an empty GUID.", nameof(experienceId));
        }

        var pass = await ReindexAsync(
            authorization,
            new ReindexExperienceRequest(scope, [experienceId], Limit: 1),
            cancellationToken).ConfigureAwait(false);

        if (pass.Records.Count > 0)
        {
            return pass.Records[0];
        }

        return pass.Outcome switch
        {
            // The scan ran and listed nothing: the record is not in this scope, or no longer exists.
            ExperienceReindexOutcome.Completed => new(ExperienceIndexingOutcome.Missing, experienceId, null, null),
            ExperienceReindexOutcome.Denied => new(ExperienceIndexingOutcome.Denied, experienceId, null, pass.Failure),
            _ => new(ExperienceIndexingOutcome.IndexFailed, experienceId, null, pass.Failure),
        };
    }

    /// <summary>
    /// Runs one scoped, explicit re-index pass: lists what the scope holds, and for each record either
    /// skips it (this model already embedded exactly that text) or re-embeds and rewrites it.
    /// Re-running the same pass over unchanged records calls no provider and writes nothing.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="request">The scope to re-index, optionally narrowed to specific records, and the bound on how many to consider.</param>
    /// <param name="cancellationToken">Cancels the operation. Cancellation is not an expected condition and propagates unwrapped.</param>
    /// <returns>A structured result with the tally and one entry per considered record.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="authorization"/>, <paramref name="request"/>, or its <see cref="ReindexExperienceRequest.Scope"/>, is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled, or a port cancelled.</exception>
    public async Task<ExperienceReindexResult> ReindexAsync(
        AuthorizationContext authorization,
        ReindexExperienceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Scope, $"{nameof(request)}.{nameof(request.Scope)}");

        cancellationToken.ThrowIfCancellationRequested();

        ExperienceIndexScanResult scan;
        try
        {
            scan = await _index
                .ScanAsync(
                    authorization,
                    new ExperienceIndexScan(
                        request.Scope,
                        ModelId,
                        IndexableStatuses,
                        MinimumConfidence,
                        request.ExperienceIds,
                        request.Limit,
                        request.StartAfterId),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Ended(
                ExperienceReindexOutcome.Failed,
                new ExperienceIndexingFailure(
                    $"The embedding index threw {ex.GetType().FullName} while listing what to re-index.",
                    NoErrors,
                    ex));
        }

        if (scan is null)
        {
            return Ended(
                ExperienceReindexOutcome.Failed,
                new ExperienceIndexingFailure("The embedding index returned no scan result at all.", NoErrors, Exception: null));
        }

        switch (scan.Outcome)
        {
            case ExperienceStoreOutcome.Found:
                break;

            case ExperienceStoreOutcome.Denied:
                return Ended(
                    ExperienceReindexOutcome.Denied,
                    new ExperienceIndexingFailure(
                        "The requested scope lies outside the host-established authorization; nothing was read, embedded, or written.",
                        NoErrors,
                        Exception: null));

            case ExperienceStoreOutcome.Invalid:
                return Ended(
                    ExperienceReindexOutcome.Invalid,
                    new ExperienceIndexingFailure(
                        "The embedding index rejected the scan as malformed. See the validation errors.",
                        scan.Errors ?? NoErrors,
                        Exception: null));

            default:
                return Ended(
                    ExperienceReindexOutcome.Failed,
                    new ExperienceIndexingFailure(
                        $"The embedding index returned '{scan.Outcome}' rather than '{ExperienceStoreOutcome.Found}'.",
                        NoErrors,
                        Exception: null));
        }

        if (scan.Targets is null)
        {
            return Ended(
                ExperienceReindexOutcome.Failed,
                new ExperienceIndexingFailure("The embedding index reported a successful scan but returned no target list.", NoErrors, Exception: null));
        }

        var results = new List<ExperienceIndexingResult>(scan.Targets.Count);
        var indexed = 0;
        var skipped = 0;
        var rejected = 0;
        var failed = 0;

        foreach (var target in scan.Targets)
        {
            if (target is null)
            {
                return Ended(
                    ExperienceReindexOutcome.Failed,
                    new ExperienceIndexingFailure("The embedding index returned a null scan target.", NoErrors, Exception: null));
            }

            var result = await IndexTargetAsync(authorization, request.Scope, target, cancellationToken).ConfigureAwait(false);
            results.Add(result);

            switch (result.Outcome)
            {
                case ExperienceIndexingOutcome.Indexed:
                    indexed++;
                    break;
                case ExperienceIndexingOutcome.Skipped:
                    skipped++;
                    break;
                case ExperienceIndexingOutcome.Stale:
                case ExperienceIndexingOutcome.Missing:
                case ExperienceIndexingOutcome.Denied:
                case ExperienceIndexingOutcome.Ineligible:
                    rejected++;
                    break;
                default:
                    failed++;
                    break;
            }
        }

        return new(
            ExperienceReindexOutcome.Completed,
            results.Count,
            indexed,
            skipped,
            rejected,
            failed,
            results,
            Failure: null,
            scan.LastExaminedId ?? (results.Count > 0 ? results[^1].ExperienceId : null));
    }

    /// <summary>
    /// Indexes one already-listed record: decide whether anything changed, embed only if it did, then
    /// write conditionally on the revision the target was read at.
    /// </summary>
    private async Task<ExperienceIndexingResult> IndexTargetAsync(
        AuthorizationContext authorization,
        Scope scope,
        ExperienceIndexTarget target,
        CancellationToken cancellationToken)
    {
        // Only the caller's own cancellation ends a pass. A provider that cancels for its own reasons
        // is the ordinary shape of a client-side timeout -- HttpClient raises its request timeout as a
        // TaskCanceledException with the caller's token untouched -- and one slow record must not
        // abandon a whole re-index with no per-record results at all.
        bool CallerCancelled() => cancellationToken.IsCancellationRequested;

        var summary = target.Summary ?? string.Empty;
        var contentHash = ExperienceEmbeddingDescriptor.ComputeContentHash(ModelId, summary);

        if (target.Stored is { } stored
            && string.Equals(stored.ModelId, ModelId, StringComparison.Ordinal)
            && string.Equals(stored.ContentHash, contentHash, StringComparison.Ordinal))
        {
            // Same model, same text: the stored vector is exactly what this pass would have produced.
            // No provider call, no write -- this is the whole point of storing the hash.
            return new(ExperienceIndexingOutcome.Skipped, target.ExperienceId, stored, null);
        }

        ReadOnlyMemory<float> vector;
        try
        {
            vector = await _generator.GenerateAsync(summary, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (CallerCancelled())
        {
            // Only the caller's own cancellation ends the pass.
            throw;
        }
        catch (Exception ex)
        {
            // Caught, never rethrown: the record stays committed and text-searchable, and this is
            // reported as retryable rather than failing whatever called us. A provider-side timeout
            // arrives here as an OperationCanceledException and is a provider failure like any other.
            return new(
                ExperienceIndexingOutcome.ProviderFailed,
                target.ExperienceId,
                target.Stored,
                new ExperienceIndexingFailure(
                    $"The embedding provider threw {ex.GetType().FullName}; the record is unchanged and still indexable later.",
                    NoErrors,
                    ex));
        }

        if (vector.Length != Dimension)
        {
            return new(
                ExperienceIndexingOutcome.ProviderFailed,
                target.ExperienceId,
                target.Stored,
                new ExperienceIndexingFailure(
                    $"The embedding provider returned a {vector.Length}-component vector where {Dimension} were declared; " +
                    "a stored descriptor must never disagree with its own vector.",
                    NoErrors,
                    Exception: null));
        }

        if (!IsFinite(vector))
        {
            // A NaN or an infinity is the right width and so would pass every later check, then be
            // rejected by the database -- reported as retryable, which is a retry loop that never ends.
            // It is a provider failure, and it is caught here, once.
            return new(
                ExperienceIndexingOutcome.ProviderFailed,
                target.ExperienceId,
                target.Stored,
                new ExperienceIndexingFailure(
                    "The embedding provider returned a vector with a non-finite component; a stored vector must be " +
                    "entirely finite or every distance computed against it is meaningless.",
                    NoErrors,
                    Exception: null));
        }

        var descriptor = new ExperienceEmbeddingDescriptor(ModelId, Dimension, contentHash, target.SourceRevision);

        ExperienceIndexWriteResult write;
        try
        {
            write = await _index
                .WriteAsync(authorization, new ExperienceIndexWrite(scope, target.ExperienceId, descriptor, vector), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (CallerCancelled())
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(
                ExperienceIndexingOutcome.IndexFailed,
                target.ExperienceId,
                target.Stored,
                new ExperienceIndexingFailure(
                    $"The embedding index threw {ex.GetType().FullName} while storing the vector; the record is unchanged.",
                    NoErrors,
                    ex));
        }

        return write?.Outcome switch
        {
            ExperienceIndexOutcome.Written => new(ExperienceIndexingOutcome.Indexed, target.ExperienceId, descriptor, null),
            ExperienceIndexOutcome.Stale => new(
                ExperienceIndexingOutcome.Stale,
                target.ExperienceId,
                target.Stored,
                new ExperienceIndexingFailure(
                    // Two ways to lose: the record moved on, or another writer stored a vector from a
                    // newer revision first. Naming the same revision twice would describe neither.
                    write.CurrentRevision != target.SourceRevision
                        ? $"The record moved to revision {write.CurrentRevision} after its summary was read at revision {target.SourceRevision}; " +
                          "the write was rejected and the stored vector is unchanged."
                        : $"A concurrent write had already stored a vector for this record from revision {target.SourceRevision} or newer; " +
                          "this write was rejected and the stored vector is unchanged.",
                    NoErrors,
                    Exception: null)),
            ExperienceIndexOutcome.Missing => new(
                ExperienceIndexingOutcome.Missing,
                target.ExperienceId,
                null,
                new ExperienceIndexingFailure(
                    "The record no longer exists within the requested scope; nothing was written and no row was created.",
                    NoErrors,
                    Exception: null)),
            ExperienceIndexOutcome.Denied => new(
                ExperienceIndexingOutcome.Denied,
                target.ExperienceId,
                target.Stored,
                new ExperienceIndexingFailure(
                    "The embedding index refused the record's scope as outside the host-established authorization; nothing was written.",
                    NoErrors,
                    Exception: null)),
            ExperienceIndexOutcome.Invalid => new(
                ExperienceIndexingOutcome.IndexFailed,
                target.ExperienceId,
                target.Stored,
                new ExperienceIndexingFailure(
                    "The embedding index rejected the write as malformed. See the validation errors.",
                    write.Errors ?? NoErrors,
                    Exception: null)),
            _ => new(
                ExperienceIndexingOutcome.IndexFailed,
                target.ExperienceId,
                target.Stored,
                new ExperienceIndexingFailure(
                    write is null
                        ? "The embedding index returned no write result at all."
                        : $"The embedding index returned '{write.Outcome}', which is not a write outcome.",
                    NoErrors,
                    Exception: null)),
        };
    }

    /// <summary>Whether every component is a real number. A NaN or an infinity poisons every distance computed against the vector.</summary>
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

    private static ExperienceReindexResult Ended(ExperienceReindexOutcome outcome, ExperienceIndexingFailure failure) =>
        new(outcome, 0, 0, 0, 0, 0, NoRecords, failure, LastExaminedId: null);
}

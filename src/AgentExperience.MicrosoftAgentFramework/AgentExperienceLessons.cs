using AgentExperience.Abstractions;
using AgentExperience.Core.Holdout;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework.Export;
using AgentExperience.MicrosoftAgentFramework.Injection;

namespace AgentExperience.MicrosoftAgentFramework;

/// <summary>
/// A read-only view of what the library has learned in one scope: each lesson's status, confidence and checks, and the
/// block text an agent in that scope would be shown for it. The one-call setup registers it; get it with
/// <c>provider.GetAgentExperienceLessons()</c> or from the container. <see cref="ExperienceLessonsHtml"/> renders a page
/// of it as HTML.
/// </summary>
/// <remarks>
/// <para>
/// It reads through <see cref="IExperienceRecordStore.QueryWithInjectionsAsync"/> -- the page, and when each lesson was
/// last injected, in one bounded store read -- under the caller's own authorization and in the caller's own scope: the
/// same boundary retrieval reads within. Lessons another scope lends through a sharing grant are not listed. It never
/// writes.
/// </para>
/// <para>
/// The scope comes from the <see cref="ExperienceIdentity"/> passed in, which in a web host must come from the host's own
/// authentication, never from the request's query string or body. Listing lessons discloses them: serve the view only
/// behind the host's own authorization. The library maps no endpoint.
/// </para>
/// </remarks>
public sealed class AgentExperienceLessons
{
    private readonly IExperienceRecordStore _store;
    private readonly ExperienceContextProvider _provider;
    private readonly TimeProvider _clock;
    private readonly AgentExperienceRegistration? _registration;
    private readonly ExperienceConsolidationService? _consolidation;

    internal AgentExperienceLessons(
        IExperienceRecordStore store,
        ExperienceContextProvider provider,
        TimeProvider clock,
        AgentExperienceRegistration? registration = null,
        ExperienceConsolidationService? consolidation = null)
    {
        _store = store;
        _provider = provider;
        _clock = clock;
        _registration = registration;
        _consolidation = consolidation;
    }

    /// <summary>The context provider whose writer and options render the blocks. For tests.</summary>
    internal ExperienceContextProvider Provider => _provider;

    /// <summary>
    /// Lists the lessons in <paramref name="identity"/>'s scope, newest first.
    /// </summary>
    /// <param name="identity">The caller, from the host's own authentication: the authorization the store checks, and the scope listed.</param>
    /// <param name="query">The statuses to list, how many, and whether to include lessons retrieval would not return; <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The page, with <see cref="ExperienceLessonsPage.HasMore"/> set when more lessons matching the query exist (or
    /// may exist: the store answers at most <see cref="ExperienceRecordQuery.MaxLimit"/> records). A store outcome
    /// other than <see cref="ExperienceStoreOutcome.Found"/> (the scope lies outside the authorization, say) is an empty
    /// page carrying that outcome, never an exception. A record that cannot be rendered is listed as
    /// <see cref="ExperienceLessonIneligibility.Unrenderable"/>, and the rest are still listed.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ExperienceLessonsQuery.Limit"/> is outside the store's bounds.</exception>
    /// <exception cref="ArgumentException"><see cref="ExperienceLessonsQuery.Statuses"/> is empty or holds an undefined status.</exception>
    /// <exception cref="InvalidOperationException">
    /// With <see cref="ExperienceLessonsQuery.IncludeMeasuredBenefit"/>, the store paged a run's holdout rows apart (out of
    /// the port's run-then-record order).
    /// </exception>
    /// <exception cref="ExperienceStoreException">With <see cref="ExperienceLessonsQuery.IncludeMeasuredBenefit"/>, the holdout read failed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<ExperienceLessonsPage> ListAsync(
        ExperienceIdentity identity,
        ExperienceLessonsQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        query ??= new ExperienceLessonsQuery();
        if (query.Limit is < ExperienceRecordQuery.MinLimit or > ExperienceRecordQuery.MaxLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                query.Limit,
                $"{nameof(ExperienceLessonsQuery.Limit)} must be from {ExperienceRecordQuery.MinLimit} to {ExperienceRecordQuery.MaxLimit}.");
        }

        if (query.Statuses is { } statuses && (statuses.Count == 0 || statuses.Any(status => !Enum.IsDefined(status))))
        {
            throw new ArgumentException(
                $"{nameof(ExperienceLessonsQuery.Statuses)} must be null, for every status, or hold at least one, each a defined {nameof(ExperienceStatus)}.",
                nameof(query));
        }

        return (await ReadAsync(identity, query, cancellationToken).ConfigureAwait(false)).Page;
    }

    /// <summary>
    /// What <see cref="ListAsync"/> lists, for a query already validated, with the record each listed lesson was described
    /// from, at the same index.
    /// </summary>
    private async Task<(ExperienceLessonsPage Page, List<ExperienceRecord> Records)> ReadAsync(
        ExperienceIdentity identity,
        ExperienceLessonsQuery query,
        CancellationToken cancellationToken)
    {
        // One record past the limit tells "exactly the limit" from "more exist". Leaving ineligible lessons out may skip
        // any number of records, so then as many as the store answers are read.
        var probe = query.IncludeIneligible ? Math.Min(query.Limit + 1, ExperienceRecordQuery.MaxLimit) : ExperienceRecordQuery.MaxLimit;
        // Story 21.8: the page and each record's last injection in one bounded read, never one read per lesson.
        var result = await _store.QueryWithInjectionsAsync(
            identity.Authorization,
            new ExperienceRecordQuery(identity.Scope, query.Statuses?.ToArray(), probe),
            cancellationToken).ConfigureAwait(false);

        var generatedAt = _clock.GetUtcNow();
        if (result is null || result.Outcome != ExperienceStoreOutcome.Found)
        {
            return (new ExperienceLessonsPage(identity.Scope, result?.Outcome ?? ExperienceStoreOutcome.Invalid, [], generatedAt), []);
        }

        var records = result.Records ?? [];
        // Tracked only when the store says so on both sides: a decorator that forwards the read but not the capability
        // (or the reverse) reads as not tracked rather than as "never".
        var injections = _store.TracksInjections ? result.Injections : null;
        var lessons = new List<ExperienceLesson>(Math.Min(records.Count, query.Limit));
        var described = new List<ExperienceRecord>(lessons.Capacity);
        var outOfScope = 0;
        var hasMore = false;
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record is null)
            {
                continue;
            }

            // Only the caller's own scope: a store that answered with anything else is not shown it.
            if (record.Scope != identity.Scope)
            {
                outOfScope++;
                continue;
            }

            var lesson = Describe(record);
            if (injections is not null && injections.TryGetValue(record.ExperienceId, out var injected) && injected is not null)
            {
                lesson = lesson with { LastInjectedAt = injected.LastInjectedAt, InjectionCount = injected.Count };
            }

            if (!lesson.IsEligible && !query.IncludeIneligible)
            {
                continue;
            }

            if (lessons.Count == query.Limit)
            {
                hasMore = true;
                break;
            }

            lessons.Add(lesson);
            described.Add(record);
        }

        // A store that answered as many records as were asked for may hold more than were read.
        hasMore |= records.Count >= probe;

        // Story 25.10: opt-in, so the default page reads nothing more and is unchanged.
        if (query.IncludeMeasuredBenefit && lessons.Count > 0)
        {
            await AddMeasuredBenefitAsync(identity, lessons, generatedAt, cancellationToken).ConfigureAwait(false);
        }

        var page = new ExperienceLessonsPage(identity.Scope, ExperienceStoreOutcome.Found, lessons, generatedAt)
        {
            HasMore = hasMore,
            OutOfScopeDropped = outOfScope,
            InjectionsTracked = injections is not null,
        };
        return (page, described);
    }

    /// <summary>
    /// Lists the lessons stored under the identity <see cref="AgentExperienceOptions.UseDevelopmentIdentity"/> runs every
    /// invocation as: the quick start's. For development and tests, like that identity.
    /// </summary>
    /// <param name="query">As for <see cref="ListAsync"/>.</param>
    /// <param name="cancellationToken">As for <see cref="ListAsync"/>.</param>
    /// <returns>As for <see cref="ListAsync"/>.</returns>
    /// <exception cref="InvalidOperationException">The setup does not use <see cref="AgentExperienceOptions.UseDevelopmentIdentity"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">As for <see cref="ListAsync"/>.</exception>
    /// <exception cref="ArgumentException">As for <see cref="ListAsync"/>.</exception>
    public Task<ExperienceLessonsPage> ListForDevelopmentIdentityAsync(
        ExperienceLessonsQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var identity = _registration?.DevelopmentIdentityOrNull(_clock)
            ?? throw new InvalidOperationException(
                $"{nameof(ListForDevelopmentIdentityAsync)} lists what {nameof(AgentExperienceOptions)}.{nameof(AgentExperienceOptions.UseDevelopmentIdentity)}() stored, "
                + "and this setup does not use it. Call ListAsync(identity) with the identity from your own authentication instead.");
        return ListAsync(identity, query, cancellationToken);
    }

    /// <summary>
    /// Hands <paramref name="sink"/> every lesson in <paramref name="identity"/>'s scope that the holdout measured as
    /// helping, so a copy kept in another memory store (a Markdown file an agent loads, Mem0, Zep, Letta, an
    /// <c>AGENTS.md</c> or skill folder) stays in step with what the library has learned. A sync, not a one-way dump: the
    /// sink is given the complete current set every time and removes what is no longer in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What is exported.</b> A lesson is in the set when the lessons view lists it in exactly the identity's scope
    /// (never one another scope lends through a grant), it is <see cref="ExperienceLesson.IsEligible"/> (retrieval would
    /// consider it), its record's outcome is <see cref="TaskVerificationStatus.Verified"/>, and its per-lesson verdict is
    /// <see cref="ExperienceBenefitVerdict.Helps"/>: one estimate for the scope, read as
    /// <see cref="ExperienceLessonsQuery.IncludeMeasuredBenefit"/> reads it, over the context provider's
    /// <see cref="ExperienceBenefitPolicy.Window"/> (30 days by default) and decided with <paramref name="options"/>
    /// (the provider's <see cref="ExperienceBenefitPolicy.MinimumRunsPerArm"/>, 20 by default, when
    /// <see langword="null"/>). <see cref="ExperienceBenefitVerdict.NoEvidence"/> and
    /// <see cref="ExperienceBenefitVerdict.Hurts"/> are never exported.
    /// </para>
    /// <para>
    /// <b>What the sink must do.</b> A lesson withdrawn, erased, no longer eligible or no longer measured as helping is
    /// simply absent from the set, and the sink removes its copy. A store that does not track the holdout gives an empty
    /// set, and the sink is still called, so stale copies are removed.
    /// </para>
    /// <para>
    /// <b>Explicit and read-only.</b> Nothing exports automatically; the host calls this. It reads under the identity's
    /// own authorization, like <see cref="ListAsync"/>, and writes nothing to the store. The lesson text is untrusted
    /// reference material, as the Historical Reference block is: whatever loads the exported copy must treat it so.
    /// Whatever the sink throws propagates to the caller unchanged.
    /// </para>
    /// </remarks>
    /// <param name="identity">The caller, from the host's own authentication: the authorization the store checks, and the scope exported.</param>
    /// <param name="sink">Where the current set goes.</param>
    /// <param name="options">The verdict's threshold; <see langword="null"/> for the context provider's benefit policy's.</param>
    /// <param name="cancellationToken">Cancels the read and the sync.</param>
    /// <returns>
    /// What was exported and what the sink reported. A store outcome other than <see cref="ExperienceStoreOutcome.Found"/>
    /// (the scope lies outside the authorization, say) is a result carrying that outcome, and the sink is not called.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> or <paramref name="sink"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ExperienceBenefitOptions.MinimumRunsPerArm"/> is less than 1.</exception>
    /// <exception cref="InvalidOperationException">
    /// The scope holds more reusable lessons than one store read answers (<see cref="ExperienceRecordQuery.MaxLimit"/>), so
    /// the set could not be read whole and the sink would remove copies of lessons it was not shown; the store paged a
    /// run's holdout rows apart; or the sink returned <see langword="null"/>.
    /// </exception>
    /// <exception cref="ExperienceStoreException">The holdout read failed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<ExperienceLessonExportResult> ExportMeasuredAsync(
        ExperienceIdentity identity,
        IExperienceLessonSink sink,
        ExperienceBenefitOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(sink);

        // Validates the options before anything is read.
        _ = ExperienceBenefitEstimator.Empty(options);
        options ??= _provider.BenefitPolicy.EstimatorOptions;

        // Only statuses retrieval can return: the rest are never eligible, and leaving them out keeps the one bounded
        // read for the lessons that can be exported.
        var (page, records) = await ReadAsync(
            identity,
            new ExperienceLessonsQuery
            {
                Statuses = ExperienceStatuses.EligibleForReuse,
                Limit = ExperienceRecordQuery.MaxLimit,
                IncludeIneligible = false,
            },
            cancellationToken).ConfigureAwait(false);

        if (page.Outcome != ExperienceStoreOutcome.Found)
        {
            return new ExperienceLessonExportResult(identity.Scope, page.Outcome, [], null, page.GeneratedAt);
        }

        if (page.HasMore)
        {
            throw new InvalidOperationException(
                $"The scope holds more reusable lessons than one store read answers ({ExperienceRecordQuery.MaxLimit}), so the "
                + "measured set cannot be read whole, and a sink given part of it would remove copies of lessons that still help. "
                + "Nothing was handed to the sink.");
        }

        var exportedAt = page.GeneratedAt;
        var estimate = await EstimateAsync(identity, options, exportedAt, cancellationToken).ConfigureAwait(false);
        if (estimate.Outcome != ExperienceStoreOutcome.Found)
        {
            return new ExperienceLessonExportResult(identity.Scope, estimate.Outcome, [], null, exportedAt);
        }

        var current = new List<ExportedLesson>();
        if (estimate.Tracked)
        {
            var helps = new Dictionary<Guid, ExperienceBenefitComparison>();
            foreach (var lesson in estimate.Lessons)
            {
                if (lesson?.Comparison is { Verdict: ExperienceBenefitVerdict.Helps } comparison)
                {
                    helps.TryAdd(lesson.ExperienceId, comparison);
                }
            }

            var seen = new HashSet<Guid>();
            for (var i = 0; i < page.Lessons.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lesson = page.Lessons[i];
                var record = records[i];
                if (!lesson.IsEligible
                    || record.Outcome?.Status != TaskVerificationStatus.Verified
                    || !helps.TryGetValue(lesson.ExperienceId, out var comparison)
                    || !seen.Add(lesson.ExperienceId))
                {
                    continue;
                }

                current.Add(new ExportedLesson(
                    record.ExperienceId,
                    record.Revision,
                    lesson.TaskId,
                    _provider.RenderLessonText(record),
                    comparison,
                    exportedAt));
            }

            current.Sort((left, right) => string.CompareOrdinal(
                left.ExperienceId.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
                right.ExperienceId.ToString("D", System.Globalization.CultureInfo.InvariantCulture)));
        }

        var sync = await sink.SyncAsync(identity.Scope, current, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The sink's {nameof(IExperienceLessonSink.SyncAsync)} returned null.");
        return new ExperienceLessonExportResult(identity.Scope, ExperienceStoreOutcome.Found, current, sync, exportedAt)
        {
            HoldoutTracked = estimate.Tracked,
        };
    }

    /// <summary>
    /// Merges the validated lessons in <paramref name="identity"/>'s scope that failed the same way and were fixed the same
    /// way into one consolidated lesson each, which supersedes them: <see cref="ExperienceConsolidationService.ConsolidateAsync"/>
    /// under the identity's own authorization, in exactly its scope (never a lesson another scope lends). Explicit: nothing
    /// consolidates automatically. The view then lists one lesson per situation, with
    /// <see cref="ExperienceLesson.ConsolidatedFrom"/> set.
    /// </summary>
    /// <remarks>
    /// The injection holdout's measured benefit is per lesson, so a consolidated lesson starts with no measurements of its
    /// own: the assignments and exposures that named its sources stay as they were, and are not carried over to it.
    /// </remarks>
    /// <param name="identity">The caller, from the host's own authentication: the authorization the store checks, and the scope consolidated.</param>
    /// <param name="options">
    /// What to merge; <see langword="null"/> for the defaults, with <see cref="ExperienceConsolidationOptions.ArgumentKeys"/> set
    /// to the argument keys <see cref="AgentExperienceOptions.Recipe"/> made visible, so a merged lesson never hides an
    /// argument value its sources' blocks showed. A host that sets <see cref="ExperienceInjectionOptions.ApproachArguments"/>
    /// itself passes the same keys here.
    /// </param>
    /// <param name="cancellationToken">Cancels the pass; a re-run finishes what it left.</param>
    /// <returns>What the pass did, per group and per source.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The container has no <see cref="ExperienceConsolidationService"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ExperienceConsolidationOptions.MinimumSources"/> is out of range.</exception>
    /// <exception cref="ArgumentException"><see cref="ExperienceConsolidationOptions.ArgumentKeys"/> names a blank tool or key.</exception>
    /// <exception cref="ExperienceStoreException">Storage failed while reading or creating.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<ExperienceConsolidationResult> ConsolidateAsync(
        ExperienceIdentity identity,
        ExperienceConsolidationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var consolidation = _consolidation
            ?? throw new InvalidOperationException(
                $"No {nameof(ExperienceConsolidationService)} is registered; AddAgentExperience registers one.");
        options ??= DefaultConsolidationOptions();
        return consolidation.ConsolidateAsync(identity.Authorization, identity.Scope, options, cancellationToken);
    }

    /// <summary>
    /// The consolidated lessons in <paramref name="identity"/>'s scope that list <paramref name="sourceId"/> as a source:
    /// <see cref="ExperienceConsolidationService.FindConsolidatedContainingAsync"/> under the identity's own authorization.
    /// Erasing a source does not reach a consolidated lesson that copied its content, so a host erasing a record calls this
    /// first and erases what it returns too.
    /// </summary>
    /// <param name="identity">The caller, from the host's own authentication.</param>
    /// <param name="sourceId">The record about to be erased.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The consolidated records that list it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The container has no <see cref="ExperienceConsolidationService"/>.</exception>
    /// <exception cref="ExperienceStoreException">Storage failed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<ExperienceConsolidatedLookupResult> FindConsolidatedContainingAsync(
        ExperienceIdentity identity,
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var consolidation = _consolidation
            ?? throw new InvalidOperationException(
                $"No {nameof(ExperienceConsolidationService)} is registered; AddAgentExperience registers one.");
        return consolidation.FindConsolidatedContainingAsync(identity.Authorization, identity.Scope, sourceId, cancellationToken);
    }

    /// <summary>The default consolidation options: the argument keys the setup's recipes made visible.</summary>
    private ExperienceConsolidationOptions DefaultConsolidationOptions()
    {
        var keys = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var recipe in _registration?.Options.Recipes ?? [])
        {
            if (recipe.Arguments.Count > 0)
            {
                keys[recipe.ToolName] = recipe.Arguments;
            }
        }

        return new ExperienceConsolidationOptions { ArgumentKeys = keys.Count > 0 ? keys : null };
    }

    /// <summary>
    /// Sets each listed lesson's <see cref="ExperienceLesson.MeasuredBenefit"/> from one estimate for the page's scope, over
    /// the context provider's benefit window and threshold (<see cref="ExperienceBenefitPolicy.Window"/> and
    /// <see cref="ExperienceBenefitPolicy.MinimumRunsPerArm"/>, whose defaults apply even with the policy off): the estimate
    /// injection acts on. Left null for a lesson no assigned run named, and for every lesson when the store does not track
    /// the holdout or refused the read.
    /// </summary>
    private async Task AddMeasuredBenefitAsync(
        ExperienceIdentity identity,
        List<ExperienceLesson> lessons,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var estimate = await EstimateAsync(identity, _provider.BenefitPolicy.EstimatorOptions, now, cancellationToken).ConfigureAwait(false);
        if (estimate is not { Tracked: true, Outcome: ExperienceStoreOutcome.Found })
        {
            return;
        }

        var measured = new Dictionary<Guid, ExperienceLessonBenefit>();
        foreach (var lesson in estimate.Lessons)
        {
            measured.TryAdd(lesson.ExperienceId, lesson);
        }

        for (var i = 0; i < lessons.Count; i++)
        {
            if (measured.TryGetValue(lessons[i].ExperienceId, out var benefit))
            {
                lessons[i] = lessons[i] with { MeasuredBenefit = benefit };
            }
        }
    }

    /// <summary>
    /// One benefit estimate for <paramref name="identity"/>'s scope, over the assignments made within the context provider's
    /// <see cref="ExperienceBenefitPolicy.Window"/> of <paramref name="now"/>, decided with <paramref name="options"/>.
    /// </summary>
    private Task<ExperienceBenefitEstimate> EstimateAsync(
        ExperienceIdentity identity,
        ExperienceBenefitOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var window = _provider.BenefitPolicy.Window;
        var from = now - DateTimeOffset.MinValue > window ? now - window : DateTimeOffset.MinValue;
        return AgentExperienceBenefit.EstimateFromStoreAsync(
            _store, identity.Authorization, identity.Scope, options, from, cancellationToken);
    }

    /// <summary>One record as a lesson. A record that cannot be rendered is a lesson marked so, never an exception.</summary>
    private ExperienceLesson Describe(ExperienceRecord record)
    {
        try
        {
            var payload = _provider.RenderAlone(record);
            var (ineligibility, reason) = _provider.StandingIneligibility(record) switch
            {
                StandingExclusion.Status => (ExperienceLessonIneligibility.Status, $"status {record.Status} is never retrieved"),
                StandingExclusion.BelowConfidenceFloor => (ExperienceLessonIneligibility.BelowConfidenceFloor,
                    $"confidence {Number(record.ReuseConfidence)} is below the retrieval floor {Number(_provider.ConfidenceFloor)}"),
                StandingExclusion.ModelAuthored => (ExperienceLessonIneligibility.ModelAuthored, "model-authored lessons are excluded"),
                StandingExclusion.UnconfirmedContent => (ExperienceLessonIneligibility.ContentUnconfirmed,
                    "its content is not confirmed, and model-authored lessons are excluded"),
                StandingExclusion.Expired => (ExperienceLessonIneligibility.Expired,
                    "its last activity is older than the retrieval policy's maximum age"),
                _ when payload.ByteCount > _provider.MaxBlockBytes => (ExperienceLessonIneligibility.OverByteBudget,
                    $"its block ({payload.ByteCount} bytes) is larger than the {_provider.MaxBlockBytes}-byte injection budget"),
                _ => ((ExperienceLessonIneligibility?)null, (string?)null),
            };

            return new ExperienceLesson(
                record.ExperienceId,
                record.TaskId ?? string.Empty,
                record.Status,
                record.ReuseConfidence,
                record.CreatedAt,
                record.UpdatedAt,
                Checks(record),
                payload.Text,
                ineligibility is null,
                reason,
                ineligibility)
            {
                // Story 28.6: a consolidated lesson says how many source records it was merged from.
                ConsolidatedFrom = ExperienceConsolidation.IsConsolidated(record) ? ExperienceConsolidation.SourcesOf(record).Count : null,
            };
        }
#pragma warning disable CA1031 // One record a store answered that cannot be rendered must not hide the rest.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new ExperienceLesson(
                record.ExperienceId,
                record.TaskId ?? string.Empty,
                record.Status,
                record.ReuseConfidence,
                record.CreatedAt,
                record.UpdatedAt,
                [],
                string.Empty,
                false,
                $"it could not be rendered ({ex.GetType().Name})",
                ExperienceLessonIneligibility.Unrenderable);
        }
    }

    private static string Number(double value) =>
        double.IsFinite(value) ? value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "(unavailable)";

    /// <summary>The record's verified checks: each check's ID, evidence kind and result, and never the evidence's detail.</summary>
    private static List<ExperienceLessonCheck> Checks(ExperienceRecord record) =>
        record.Outcome?.Evidence is { } evidence
            ? evidence.Where(item => item is not null)
                .Select(item => new ExperienceLessonCheck(item.CheckId ?? string.Empty, item.Kind ?? string.Empty, item.Result))
                .ToList()
            : [];
}

/// <summary>What <see cref="AgentExperienceLessons.ListAsync"/> lists.</summary>
public sealed record ExperienceLessonsQuery
{
    /// <summary>The statuses to list; <see langword="null"/> (the default) lists every status. When set, at least one, each defined.</summary>
    public IReadOnlyList<ExperienceStatus>? Statuses { get; init; }

    /// <summary>
    /// How many records to read, from <see cref="ExperienceRecordQuery.MinLimit"/> to
    /// <see cref="ExperienceRecordQuery.MaxLimit"/>; <see cref="ExperienceRecordQuery.DefaultLimit"/> by default. With
    /// <see cref="IncludeIneligible"/> off, the lessons left out still count against it.
    /// </summary>
    public int Limit { get; init; } = ExperienceRecordQuery.DefaultLimit;

    /// <summary>
    /// Whether to list lessons retrieval would not currently return (quarantined, below the confidence floor, expired,
    /// and so on), each marked with why. <see langword="true"/> by default.
    /// </summary>
    public bool IncludeIneligible { get; init; } = true;

    /// <summary>
    /// Whether to read what the injection holdout measured for each listed lesson into
    /// <see cref="ExperienceLesson.MeasuredBenefit"/>: one benefit estimate for the page's scope, read as
    /// <see cref="AgentExperienceBenefit.EstimateAsync"/> reads it, over the context provider's
    /// <see cref="ExperienceBenefitPolicy.Window"/> and <see cref="ExperienceBenefitPolicy.MinimumRunsPerArm"/> (30 days and
    /// 20 by default, whether or not the policy is on), so it shows why injection omits or prefers a lesson.
    /// <see langword="false"/> by default: then nothing more is read and the page is as without it. A store failure on that
    /// read is thrown as <see cref="ExperienceStoreException"/>.
    /// </summary>
    public bool IncludeMeasuredBenefit { get; init; }
}

/// <summary>One page of lessons from one scope.</summary>
/// <param name="Scope">The scope listed: the caller's own.</param>
/// <param name="Outcome">The store's outcome: <see cref="ExperienceStoreOutcome.Found"/>, or why the page is empty.</param>
/// <param name="Lessons">The lessons, newest first.</param>
/// <param name="GeneratedAt">When the page was read, by the setup's clock.</param>
public sealed record ExperienceLessonsPage(
    Scope Scope,
    ExperienceStoreOutcome Outcome,
    IReadOnlyList<ExperienceLesson> Lessons,
    DateTimeOffset GeneratedAt)
{
    /// <summary>The scope listed: the caller's own.</summary>
    public Scope Scope { get; init; } = Scope ?? throw new ArgumentNullException(nameof(Scope));

    /// <summary>The lessons, newest first.</summary>
    public IReadOnlyList<ExperienceLesson> Lessons { get; init; } = Lessons ?? throw new ArgumentNullException(nameof(Lessons));

    /// <summary>
    /// Whether more lessons matching the query exist past <see cref="Lessons"/>, or may: the store answers at most
    /// <see cref="ExperienceRecordQuery.MaxLimit"/> records, and when it answered that many, more may be stored.
    /// </summary>
    public bool HasMore { get; init; }

    /// <summary>How many records the store answered from outside the caller's scope, which were left out.</summary>
    public int OutOfScopeDropped { get; init; }

    /// <summary>
    /// Whether the store tracks injections (<see cref="IExperienceRecordStore.TracksInjections"/>, and its
    /// <see cref="IExperienceRecordStore.QueryWithInjectionsAsync"/> read them), so that a lesson's
    /// <see cref="ExperienceLesson.LastInjectedAt"/> of <see langword="null"/> means "never injected". .NET, Python and Go
    /// agents all record their injections (the ports from contract 1.4). When <see langword="false"/> -- a store that
    /// does not track them, or a PostgreSQL schema whose application role has not been granted <c>0026</c>'s table yet
    /// -- it means "not known".
    /// </summary>
    public bool InjectionsTracked { get; init; }
}

/// <summary>One stored lesson, as the lessons view shows it.</summary>
/// <param name="ExperienceId">The record's ID.</param>
/// <param name="TaskId">The task it was captured under.</param>
/// <param name="Status">Its lifecycle status.</param>
/// <param name="Confidence">Its reuse confidence, in [0, 1].</param>
/// <param name="CreatedAt">When it was stored.</param>
/// <param name="UpdatedAt">Its last lifecycle activity.</param>
/// <param name="Checks">The checks its outcome was verified by: ID, evidence kind and result. No evidence detail.</param>
/// <param name="BlockText">
/// The Historical Reference block an agent in this scope would be shown for this record, rendered by the container's
/// context provider's writer and options. Two parts differ from a real injection: it carries no ranking, which depends on
/// the task, so its ranking line says there is none; and it is the record alone, numbered 1, where a real block numbers
/// it by rank among the others. Rendered for every status, including those retrieval never returns; see
/// <see cref="IsEligible"/>. Empty when the record could not be rendered. Untrusted text: encode it before showing it in a page.
/// </param>
/// <param name="IsEligible">Whether retrieval would currently consider it, setting the task aside, and its block fits the injection budget.</param>
/// <param name="IneligibleReason">Why not, in words, when <see cref="IsEligible"/> is <see langword="false"/>.</param>
/// <param name="Ineligibility">Why not, as a code, when <see cref="IsEligible"/> is <see langword="false"/>.</param>
public sealed record ExperienceLesson(
    Guid ExperienceId,
    string TaskId,
    ExperienceStatus Status,
    double Confidence,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ExperienceLessonCheck> Checks,
    string BlockText,
    bool IsEligible,
    string? IneligibleReason,
    ExperienceLessonIneligibility? Ineligibility = null)
{
    /// <summary>The task it was captured under.</summary>
    public string TaskId { get; init; } = TaskId ?? throw new ArgumentNullException(nameof(TaskId));

    /// <summary>The checks its outcome was verified by.</summary>
    public IReadOnlyList<ExperienceLessonCheck> Checks { get; init; } = Checks ?? throw new ArgumentNullException(nameof(Checks));

    /// <summary>The block an agent in this scope would be shown for it (see the primary constructor's parameter doc).</summary>
    public string BlockText { get; init; } = BlockText ?? throw new ArgumentNullException(nameof(BlockText));

    /// <summary>
    /// When an agent in this scope was last shown this lesson: the latest injected block that delivered it, by the
    /// injecting provider's clock (clamped to the store's). One of three states: an instant; <see langword="null"/>
    /// with the page's <see cref="ExperienceLessonsPage.InjectionsTracked"/> set, meaning never injected (by a .NET,
    /// Python or Go agent: the ports record their injections too); or <see langword="null"/> with it unset, meaning not
    /// known. Only injections in this scope count; another scope injecting it through a sharing grant does not. It is
    /// recorded in the background after the block is handed over, so it can lag an injection by a moment.
    /// </summary>
    public DateTimeOffset? LastInjectedAt { get; init; }

    /// <summary>How many injected blocks delivered this lesson in this scope; 0 when never (or not tracked).</summary>
    public long InjectionCount { get; init; }

    /// <summary>
    /// What the injection holdout measured for this lesson: treatment runs given it against holdout runs denied it, with
    /// the verdict (<see cref="ExperienceBenefitVerdict.Hurts"/> is why the benefit policy omits it). Set only when the
    /// query asked for it (<see cref="ExperienceLessonsQuery.IncludeMeasuredBenefit"/>); <see langword="null"/> otherwise,
    /// when no assigned run in the window named it, or when the store does not track the holdout.
    /// </summary>
    public ExperienceLessonBenefit? MeasuredBenefit { get; init; }

    /// <summary>
    /// When this lesson is a consolidated one (<see cref="AgentExperienceLessons.ConsolidateAsync"/>), how many
    /// source records it was merged from: the records its provenance lists, each now superseded by it. Its lesson text says
    /// how many independent runs those were. <see langword="null"/> for every other lesson.
    /// </summary>
    public int? ConsolidatedFrom { get; init; }
}

/// <summary>Why retrieval would not currently use a lesson.</summary>
public enum ExperienceLessonIneligibility
{
    /// <summary>Its status is never retrieved (quarantined, retired, superseded, and so on).</summary>
    Status,

    /// <summary>Its confidence is below the retrieval policy's floor, or is not a number.</summary>
    BelowConfidenceFloor,

    /// <summary>Its lesson is model-authored, and the context provider excludes model-authored lessons.</summary>
    ModelAuthored,

    /// <summary>With provenance signing, nothing confirms its content, and the context provider excludes model-authored lessons.</summary>
    ContentUnconfirmed,

    /// <summary>Its last activity is older than the retrieval policy's maximum age.</summary>
    Expired,

    /// <summary>Its block alone is larger than the context provider's byte budget, so it would never fit.</summary>
    OverByteBudget,

    /// <summary>It could not be rendered.</summary>
    Unrenderable,
}

/// <summary>One check a lesson's outcome was verified by.</summary>
/// <param name="CheckId">The check's ID.</param>
/// <param name="EvidenceKind">The kind of evidence it produced.</param>
/// <param name="Result">The check's result.</param>
public sealed record ExperienceLessonCheck(string CheckId, string EvidenceKind, CheckResult Result)
{
    /// <summary>The check's ID.</summary>
    public string CheckId { get; init; } = CheckId ?? throw new ArgumentNullException(nameof(CheckId));

    /// <summary>The kind of evidence it produced.</summary>
    public string EvidenceKind { get; init; } = EvidenceKind ?? throw new ArgumentNullException(nameof(EvidenceKind));
}

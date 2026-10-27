using AgentExperience.Abstractions;
using AgentExperience.Core.Holdout;
using AgentExperience.Core.Retrieval;
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

    internal AgentExperienceLessons(
        IExperienceRecordStore store,
        ExperienceContextProvider provider,
        TimeProvider clock,
        AgentExperienceRegistration? registration = null)
    {
        _store = store;
        _provider = provider;
        _clock = clock;
        _registration = registration;
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
            return new ExperienceLessonsPage(identity.Scope, result?.Outcome ?? ExperienceStoreOutcome.Invalid, [], generatedAt);
        }

        var records = result.Records ?? [];
        // Tracked only when the store says so on both sides: a decorator that forwards the read but not the capability
        // (or the reverse) reads as not tracked rather than as "never".
        var injections = _store.TracksInjections ? result.Injections : null;
        var lessons = new List<ExperienceLesson>(Math.Min(records.Count, query.Limit));
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
        }

        // A store that answered as many records as were asked for may hold more than were read.
        hasMore |= records.Count >= probe;

        // Story 25.10: opt-in, so the default page reads nothing more and is unchanged.
        if (query.IncludeMeasuredBenefit && lessons.Count > 0)
        {
            await AddMeasuredBenefitAsync(identity, lessons, generatedAt, cancellationToken).ConfigureAwait(false);
        }

        return new ExperienceLessonsPage(identity.Scope, ExperienceStoreOutcome.Found, lessons, generatedAt)
        {
            HasMore = hasMore,
            OutOfScopeDropped = outOfScope,
            InjectionsTracked = injections is not null,
        };
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
        var policy = _provider.BenefitPolicy;
        var from = now - DateTimeOffset.MinValue > policy.Window ? now - policy.Window : DateTimeOffset.MinValue;
        var estimate = await AgentExperienceBenefit.EstimateFromStoreAsync(
            _store, identity.Authorization, identity.Scope, policy.EstimatorOptions, from, cancellationToken).ConfigureAwait(false);
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
                ineligibility);
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

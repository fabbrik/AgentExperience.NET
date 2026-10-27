using System.Collections.Concurrent;
using AgentExperience.Abstractions;
using AgentExperience.Core.Diagnostics;
using AgentExperience.Core.Holdout;
using AgentExperience.MicrosoftAgentFramework.Diagnostics;

namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>
/// One provider's cached benefit estimates (<see cref="ExperienceBenefitPolicy"/>), keyed by request scope and the
/// authorization's tenant and principal. <see cref="Current"/> answers from the cache at once and, when the entry is
/// missing or stale, starts one background refresh for that key. Never throws and never waits on the store.
/// </summary>
internal sealed class ExperienceBenefitCache
{
    /// <summary>The most keys kept; past it the entry refreshed longest ago is dropped to make room.</summary>
    internal const int MaxEntries = 10_000;

    private readonly IExperienceRecordStore _store;
    private readonly ExperienceBenefitPolicy _policy;
    private readonly ExperienceBenefitOptions _estimatorOptions;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<Key, Entry> _entries = new();
    private readonly ConcurrentDictionary<Task, byte> _refreshes = new();

    internal ExperienceBenefitCache(IExperienceRecordStore store, ExperienceBenefitPolicy policy, TimeProvider clock)
    {
        _store = store;
        _policy = policy;
        _estimatorOptions = policy.EstimatorOptions;
        _clock = clock;
    }

    /// <summary>
    /// What the cache holds for this principal and scope, or <see langword="null"/> when it holds nothing usable; starts a
    /// background refresh first when the entry is missing or older than <see cref="ExperienceBenefitPolicy.RefreshInterval"/>
    /// and none is running for it. With <see cref="ExperienceBenefitPolicy.PerModel"/>, the view for
    /// <paramref name="model"/> (the invoking agent's), falling back to the pooled verdicts where that model's group has no
    /// evidence. Never throws.
    /// </summary>
    internal BenefitView? Current(AuthorizationContext authorization, Scope scope, string? model = null)
    {
        try
        {
            var key = new Key(scope, authorization.TenantId, authorization.PrincipalId);
            if (!_entries.TryGetValue(key, out var entry))
            {
                if (_entries.Count >= MaxEntries)
                {
                    Evict();
                }

                entry = _entries.GetOrAdd(key, static _ => new Entry());
            }

            var now = _clock.GetUtcNow();
            lock (entry)
            {
                if (entry.Refreshing is null && (entry.RefreshedAt is not { } at || now - at >= _policy.RefreshInterval))
                {
                    entry.Refreshing = Start(entry, authorization, scope);
                }

                return entry.Views?.For(_policy.PerModel ? model : null);
            }
        }
        catch (Exception)
        {
            // A cache that cannot answer has no effect: the invocation is injected as without the policy.
            return null;
        }
    }

    /// <summary>Completes once every refresh started so far has ended. For tests.</summary>
    internal Task SettledAsync() => Task.WhenAll(_refreshes.Keys);

    /// <summary>How many keys are cached. For tests.</summary>
    internal int Count => _entries.Count;

    private Task Start(Entry entry, AuthorizationContext authorization, Scope scope)
    {
        var work = Task.Run(() => RefreshAsync(entry, authorization, scope), CancellationToken.None);
        _refreshes.TryAdd(work, 0);
        _ = work.ContinueWith(
            done => _refreshes.TryRemove(done, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return work;
    }

    /// <summary>
    /// One refresh: reads the window's estimate within <see cref="ExperienceBenefitPolicy.RefreshTimeout"/> and replaces the
    /// entry's view when the store answered; otherwise keeps it. Reported as <c>benefit_refresh</c>. Never throws.
    /// </summary>
    private async Task RefreshAsync(Entry entry, AuthorizationContext authorization, Scope scope)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var outcome = InjectionDiagnostics.RefreshedOutcome;
        ExperienceOperationErrorClass? errorClass = null;
        BenefitViews? view = null;
        var replaced = false;
        try
        {
            var now = _clock.GetUtcNow();
            var from = now - DateTimeOffset.MinValue > _policy.Window ? now - _policy.Window : DateTimeOffset.MinValue;
            var source = new CancellationTokenSource();

            // On the thread pool, so a store that blocks before its first await cannot hold the timeout off. The token
            // source outlives an abandoned read, and is disposed once the read ends.
            var read = Task.Run(
                () => AgentExperienceBenefit.EstimateFromStoreAsync(_store, authorization, scope, _estimatorOptions, from, source.Token),
                CancellationToken.None);
            _ = read.ContinueWith(
                ended =>
                {
                    _ = ended.Exception;
                    source.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            try
            {
                var estimate = await read.WaitAsync(_policy.RefreshTimeout, _clock).ConfigureAwait(false);
                view = BenefitViews.From(estimate);
                replaced = true;
            }
            catch (TimeoutException) when (!read.IsCompleted)
            {
                try
                {
                    await source.CancelAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Disposed because the read just ended, or a throwing callback: the refresh is abandoned either way.
                }

                throw;
            }
        }
        catch (TimeoutException)
        {
            outcome = InjectionDiagnostics.OutcomeUnknownOutcome;
            errorClass = ExperienceOperationErrorClass.Timeout;
        }
        catch (OperationCanceledException)
        {
            outcome = InjectionDiagnostics.OutcomeUnknownOutcome;
            errorClass = ExperienceOperationErrorClass.Cancelled;
        }
        catch (Exception ex)
        {
            outcome = InjectionDiagnostics.NotRefreshedOutcome;
            errorClass = ex is ExperienceStoreException ? ExperienceOperationErrorClass.Infrastructure : ExperienceOperationErrorClass.Unexpected;
        }

        lock (entry)
        {
            if (replaced)
            {
                entry.Views = view;
            }

            // A failed refresh waits out the interval too, so a failing store is not asked on every invocation.
            entry.RefreshedAt = _clock.GetUtcNow();
            entry.Refreshing = null;
        }

        InjectionDiagnostics.BackgroundRecorded(InjectionDiagnostics.BenefitRefresh, outcome, started, errorClass);
    }

    /// <summary>Drops the entry refreshed longest ago (one never refreshed counts as oldest), unless it is refreshing.</summary>
    private void Evict()
    {
        KeyValuePair<Key, Entry>? oldest = null;
        foreach (var pair in _entries)
        {
            if (pair.Value.Refreshing is not null)
            {
                continue;
            }

            if (oldest is null || (pair.Value.RefreshedAt ?? DateTimeOffset.MinValue) < (oldest.Value.Value.RefreshedAt ?? DateTimeOffset.MinValue))
            {
                oldest = pair;
            }
        }

        if (oldest is { } drop)
        {
            _entries.TryRemove(drop);
        }
    }

    private readonly record struct Key(Scope Scope, string TenantId, string PrincipalId);

    private sealed class Entry
    {
        public BenefitViews? Views { get; set; }

        public DateTimeOffset? RefreshedAt { get; set; }

        public Task? Refreshing { get; set; }
    }
}

/// <summary>What one estimate means for injection: whether the scope's lessons together hurt, and which lessons hurt or help.</summary>
internal sealed class BenefitView
{
    private BenefitView(bool scopeHurts, HashSet<Guid> hurts, HashSet<Guid> helps)
    {
        ScopeHurts = scopeHurts;
        Hurts = hurts;
        Helps = helps;
    }

    /// <summary>Whether the overall verdict is <see cref="ExperienceBenefitVerdict.Hurts"/>: then every record is omitted.</summary>
    internal bool ScopeHurts { get; }

    /// <summary>The lessons measured <see cref="ExperienceBenefitVerdict.Hurts"/>.</summary>
    internal IReadOnlySet<Guid> Hurts { get; }

    /// <summary>The lessons measured <see cref="ExperienceBenefitVerdict.Helps"/>.</summary>
    internal IReadOnlySet<Guid> Helps { get; }

    /// <summary>Whether this view changes anything at all.</summary>
    internal bool Acts => ScopeHurts || Hurts.Count > 0 || Helps.Count > 0;

    /// <summary>Whether <paramref name="experienceId"/> is to be omitted as measured harmful.</summary>
    internal bool Harmful(Guid experienceId) => ScopeHurts || Hurts.Contains(experienceId);

    /// <summary>
    /// The pooled view of <paramref name="estimate"/>, or <see langword="null"/> when it must not be acted on: absent, not
    /// tracked, or a store outcome other than <see cref="ExperienceStoreOutcome.Found"/>.
    /// </summary>
    internal static BenefitView? From(ExperienceBenefitEstimate? estimate) =>
        Usable(estimate) ? Build(estimate!, model: null) : null;

    /// <summary>Whether <paramref name="estimate"/> may be acted on at all.</summary>
    internal static bool Usable(ExperienceBenefitEstimate? estimate) =>
        estimate is { Tracked: true, Outcome: ExperienceStoreOutcome.Found };

    /// <summary>
    /// The view for <paramref name="model"/>'s runs, or the pooled view when <paramref name="model"/> is
    /// <see langword="null"/>: the scope's verdict is that model's group's, and a lesson's is its runs of that model's,
    /// pooled over the models that learned it; wherever that group has <see cref="ExperienceBenefitVerdict.NoEvidence"/>,
    /// the pooled verdict stands. <see langword="null"/> when the view changes nothing.
    /// </summary>
    internal static BenefitView? Build(ExperienceBenefitEstimate estimate, string? model)
    {
        var minimum = estimate.MinimumRunsPerArm;
        var hurts = new HashSet<Guid>();
        var helps = new HashSet<Guid>();
        foreach (var lesson in estimate.Lessons ?? [])
        {
            if (lesson is null)
            {
                continue;
            }

            var verdict = lesson.Comparison?.Verdict ?? ExperienceBenefitVerdict.NoEvidence;
            if (model is not null && ForModel(lesson.ByModelPair, model, minimum) is { } own and not ExperienceBenefitVerdict.NoEvidence)
            {
                verdict = own;
            }

            switch (verdict)
            {
                case ExperienceBenefitVerdict.Hurts:
                    hurts.Add(lesson.ExperienceId);
                    break;
                case ExperienceBenefitVerdict.Helps:
                    helps.Add(lesson.ExperienceId);
                    break;
            }
        }

        var scopeVerdict = estimate.Overall?.Verdict ?? ExperienceBenefitVerdict.NoEvidence;
        if (model is not null
            && estimate.ByConsumingModel?.FirstOrDefault(group => string.Equals(group?.Model, model, StringComparison.Ordinal)) is { Comparison.Verdict: var modelVerdict }
            && modelVerdict != ExperienceBenefitVerdict.NoEvidence)
        {
            scopeVerdict = modelVerdict;
        }

        var view = new BenefitView(scopeVerdict == ExperienceBenefitVerdict.Hurts, hurts, helps);
        return view.Acts ? view : null;
    }

    /// <summary>
    /// One lesson's verdict for <paramref name="model"/>'s runs: its pairs whose consuming model is <paramref name="model"/>,
    /// their counts summed and decided as the estimator decides any group. <see langword="null"/> when there are none.
    /// </summary>
    private static ExperienceBenefitVerdict? ForModel(IReadOnlyList<ExperienceModelPairBenefit>? pairs, string model, int minimum)
    {
        int treatmentRuns = 0, treatmentSuccesses = 0, holdoutRuns = 0, holdoutSuccesses = 0;
        var any = false;
        foreach (var pair in pairs ?? [])
        {
            if (pair?.Comparison is not { } comparison || !string.Equals(pair.RunModel, model, StringComparison.Ordinal))
            {
                continue;
            }

            any = true;
            treatmentRuns += comparison.Treatment?.Runs ?? 0;
            treatmentSuccesses += comparison.Treatment?.Successes ?? 0;
            holdoutRuns += comparison.Holdout?.Runs ?? 0;
            holdoutSuccesses += comparison.Holdout?.Successes ?? 0;
        }

        return any
            ? ExperienceBenefitEstimator.Compare(treatmentRuns, treatmentSuccesses, holdoutRuns, holdoutSuccesses, Math.Max(1, minimum)).Verdict
            : null;
    }
}

/// <summary>
/// One estimate's views: the pooled one, and with <see cref="ExperienceBenefitPolicy.PerModel"/> one per consuming model the
/// estimate measured. A model it did not measure reads the pooled view.
/// </summary>
internal sealed class BenefitViews
{
    private readonly BenefitView? _pooled;
    private readonly Dictionary<string, BenefitView?> _byModel;

    private BenefitViews(BenefitView? pooled, Dictionary<string, BenefitView?> byModel)
    {
        _pooled = pooled;
        _byModel = byModel;
    }

    /// <summary>The view for <paramref name="model"/>, or the pooled one when it is <see langword="null"/> or was not measured.</summary>
    internal BenefitView? For(string? model) =>
        model is not null && _byModel.TryGetValue(model, out var view) ? view : _pooled;

    /// <summary>The views of <paramref name="estimate"/>, or <see langword="null"/> when it must not be acted on.</summary>
    internal static BenefitViews? From(ExperienceBenefitEstimate? estimate)
    {
        if (!BenefitView.Usable(estimate))
        {
            return null;
        }

        var byModel = new Dictionary<string, BenefitView?>(StringComparer.Ordinal);
        foreach (var group in estimate!.ByConsumingModel ?? [])
        {
            // An unrecorded model is never an agent's own: an agent without one reads the pooled view.
            if (group?.Model is { } model && model != ExperienceBenefitEstimate.UnknownModel)
            {
                byModel[model] = BenefitView.Build(estimate, model);
            }
        }

        return new BenefitViews(BenefitView.Build(estimate, model: null), byModel);
    }
}

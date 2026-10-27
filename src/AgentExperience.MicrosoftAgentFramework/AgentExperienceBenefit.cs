using AgentExperience.Abstractions;
using AgentExperience.Core.Holdout;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.MicrosoftAgentFramework;

/// <summary>
/// Whether the lessons help, measured from the injection holdout. With
/// <see cref="Injection.ExperienceInjectionOptions.Holdout"/> on, some captured runs are given their lessons and the rest
/// have them withheld; this compares how often each arm's runs verified.
/// </summary>
/// <remarks>
/// <para>
/// It reads through <see cref="IExperienceRecordStore.QueryHoldoutOutcomesAsync"/>, a page at a time, under the caller's
/// own authorization and in exactly the scope passed in (the scope the blocks were built in), and folds each page into an
/// <see cref="ExperienceBenefitAccumulator"/>, which gives what <see cref="ExperienceBenefitEstimator"/> gives over the same
/// rows while holding one page at a time. It never writes, and it changes nothing about ranking, confidence or injection.
/// </para>
/// <para>
/// The authorization and scope must come from the host's own authentication, never from a request's query string or
/// body. The estimate names lessons by ID and counts runs: serve it only behind the host's own authorization.
/// </para>
/// </remarks>
public static class AgentExperienceBenefit
{
    /// <summary>Estimates the lessons' benefit in <paramref name="scope"/>, overall and per lesson.</summary>
    /// <param name="services">The container holding the <see cref="IExperienceRecordStore"/>.</param>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The exact scope whose holdout assignments are read.</param>
    /// <param name="options">The verdict's threshold; <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The estimate. A store that does not record holdout assignments gives an empty estimate with
    /// <see cref="ExperienceBenefitEstimate.Tracked"/> <see langword="false"/>; a store outcome other than
    /// <see cref="ExperienceStoreOutcome.Found"/> (the scope lies outside the authorization, say) gives an empty estimate
    /// carrying that outcome, never an exception.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ExperienceBenefitOptions.MinimumRunsPerArm"/> is less than 1.</exception>
    /// <exception cref="InvalidOperationException">
    /// No <see cref="IExperienceRecordStore"/> is registered, or the store paged a run's rows apart (out of the port's
    /// run-then-record order), so the run could not be counted once.
    /// </exception>
    /// <exception cref="ExperienceStoreException">The store failed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<ExperienceBenefitEstimate> EstimateAsync(
        IServiceProvider services,
        AuthorizationContext authorization,
        Scope scope,
        ExperienceBenefitOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);

        // Validates the options before anything is read.
        _ = ExperienceBenefitEstimator.Empty(options);
        var store = services.GetRequiredService<IExperienceRecordStore>();
        return await EstimateFromStoreAsync(store, authorization, scope, options, assignedFrom: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads every holdout row in <paramref name="scope"/> through <paramref name="store"/>, a page at a time, and estimates
    /// from those assigned at or after <paramref name="assignedFrom"/> (every row when <see langword="null"/>). The filter is
    /// applied in memory, before the estimator. Each page is folded into an <see cref="ExperienceBenefitAccumulator"/> and
    /// released, so memory grows with runs and lessons, not rows. Behaves exactly as the public overload otherwise.
    /// </summary>
    internal static async Task<ExperienceBenefitEstimate> EstimateFromStoreAsync(
        IExperienceRecordStore store,
        AuthorizationContext authorization,
        Scope scope,
        ExperienceBenefitOptions? options,
        DateTimeOffset? assignedFrom,
        CancellationToken cancellationToken)
    {
        var empty = ExperienceBenefitEstimator.Empty(options);
        if (!store.TracksHoldout)
        {
            return empty with { Tracked = false };
        }

        // Folded run by run as the pages arrive (the port pages by run, then record): only one page is held at a time.
        var accumulator = new ExperienceBenefitAccumulator(options);
        HoldoutOutcomeCursor? after = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await store.QueryHoldoutOutcomesAsync(
                authorization,
                new HoldoutOutcomeQuery(scope, HoldoutOutcomeQuery.MaxLimit, after),
                cancellationToken).ConfigureAwait(false);

            if (page is null || page.Outcome != ExperienceStoreOutcome.Found)
            {
                return empty with { Outcome = page?.Outcome ?? ExperienceStoreOutcome.Invalid };
            }

            // A store that forwards the capability but not the read reads as not tracked.
            if (!page.Tracked)
            {
                return empty with { Tracked = false };
            }

            var pageRows = page.Rows ?? [];
            accumulator.AddRange(pageRows.Where(row => row is not null && (assignedFrom is not { } from || row.AssignedAt >= from)));
            if (pageRows.Count < HoldoutOutcomeQuery.MaxLimit || pageRows[^1] is not { } last)
            {
                break;
            }

            var next = new HoldoutOutcomeCursor(last.RunId, last.ExperienceId);

            // A store whose page does not move on would loop forever.
            if (next == after)
            {
                break;
            }

            after = next;
        }

        return accumulator.ToEstimate();
    }
}

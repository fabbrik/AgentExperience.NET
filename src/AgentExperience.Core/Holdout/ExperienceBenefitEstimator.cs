using AgentExperience.Abstractions;

namespace AgentExperience.Core.Holdout;

/// <summary>What the benefit estimate claims, if anything.</summary>
public enum ExperienceBenefitVerdict
{
    /// <summary>
    /// The data cannot support a claim: either arm has fewer than <see cref="ExperienceBenefitOptions.MinimumRunsPerArm"/>
    /// runs with a verified outcome, or the 95% interval of the difference includes zero.
    /// </summary>
    NoEvidence = 0,

    /// <summary>Runs given the lessons succeed more often: the 95% interval of the difference lies wholly above zero.</summary>
    Helps = 1,

    /// <summary>Runs given the lessons succeed less often: the 95% interval of the difference lies wholly below zero.</summary>
    Hurts = 2,
}

/// <summary>How <see cref="ExperienceBenefitEstimator"/> decides a verdict.</summary>
public sealed record ExperienceBenefitOptions
{
    /// <summary>The default for <see cref="MinimumRunsPerArm"/>.</summary>
    public const int DefaultMinimumRunsPerArm = 20;

    /// <summary>
    /// The fewest runs with a verified outcome (<see cref="TaskVerificationStatus.Verified"/> or
    /// <see cref="TaskVerificationStatus.Failed"/>) each arm needs before the verdict can be anything but
    /// <see cref="ExperienceBenefitVerdict.NoEvidence"/>. At least 1; <see cref="DefaultMinimumRunsPerArm"/> by default.
    /// </summary>
    public int MinimumRunsPerArm { get; init; } = DefaultMinimumRunsPerArm;
}

/// <summary>One arm's verified runs.</summary>
/// <param name="Runs">How many runs in this arm have a verified outcome (success or failure).</param>
/// <param name="Successes">How many of them verified (<see cref="TaskVerificationStatus.Verified"/>).</param>
public sealed record ExperienceBenefitArm(int Runs, int Successes)
{
    /// <summary>The success rate, <see cref="Successes"/> / <see cref="Runs"/>; <see langword="null"/> when there are no runs.</summary>
    public double? Rate => Runs == 0 ? null : (double)Successes / Runs;
}

/// <summary>
/// The two arms compared: per-arm counts and rates, the difference (treatment minus holdout) with its 95% Newcombe
/// hybrid-score interval, the verdict, and the runs left out of the rates.
/// </summary>
/// <param name="Treatment">The runs given the lessons.</param>
/// <param name="Holdout">The runs the lessons were withheld from.</param>
/// <param name="Difference">Treatment rate minus holdout rate; <see langword="null"/> when either arm has no runs.</param>
/// <param name="Lower">The 95% interval's lower bound, in [-1, 1]; <see langword="null"/> when either arm has no runs.</param>
/// <param name="Upper">The 95% interval's upper bound, in [-1, 1]; <see langword="null"/> when either arm has no runs.</param>
/// <param name="Verdict">What the comparison supports.</param>
/// <param name="Unverified">Runs whose finalized record's outcome is <see cref="TaskVerificationStatus.Unknown"/>: left out of the rates.</param>
/// <param name="Pending">Runs with no finalized record the caller could read (not finalized yet, or undecryptable): left out of the rates.</param>
/// <param name="MixedArm">Runs recorded in both arms (the holdout's rate or salt changed mid-run): left out of the rates.</param>
public sealed record ExperienceBenefitComparison(
    ExperienceBenefitArm Treatment,
    ExperienceBenefitArm Holdout,
    double? Difference,
    double? Lower,
    double? Upper,
    ExperienceBenefitVerdict Verdict,
    int Unverified,
    int Pending,
    int MixedArm);

/// <summary>The comparison over the runs whose blocks named one lesson: holdout runs were denied it, treatment runs given it.</summary>
/// <param name="ExperienceId">The lesson.</param>
/// <param name="Comparison">The arms compared over those runs alone.</param>
public sealed record ExperienceLessonBenefit(Guid ExperienceId, ExperienceBenefitComparison Comparison);

/// <summary>What <see cref="ExperienceBenefitEstimator.Estimate"/> found: overall, and per lesson.</summary>
/// <param name="Overall">Every assigned run compared.</param>
/// <param name="Lessons">One comparison per lesson any assigned run's block named, ordered by <see cref="ExperienceLessonBenefit.ExperienceId"/>.</param>
public sealed record ExperienceBenefitEstimate(ExperienceBenefitComparison Overall, IReadOnlyList<ExperienceLessonBenefit> Lessons)
{
    /// <summary>
    /// Whether the store records holdout assignments at all. When <see langword="false"/> nothing was read, every count is
    /// 0, and the verdict is <see cref="ExperienceBenefitVerdict.NoEvidence"/>: absence of rows says nothing about benefit.
    /// </summary>
    public bool Tracked { get; init; } = true;

    /// <summary>
    /// The store's outcome: <see cref="ExperienceStoreOutcome.Found"/>, or why nothing was read
    /// (<see cref="ExperienceStoreOutcome.Denied"/> for a scope outside the authorization, say), in which case the estimate is empty.
    /// </summary>
    public ExperienceStoreOutcome Outcome { get; init; } = ExperienceStoreOutcome.Found;

    /// <summary>The <see cref="ExperienceBenefitOptions.MinimumRunsPerArm"/> the verdicts were decided with.</summary>
    public int MinimumRunsPerArm { get; init; } = ExperienceBenefitOptions.DefaultMinimumRunsPerArm;
}

/// <summary>
/// Turns holdout assignment rows, each carrying its run's verified outcome, into an answer to "do the lessons
/// help?". Pure and deterministic: no I/O, no clock, and the same rows in any order give the same estimate.
/// </summary>
/// <remarks>
/// <para>
/// A run's outcome is its own finalized record's: <see cref="TaskVerificationStatus.Verified"/> is a success,
/// <see cref="TaskVerificationStatus.Failed"/> a failure. A run with an <see cref="TaskVerificationStatus.Unknown"/>
/// outcome, with no outcome yet, or recorded in both arms is left out of the rates and counted on its own.
/// </para>
/// <para>
/// The difference's interval is Newcombe's hybrid score interval (method 10 of Newcombe, 1998), built from each arm's
/// Wilson score interval at z = 1.959963984540054, which stays inside [-1, 1] and behaves at rates of 0 and 1 where the
/// Wald interval does not. Nothing here changes ranking, confidence or injection.
/// </para>
/// </remarks>
public static class ExperienceBenefitEstimator
{
    /// <summary>The two-sided 95% normal quantile.</summary>
    public const double Z = 1.959963984540054;

    /// <summary>Estimates benefit overall and per lesson from <paramref name="rows"/>.</summary>
    /// <param name="rows">Assignment rows with their runs' outcomes, as <see cref="IExperienceRecordStore.QueryHoldoutOutcomesAsync"/> reads them. A null row is ignored.</param>
    /// <param name="options">The verdict's threshold; <see langword="null"/> for the defaults.</param>
    /// <returns>The estimate.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ExperienceBenefitOptions.MinimumRunsPerArm"/> is less than 1.</exception>
    public static ExperienceBenefitEstimate Estimate(IEnumerable<HoldoutOutcomeRow> rows, ExperienceBenefitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var minimum = MinimumRunsPerArm(options);

        // Sorted first, so the outcome a run is read with never depends on the order the rows came in.
        var sorted = rows.Where(row => row is not null)
            .OrderBy(row => row.RunId)
            .ThenBy(row => row.ExperienceId)
            .ToList();

        var runs = new Dictionary<Guid, RunClass>();
        var lessons = new SortedDictionary<Guid, HashSet<Guid>>();
        foreach (var group in sorted.GroupBy(row => row.RunId))
        {
            runs[group.Key] = Classify(group);
            foreach (var row in group)
            {
                if (!lessons.TryGetValue(row.ExperienceId, out var named))
                {
                    named = [];
                    lessons[row.ExperienceId] = named;
                }

                named.Add(group.Key);
            }
        }

        var overall = Tally(runs.Values, minimum);
        var perLesson = lessons
            .Select(lesson => new ExperienceLessonBenefit(lesson.Key, Tally(lesson.Value.Select(run => runs[run]), minimum)))
            .ToList();

        return new ExperienceBenefitEstimate(overall, perLesson) { MinimumRunsPerArm = minimum };
    }

    /// <summary>An estimate with nothing in it: every count 0 and no verdict.</summary>
    /// <param name="options">The verdict's threshold; <see langword="null"/> for the defaults.</param>
    /// <returns>The empty estimate.</returns>
    public static ExperienceBenefitEstimate Empty(ExperienceBenefitOptions? options = null) => Estimate([], options);

    /// <summary>Compares two arms' verified counts: the rates, the difference, its interval and the verdict.</summary>
    internal static ExperienceBenefitComparison Compare(
        int treatmentRuns,
        int treatmentSuccesses,
        int holdoutRuns,
        int holdoutSuccesses,
        int minimumRunsPerArm,
        int unverified = 0,
        int pending = 0,
        int mixedArm = 0)
    {
        var treatment = new ExperienceBenefitArm(treatmentRuns, treatmentSuccesses);
        var holdout = new ExperienceBenefitArm(holdoutRuns, holdoutSuccesses);
        if (treatmentRuns == 0 || holdoutRuns == 0)
        {
            return new(treatment, holdout, null, null, null, ExperienceBenefitVerdict.NoEvidence, unverified, pending, mixedArm);
        }

        var (difference, lower, upper) = Newcombe(treatmentSuccesses, treatmentRuns, holdoutSuccesses, holdoutRuns);
        var verdict = treatmentRuns < minimumRunsPerArm || holdoutRuns < minimumRunsPerArm
            ? ExperienceBenefitVerdict.NoEvidence
            : lower > 0 ? ExperienceBenefitVerdict.Helps
            : upper < 0 ? ExperienceBenefitVerdict.Hurts
            : ExperienceBenefitVerdict.NoEvidence;
        return new(treatment, holdout, difference, lower, upper, verdict, unverified, pending, mixedArm);
    }

    /// <summary>The Wilson score interval for <paramref name="successes"/> of <paramref name="n"/> (n &gt; 0), clamped to [0, 1].</summary>
    internal static (double Lower, double Upper) Wilson(int successes, int n)
    {
        var p = (double)successes / n;
        var z2 = Z * Z;
        var denominator = 1 + (z2 / n);
        var centre = (p + (z2 / (2d * n))) / denominator;
        var half = Z * Math.Sqrt((p * (1 - p) / n) + (z2 / (4d * n * n))) / denominator;
        return (Math.Max(0d, centre - half), Math.Min(1d, centre + half));
    }

    /// <summary>The difference p1 - p2 and Newcombe's hybrid score interval for it (both arms non-empty), clamped to [-1, 1].</summary>
    internal static (double Difference, double Lower, double Upper) Newcombe(int successes1, int n1, int successes2, int n2)
    {
        var p1 = (double)successes1 / n1;
        var p2 = (double)successes2 / n2;
        var (l1, u1) = Wilson(successes1, n1);
        var (l2, u2) = Wilson(successes2, n2);
        var d = p1 - p2;
        var lower = d - Math.Sqrt(((p1 - l1) * (p1 - l1)) + ((u2 - p2) * (u2 - p2)));
        var upper = d + Math.Sqrt(((u1 - p1) * (u1 - p1)) + ((p2 - l2) * (p2 - l2)));
        return (d, Math.Max(-1d, lower), Math.Min(1d, upper));
    }

    private static int MinimumRunsPerArm(ExperienceBenefitOptions? options)
    {
        var minimum = (options ?? new ExperienceBenefitOptions()).MinimumRunsPerArm;
        if (minimum < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                minimum,
                $"{nameof(ExperienceBenefitOptions.MinimumRunsPerArm)} must be at least 1.");
        }

        return minimum;
    }

    /// <summary>One run: its arm when it has exactly one, and its outcome (the first row's that carries one).</summary>
    private static RunClass Classify(IEnumerable<HoldoutOutcomeRow> rows)
    {
        HoldoutArm? arm = null;
        var mixed = false;
        TaskVerificationStatus? outcome = null;
        foreach (var row in rows)
        {
            if (arm is null)
            {
                arm = row.Arm;
            }
            else if (arm != row.Arm)
            {
                mixed = true;
            }

            outcome ??= row.RunOutcome;
        }

        return new RunClass(mixed ? null : arm, outcome);
    }

    private static ExperienceBenefitComparison Tally(IEnumerable<RunClass> runs, int minimum)
    {
        int treatmentRuns = 0, treatmentSuccesses = 0, holdoutRuns = 0, holdoutSuccesses = 0;
        int unverified = 0, pending = 0, mixed = 0;
        foreach (var run in runs)
        {
            if (run.Arm is not { } arm)
            {
                mixed++;
                continue;
            }

            switch (run.Outcome)
            {
                case null:
                    pending++;
                    continue;
                case TaskVerificationStatus.Verified or TaskVerificationStatus.Failed:
                    break;
                default:
                    unverified++;
                    continue;
            }

            var success = run.Outcome == TaskVerificationStatus.Verified ? 1 : 0;
            if (arm == HoldoutArm.Treatment)
            {
                treatmentRuns++;
                treatmentSuccesses += success;
            }
            else
            {
                holdoutRuns++;
                holdoutSuccesses += success;
            }
        }

        return Compare(treatmentRuns, treatmentSuccesses, holdoutRuns, holdoutSuccesses, minimum, unverified, pending, mixed);
    }

    /// <summary>A run's arm (<see langword="null"/> when it was recorded in both) and outcome.</summary>
    internal readonly record struct RunClass(HoldoutArm? Arm, TaskVerificationStatus? Outcome);
}

/// <summary>
/// The incremental form of <see cref="ExperienceBenefitEstimator.Estimate"/>: rows are added a run at a time, as
/// <see cref="IExperienceRecordStore.QueryHoldoutOutcomesAsync"/> pages them, and each run is folded into running counts
/// once the next run starts. It holds a set of the run IDs it has seen and one counter set per lesson, never the rows:
/// memory grows with runs and lessons, not with rows. For the same rows it gives exactly the estimate
/// <see cref="ExperienceBenefitEstimator.Estimate"/> gives.
/// </summary>
/// <remarks>
/// Every row of one run must be added before any row of the next (the order the port pages them in, by run then record);
/// within a run the rows may come in any order. A row for a run already folded is refused with an
/// <see cref="InvalidOperationException"/>, since folding it again would count the run twice. Not thread-safe.
/// </remarks>
public sealed class ExperienceBenefitAccumulator
{
    private readonly int _minimum;
    private readonly HashSet<Guid> _folded = [];
    private readonly SortedDictionary<Guid, Counts> _lessons = [];
    private readonly HashSet<Guid> _openLessons = [];
    private Counts _overall;
    private Guid? _openRun;
    private HoldoutArm? _openArm;
    private bool _openMixed;
    private Guid _openOutcomeFrom;
    private TaskVerificationStatus? _openOutcome;

    /// <summary>Starts an empty accumulation.</summary>
    /// <param name="options">The verdict's threshold; <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ExperienceBenefitOptions.MinimumRunsPerArm"/> is less than 1.</exception>
    public ExperienceBenefitAccumulator(ExperienceBenefitOptions? options = null)
    {
        _minimum = ExperienceBenefitEstimator.Empty(options).MinimumRunsPerArm;
    }

    /// <summary>Adds one row. A null row is ignored, as <see cref="ExperienceBenefitEstimator.Estimate"/> ignores one.</summary>
    /// <param name="row">An assignment row with its run's outcome.</param>
    /// <exception cref="InvalidOperationException"><paramref name="row"/>'s run was already folded: its rows were not contiguous.</exception>
    public void Add(HoldoutOutcomeRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (_openRun != row.RunId)
        {
            if (_folded.Contains(row.RunId))
            {
                throw new InvalidOperationException(
                    $"Run {row.RunId} was added again after another run's rows: add every row of a run before the next run's.");
            }

            Fold();
            _openRun = row.RunId;
            _openArm = row.Arm;
        }
        else if (_openArm != row.Arm)
        {
            _openMixed = true;
        }

        // The outcome the pure form reads a run with: the first carried one in record order.
        if (row.RunOutcome is { } outcome && (_openOutcome is null || row.ExperienceId.CompareTo(_openOutcomeFrom) < 0))
        {
            _openOutcome = outcome;
            _openOutcomeFrom = row.ExperienceId;
        }

        _openLessons.Add(row.ExperienceId);
    }

    /// <summary>Adds every row of <paramref name="rows"/>, in order.</summary>
    /// <param name="rows">Assignment rows with their runs' outcomes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A run's rows were not contiguous.</exception>
    public void AddRange(IEnumerable<HoldoutOutcomeRow?> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows)
        {
            Add(row);
        }
    }

    /// <summary>
    /// The estimate over every row added so far. The run being added is folded first, so no further row of that run may
    /// be added afterwards; rows of later runs may.
    /// </summary>
    /// <returns>The estimate, equal to <see cref="ExperienceBenefitEstimator.Estimate"/> over the same rows.</returns>
    public ExperienceBenefitEstimate ToEstimate()
    {
        Fold();
        var perLesson = _lessons
            .Select(lesson => new ExperienceLessonBenefit(lesson.Key, lesson.Value.Compare(_minimum)))
            .ToList();
        return new ExperienceBenefitEstimate(_overall.Compare(_minimum), perLesson) { MinimumRunsPerArm = _minimum };
    }

    private void Fold()
    {
        if (_openRun is not { } run)
        {
            return;
        }

        var runClass = new ExperienceBenefitEstimator.RunClass(_openMixed ? null : _openArm, _openOutcome);
        _overall.Count(runClass);
        foreach (var lesson in _openLessons)
        {
            _lessons.TryGetValue(lesson, out var counts);
            counts.Count(runClass);
            _lessons[lesson] = counts;
        }

        _folded.Add(run);
        _openRun = null;
        _openArm = null;
        _openMixed = false;
        _openOutcome = null;
        _openOutcomeFrom = Guid.Empty;
        _openLessons.Clear();
    }

    /// <summary>One comparison's running counts, tallied as the pure form tallies a run.</summary>
    private struct Counts
    {
        private int _treatmentRuns, _treatmentSuccesses, _holdoutRuns, _holdoutSuccesses, _unverified, _pending, _mixed;

        public void Count(ExperienceBenefitEstimator.RunClass run)
        {
            if (run.Arm is not { } arm)
            {
                _mixed++;
                return;
            }

            switch (run.Outcome)
            {
                case null:
                    _pending++;
                    return;
                case TaskVerificationStatus.Verified or TaskVerificationStatus.Failed:
                    break;
                default:
                    _unverified++;
                    return;
            }

            var success = run.Outcome == TaskVerificationStatus.Verified ? 1 : 0;
            if (arm == HoldoutArm.Treatment)
            {
                _treatmentRuns++;
                _treatmentSuccesses += success;
            }
            else
            {
                _holdoutRuns++;
                _holdoutSuccesses += success;
            }
        }

        public readonly ExperienceBenefitComparison Compare(int minimum) =>
            ExperienceBenefitEstimator.Compare(
                _treatmentRuns, _treatmentSuccesses, _holdoutRuns, _holdoutSuccesses, minimum, _unverified, _pending, _mixed);
    }
}

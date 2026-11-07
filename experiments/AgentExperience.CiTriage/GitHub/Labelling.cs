namespace AgentExperience.CiTriage.GitHub;

/// <summary>What the repository's own later history says a failure was.</summary>
public enum LabelKind
{
    /// <summary>A later green run on another commit, at most <see cref="Labelling.MaxFixFiles"/> files away: those files are the fix.</summary>
    Code,

    /// <summary>A later green run on the same commit: nothing changed, so the failure was flaky.</summary>
    Flaky,

    /// <summary>The fix touched more than <see cref="Labelling.MaxFixFiles"/> files: no single answer to score against. Never triaged.</summary>
    Ambiguous,

    /// <summary>No later green run on the branch yet. Never triaged.</summary>
    Pending,

    /// <summary>A commit is not in the clone and could not be fetched, so the diff is unknown. Never triaged.</summary>
    Unavailable,
}

/// <summary>A failure's label, the fix files for <see cref="LabelKind.Code"/>, and the green run that decided it.</summary>
public sealed record FailureLabel(LabelKind Kind, IReadOnlyList<string> FixFiles, CiRun? Green)
{
    public bool Triageable => Kind is LabelKind.Code or LabelKind.Flaky;

    /// <summary>The ledger's spelling.</summary>
    public string Name => Kind.ToString().ToLowerInvariant();
}

/// <summary>A failed CI run and its label.</summary>
public sealed record CiFailure(CiRun Run, FailureLabel Label);

/// <summary>
/// The ground-truth rules. A failure is the final attempt of a CI run with conclusion <c>failure</c>. Its label comes from
/// the first later completed CI run on the same branch that succeeded: the same head commit is <c>flaky</c>; another
/// commit is <c>code</c> with <c>git diff --name-only failSha greenSha</c> as the fix files when that is at most ten files,
/// else <c>ambiguous</c>; no such run yet is <c>pending</c>.
/// </summary>
public static class Labelling
{
    public const int MaxFixFiles = 10;

    /// <summary>The first run on the failure's branch, created after it, that succeeded; <see langword="null"/> when there is none.</summary>
    public static CiRun? FindGreen(CiRun failure, IEnumerable<CiRun> runs) =>
        runs
            .Where(run => run.Succeeded
                && string.Equals(run.Branch, failure.Branch, StringComparison.Ordinal)
                && run.CreatedAt > failure.CreatedAt)
            .OrderBy(run => run.CreatedAt)
            .ThenBy(run => run.Id)
            .FirstOrDefault();

    /// <summary>
    /// The label for a failure whose green run is <paramref name="green"/>, given the diff between the two commits
    /// (<see langword="null"/> when it could not be read). A different commit with an empty diff (a re-run pushed as an
    /// empty commit) changed no file, so it is labelled flaky like a same-commit green.
    /// </summary>
    public static FailureLabel Classify(CiRun failure, CiRun? green, IReadOnlyList<string>? diff)
    {
        if (green is null)
        {
            return new FailureLabel(LabelKind.Pending, [], null);
        }

        if (string.Equals(green.HeadSha, failure.HeadSha, StringComparison.Ordinal))
        {
            return new FailureLabel(LabelKind.Flaky, [], green);
        }

        if (diff is null)
        {
            return new FailureLabel(LabelKind.Unavailable, [], green);
        }

        return diff.Count switch
        {
            0 => new FailureLabel(LabelKind.Flaky, [], green),
            > MaxFixFiles => new FailureLabel(LabelKind.Ambiguous, [], green),
            _ => new FailureLabel(LabelKind.Code, [.. diff.Order(StringComparer.Ordinal)], green),
        };
    }

    /// <summary>Every failed run, labelled, in the order they completed.</summary>
    public static async Task<IReadOnlyList<CiFailure>> LabelAsync(IReadOnlyList<CiRun> runs, IRepository repository, CancellationToken cancellationToken)
    {
        var failures = new List<CiFailure>();
        foreach (var failure in runs.Where(run => run.Failed).OrderBy(run => run.CompletedAt).ThenBy(run => run.Id))
        {
            var green = FindGreen(failure, runs);
            IReadOnlyList<string>? diff = null;
            if (green is not null && !string.Equals(green.HeadSha, failure.HeadSha, StringComparison.Ordinal))
            {
                diff = await repository.DiffNamesAsync(failure.HeadSha, green.HeadSha, cancellationToken).ConfigureAwait(false);
            }

            failures.Add(new CiFailure(failure, Classify(failure, green, diff)));
        }

        return failures;
    }
}

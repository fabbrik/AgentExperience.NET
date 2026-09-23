using System.Globalization;

namespace AgentExperience.ReuseBaseline.Harness;

/// <summary>
/// The task set is not one a trial may be run from. Thrown before any trial starts, because a task
/// that is both learned from and evaluated on makes every number that follows meaningless.
/// </summary>
/// <param name="message">What is wrong with the task set.</param>
public sealed class TaskSetException(string message) : Exception(message);

/// <summary>
/// One task the simulated agent is asked to resolve: an incident, and the one approach that
/// resolves it.
/// </summary>
/// <remarks>
/// <see cref="ResolvingStrategy"/> is the task's ground truth. It is given to the <em>tool</em>, which
/// is what decides whether an attempt passed; it is never given to the agent, which has to either
/// read it out of an injected Historical Reference or find it by exploring.
/// </remarks>
/// <param name="TaskId">The task's identity. Learning and evaluation identities must be disjoint.</param>
/// <param name="Text">What the agent is asked, verbatim. Also what retrieval matches on.</param>
/// <param name="ResolvingStrategy">The one strategy whose check exits zero for this task.</param>
public sealed record ReuseBaselineTask(string TaskId, string Text, string ResolvingStrategy);

/// <summary>
/// How much of one task's wording the other repeats, as the harness measures it.
/// </summary>
/// <param name="LearningTaskId">The learning task.</param>
/// <param name="EvaluationTaskId">The evaluation task.</param>
/// <param name="SharedWords">The content words the two texts share, in ordinal order.</param>
/// <param name="Overlap">
/// <c>|shared| / min(|learning content words|, |evaluation content words|)</c>. One means every
/// content word of the shorter text appears in the longer one.
/// </param>
public sealed record TaskTextOverlap(
    string LearningTaskId,
    string EvaluationTaskId,
    IReadOnlyList<string> SharedWords,
    double Overlap);

/// <summary>
/// A versioned task set with its learning tasks and its evaluation tasks declared separately, and the
/// strategy space the simulated agent explores.
/// </summary>
/// <remarks>
/// <para>
/// <b>The two sets are disjoint in substance, and <see cref="Validate"/> refuses to let a run start
/// otherwise.</b> Disjoint identifiers are not enough and were not enough: the first version of this
/// task set paired the learning task <em>"A settlement batch has stalled because the ledger row it
/// writes is held by a stale session"</em> with the evaluation task <em>"A settlement batch has
/// stalled: the ledger row it writes is still held by a stale session"</em>, which have different
/// identifiers and the same sentence. Retrieval here is word overlap, so the memory-enabled arm's
/// advantage was a near-verbatim lookup. <see cref="Validate"/> therefore measures how much wording
/// the two sets share and refuses the set above <see cref="MaxPermittedOverlap"/>, so identifier
/// disjointness can never again stand in for task disjointness.
/// </para>
/// <para>
/// <see cref="ExplorationOrder"/> is the declared, fixed order the agent tries strategies in when it
/// has no injected experience to go on. It is part of the task set rather than hidden in the agent
/// so that the number of failed attempts a memory-disabled trial costs is readable from the task set
/// alone: it is the position of the task's resolving strategy in this list.
/// </para>
/// </remarks>
/// <param name="Version">The version string, which must equal the pre-registration's <c>taskSetVersion</c>.</param>
/// <param name="ExplorationOrder">The fixed order strategies are tried in with no injected experience.</param>
/// <param name="LearningTasks">The tasks whose runs become Experience Records. Never evaluated on.</param>
/// <param name="EvaluationTasks">The tasks the trials run. Never learned from.</param>
public sealed record ReuseBaselineTaskSet(
    string Version,
    IReadOnlyList<string> ExplorationOrder,
    IReadOnlyList<ReuseBaselineTask> LearningTasks,
    IReadOnlyList<ReuseBaselineTask> EvaluationTasks)
{
    /// <summary>
    /// The most wording an evaluation task may share with a learning task before the task set is
    /// refused: half the content words of the shorter of the two.
    /// </summary>
    /// <remarks>
    /// A threshold rather than zero, because two tasks that share a failure mode legitimately share
    /// some vocabulary -- the domain is the thing they have in common. Half is far above what a
    /// genuinely reworded task reaches and far below what a paraphrase reaches: the paraphrases this
    /// check was written to refuse scored 1.00 and 0.88.
    /// </remarks>
    public const double MaxPermittedOverlap = 0.5;

    /// <summary>
    /// The fewest content words a task's text may carry. Below this the overlap measure is noise --
    /// two three-word sentences sharing one word already score 0.33 -- so a task that short is
    /// refused rather than waved through.
    /// </summary>
    public const int MinimumContentWords = 4;

    private static readonly char[] WordSeparators =
        [' ', '\t', '\n', '\r', '.', ',', ';', ':', '\'', '"', '(', ')', '-', '/', '?', '!'];

    /// <summary>
    /// The function words the overlap measure ignores, declared here in the open so the number it
    /// produces is checkable by hand.
    /// </summary>
    /// <remarks>
    /// Two English sentences about anything at all share their articles, prepositions and auxiliary
    /// verbs. Counting those would let a paraphrase hide behind them, and would flag two unrelated
    /// tasks as similar. The list is deliberately small and ordinary; it is not a stemmer and it is
    /// not trying to be one.
    /// </remarks>
    public static IReadOnlyCollection<string> StopWords { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "a", "an", "the", "is", "are", "was", "were", "be", "been", "being", "am",
        "has", "have", "had", "it", "its", "they", "them", "their", "he", "she", "we", "you", "i",
        "and", "or", "but", "because", "while", "when", "that", "this", "these", "those",
        "to", "of", "in", "on", "at", "by", "for", "with", "from", "as", "so", "into", "onto",
        "still", "not", "no", "yet", "after", "before", "under", "over", "up", "down", "out",
        "than", "then", "there", "here", "what", "which", "who", "whom", "whose", "how", "why",
        "all", "any", "some", "one", "two", "does", "do", "did", "will", "would", "can", "cannot",
        "could", "should", "may", "might", "must", "nothing", "nobody", "every", "each", "since",
        "again", "also", "very", "just", "now", "new", "another", "other", "same", "been",
    };

    /// <summary>
    /// Refuses the task set unless every property the measurement rests on holds.
    /// </summary>
    /// <exception cref="TaskSetException">
    /// A task id appears in both sets, a set repeats an id, a set is empty, an evaluation task
    /// repeats a learning task's wording, or a task's resolving strategy is not one the agent can
    /// explore.
    /// </exception>
    public void Validate()
    {
        if (LearningTasks.Count == 0)
        {
            throw new TaskSetException("The task set declares no learning tasks, so the memory-enabled condition has nothing to be enabled with.");
        }

        if (EvaluationTasks.Count == 0)
        {
            throw new TaskSetException("The task set declares no evaluation tasks, so there is nothing to measure.");
        }

        if (ExplorationOrder.Count == 0)
        {
            throw new TaskSetException("The task set declares no exploration order, so an agent with no injected experience could not act at all.");
        }

        var explorable = new HashSet<string>(ExplorationOrder, StringComparer.Ordinal);
        if (explorable.Count != ExplorationOrder.Count)
        {
            throw new TaskSetException("The exploration order repeats a strategy, so the position of a resolving strategy in it would be ambiguous.");
        }

        var learning = RequireDistinct(LearningTasks, "learning");
        var evaluation = RequireDistinct(EvaluationTasks, "evaluation");

        // Frozen rule 9, first half. Reported as the whole overlap rather than the first one found,
        // so a reader fixing the task set sees all of it at once.
        var overlap = learning.Intersect(evaluation, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
        if (overlap.Count > 0)
        {
            throw new TaskSetException(
                $"Task id(s) [{string.Join(", ", overlap)}] appear in both the learning set and the evaluation set. "
                + "The two must be disjoint: evaluating on what was learned from measures a lookup, not reuse.");
        }

        // Frozen rule 9, second half, and the fix for the review's highest-priority finding. Two
        // tasks with different identifiers and the same sentence are not disjoint in any sense that
        // matters to a measurement whose retrieval is word overlap.
        foreach (var task in LearningTasks.Concat(EvaluationTasks))
        {
            var content = ContentWords(task.Text);
            if (content.Count < MinimumContentWords)
            {
                throw new TaskSetException(string.Format(
                    CultureInfo.InvariantCulture,
                    "Task '{0}' carries {1} content word(s) and at least {2} are required. Below that the wording-overlap check "
                        + "below is noise, so a task that short cannot be shown to be distinct from a learning task at all.",
                    task.TaskId,
                    content.Count,
                    MinimumContentWords));
            }
        }

        var tooSimilar = Overlaps()
            .Where(pair => pair.Overlap >= MaxPermittedOverlap)
            .OrderByDescending(pair => pair.Overlap)
            .ThenBy(pair => pair.EvaluationTaskId, StringComparer.Ordinal)
            .ToList();

        if (tooSimilar.Count > 0)
        {
            throw new TaskSetException(string.Format(
                CultureInfo.InvariantCulture,
                "Evaluation task(s) repeat the wording of a learning task: {0}. The threshold is {1}. "
                    + "Disjoint task ids are not disjoint tasks: retrieval here is word overlap, so evaluating on a reworded "
                    + "learning task measures a near-verbatim lookup and reports it as reuse.",
                string.Join("; ", tooSimilar.Select(pair => string.Format(
                    CultureInfo.InvariantCulture,
                    "'{0}' shares {1} of '{2}' (shared: {3})",
                    pair.EvaluationTaskId,
                    pair.Overlap.ToString("F2", CultureInfo.InvariantCulture),
                    pair.LearningTaskId,
                    string.Join(", ", pair.SharedWords)))),
                MaxPermittedOverlap.ToString("F2", CultureInfo.InvariantCulture)));
        }

        foreach (var task in LearningTasks.Concat(EvaluationTasks))
        {
            if (!explorable.Contains(task.ResolvingStrategy))
            {
                throw new TaskSetException(
                    $"Task '{task.TaskId}' is resolved by strategy '{task.ResolvingStrategy}', which is not in the exploration order, "
                    + "so a memory-disabled trial could never resolve it and the baseline would be unbounded rather than measured.");
            }
        }
    }

    /// <summary>
    /// How much wording each evaluation task shares with each learning task, every pair, measured
    /// the way <see cref="Validate"/> measures it. The report prints the worst of these so a reader
    /// can judge the separation rather than take it on trust.
    /// </summary>
    public IReadOnlyList<TaskTextOverlap> Overlaps()
    {
        var pairs = new List<TaskTextOverlap>();

        foreach (var learning in LearningTasks)
        {
            var left = ContentWords(learning.Text);

            foreach (var evaluation in EvaluationTasks)
            {
                var right = ContentWords(evaluation.Text);
                var shared = left.Intersect(right, StringComparer.Ordinal).OrderBy(word => word, StringComparer.Ordinal).ToList();
                var smaller = Math.Min(left.Count, right.Count);

                pairs.Add(new TaskTextOverlap(
                    learning.TaskId,
                    evaluation.TaskId,
                    shared,
                    smaller == 0 ? 0d : (double)shared.Count / smaller));
            }
        }

        return pairs;
    }

    /// <summary>
    /// The content words of <paramref name="text"/>: its words, lowercased, with
    /// <see cref="StopWords"/> removed.
    /// </summary>
    /// <param name="text">The task text.</param>
    public static IReadOnlySet<string> ContentWords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var words = text
            .Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => word.ToLowerInvariant())
            .Where(word => !StopWords.Contains(word));

        return new HashSet<string>(words, StringComparer.Ordinal);
    }

    /// <summary>
    /// How many attempts a memory-disabled trial fails before it resolves <paramref name="task"/>:
    /// the position of the task's resolving strategy in the declared exploration order.
    /// </summary>
    /// <remarks>
    /// This is the fixture's own arithmetic, stated here so a reader can check the memory-disabled
    /// arm's numbers against the task set without running anything. It is not used by the harness to
    /// produce a metric -- every reported value is read back out of the captured run -- but the tests
    /// compare the two, so the reported means stop depending on the golden file to be checked.
    /// </remarks>
    /// <param name="task">The task.</param>
    public int ExpectedExploringFailures(ReuseBaselineTask task) => ExpectedFailuresGiven(task, []);

    /// <summary>
    /// How many attempts a trial fails before it resolves <paramref name="task"/> when the strategies
    /// <paramref name="fromContext"/> were named in an injected block: the position of the task's
    /// resolving strategy in the candidate list those strategies produce.
    /// </summary>
    /// <remarks>
    /// The candidate list is worked out here, from the task set, and independently of
    /// <c>PolicyChatClient</c>, which works out its own. The tests compare the two: a harness whose
    /// measured cost does not match the cost its own task set implies is not measuring what it says
    /// it measures, and that check does not depend on the golden report.
    /// </remarks>
    /// <param name="task">The task.</param>
    /// <param name="fromContext">The strategies an injected block named, in the order it named them.</param>
    /// <returns>The number of failed attempts, or <c>-1</c> when nothing in the candidate list resolves the task.</returns>
    public int ExpectedFailuresGiven(ReuseBaselineTask task, IReadOnlyList<string> fromContext)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(fromContext);

        var candidates = new List<string>();

        foreach (var strategy in fromContext.Concat(ExplorationOrder))
        {
            if (!candidates.Contains(strategy, StringComparer.Ordinal))
            {
                candidates.Add(strategy);
            }
        }

        return candidates.IndexOf(task.ResolvingStrategy);
    }

    private static HashSet<string> RequireDistinct(IReadOnlyList<ReuseBaselineTask> tasks, string which)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.TaskId))
            {
                throw new TaskSetException($"A {which} task has no id.");
            }

            if (!ids.Add(task.TaskId))
            {
                throw new TaskSetException($"The {which} set names task id '{task.TaskId}' twice.");
            }
        }

        return ids;
    }
}

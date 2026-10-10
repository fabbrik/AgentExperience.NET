using System.Globalization;
using System.Text;

namespace AgentExperience.RetrievalQuality.Harness;

/// <summary>
/// Renders one adapter's run as the plain-text report its golden file holds.
/// </summary>
/// <remarks>
/// Byte-for-byte deterministic: every number is formatted with the invariant culture, lines end in <c>\n</c> whatever
/// the platform, and everything is printed in corpus order. It prints every query, including every miss, so a change
/// in retrieval shows up as a diff of the queries it moved and not only of the totals.
/// </remarks>
internal static class RetrievalQualityReport
{
    /// <summary>The line separator, the same on every platform.</summary>
    public const string LineSeparator = "\n";

    /// <summary>What a measure with nothing to average over prints.</summary>
    public const string Undefined = "-";

    /// <summary>How many candidates a query's line lists: injection's default <c>MaxRecords</c>.</summary>
    private const int Shown = 8;

    /// <summary>The report for <paramref name="result"/>.</summary>
    public static string Render(RetrievalQualityResult result)
    {
        var corpus = result.Corpus;
        var builder = new StringBuilder();
        void Line(string text = "") => builder.Append(text).Append(LineSeparator);

        Line($"RETRIEVAL QUALITY: {result.Adapter}");
        Line();
        Line("What it measures: lexical recall of IExperienceCandidateSource.SearchAsync, called directly. No model, no");
        Line("retrieval service, no clock, no environment and no ranking policy: only which records the text matches, and");
        Line("in what order.");
        Line();

        var families = corpus.Families;
        var familyRecords = corpus.Records.Count(r => families.Contains(r.Group));
        Line(Invariant($"Corpus: {corpus.Records.Count} records: {familyRecords} in {families.Count} task families ({string.Join(", ", families)}),"));
        Line(Invariant($"{corpus.Records.Count(r => r.Group == RetrievalCorpus.Distractor)} distractors, {corpus.Records.Count(r => r.Group == RetrievalCorpus.Ineligible)} ineligible ({Ineligibility(corpus)})."));
        Line(Invariant($"Queries: {corpus.Queries.Count} ({string.Join(", ", RetrievalCorpus.Categories.Select(c => $"{c} {corpus.Queries.Count(q => q.Category == c)}"))})."));
        Line(Invariant($"Search: Limit {RetrievalQualityRun.Limit}, MinimumConfidence {RetrievalQualityRun.MinimumConfidence:0.00}, statuses {string.Join(", ", ExperienceStatuses.EligibleForReuse)}."));
        Line();
        Line("R@k: recall in the first k candidates, and MRR: mean reciprocal rank of the first expected record, both over");
        Line("the queries that expect a record (lab). P@k: expected records in the first k divided by k, over every query");
        Line("(a query that expects nothing counts 0). zero: share of every query that got no candidate. k = 8 is what");
        Line("injection shows an agent by default.");
        Line();

        Line("METRICS");
        Line(Row("category", "n", "lab", "R@1", "R@3", "R@8", "P@3", "P@8", "MRR", "zero"));
        Line(Row(RetrievalMetrics.Of(result.Outcomes), "overall"));
        foreach (var category in RetrievalCorpus.Categories)
        {
            Line(Row(RetrievalMetrics.Of([.. result.Outcomes.Where(o => o.Query.Category == category)]), category));
        }

        Line();
        Line("MISSES (expected records not in the first 8 candidates)");
        var missed = result.Outcomes.Where(o => RetrievalMetrics.Misses(o, Shown).Count > 0).ToList();
        if (missed.Count == 0)
        {
            Line("none");
        }

        foreach (var outcome in missed)
        {
            var reason = outcome.Returned.Count == 0 ? "zero candidates" : Invariant($"{outcome.Returned.Count} candidates");
            Line($"{outcome.Query.Id}: {string.Join(", ", RetrievalMetrics.Misses(outcome, Shown))} ({reason})");
        }

        Line();
        Line("FALSE POSITIVES IN THE FIRST 8 (candidates no query label expects)");
        var noisy = result.Outcomes.Where(o => o.Returned.Take(Shown).Any(id => !o.Query.Expected.Contains(id))).ToList();
        if (noisy.Count == 0)
        {
            Line("none");
        }

        foreach (var outcome in noisy)
        {
            Line($"{outcome.Query.Id}: {string.Join(", ", outcome.Returned.Take(Shown).Where(id => !outcome.Query.Expected.Contains(id)))}");
        }

        Line();
        Line("QUERIES");
        foreach (var outcome in result.Outcomes)
        {
            var query = outcome.Query;
            Line();
            Line($"{query.Id} [{query.Category}]");
            Line($"  text: {query.Text}");
            Line("  expected: " + (query.Expected.Count == 0
                ? "none"
                : string.Join(", ", query.Expected.Select(id => RetrievalMetrics.Rank(outcome, id) is { } rank ? Invariant($"{id} (rank {rank})") : $"{id} (not returned)"))));
            Line(outcome.Returned.Count == 0
                ? "  returned: none"
                : Invariant($"  returned {outcome.Returned.Count}: {string.Join(", ", outcome.Returned.Take(Shown))}{(outcome.Returned.Count > Shown ? $", ... {outcome.Returned.Count - Shown} more" : string.Empty)}"));
        }

        return builder.ToString();
    }

    private static string Ineligibility(RetrievalCorpus corpus)
    {
        var ineligible = corpus.Records.Where(r => r.Group == RetrievalCorpus.Ineligible).ToList();
        var outOfScope = ineligible.Count(r => r.Scope != RetrievalCorpus.QueryScope);
        var notReusable = ineligible.Count(r => r.Scope == RetrievalCorpus.QueryScope && !ExperienceStatuses.IsEligibleForReuse(r.Status));
        var belowFloor = ineligible.Count - outOfScope - notReusable;
        return Invariant($"{outOfScope} out of scope, {notReusable} in a status not eligible for reuse, {belowFloor} below the confidence floor");
    }

    private static string Row(RetrievalMetrics metrics, string label) => Row(
        label,
        Invariant($"{metrics.Queries}"),
        Invariant($"{metrics.Labelled}"),
        Number(metrics.RecallAt1),
        Number(metrics.RecallAt3),
        Number(metrics.RecallAt8),
        Number(metrics.PrecisionAt3),
        Number(metrics.PrecisionAt8),
        Number(metrics.MeanReciprocalRank),
        Number(metrics.ZeroCandidateShare));

    private static string Row(string label, params string[] cells) =>
        (label.PadRight(20) + string.Join(" ", cells.Select((cell, index) => index < 2 ? cell.PadLeft(4) : cell.PadLeft(6)))).TrimEnd();

    private static string Number(double? value) => value is { } v ? v.ToString("0.000", CultureInfo.InvariantCulture) : Undefined;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

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
        Line("the queries that expect a record (lab). P@k: share of the first k candidates that are expected, over the");
        Line("labelled queries that got a candidate (- when none did). FP@8: unexpected candidates in the first 8, averaged");
        Line("over every query: the noise an agent would be shown. zero: share of every query that got no candidate.");
        Line("k = 8 is what injection shows an agent by default.");
        Line();

        Line("METRICS");
        Line(Row("category", "n", "lab", "R@1", "R@3", "R@8", "P@3", "P@8", "FP@8", "MRR", "zero"));
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

        RenderSignatures(result, Line);
        RenderInjectionFloor(result, Line);
        return builder.ToString();
    }

    /// <summary>
    /// Story 28.2: the signature channel's section: what it measures, its recall and false positives for the exact and
    /// the coarse match, and every query's two answers.
    /// </summary>
    private static void RenderSignatures(RetrievalQualityResult result, Action<string> line)
    {
        var corpus = result.Corpus;
        var holders = corpus.Failures.Select(f => corpus.Record(f.Record)).ToList();
        var labelled = corpus.SignatureQueries.Count(q => q.Expected.Count > 0);

        line(string.Empty);
        line("SIGNATURE CHANNEL");
        line(string.Empty);
        line("What it measures: IExperienceSignatureSource.FindBySignatureAsync, called directly with the failure signature of");
        line("a failed call seen in a sibling run (other paths, numbers, IDs and literals). exact: the same tool, error class");
        line("and fingerprint. coarse: the same tool and error class, whatever the message. A query's expected records hold");
        line("the same failure; a query with none expected is a failure no lesson knows, so every hit is a false positive.");
        line(Invariant($"Failures: {holders.Count} records hold a failed call ({holders.Count(r => r.IsEligible)} eligible, {holders.Count(r => !r.IsEligible)} ineligible, holding a queried failure)."));
        line(Invariant($"Queries: {corpus.SignatureQueries.Count} ({labelled} with a lesson, {corpus.SignatureQueries.Count - labelled} without). Same statuses, floor and limit as above."));
        line(string.Empty);
        line(Row("match", "n", "lab", "R@1", "R@3", "R@8", "P@3", "P@8", "FP@8", "MRR", "zero"));
        line(Row(RetrievalMetrics.Of([.. result.Signatures.Select(o => o.Exact)]), "exact"));
        line(Row(RetrievalMetrics.Of([.. result.Signatures.Select(o => o.Coarse)]), "coarse"));

        foreach (var outcome in result.Signatures)
        {
            var query = outcome.Query;
            var signature = query.Signature;
            line(string.Empty);
            line($"{query.Id} [{signature.ToolName}, {signature.ErrorClass}]");
            line($"  error: {query.Error.Split('\n')[0]}");
            line("  expected: " + (query.Expected.Count == 0 ? "none" : string.Join(", ", query.Expected)));
            line("  exact: " + Returned(outcome.Exact));
            line("  coarse: " + Returned(outcome.Coarse));
        }

        static string Returned(QueryOutcome outcome) => outcome.Returned.Count == 0
            ? "none"
            : string.Join(", ", outcome.Returned.Take(Shown)) + (outcome.Returned.Count > Shown ? Invariant($", ... {outcome.Returned.Count - Shown} more") : string.Empty);
    }

    /// <summary>
    /// Story 28.5: the injection floor's sweep: per text-coverage floor, recall and noise of what would reach the block, the
    /// distractor-overlap queries' noise, and which expected records and which unexpected ones each floor changes.
    /// </summary>
    private static void RenderInjectionFloor(RetrievalQualityResult result, Action<string> line)
    {
        line(string.Empty);
        line("INJECTION FLOOR");
        line(string.Empty);
        line("What it measures: what would reach the injected block at MaxRecords = 8 under ExperienceInjectionOptions.MinimumMatch");
        line("with MinimumTextCoverage f and no vector channel: the text candidates above, in order, whose share of the query's");
        line("distinct terms is at least f, cut to 8. f = 0 is the block with no floor, so its row repeats the overall metrics.");
        line("dFP@8: FP@8 over the distractor-overlap queries only. R@8, FP@8 and zero as above.");
        line(string.Empty);
        line(Row("floor", "n", "lab", "R@8", "FP@8", "dFP@8", "zero"));
        var unfloored = InjectionFloorSweep.Apply(result, 0d);
        foreach (var floor in InjectionFloorSweep.Floors)
        {
            var floored = InjectionFloorSweep.Apply(result, floor);
            var metrics = RetrievalMetrics.Of(floored);
            var distractor = RetrievalMetrics.Of([.. floored.Where(o => o.Query.Category == "distractor-overlap")]);
            line(Row(
                floor.ToString("0.0", CultureInfo.InvariantCulture),
                Invariant($"{metrics.Queries}"),
                Invariant($"{metrics.Labelled}"),
                Number(metrics.RecallAt8),
                Number(metrics.FalsePositivesAt8),
                Number(distractor.FalsePositivesAt8),
                Number(metrics.ZeroCandidateShare)));
        }

        foreach (var floor in InjectionFloorSweep.Floors.Where(f => f > 0d))
        {
            var floored = InjectionFloorSweep.Apply(result, floor);
            line(string.Empty);
            line(Invariant($"at {floor:0.0}:"));
            var lost = unfloored.Zip(floored)
                .Select(pair => (pair.First.Query.Id, Ids: pair.First.Returned.Except(pair.Second.Returned).Where(pair.First.Query.Expected.Contains).ToList()))
                .Where(entry => entry.Ids.Count > 0)
                .Select(entry => $"{entry.Id} ({string.Join(", ", entry.Ids)})")
                .ToList();
            line("  expected records it keeps out:" + (lost.Count == 0 ? " none" : string.Empty));
            lost.ForEach(entry => line("    " + entry));
            var noise = floored
                .Select(o => (o.Query.Id, Ids: o.Returned.Where(id => !o.Query.Expected.Contains(id)).ToList()))
                .Where(entry => entry.Ids.Count > 0)
                .Select(entry => $"{entry.Id} ({string.Join(", ", entry.Ids)})")
                .ToList();
            line("  unexpected records still injected:" + (noise.Count == 0 ? " none" : string.Empty));
            noise.ForEach(entry => line("    " + entry));
        }
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
        Number(metrics.FalsePositivesAt8),
        Number(metrics.MeanReciprocalRank),
        Number(metrics.ZeroCandidateShare));

    private static string Row(string label, params string[] cells) =>
        (label.PadRight(20) + string.Join(" ", cells.Select((cell, index) => index < 2 ? cell.PadLeft(4) : cell.PadLeft(6)))).TrimEnd();

    private static string Number(double? value) => value is { } v ? v.ToString("0.000", CultureInfo.InvariantCulture) : Undefined;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

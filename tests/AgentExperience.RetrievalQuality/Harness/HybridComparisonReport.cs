using System.Globalization;
using System.Text;

namespace AgentExperience.RetrievalQuality.Harness;

/// <summary>
/// Renders the opt-in hybrid column beside the text column, per query category, for a run against a real embedding
/// provider. It is never a golden: a provider's vectors are not reproducible byte for byte, so the report is written to
/// the test output (and to a file on request), to be read and, when it is worth keeping, committed by hand.
/// </summary>
internal static class HybridComparisonReport
{
    public static string Render(HybridRetrievalResult result, string provider)
    {
        var builder = new StringBuilder();
        void Line(string text = "") => builder.Append(text).Append(RetrievalQualityReport.LineSeparator);

        Line("RETRIEVAL QUALITY: hybrid column (in-memory, text vs text + vectors)");
        Line();
        Line($"Embedding provider: {provider}");
        Line(Invariant($"Embedded: {result.Embedded} records (the eligible ones in the query scope), by their sanitized retrieval summary."));
        Line("Hybrid: Core's merge rule only (one entry per record, the higher relevance kept), ordered by that relevance,");
        Line("without Core's other ranking components. Vector candidates below the one-call setup's default floor (cosine");
        Line("0.5, relevance 0.75) are dropped, as the setup drops them.");
        Line("Caveat: the two channels' relevances are on different scales (text: weighted term coverage; vectors:");
        Line("(1 + cosine) / 2, where unrelated pairs still score well above 0.5), so the merged order is not a calibrated");
        Line("ranking: a weak vector match can outrank a real text match.");
        Line();
        Line(Row("category", "column", "R@1", "R@3", "R@8", "P@8", "FP@8", "MRR", "zero"));
        Line(Rows("overall", result.Text.Outcomes, result.Hybrid.Outcomes));
        foreach (var category in RetrievalCorpus.Categories)
        {
            Line(Rows(
                category,
                [.. result.Text.Outcomes.Where(o => o.Query.Category == category)],
                [.. result.Hybrid.Outcomes.Where(o => o.Query.Category == category)]));
        }

        Line();
        Line("FIRST 3 PER QUERY (text | hybrid)");
        for (var i = 0; i < result.Text.Outcomes.Count; i++)
        {
            var text = result.Text.Outcomes[i];
            var hybrid = result.Hybrid.Outcomes[i];
            Line($"{text.Query.Id}: expected {string.Join(", ", text.Query.Expected.DefaultIfEmpty("none"))} | "
                + $"{string.Join(", ", text.Returned.Take(3).DefaultIfEmpty("none"))} | {string.Join(", ", hybrid.Returned.Take(3).DefaultIfEmpty("none"))}");
        }

        return builder.ToString();
    }

    private static string Rows(string category, IReadOnlyList<QueryOutcome> text, IReadOnlyList<QueryOutcome> hybrid) =>
        Row(category, "text", Cells(RetrievalMetrics.Of(text))) + RetrievalQualityReport.LineSeparator
        + Row(string.Empty, "hybrid", Cells(RetrievalMetrics.Of(hybrid)));

    private static string[] Cells(RetrievalMetrics metrics) =>
    [
        Number(metrics.RecallAt1),
        Number(metrics.RecallAt3),
        Number(metrics.RecallAt8),
        Number(metrics.PrecisionAt8),
        Number(metrics.FalsePositivesAt8),
        Number(metrics.MeanReciprocalRank),
        Number(metrics.ZeroCandidateShare),
    ];

    private static string Row(string category, string column, params string[] cells) =>
        (category.PadRight(20) + column.PadRight(8) + string.Join(" ", cells.Select(cell => cell.PadLeft(6)))).TrimEnd();

    private static string Number(double? value) =>
        value is { } v ? v.ToString("0.000", CultureInfo.InvariantCulture) : RetrievalQualityReport.Undefined;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

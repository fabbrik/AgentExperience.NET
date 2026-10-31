using System.Text;
using AgentExperience.RetrievalQuality.Harness;

namespace AgentExperience.RetrievalQuality;

/// <summary>
/// Story 20.1: the retrieval benchmark's checks, run once per adapter by a subclass. The corpus is seeded into a fresh
/// store (or a fresh tenant of one), every query is searched through the adapter's candidate source, and the report is
/// compared with the adapter's checked-in golden, byte for byte.
/// </summary>
/// <remarks>
/// <para>
/// The golden is what makes a change to retrieval visible: any change to which records a query finds, or in what
/// order, fails here until the report is regenerated, and the regenerated file's diff is the change, query by query.
/// Story 20.2 judges its fix by that diff.
/// </para>
/// <para>
/// The other checks hold whatever the numbers are: no search returns an ineligible record, and two runs give the same
/// bytes. A record is eligible or not by the corpus alone, never by what came back.
/// </para>
/// </remarks>
public abstract class RetrievalQualityTests
{
    /// <summary>The adapter's name in the report.</summary>
    protected abstract string Adapter { get; }

    /// <summary>The adapter's golden file, beside the project file.</summary>
    protected abstract string GoldenFileName { get; }

    /// <summary>Seeds the corpus into a store no other run uses, and runs every query.</summary>
    private protected abstract Task<RetrievalQualityResult> RunAsync(RetrievalCorpus corpus);

    [Fact]
    public async Task The_report_matches_the_checked_in_golden_byte_for_byte()
    {
        var rendered = RetrievalQualityReport.Render(await RunAsync(RetrievalCorpus.Load()));
        GoldenFile.RegenerateIfRequested(GoldenFileName, rendered);

        var golden = GoldenFile.Read(GoldenFileName);
        if (golden != rendered)
        {
            Assert.Fail(GoldenFile.DriftHint(GoldenFileName) + "\n" + FirstDifference(golden, rendered));
        }

        Assert.Equal(Encoding.UTF8.GetBytes(golden), Encoding.UTF8.GetBytes(rendered));
        Assert.DoesNotContain('\r', rendered);
        Assert.StartsWith($"RETRIEVAL QUALITY: {Adapter}\n", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_search_returns_an_ineligible_record()
    {
        var corpus = RetrievalCorpus.Load();
        var result = await RunAsync(corpus);

        // The corpus's ineligible records copy family records' words, so a source that ignored scope, status or the
        // confidence floor would return them; this check is not vacuous.
        Assert.Contains(corpus.Records, r => !r.IsEligible && r.Scope != RetrievalCorpus.QueryScope);
        Assert.Contains(corpus.Records, r => !r.IsEligible && !ExperienceStatuses.IsEligibleForReuse(r.Status));
        Assert.Contains(corpus.Records, r => !r.IsEligible && r.Confidence < RetrievalQualityRun.MinimumConfidence);

        // Story 28.2: ineligible records hold queried failures too, so the signature lookups are not vacuous either.
        Assert.Contains(corpus.Failures, f => !corpus.Record(f.Record).IsEligible);

        var hits = result.Outcomes
            .Concat(result.Signatures.SelectMany(s => new[] { s.Exact, s.Coarse }))
            .SelectMany(o => o.Returned.Where(id => !corpus.Record(id).IsEligible).Select(id => $"{o.Query.Id} returned {id}"))
            .ToList();
        Assert.True(hits.Count == 0, "Ineligible records were returned:\n" + string.Join("\n", hits));
    }

    [Fact]
    public async Task Two_runs_render_the_same_bytes()
    {
        var first = RetrievalQualityReport.Render(await RunAsync(RetrievalCorpus.Load()));
        var second = RetrievalQualityReport.Render(await RunAsync(RetrievalCorpus.Load()));

        Assert.Equal(Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(second));
    }

    /// <summary>
    /// A short query in a record's own words is the case lexical search exists for: both adapters put the record in
    /// the first three candidates.
    /// </summary>
    [Fact]
    public async Task Every_exact_query_finds_its_records_in_the_first_three()
    {
        var result = await RunAsync(RetrievalCorpus.Load());

        var exact = result.Outcomes.Where(o => o.Query.Category == "exact").ToList();
        Assert.NotEmpty(exact);
        var misses = exact
            .SelectMany(o => RetrievalMetrics.Misses(o, 3).Select(id => $"{o.Query.Id} did not find {id} in the first three: {string.Join(", ", o.Returned.Take(3))}"))
            .ToList();
        Assert.True(misses.Count == 0, string.Join("\n", misses));
    }

    /// <summary>
    /// Story 28.2: on both adapters, an exact signature match never returns a record that does not hold the queried
    /// failure, a coarse match finds every record that does, and coarse matching only adds after the exact matches. Which
    /// sibling failures an exact match misses (another package or container name is another fingerprint) is the
    /// golden's to show, not this check's.
    /// </summary>
    [Fact]
    public async Task The_signature_channel_s_exact_match_is_precise_and_its_coarse_match_finds_every_holder()
    {
        var result = await RunAsync(RetrievalCorpus.Load());

        Assert.NotEmpty(result.Signatures);
        var problems = new List<string>();
        foreach (var outcome in result.Signatures)
        {
            var expected = outcome.Query.Expected;
            problems.AddRange(outcome.Exact.Returned.Where(id => !expected.Contains(id)).Select(id => $"{outcome.Query.Id}: exact returned {id}"));
            problems.AddRange(expected.Where(id => !outcome.Coarse.Returned.Contains(id)).Select(id => $"{outcome.Query.Id}: coarse missed {id}"));
            if (!outcome.Coarse.Returned.Take(outcome.Exact.Returned.Count).SequenceEqual(outcome.Exact.Returned))
            {
                problems.Add($"{outcome.Query.Id}: the coarse match does not start with the exact one");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.Contains(result.Signatures, o => o.Exact.Returned.Count > 0);
    }

    /// <summary>
    /// Story 28.5: the injection floor's sweep at 0 is the block with no floor, so its row is the overall metrics' R@8
    /// and FP@8, and the golden's sweep table says so.
    /// </summary>
    [Fact]
    public async Task The_floor_sweep_at_zero_repeats_the_overall_recall_and_noise()
    {
        var result = await RunAsync(RetrievalCorpus.Load());

        var overall = RetrievalMetrics.Of(result.Outcomes);
        var unfloored = RetrievalMetrics.Of(InjectionFloorSweep.Apply(result, 0d));
        Assert.Equal(overall.RecallAt8, unfloored.RecallAt8);
        Assert.Equal(overall.FalsePositivesAt8, unfloored.FalsePositivesAt8);

        var golden = GoldenFile.Read(GoldenFileName).Split('\n');
        string Cells(string line) => string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var overallRow = Cells(golden.First(line => line.StartsWith("overall ", StringComparison.Ordinal))).Split(' ');
        var zeroRow = Cells(golden.First(line => line.StartsWith("0.0 ", StringComparison.Ordinal))).Split(' ');
        Assert.Equal((overallRow[5], overallRow[8]), (zeroRow[3], zeroRow[4]));
    }

    private static string FirstDifference(string golden, string rendered)
    {
        var expected = golden.Split('\n');
        var actual = rendered.Split('\n');
        for (var i = 0; i < Math.Max(expected.Length, actual.Length); i++)
        {
            var e = i < expected.Length ? expected[i] : "(end of file)";
            var a = i < actual.Length ? actual[i] : "(end of file)";
            if (e != a)
            {
                return $"First difference at line {i + 1}:\n  golden:   {e}\n  rendered: {a}";
            }
        }

        return "The lines agree; the files differ in their line endings or encoding.";
    }
}

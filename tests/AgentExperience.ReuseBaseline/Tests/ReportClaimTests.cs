using System.Text.RegularExpressions;
using AgentExperience.ReuseBaseline.Harness;

namespace AgentExperience.ReuseBaseline.Tests;

/// <summary>
/// What this report is allowed to say about itself.
/// </summary>
/// <remarks>
/// <para>
/// The 4.2 sample's <c>ImprovementClaimTests</c> can simply forbid every benefit word, because that
/// sample claims nothing. This report is different: it states a <em>gated</em> result, so the guard
/// has to be about qualification rather than silence. Three things are asserted. The report says, in
/// its own headline, that it measures the harness rather than a model. Every benefit verdict it
/// prints carries its qualification in the same line. And the comparative-quality vocabulary that
/// would turn a fixture's arithmetic into a finding does not appear at all.
/// </para>
/// <para>
/// A deny-list is a blunt instrument and it is not the real defence -- a rephrased claim walks past
/// any list of words. The real defence is
/// <see cref="GoldenReportTests.The_reference_experiment_renders_the_checked_in_report_byte_for_byte"/>:
/// a new sentence cannot reach the repository without appearing as a diff somebody had to accept.
/// </para>
/// </remarks>
public class ReportClaimTests
{
    /// <summary>Words that would turn this harness's arithmetic into a comparative-quality claim.</summary>
    private static readonly string[] QualityClaims =
    [
        "faster", "better", "improved accuracy", "improves accuracy", "outperform", "% improvement",
        "learns", "improves", "smarter", "speedup", "speed-up", "sooner", "reduces",
        "fewer tool calls", "in half", "more accurate", "higher quality", "twice as",
    ];

    /// <summary>Shapes of claim that no single word catches.</summary>
    private static readonly (string Name, Regex Pattern)[] QualityPatterns =
    [
        ("cut ... time", new Regex(@"\bcut\b[^.\n]{0,60}\btimes?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        ("N% fewer/less/faster", new Regex(@"\b\d+(\.\d+)?\s*%\s*(fewer|less|faster|better|more|improvement|reduction)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        ("N times faster/better", new Regex(@"\b\d+(\.\d+)?\s*x\s+(faster|better)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
    ];

    public static TheoryData<string> ScannedArms() => new("reference", "negative-control", "wrong-strategy", "faulted");

    [Theory]
    [MemberData(nameof(ScannedArms))]
    public async Task Nothing_the_report_says_about_itself_is_a_quality_claim(string arm)
    {
        var text = await ReportAsync(arm);

        Assert.True(text.Length > 2000, $"The {arm} report came back as {text.Length} characters, so the scan below proves nothing.");

        foreach (var claim in QualityClaims)
        {
            Assert.DoesNotContain(claim, text, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var (name, pattern) in QualityPatterns)
        {
            var match = pattern.Match(text);
            Assert.False(match.Success, $"The {arm} report contains a '{name}' claim: \"{match.Value}\".");
        }
    }

    [Theory]
    [MemberData(nameof(ScannedArms))]
    public async Task The_report_states_that_it_measures_the_harness_rather_than_a_model(string arm)
    {
        var text = await ReportAsync(arm);

        // Not enough to avoid the words: it has to say what it is.
        Assert.Contains(ReuseBaselineReport.MeasuresStatement, text, StringComparison.Ordinal);
        Assert.Contains("the harness, not a model", text, StringComparison.Ordinal);
        Assert.Contains("no model credential", text, StringComparison.Ordinal);
        Assert.Contains("every IChatClient in it is a fake", text, StringComparison.Ordinal);

        // And it names what a real result would need instead of leaving the gap implicit.
        Assert.Contains("What a real result would require", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ScannedArms))]
    public async Task Every_benefit_verdict_carries_its_qualification_in_the_same_line(string arm)
    {
        var text = await ReportAsync(arm);

        var unqualified = text
            .Split(ReuseBaselineReport.LineSeparator)
            .Where(line => line.Contains(nameof(GateVerdict.BenefitDemonstrated), StringComparison.Ordinal))
            .Where(line => !line.Contains(ReuseBaselineReport.VerdictQualification, StringComparison.Ordinal))
            .Where(line => !line.Contains(nameof(GateVerdict.NoDemonstratedBenefit), StringComparison.Ordinal))
            .ToList();

        Assert.True(unqualified.Count == 0, $"The {arm} report states a benefit without its qualification: \"{unqualified.FirstOrDefault()}\".");
    }

    [Fact]
    public async Task The_report_labels_the_primary_metric_numbers_as_fixture_determined()
    {
        var text = await ReportAsync("reference");

        Assert.Contains("fixture-determined", text, StringComparison.Ordinal);
        Assert.Contains("nothing about a model follows from", text, StringComparison.Ordinal);

        // The agent policy is printed rather than left in source, so the fixture is legible.
        Assert.Contains("AGENT POLICY", text, StringComparison.Ordinal);
        Assert.Contains("exploration order", text, StringComparison.Ordinal);

        // And the report says that the shipped library could not have carried the working approach on
        // its own, so a reader knows which part of the difference the harness itself supplied. Story
        // 4.6 gave the injected block an ordered tool-name approach line, so the report now has to say
        // why that still is not enough here -- every strategy is the same tool under a different
        // argument, and arguments are exactly what the block still never carries.
        var normalized = Normalize(text);
        Assert.Contains("DefaultExperienceReflector is domain-blind", normalized, StringComparison.Ordinal);
        Assert.Contains("ordered tool NAMES of a verified run's final attempt, but never a tool's arguments", normalized, StringComparison.Ordinal);
        Assert.Contains("the block's own approach line cannot tell the conditions apart here", normalized, StringComparison.Ordinal);
        Assert.Contains("nothing about a working approach would reach a later run", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_report_states_that_the_gate_has_no_fallback()
    {
        // Normalized, because the sentence is wrapped to a fixed width and a line break must not be
        // what decides whether the report said this.
        var text = Normalize(await ReportAsync("reference"));

        Assert.Contains("no second gate to fall back to", text, StringComparison.Ordinal);
        Assert.Contains("evaluated once", text, StringComparison.Ordinal);
        Assert.Contains("There is no code path that turns a failed gate into a passing one", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The report says which of its gate terms could not have failed in the arm it reports on.
    /// </summary>
    /// <remarks>
    /// Two of the three cannot fail in either pre-registered arm by construction: every evaluation
    /// task is resolvable inside the attempt limit, and nothing the reference reflector writes names
    /// the guarded tool. "All three terms held" without that disclosure reads as more than it is.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ScannedArms))]
    public async Task The_report_says_which_gate_terms_could_not_have_failed_in_this_arm(string arm)
    {
        var text = Normalize(await ReportAsync(arm));

        Assert.Contains("WHICH OF THOSE TERMS WAS LIVE IN THIS ARM", text, StringComparison.Ordinal);
        Assert.Contains("CANNOT FAIL HERE: verified_success_rate", text, StringComparison.Ordinal);
        Assert.Contains("CANNOT FAIL HERE: unauthorized_tool_executions", text, StringComparison.Ordinal);
        Assert.Contains("LIVE: failed_attempts", text, StringComparison.Ordinal);
    }

    /// <summary>The report says the 100% retrieval hit rate is designed in rather than observed.</summary>
    [Theory]
    [MemberData(nameof(ScannedArms))]
    public async Task The_report_says_the_retrieval_hit_rate_is_an_assumption_of_the_design(string arm)
    {
        var text = Normalize(await ReportAsync(arm));

        Assert.Contains("RETRIEVAL HIT RATE, AND WHY IT IS DESIGNED IN", text, StringComparison.Ordinal);
        Assert.Contains("A zero retrieval-miss rate here is an assumption of the design", text, StringComparison.Ordinal);
        Assert.Contains("by construction", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The report says the printed standard deviations are between-task variation in a deterministic
    /// fixture, not sampling variance.
    /// </summary>
    [Theory]
    [MemberData(nameof(ScannedArms))]
    public async Task The_report_says_the_printed_dispersion_is_not_sampling_variance(string arm)
    {
        var text = Normalize(await ReportAsync(arm));

        Assert.Contains("WHAT THE PRINTED sd IS AND IS NOT", text, StringComparison.Ordinal);
        Assert.Contains("This experiment is deterministic", text, StringComparison.Ordinal);
        Assert.Contains("are not sampling variance", text, StringComparison.Ordinal);
        Assert.Contains("between-task variation in a fixture", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The report prints both task texts, so a reader can judge the learning/evaluation separation
    /// rather than take the word "disjoint" on trust.
    /// </summary>
    [Theory]
    [MemberData(nameof(ScannedArms))]
    public async Task The_report_prints_every_task_text_and_the_worst_wording_overlap(string arm)
    {
        var result = arm switch
        {
            "reference" => await ExperimentFacts.ReferenceAsync(),
            "negative-control" => await ExperimentFacts.NegativeControlAsync(),
            "wrong-strategy" => await ExperimentFacts.WrongStrategyAsync(),
            _ => await ExperimentFacts.FaultedAsync(),
        };

        var text = Normalize(await ReportAsync(arm));

        foreach (var task in result.Arm.TaskSet.LearningTasks.Concat(result.Arm.TaskSet.EvaluationTasks))
        {
            Assert.Contains(task.Text, text, StringComparison.Ordinal);
        }

        Assert.Contains("SEPARATION.", text, StringComparison.Ordinal);
        Assert.Contains("The worst pair here is", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The report names every amendment the pre-registration declares, and says plainly that one was
    /// made after results existed.
    /// </summary>
    /// <remarks>
    /// The header prints a git blob identity, which invites a reader to believe the file was fixed
    /// before any result existed. For this file that is not true. The disclosure has to be on the
    /// page the claim is made on, and a future amendment must not be able to reach the repository
    /// without appearing there: this test is what stops it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ScannedArms))]
    public async Task The_report_names_every_amendment_the_preregistration_declares(string arm)
    {
        var text = Normalize(await ReportAsync(arm));
        var design = ExperimentFacts.Design();

        Assert.NotEmpty(design.Amendments);
        Assert.Contains(ReuseBaselineReport.AmendedStatement, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ReuseBaselineReport.NeverAmendedStatement, text, StringComparison.Ordinal);

        Assert.Contains(
            Normalize(design.Amendments.Count + " recorded, " + design.AmendmentsAfterResults + " of them made after results already existed."),
            text,
            StringComparison.Ordinal);

        foreach (var amendment in design.Amendments)
        {
            Assert.Contains(Normalize(amendment.Change), text, StringComparison.Ordinal);
            Assert.Contains(Normalize(amendment.Why), text, StringComparison.Ordinal);
            Assert.Contains(Normalize(amendment.ResultsChanged), text, StringComparison.Ordinal);
            Assert.Contains(
                amendment.ResultsExisted ? "MADE AFTER RESULTS EXISTED" : "made before any result existed",
                text,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// And an unamended pre-registration positively says so, rather than printing nothing and
    /// leaving "never amended" indistinguishable from "amendments not shown".
    /// </summary>
    [Fact]
    public async Task A_report_on_an_unamended_preregistration_says_so_in_those_words()
    {
        var text = Normalize(await ExperimentFacts.RenderWithNoAmendmentsAsync());

        Assert.Contains(ReuseBaselineReport.NeverAmendedStatement, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ReuseBaselineReport.AmendedStatement, text, StringComparison.Ordinal);
        Assert.DoesNotContain("MADE AFTER RESULTS EXISTED", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every dispersion figure in the per-condition table carries the marker that ties it to the
    /// paragraph saying what it is.
    /// </summary>
    [Theory]
    [MemberData(nameof(ScannedArms))]
    public async Task Every_printed_sd_carries_the_marker_that_says_it_is_not_sampling_variance(string arm)
    {
        var report = arm switch
        {
            "reference" => ReuseBaselineReport.RenderDeterministic(await ExperimentFacts.ReferenceAsync()),
            "negative-control" => ReuseBaselineReport.RenderDeterministic(await ExperimentFacts.NegativeControlAsync()),
            "wrong-strategy" => ReuseBaselineReport.RenderDeterministic(await ExperimentFacts.WrongStrategyAsync()),
            _ => ReuseBaselineReport.RenderDeterministic(await ExperimentFacts.FaultedAsync()),
        };

        // The metric rows themselves, not the paragraph that happens to talk about them.
        var sdLines = report
            .Split(ReuseBaselineReport.LineSeparator)
            .Where(line => line.Contains("n=", StringComparison.Ordinal) && line.Contains(" mean ", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(sdLines);
        Assert.All(sdLines, line => Assert.Contains("sd" + ReuseBaselineReport.DispersionMarker, line, StringComparison.Ordinal));

        // And the marker leads the paragraph it points at.
        Assert.Contains(
            "  " + ReuseBaselineReport.DispersionMarker + " WHAT THE PRINTED sd IS AND IS NOT",
            report,
            StringComparison.Ordinal);
    }

    /// <summary>Collapses the report's fixed-width wrapping so an assertion is about words, not line breaks.</summary>
    private static string Normalize(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

    private static async Task<string> ReportAsync(string arm) => arm switch
    {
        "reference" => ReuseBaselineReport.Render(await ExperimentFacts.ReferenceAsync()),
        "negative-control" => ReuseBaselineReport.Render(await ExperimentFacts.NegativeControlAsync()),
        "wrong-strategy" => ReuseBaselineReport.Render(await ExperimentFacts.WrongStrategyAsync()),
        "faulted" => ReuseBaselineReport.Render(await ExperimentFacts.FaultedAsync()),
        _ => throw new ArgumentOutOfRangeException(nameof(arm)),
    };
}

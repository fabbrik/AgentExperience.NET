using System.Globalization;
using System.Text;
using AgentExperience.Core.Holdout;

namespace AgentExperience.CiTriage;

/// <summary>What the report says about the run that produced it (no key, no path, no host).</summary>
/// <param name="Date">The day the pass ran.</param>
/// <param name="Model">The model, or the scripted stand-in.</param>
/// <param name="Storage">in-memory, or PostgreSQL.</param>
/// <param name="Scripted">Whether this was the offline stand-in over fake data.</param>
public sealed record ReportContext(DateOnly Date, string Model, string Storage, bool Scripted);

/// <summary>The dated Markdown report: the ledger by arm, and the library's own benefit estimate.</summary>
public static class Report
{
    public static string FileName(DateOnly date) => "ci-triage-" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".md";

    public static string Render(ReportContext context, ReplayResult result, IReadOnlyList<LedgerRow> ledger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(ledger);
        var b = new StringBuilder();
        var date = context.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        b.Append("# CI triage, ").Append(date).Append("\n\n");
        if (context.Scripted)
        {
            b.Append("**Scripted run.** A deterministic stand-in over built-in fake CI data: no network, no model. It shows the loop working, not whether lessons help.\n\n");
        }

        b.Append("- Model: `").Append(context.Model).Append("`\n");
        b.Append("- Storage: ").Append(context.Storage).Append('\n');
        b.Append("- Holdout rate: ").Append(Number(TriageHost.HoldoutRate)).Append(" (on runs that had a lesson to inject)\n");
        b.Append("- This pass: ").Append(result.ModelCalls.ToString(CultureInfo.InvariantCulture)).Append(" model calls, ")
            .Append((result.InputTokens + result.OutputTokens).ToString(CultureInfo.InvariantCulture)).Append(" tokens, about USD ")
            .Append(result.CostUsd.ToString("0.0000", CultureInfo.InvariantCulture)).Append('\n');
        if (result.StoppedReason is { } stopped)
        {
            b.Append("- **Stopped early:** ").Append(stopped).Append(" Failures after that point are in the ledger as `skipped: stopped`.\n");
        }

        b.Append("\n## Failures\n\n");
        b.Append("| Label | Failures | Triaged | Skipped |\n| --- | ---: | ---: | ---: |\n");
        foreach (var group in ledger.GroupBy(row => row.Label).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            b.Append("| ").Append(group.Key).Append(" | ").Append(group.Count().ToString(CultureInfo.InvariantCulture))
                .Append(" | ").Append(group.Count(row => !row.Skipped).ToString(CultureInfo.InvariantCulture))
                .Append(" | ").Append(group.Count(row => row.Skipped).ToString(CultureInfo.InvariantCulture)).Append(" |\n");
        }

        var skipReasons = ledger.Where(row => row.Skipped).GroupBy(row => row.Predicted).OrderBy(group => group.Key, StringComparer.Ordinal).ToList();
        if (skipReasons.Count > 0)
        {
            b.Append("\nSkipped: ").Append(string.Join(", ", skipReasons.Select(group => group.Count().ToString(CultureInfo.InvariantCulture) + " " + group.Key["skipped: ".Length..]))).Append(".\n");
        }

        b.Append("\n## Triage by arm (ledger)\n\n");
        b.Append("| Arm | Triaged | Correct |\n| --- | ---: | ---: |\n");
        foreach (var arm in new[] { "treatment", "holdout", "none" })
        {
            var rows = ledger.Where(row => row.Arm == arm).ToList();
            b.Append("| ").Append(arm).Append(" | ").Append(rows.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" | ").Append(rows.Count(row => row.Correct == "true").ToString(CultureInfo.InvariantCulture)).Append(" |\n");
        }

        b.Append("\n`treatment` runs were given the lessons that matched; `holdout` runs had them withheld; `none` had no lesson to give (the first failures, or no match), so the holdout did not assign them.\n");

        var o = result.Estimate.Overall;
        b.Append("\n## The library's estimate\n\n");
        b.Append("From `AgentExperienceBenefit.EstimateAsync` over the holdout's rows and each run's finalized outcome.\n\n");
        b.Append("| Arm | Runs | Successes | Rate |\n| --- | ---: | ---: | ---: |\n");
        Arm(b, "treatment", o.Treatment);
        Arm(b, "holdout", o.Holdout);
        b.Append("\n- Difference (treatment minus holdout): ").Append(o.Difference is { } d ? Signed(d) : "n/a")
            .Append(", 95% interval ").Append(o.Lower is { } lower && o.Upper is { } upper ? Signed(lower) + " to " + Signed(upper) : "n/a").Append('\n');
        b.Append("- Verdict: **").Append(o.Verdict.ToString()).Append("**\n");
        b.Append("- Pending: ").Append(o.Pending.ToString(CultureInfo.InvariantCulture))
            .Append(", Unverified: ").Append(o.Unverified.ToString(CultureInfo.InvariantCulture))
            .Append(", Mixed arm: ").Append(o.MixedArm.ToString(CultureInfo.InvariantCulture)).Append('\n');

        var minimum = result.Estimate.MinimumRunsPerArm;
        if (o.Verdict == ExperienceBenefitVerdict.NoEvidence)
        {
            b.Append('\n');
            if (o.Treatment.Runs < minimum || o.Holdout.Runs < minimum)
            {
                b.Append("The verdict is `NoEvidence` for lack of runs: each arm needs at least ")
                    .Append(minimum.ToString(CultureInfo.InvariantCulture)).Append(" runs with a verified outcome, and this has ")
                    .Append(o.Treatment.Runs.ToString(CultureInfo.InvariantCulture)).Append(" treatment and ")
                    .Append(o.Holdout.Runs.ToString(CultureInfo.InvariantCulture)).Append(" holdout. It says nothing yet about whether the lessons help.\n");
            }
            else
            {
                b.Append("The verdict is `NoEvidence`: the interval includes zero. That is not \"no effect\"; the data cannot support a claim either way.\n");
            }
        }

        b.Append("\n## What this can and cannot say\n\n");
        b.Append("- Correctness is scored against the repository's own later history: the files the fix commit changed (at most ten), or a green re-run of the same commit (flaky). A right answer that differs from the fix actually made counts as wrong.\n");
        b.Append("- Only failures whose fix was already known are handled, because a captured run lives only in memory and cannot be finalized after the process ends (story 26.2).\n");
        b.Append("- Arms are assigned only on runs that had a lesson to inject, so early failures are never compared.\n");
        b.Append("- One pass looks once: re-running until the verdict looks good would overstate it.\n");
        return b.ToString();
    }

    private static void Arm(StringBuilder b, string name, ExperienceBenefitArm arm) =>
        b.Append("| ").Append(name).Append(" | ").Append(arm.Runs.ToString(CultureInfo.InvariantCulture))
            .Append(" | ").Append(arm.Successes.ToString(CultureInfo.InvariantCulture))
            .Append(" | ").Append(arm.Rate is { } rate ? (rate * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : "n/a").Append(" |\n");

    private static string Signed(double value) => value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString("0.0#", CultureInfo.InvariantCulture);
}

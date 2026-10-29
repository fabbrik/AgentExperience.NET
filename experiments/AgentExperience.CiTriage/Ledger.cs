using System.Globalization;
using System.Text;

namespace AgentExperience.CiTriage;

/// <summary>
/// One ledger row: a failed CI run, its label, the holdout arm its triage ran in, what the agent predicted, whether it
/// was right, and what it cost. Skipped failures have <c>-</c> for arm and correct and say why in <see cref="Predicted"/>.
/// </summary>
/// <param name="RunId">The GitHub run id.</param>
/// <param name="Time">When the failing run completed.</param>
/// <param name="Branch">Its branch.</param>
/// <param name="Label">code, flaky, ambiguous, pending or unavailable.</param>
/// <param name="Arm">treatment (lessons given), holdout (lessons withheld), none (no lesson matched), or <c>-</c>.</param>
/// <param name="Predicted">flaky, code:a,b, unparsed, or skipped: reason.</param>
/// <param name="Correct">true, false, or <c>-</c>.</param>
/// <param name="CostUsd">Estimated model spend of this triage.</param>
/// <param name="Note">What finalization did, or why something went wrong.</param>
public sealed record LedgerRow(long RunId, DateTimeOffset Time, string Branch, string Label, string Arm, string Predicted, string Correct, decimal CostUsd, string Note)
{
    public const string Header = "run_id\ttime\tbranch\tlabel\tarm\tpredicted\tcorrect\tcost_usd\tnote";

    public bool Skipped => Predicted.StartsWith("skipped:", StringComparison.Ordinal);

    /// <summary>A failure a later pass should not process again: anything but one still waiting for its fix or its log.</summary>
    public bool Final => Label is not ("pending" or "unavailable");

    public string ToTsv() => string.Join('\t',
        RunId.ToString(CultureInfo.InvariantCulture),
        Time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        Clean(Branch),
        Label,
        Arm,
        Clean(Predicted),
        Correct,
        CostUsd.ToString("0.000000", CultureInfo.InvariantCulture),
        Clean(Note));

    public static LedgerRow Parse(string line)
    {
        var fields = line.Split('\t');
        if (fields.Length != 9)
        {
            throw new FormatException($"A ledger row has {fields.Length} fields, not 9.");
        }

        return new LedgerRow(
            long.Parse(fields[0], CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(fields[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            fields[2],
            fields[3],
            fields[4],
            fields[5],
            fields[6],
            decimal.Parse(fields[7], CultureInfo.InvariantCulture),
            fields[8]);
    }

    private static string Clean(string value) => value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}

/// <summary>The TSV ledger: rewritten whole after an in-memory replay, appended to across PostgreSQL passes.</summary>
public static class Ledger
{
    public const string FileName = "ci-triage-ledger.tsv";

    public static IReadOnlyList<LedgerRow> Read(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        return [.. File.ReadAllLines(path).Skip(1).Where(line => line.Length > 0).Select(LedgerRow.Parse)];
    }

    public static void Write(string path, IEnumerable<LedgerRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var builder = new StringBuilder();
        builder.Append(LedgerRow.Header).Append('\n');
        foreach (var row in rows.OrderBy(row => row.Time).ThenBy(row => row.RunId))
        {
            builder.Append(row.ToTsv()).Append('\n');
        }

        File.WriteAllText(path, builder.ToString());
    }
}

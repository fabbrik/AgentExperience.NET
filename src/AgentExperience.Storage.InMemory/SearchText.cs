using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using AgentExperience.Abstractions;

namespace AgentExperience.Storage.InMemory;

/// <summary>
/// The words of one record's searchable text, taken once when the record is created: a record's task ID, task summary
/// and lesson never change afterwards (a commit moves only its status, revision, counters and <c>UpdatedAt</c>).
/// </summary>
/// <param name="TaskId">The words of <see cref="ExperienceRecord.TaskId"/>.</param>
/// <param name="Summary">The words of <see cref="ExperienceRecord.TaskSummary"/>.</param>
/// <param name="Lesson">The words of the reflection's lesson.</param>
internal sealed record RecordSearchText(FrozenSet<string> TaskId, FrozenSet<string> Summary, FrozenSet<string> Lesson)
{
    /// <summary>Whether <paramref name="term"/> is a word of any of the three fields.</summary>
    public bool Contains(string term) => Summary.Contains(term) || TaskId.Contains(term) || Lesson.Contains(term);
}

/// <summary>
/// How the in-memory candidate source reads text: Unicode-normalized, case-folded words, with the common English
/// stopwords dropped from a query.
/// </summary>
internal static class SearchText
{
    /// <summary>
    /// The most text indexed per record, across its task ID, task summary and lesson in that order: the same bound
    /// the PostgreSQL store's search column applies (<c>left(..., 100000)</c> in migration 0003).
    /// </summary>
    public const int MaxIndexedLength = 100_000;

    /// <summary>
    /// The English stopwords a query ignores: the Snowball list PostgreSQL's <c>english</c> text-search configuration
    /// drops, so a query term that PostgreSQL would never match on is not one this store matches on either.
    /// </summary>
    private static readonly FrozenSet<string> Stopwords = new[]
    {
        "i", "me", "my", "myself", "we", "our", "ours", "ourselves", "you", "your", "yours", "yourself", "yourselves",
        "he", "him", "his", "himself", "she", "her", "hers", "herself", "it", "its", "itself", "they", "them", "their",
        "theirs", "themselves", "what", "which", "who", "whom", "this", "that", "these", "those", "am", "is", "are",
        "was", "were", "be", "been", "being", "have", "has", "had", "having", "do", "does", "did", "doing", "a", "an",
        "the", "and", "but", "if", "or", "because", "as", "until", "while", "of", "at", "by", "for", "with", "about",
        "against", "between", "into", "through", "during", "before", "after", "above", "below", "to", "from", "up",
        "down", "in", "out", "on", "off", "over", "under", "again", "further", "then", "once", "here", "there", "when",
        "where", "why", "how", "all", "any", "both", "each", "few", "more", "most", "other", "some", "such", "no",
        "nor", "not", "only", "own", "same", "so", "than", "too", "very", "s", "t", "can", "will", "just", "don",
        "should", "now",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The words of <paramref name="record"/>'s searchable text, within <see cref="MaxIndexedLength"/>.</summary>
    public static RecordSearchText Index(ExperienceRecord record)
    {
        var budget = MaxIndexedLength;
        return new RecordSearchText(
            Take(record.TaskId, ref budget),
            Take(record.TaskSummary, ref budget),
            Take(record.Reflection?.Lesson, ref budget));

        static FrozenSet<string> Take(string? text, ref int budget)
        {
            if (string.IsNullOrEmpty(text) || budget <= 0)
            {
                // The separator between fields counts towards the bound too, as it does in PostgreSQL's concatenation.
                budget--;
                return FrozenSet<string>.Empty;
            }

            var taken = text.Length <= budget ? text : text[..budget];
            budget -= taken.Length + 1;
            return Words(taken).ToFrozenSet(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// The distinct terms a query searches for: its words, less stopwords and terms shorter than two characters. Empty
    /// when nothing is left, and then nothing matches.
    /// </summary>
    public static IReadOnlyList<string> QueryTerms(string text) =>
        [.. Words(text).Where(word => word.EnumerateRunes().Count() >= 2 && !Stopwords.Contains(word)).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// The words of <paramref name="text"/>: normalized to Unicode form KC and lower-cased (invariant culture), then
    /// split into runs of letters and digits. A combining mark continues the word it follows.
    /// </summary>
    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var rune in Normalize(text).ToLowerInvariant().EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune)
                || (word.Length > 0 && Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark))
            {
                word.Append(rune.ToString());
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }

        return words;
    }

    private static string Normalize(string text)
    {
        try
        {
            return text.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            // Not valid UTF-16 (a lone surrogate). Enumerating runes reads it as U+FFFD, which is no letter or digit.
            return text;
        }
    }
}

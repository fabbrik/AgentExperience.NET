namespace AgentExperience.Tests.Shared;

/// <summary>
/// The repository README's first screen: the lines a reader sees before scrolling, which must hold what the library
/// is, the quick-start setup, a shortened block the agent sees, the measured result and the comparison link. Linked
/// into the MAF adapter tests (which pin the setup and the shortened block) and the end-to-end sample tests (which pin
/// the whole block, below the first screen).
/// </summary>
internal static class ReadmeFirstScreen
{
    /// <summary>How many README lines the first screen is.</summary>
    public const int Lines = 60;

    /// <summary>The line that stands for one or more lines left out of an excerpt.</summary>
    public const string Elision = "…";

    /// <summary>The README split into lines, with line endings normalized.</summary>
    public static string[] Split(string readme) => readme.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    /// <summary>The 1-based number of the first README line that satisfies <paramref name="match"/>, or 0.</summary>
    public static int LineOf(string[] readme, Func<string, bool> match) => Array.FindIndex(readme, line => match(line)) + 1;

    /// <summary>
    /// Asserts that <paramref name="excerpt"/> is cut from <paramref name="source"/>: it starts with the source's first
    /// line and ends with its last, every other line is a source line in order, and each run of left-out lines is
    /// exactly one <see cref="Elision"/> line (never two in a row, never none).
    /// </summary>
    public static void AssertExcerptOf(IReadOnlyList<string> excerpt, IReadOnlyList<string> source)
    {
        Assert.DoesNotContain(Elision, source);
        Assert.Contains(Elision, excerpt);
        Assert.Equal(source[0], excerpt[0]);
        Assert.Equal(source[^1], excerpt[^1]);

        var cursor = 0;
        var skipping = false;
        foreach (var line in excerpt)
        {
            if (line == Elision)
            {
                Assert.False(skipping, $"Two {Elision} lines in a row.");
                skipping = true;
                continue;
            }

            if (skipping)
            {
                // An elision leaves out at least one line, so the next kept line comes after the one at the cursor.
                var found = IndexOf(source, line, cursor + 1);
                Assert.True(found > cursor, $"The excerpt line \"{line}\" is not in the source after its line {cursor + 1}.");
                cursor = found + 1;
            }
            else
            {
                Assert.True(cursor < source.Count && source[cursor] == line, $"The excerpt line \"{line}\" is not the source's next line ({cursor + 1}), and no {Elision} line says so.");
                cursor++;
            }

            skipping = false;
        }

        Assert.Equal(source.Count, cursor);
    }

    private static int IndexOf(IReadOnlyList<string> lines, string value, int start)
    {
        for (var i = start; i < lines.Count; i++)
        {
            if (lines[i] == value)
            {
                return i;
            }
        }

        return -1;
    }
}

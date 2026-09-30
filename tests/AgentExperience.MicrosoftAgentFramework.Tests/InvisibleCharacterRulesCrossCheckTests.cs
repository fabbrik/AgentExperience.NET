using System.Reflection;
using System.Text;
using AgentExperience.Core.Reflections;
using AgentExperience.MicrosoftAgentFramework.Injection;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// Story 14.1 reimplements story 8.2's invisible-character rule in Core, for screening reflections,
/// because Core grants its internals to no other assembly. This holds the two copies to one rule, code
/// point by code point: the Historical Reference writer's removal mode (what it checks a tool name's
/// markers against) and Core's reflection neutralization must agree on every Unicode scalar value and
/// on a lone surrogate of either kind.
/// </summary>
/// <remarks>
/// Both routines are private to their assemblies, so they are reached by reflection. If either is
/// renamed or its signature changes, this test fails loudly rather than silently checking nothing.
/// </remarks>
public class InvisibleCharacterRulesCrossCheckTests
{
    private static readonly Func<string, string> Writer = WriterRemoval();
    private static readonly Func<string, string> Screening = ScreeningNeutralize();

    [Fact]
    public void Reflection_screening_and_the_Historical_Reference_writer_classify_every_code_point_alike()
    {
        const int Chunk = 4_096;
        var builder = new StringBuilder(Chunk * 4);

        for (var start = 0; start <= 0x10FFFF; start += Chunk)
        {
            builder.Clear();
            for (var value = start; value < Math.Min(start + Chunk, 0x110000); value++)
            {
                if (value is >= 0xD800 and <= 0xDFFF)
                {
                    continue;
                }

                // Each scalar between two letters, so a removal and a space are told apart.
                builder.Append('a').Append(char.ConvertFromUtf32(value)).Append('b');
            }

            var text = builder.ToString();
            Assert.Equal(Writer(text), Screening(text));
        }
    }

    [Fact]
    public void Surrogates_whitespace_separators_ignorables_and_mark_runs_are_handled_alike()
    {
        // Inline, not [InlineData]: a test framework may re-encode an ill-formed string it serializes.
        string[] cases =
        [
            "a\uD800b",
            "a\uDC00b",
            "\uDBFF",
            "x\uDC00\uD800y",
            "tab\there, nl\nthere, nbsp\u00A0there, zwsp\u200Bthere, rlo\u202Ethere, ls\u2028there, ps\u2029there",
            "vs\uFE0Fx \U000E0100y cgj\u034Fz fillers\u115F\u1160\u3164\uFFA0 braille\u2800",
            "e\u0301\u0301\u0301\u0301\u0301\u0301\u0301",
            "e\u0301\u0301\u200B\u0301\u0301\u0301 f\u20DD\u20DD\u20DD\u20DD\u20DD \u0903\u0903\u0903\u0903\u0903",
            "\u0301\u0301\u0301\u0301\u0301 leading marks",
            "e\u0301\u0301\u0301\u0301 e\u0301\u0301\u0301\u0301",
        ];

        foreach (var text in cases)
        {
            Assert.Equal(Writer(text), Screening(text));
        }
    }

    [Fact]
    public void A_run_of_combining_marks_is_cut_to_four_by_both()
    {
        var text = "e" + new string('\u0301', 9);

        Assert.Equal("e" + new string('\u0301', 4), Screening(text));
        Assert.Equal(Screening(text), Writer(text));
    }

    [Fact]
    public void A_string_with_nothing_to_remove_comes_back_as_the_same_instance_from_both()
    {
        var text = new string("plain text, café, 👍".ToCharArray());

        Assert.Same(text, Writer(text));
        Assert.Same(text, Screening(text));
    }

    private static Func<string, string> WriterRemoval()
    {
        var method = typeof(HistoricalReferenceWriter).GetMethod(
            "Visible",
            BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(string), typeof(bool), typeof(bool)])
            ?? throw new InvalidOperationException("HistoricalReferenceWriter.Visible(string, bool, bool) was not found.");

        var visible = method.CreateDelegate<Func<string, bool, bool, string>>();
        return text => visible(text, false, true);
    }

    private static Func<string, string> ScreeningNeutralize()
    {
        var method = typeof(ReflectionScreening).GetMethod(
            "Neutralize",
            BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(string)])
            ?? throw new InvalidOperationException("ReflectionScreening.Neutralize(string) was not found.");

        return method.CreateDelegate<Func<string, string>>();
    }
}

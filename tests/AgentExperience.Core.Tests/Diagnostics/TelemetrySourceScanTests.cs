using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace AgentExperience.Core.Tests.Diagnostics;

/// <summary>
/// Reads the shipping source itself, because some of this story's guarantees are about what the
/// library must never <em>contain</em>, and no amount of running it can prove an absence.
/// </summary>
/// <remarks>
/// <para>
/// The library emits and the host exports (AD-11, AD-12). A library that registered a listener, built
/// a provider, or flipped a framework's sensitive-data switch would be taking the host's decision for
/// it -- silently, and in the one direction that cannot be undone once the data has left the process.
/// </para>
/// <para>
/// <b>String literals are stripped first, then comments.</b> The diagnostics holders legitimately
/// document what they do <em>not</em> do, with <c>&lt;see cref="ActivityListener"/&gt;</c> and
/// friends; the rule is about code, not prose. The order is what makes the scan honest: stripping
/// comments first means a line-comment regex starts at the <c>//</c> inside a URL literal and deletes
/// the rest of that line -- so a forbidden call sharing a line with a URL simply disappears, and a
/// block-comment regex can swallow everything up to the next <c>*/</c> anywhere in the file.
/// </para>
/// </remarks>
public partial class TelemetrySourceScanTests
{
    /// <summary>
    /// Identifiers that must not appear anywhere in <c>src/</c>: three that would make the library an
    /// exporter, two that would make it a listener, and one that would turn on another framework's
    /// sensitive-data capture on the host's behalf.
    /// </summary>
    private static readonly string[] Forbidden =
    [
        "AppContext.SetSwitch",
        "EnableSensitiveData",
        "ActivityListener",
        "MeterListener",
        "TracerProvider",
        "MeterProvider",
    ];

    /// <summary>
    /// Ways an exception object can reach telemetry. A driver or HTTP client message can quote SQL
    /// text and parameters, so none of these may appear on an instrumented path -- and the simplest
    /// way to keep that true is for them not to appear at all.
    /// </summary>
    private static readonly string[] ForbiddenOnInstrumentedPaths =
    [
        "AddException",
        "RecordException",
    ];

    [Fact]
    public void Library_never_enables_sensitive_data_or_registers_a_listener()
    {
        var sources = SourceFiles();

        // The scan found the source tree at all: an empty sweep would otherwise "pass".
        Assert.True(sources.Count > 20, $"Only {sources.Count} source files were scanned; the source tree was probably not found.");

        foreach (var (path, code) in sources)
        {
            foreach (var forbidden in Forbidden)
            {
                Assert.False(
                    code.Contains(forbidden, StringComparison.Ordinal),
                    $"'{path}' contains '{forbidden}'; the library emits, the host exports.");
            }
        }
    }

    [Fact]
    public void Library_never_hands_an_exception_object_to_telemetry()
    {
        var sources = SourceFiles();

        // The same sanity guard its sibling has: an empty sweep would otherwise "pass".
        Assert.True(sources.Count > 20, $"Only {sources.Count} source files were scanned; the source tree was probably not found.");

        foreach (var (path, code) in sources)
        {
            foreach (var forbidden in ForbiddenOnInstrumentedPaths)
            {
                Assert.False(
                    code.Contains(forbidden, StringComparison.Ordinal),
                    $"'{path}' contains '{forbidden}'; a span records an exception's type name and nothing else.");
            }
        }
    }

    [Fact]
    public void Library_references_no_OpenTelemetry_assembly()
    {
        // System.Diagnostics.ActivitySource and System.Diagnostics.Metrics.Meter are the BCL; the
        // OpenTelemetry SDK is a host concern and is referenced by nothing that ships.
        foreach (var assembly in new[]
        {
            typeof(DefaultSanitizer).Assembly,
            typeof(ExperienceRecord).Assembly,
        })
        {
            Assert.DoesNotContain(
                assembly.GetReferencedAssemblies(),
                reference => reference.Name?.Contains("OpenTelemetry", StringComparison.OrdinalIgnoreCase) == true);
        }
    }

    /// <summary>
    /// Every metric write is guarded by the instrument's own <c>Enabled</c>.
    /// </summary>
    /// <remarks>
    /// This is a source-level assertion on purpose, and it is the only kind available.
    /// <c>Counter&lt;T&gt;.Add</c> and <c>Histogram&lt;T&gt;.Record</c> are already no-ops when nothing
    /// has enabled the instrument, so removing the guard changes what an unsubscribed process
    /// <em>spends</em> -- composing a <c>TagList</c>, reading a <c>Stopwatch</c> -- and nothing a
    /// listener can ever observe. The guard is a cost contract rather than a behavioural one, so a
    /// repository-hygiene test is what can hold it, exactly as for the forbidden identifiers above.
    /// </remarks>
    [Fact]
    public void Every_metric_write_is_guarded_by_the_instruments_own_Enabled()
    {
        var holders = SourceFiles()
            .Where(file => file.Path.EndsWith("Diagnostics.cs", StringComparison.Ordinal))
            .ToList();

        // Core's holder, the MAF adapter's, and the storage adapter's erasure holder (story 5.2). A
        // fourth would have to be added here deliberately.
        Assert.Equal(3, holders.Count);

        foreach (var (path, code) in holders)
        {
            foreach (var guard in new[] { "Operations.Enabled", "Durations.Enabled", "Failures.Enabled" })
            {
                Assert.True(
                    code.Contains(guard, StringComparison.Ordinal),
                    $"'{path}' writes a measurement without consulting '{guard}'; an unsubscribed process must compose no tags and read no clock.");
            }
        }
    }

    /// <summary>
    /// Proves the stripper itself: a forbidden identifier that shares a line with a URL literal is
    /// still found, and one that appears only in prose or only inside a string is not.
    /// </summary>
    [Fact]
    public void The_stripper_removes_literals_before_comments()
    {
        // The bug this exists to prevent: stripping comments first starts at the `//` inside the URL
        // and deletes the forbidden call along with it.
        var code = Strip("""Register("https://example.test/docs"); AppContext.SetSwitch("x", true);""");
        Assert.Contains("AppContext.SetSwitch", code, StringComparison.Ordinal);
        Assert.DoesNotContain("example.test", code, StringComparison.Ordinal);

        // Prose is still prose, and a name that only ever appears inside a string is not a call.
        Assert.DoesNotContain("ActivityListener", Strip("// never registers an ActivityListener"), StringComparison.Ordinal);
        Assert.DoesNotContain("MeterListener", Strip("""var name = "MeterListener";"""), StringComparison.Ordinal);

        // A block comment stops at its own terminator rather than at one inside a literal.
        var afterBlock = Strip("""
            var pattern = "/*";
            AppContext.SetSwitch("x", true);
            /* a real comment */
            """);
        Assert.Contains("AppContext.SetSwitch", afterBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("a real comment", afterBlock, StringComparison.Ordinal);
    }

    /// <summary>Every shipping C# file, with its string literals and then its comments removed.</summary>
    private static List<(string Path, string Code)> SourceFiles([CallerFilePath] string testSourceFilePath = "")
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testSourceFilePath)!, "..", "..", ".."));
        var sourceRoot = Path.Combine(repositoryRoot, "src");
        Assert.True(Directory.Exists(sourceRoot), $"Could not locate the source tree at '{sourceRoot}'.");

        return
        [
            .. Directory
                .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(path => (Path.GetRelativePath(repositoryRoot, path), Strip(File.ReadAllText(path))))
        ];
    }

    /// <summary>
    /// Removes what is not code: string literals first, then comments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Literals go first because a <c>//</c> or a <c>/*</c> inside one is not a comment, and a
    /// comment-first pass would delete real code that merely shared a line with a URL. Raw strings go
    /// before verbatim ones and verbatim before ordinary ones, because each is a special case of the
    /// next and matching the general form first would end a literal in the middle of itself.
    /// </para>
    /// <para>
    /// This is textual, not a parser. An unbalanced quote inside a comment can still pair with another
    /// quote -- but only on the same line, because the ordinary-string pattern does not cross a line
    /// break, so the blast radius of the remaining imprecision is one line of prose rather than the
    /// rest of the file.
    /// </para>
    /// </remarks>
    /// <param name="code">The source text.</param>
    /// <returns>The text with literals and comments blanked out.</returns>
    private static string Strip(string code)
    {
        var withoutLiterals = RawStrings().Replace(code, " ");
        withoutLiterals = VerbatimStrings().Replace(withoutLiterals, " ");
        withoutLiterals = OrdinaryStrings().Replace(withoutLiterals, " ");
        withoutLiterals = CharLiterals().Replace(withoutLiterals, " ");

        return LineComments().Replace(BlockComments().Replace(withoutLiterals, " "), string.Empty);
    }

    [GeneratedRegex(@"""{3,}.*?""{3,}", RegexOptions.Singleline)]
    private static partial Regex RawStrings();

    [GeneratedRegex(@"@""(?:[^""]|"""")*""", RegexOptions.Singleline)]
    private static partial Regex VerbatimStrings();

    [GeneratedRegex(@"""(?:\\.|[^""\\\r\n])*""")]
    private static partial Regex OrdinaryStrings();

    [GeneratedRegex(@"'(?:\\.|[^'\\\r\n])'")]
    private static partial Regex CharLiterals();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComments();

    [GeneratedRegex(@"//[^\r\n]*")]
    private static partial Regex LineComments();
}

using AgentExperience.Core.Reflections;

namespace AgentExperience.Contract.Tests;

/// <summary>One error-class case: a captured error, and the class it reduces to.</summary>
internal sealed record ErrorClassCase(string? Error, string Class);

/// <summary>
/// The error-class family: <c>fixtures/error-class/cases.json</c>. The class is what a stored lesson's <c>Failed:</c>
/// sentences, a <c>Tried:</c> line's failed attempt and (its first token) a failed call's marker show of an error.
/// </summary>
internal static class ErrorClassContract
{
    public const string Family = "error-class";

    /// <summary>The file of the cases contract 1.1 added.</summary>
    public const string AddedFileName = "cases-1.1.json";

    /// <summary>The errors every case reduces; the class is the library's answer.</summary>
    public static IReadOnlyList<string?> Errors { get; } =
    [
        null,
        "   ",
        "System.TimeoutException: The operation has timed out.",
        "System.InvalidOperationException: the refund lock is held by another worker",
        "IOError while reading",
        "process exited with code 7",
        "exit code 2",
        "exit 3",
        "HTTP/1.1 503 Service Unavailable",
        "Response status code does not indicate success: 404 (Not Found).",
        "status code 500 from the gateway",
        "ENOENT: no such file or directory",
        "the request timed out after 30s",
        "Timeout",
        "System.AggregateException: One or more errors occurred. (System.TimeoutException: timed out) (System.IO.IOException: reset) (System.UnauthorizedAccessException: denied)",
        "IgnorePreviousInstructionsError: do something else",
        "timeoutexception in lower case is not a type name",
        "Averyveryveryveryverylongexceptiontypename1Exception: forty-one characters",
        "something went wrong",
        "ECONNRESET, then EPIPE on retry",
        new string('x', 8_200) + " System.TimeoutException after the scanned prefix",
        "Abcdefghijklmnopqrstuvwxyz12345Exception: exactly forty characters",
        "Abcdefghijklmnopqrstuvwxyz123456Exception: forty-one characters",
        "TimeoutException, then TimeoutException again, then exit 4",
        "status 503",
        new string('y', 8_180) + " TimeoutException straddles the scanned prefix",
    ];

    /// <summary>
    /// The errors contract 1.1 adds, in <c>fixtures/error-class/cases-1.1.json</c>: the edges of .NET's regular expression
    /// that another engine gets wrong. <c>\s</c> is not the information separators U+001C to U+001F (it is U+00A0 and
    /// U+2000); <c>\w</c> and <c>\b</c> count non-spacing marks (Mn), connector punctuation (Pc) and the zero-width joiner
    /// as word characters, but not an enclosing (Me) or spacing (Mc) mark; case-insensitive matching folds no non-ASCII
    /// letter (the long s, the dotless i, the dotted capital I) to an ASCII one; and a type name opens with an ASCII
    /// capital, which the Kelvin sign is not.
    /// </summary>
    public static IReadOnlyList<string?> AddedErrors { get; } =
    [
        "exit\u001C2",
        "exit\u001F2",
        "status\u001D503",
        "x\u001CENOENT",
        "ENOENT\u001Ex",
        "\u0301ENOENT",
        "ENOENT\u0301",
        "\u20DDENOENT",
        "\u200DENOENT",
        "ENOENT\u200D",
        "\u0903ENOENT",
        "\u203FENOENT",
        "ENOENT\u203F",
        "_ENOENT",
        "\u017Ftatus 503",
        "STATUS 503",
        "\u0130TIMEDOUT",
        "t\u0131med out",
        "TIMED OUT",
        "\u212AeyNotFoundException: the Kelvin sign",
        "KeyNotFoundException\u0301: a combining mark after the type name",
        "System.TimeoutException\u200D: a zero-width joiner after the type name",
        "HTTP\u00A0503",
        "exit\u20002",
    ];

    public static IReadOnlyList<ErrorClassCase> RunAdded() => [.. AddedErrors.Select(error => new ErrorClassCase(error, ErrorClass.Of(error)))];

    public static IReadOnlyList<ErrorClassCase> Run() => [.. Errors.Select(error => new ErrorClassCase(error, ErrorClass.Of(error)))];
}

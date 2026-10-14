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
    ];

    public static IReadOnlyList<ErrorClassCase> Run() => [.. Errors.Select(error => new ErrorClassCase(error, ErrorClass.Of(error)))];
}

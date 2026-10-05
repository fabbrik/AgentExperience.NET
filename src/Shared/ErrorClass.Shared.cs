using System.Collections.Frozen;
using System.Text.RegularExpressions;

// Shared source (story 18.1): the one deterministic rule that reduces a captured error to a short class. Core's
// DefaultExperienceReflector (the lesson's "Failed:" sentences) and the MAF adapter's HistoricalReferenceWriter (the
// "Tried:" lines) both link this file, so the stored lesson and the injected block name a failure the same way. It
// depends on nothing but the BCL. Each project defines exactly one of the constants below, which puts the type in
// that project's own namespace.
#if AGENTEXPERIENCE_CORE && AGENTEXPERIENCE_MAF
#error Define exactly one of AGENTEXPERIENCE_CORE and AGENTEXPERIENCE_MAF.
#elif AGENTEXPERIENCE_CORE
namespace AgentExperience.Core.Reflections;
#elif AGENTEXPERIENCE_MAF
namespace AgentExperience.MicrosoftAgentFramework.Reflections;
#else
#error Define AGENTEXPERIENCE_CORE or AGENTEXPERIENCE_MAF to select the namespace of the shared error classifier.
#endif

/// <summary>
/// The error class of a captured error: a short text built only from tokens this type recognises, never from any other
/// part of the error. The tokens are .NET-style exception type names, copied from the error as written when they are
/// a capitalised word of letters and digits ending in <c>Exception</c> or <c>Error</c> of at most
/// <see cref="MaxTokenLength"/> characters (<c>TimeoutException</c> out of <c>System.TimeoutException</c>,
/// <c>IOError</c>); exit codes (<c>exit 2</c>, from <c>exit 2</c>, <c>exit code 2</c>, <c>exited 2</c> or
/// <c>exited with code 2</c>); HTTP statuses (<c>HTTP 503</c>, from <c>HTTP 503</c>, <c>HTTP/1.1 503</c> or .NET's
/// <c>Response status code does not indicate success: 503</c>; <c>status 503</c> from <c>status 503</c> or
/// <c>status code 503</c>); POSIX errno names (<c>ENOENT</c>, from a fixed list); and timeouts (<c>Timeout</c>, from
/// <c>timeout</c> or <c>timed out</c>, left out when a timeout exception type is already among the tokens). At most <see cref="MaxTokens"/> distinct tokens, each at most
/// <see cref="MaxTokenLength"/> characters, in the order they first appear, joined with <c>", "</c>;
/// <see cref="Unclassified"/> when none is found.
/// </summary>
/// <remarks>
/// Every token is ASCII letters, digits, a space or a minus sign, so a class can carry neither a line break, nor a
/// field label (no colon), nor a block marker. A hostile error can still contribute an exception-shaped word of its
/// own choosing (<c>IgnorePreviousInstructionsError</c>); that is bounded, not prevented.
/// </remarks>
internal static class ErrorClass
{
    /// <summary>The class of an error in which no token is recognised.</summary>
    public const string Unclassified = "unclassified error";

    /// <summary>The most tokens one class carries.</summary>
    public const int MaxTokens = 3;

    /// <summary>
    /// The most characters one token may have. An exception type name is the one token copied from the error as
    /// written, so a longer name is not a token.
    /// </summary>
    public const int MaxTokenLength = 40;

    /// <summary>The most characters of an error that are scanned; a token after them is not seen.</summary>
    public const int MaxScannedLength = 8192;

    private const string Separator = ", ";

    private const string TimeoutToken = "Timeout";

    // NonBacktracking: matching is linear in the (capped) input, so classification needs no timeout and always
    // finishes with the same answer.
    private static readonly Regex Tokens = new(
        @"(?<type>\b[A-Z][A-Za-z0-9]{0,38}(?:Exception|Error)\b)"
        + @"|(?<exit>\b(?i:exit(?:ed)?)(?:\s+(?i:with))?(?:\s+(?i:code))?\s*[:=]?\s*(?<exitcode>-?[0-9]{1,6})\b)"
        + @"|(?<http>\b(?i:http)(?:/[0-9](?:\.[0-9])?)?\s*(?<httpcode>[1-5][0-9]{2})\b)"
        + @"|(?<dotnet>(?i:status\s+code\s+does\s+not\s+indicate\s+success)\s*:\s*(?<dotnetcode>[1-5][0-9]{2})\b)"
        + @"|(?<status>\b(?i:status)(?:\s+(?i:code))?\s*[:=]?\s*(?<statuscode>[1-5][0-9]{2})\b)"
        + @"|(?<errno>\bE[A-Z0-9]{2,15}\b)"
        + @"|(?<timeout>\b(?i:time[ -]?out|timed\s+out)\b)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking);

    /// <summary>The POSIX errno names recognised as tokens. Any other capitalised word starting with E is not one.</summary>
    private static readonly FrozenSet<string> ErrnoNames = new[]
    {
        "E2BIG", "EACCES", "EADDRINUSE", "EADDRNOTAVAIL", "EAFNOSUPPORT", "EAGAIN", "EALREADY", "EBADF", "EBADMSG",
        "EBUSY", "ECANCELED", "ECHILD", "ECONNABORTED", "ECONNREFUSED", "ECONNRESET", "EDEADLK", "EDESTADDRREQ", "EDOM",
        "EDQUOT", "EEXIST", "EFAULT", "EFBIG", "EHOSTUNREACH", "EIDRM", "EILSEQ", "EINPROGRESS", "EINTR", "EINVAL",
        "EIO", "EISCONN", "EISDIR", "ELOOP", "EMFILE", "EMLINK", "EMSGSIZE", "EMULTIHOP", "ENAMETOOLONG", "ENETDOWN",
        "ENETRESET", "ENETUNREACH", "ENFILE", "ENOBUFS", "ENODATA", "ENODEV", "ENOENT", "ENOEXEC", "ENOLCK", "ENOLINK",
        "ENOMEM", "ENOMSG", "ENOPROTOOPT", "ENOSPC", "ENOSR", "ENOSTR", "ENOSYS", "ENOTCONN", "ENOTDIR", "ENOTEMPTY",
        "ENOTRECOVERABLE", "ENOTSOCK", "ENOTSUP", "ENOTTY", "ENXIO", "EOPNOTSUPP", "EOVERFLOW", "EOWNERDEAD", "EPERM",
        "EPIPE", "EPROTO", "EPROTONOSUPPORT", "EPROTOTYPE", "ERANGE", "EROFS", "ESPIPE", "ESRCH", "ESTALE", "ETIME",
        "ETIMEDOUT", "ETXTBSY", "EWOULDBLOCK", "EXDEV",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The class of <paramref name="error"/>; <see cref="Unclassified"/> for a blank one or one with no recognised token.</summary>
    public static string Of(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return Unclassified;
        }

        var scanned = error.Length > MaxScannedLength ? error[..MaxScannedLength] : error;
        var found = new List<string>();
        for (var match = Tokens.Match(scanned); match.Success; match = match.NextMatch())
        {
            var token = Token(match);
            if (token is not null && token.Length <= MaxTokenLength && !found.Contains(token, StringComparer.Ordinal))
            {
                found.Add(token);
            }
        }

        // A timeout exception already says it timed out: "TimeoutException, Timeout" would say it twice.
        if (found.Any(token => token != TimeoutToken && token.Contains(TimeoutToken, StringComparison.OrdinalIgnoreCase)
            && (token.EndsWith("Exception", StringComparison.Ordinal) || token.EndsWith("Error", StringComparison.Ordinal))))
        {
            found.Remove(TimeoutToken);
        }

        return found.Count == 0 ? Unclassified : string.Join(Separator, found.Take(MaxTokens));
    }

    private static string? Token(Match match)
    {
        if (match.Groups["type"].Success)
        {
            return match.Groups["type"].Value;
        }

        if (match.Groups["exit"].Success)
        {
            return "exit " + match.Groups["exitcode"].Value;
        }

        if (match.Groups["http"].Success)
        {
            return "HTTP " + match.Groups["httpcode"].Value;
        }

        if (match.Groups["dotnet"].Success)
        {
            return "HTTP " + match.Groups["dotnetcode"].Value;
        }

        if (match.Groups["status"].Success)
        {
            return "status " + match.Groups["statuscode"].Value;
        }

        if (match.Groups["errno"].Success)
        {
            var name = match.Groups["errno"].Value;
            return ErrnoNames.Contains(name) ? name : null;
        }

        return match.Groups["timeout"].Success ? TimeoutToken : null;
    }
}

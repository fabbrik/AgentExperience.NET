namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>
/// A validated, immutable snapshot of the host's <see cref="ExperienceInjectionOptions.ApproachArguments"/>:
/// per tool name, the argument keys whose values the <c>Approach:</c> line may show, in the order the
/// host listed them.
/// </summary>
/// <remarks>
/// Taken once, when the provider is constructed, so a host that mutates the dictionary afterwards
/// changes nothing about what an already-built provider shows. Tool names and argument keys are
/// matched ordinally, whatever comparer the host's own dictionary used: an allowlist that matched
/// more loosely than it reads would show values the host did not name.
/// </remarks>
internal sealed class ApproachArgumentAllowlist
{
    /// <summary>The most characters one allowlisted argument key may have.</summary>
    internal const int MaxKeyLength = 64;

    /// <summary>
    /// Characters a key may not contain, because the <c>Approach:</c> line uses them to delimit an
    /// argument: a key holding one could make one argument read as two, or as the end of the call.
    /// A key is written into the line as configured, so it is refused here rather than escaped there.
    /// </summary>
    internal const string ForbiddenKeyCharacters = "=(),\"\\";

    private readonly Dictionary<string, string[]> _keys;

    private ApproachArgumentAllowlist(Dictionary<string, string[]> keys) => _keys = keys;

    /// <summary>The empty allowlist: every <c>Approach:</c> line is tool names only.</summary>
    internal static ApproachArgumentAllowlist Empty { get; } = new(new Dictionary<string, string[]>(StringComparer.Ordinal));

    /// <summary>Whether no argument of any tool may be shown.</summary>
    internal bool IsEmpty => _keys.Count == 0;

    /// <summary>
    /// Validates and snapshots <paramref name="allowlist"/>.
    /// </summary>
    /// <param name="allowlist">The host's allowlist, or <see langword="null"/> for none.</param>
    /// <param name="paramName">The name reported on a validation failure.</param>
    /// <exception cref="ArgumentException">A tool name is blank; a tool's key list is <see langword="null"/>; a key is blank, longer than <see cref="MaxKeyLength"/>, holds a whitespace, control, format or surrogate character or one of <see cref="ForbiddenKeyCharacters"/>, or is listed twice for one tool.</exception>
    internal static ApproachArgumentAllowlist From(IEnumerable<KeyValuePair<string, IReadOnlyList<string>>>? allowlist, string paramName)
    {
        if (allowlist is null)
        {
            return Empty;
        }

        var keys = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (toolName, argumentKeys) in allowlist)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                throw new ArgumentException("An approach-argument allowlist entry names a blank tool.", paramName);
            }

            if (argumentKeys is null)
            {
                throw new ArgumentException($"The approach-argument allowlist entry for tool '{toolName}' has a null key list.", paramName);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ordered = new List<string>(argumentKeys.Count);
            foreach (var key in argumentKeys)
            {
                if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength || key.Any(IsForbidden))
                {
                    throw new ArgumentException(
                        $"The approach-argument allowlist for tool '{toolName}' holds a key that is blank, longer than {MaxKeyLength} characters, or contains whitespace, a control, format or surrogate character, or one of the line's own delimiters ({ForbiddenKeyCharacters}).",
                        paramName);
                }

                if (!seen.Add(key))
                {
                    throw new ArgumentException($"The approach-argument allowlist for tool '{toolName}' lists the key '{key}' twice.", paramName);
                }

                ordered.Add(key);
            }

            // A second entry for the same tool -- possible when the source is a sequence of pairs rather
            // than a dictionary -- is refused rather than merged: which list wins would be a guess.
            if (!keys.TryAdd(toolName, [.. ordered]))
            {
                throw new ArgumentException($"The approach-argument allowlist names tool '{toolName}' twice.", paramName);
            }
        }

        return keys.Count == 0 ? Empty : new ApproachArgumentAllowlist(keys);
    }

    private static bool IsForbidden(char character) =>
        char.IsWhiteSpace(character)
        || char.IsControl(character)
        || char.IsSurrogate(character)
        || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format
        || ForbiddenKeyCharacters.Contains(character, StringComparison.Ordinal);

    /// <summary>The argument keys allowlisted for <paramref name="toolName"/>, in the host's order; empty when none are.</summary>
    internal IReadOnlyList<string> KeysFor(string? toolName) =>
        toolName is not null && _keys.TryGetValue(toolName, out var keys) ? keys : [];
}

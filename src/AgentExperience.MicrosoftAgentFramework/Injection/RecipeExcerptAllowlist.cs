namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>
/// A validated, immutable snapshot of the host's <see cref="ExperienceInjectionOptions.RecipeExcerpts"/>: the tool
/// names whose recipe excerpts the <c>Worked:</c> attempt's line may show, matched ordinally.
/// </summary>
internal sealed class RecipeExcerptAllowlist
{
    /// <summary>The most characters one allowlisted tool name may have.</summary>
    internal const int MaxToolNameLength = 256;

    private readonly HashSet<string> _tools;

    private RecipeExcerptAllowlist(HashSet<string> tools) => _tools = tools;

    /// <summary>The empty allowlist: no excerpt is shown.</summary>
    internal static RecipeExcerptAllowlist Empty { get; } = new(new HashSet<string>(StringComparer.Ordinal));

    /// <summary>Whether no tool's excerpt may be shown.</summary>
    internal bool IsEmpty => _tools.Count == 0;

    /// <summary>Whether <paramref name="toolName"/>'s excerpt may be shown.</summary>
    internal bool Allows(string? toolName) => toolName is not null && _tools.Contains(toolName);

    /// <summary>
    /// Validates and snapshots <paramref name="toolNames"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A tool name is blank, longer than <see cref="MaxToolNameLength"/>, holds a control character, or is listed twice.</exception>
    internal static RecipeExcerptAllowlist From(IEnumerable<string>? toolNames, string paramName)
    {
        if (toolNames is null)
        {
            return Empty;
        }

        var tools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var toolName in toolNames)
        {
            if (string.IsNullOrWhiteSpace(toolName) || toolName.Length > MaxToolNameLength || toolName.Any(char.IsControl))
            {
                throw new ArgumentException(
                    $"A recipe-excerpt allowlist entry names a tool that is blank, longer than {MaxToolNameLength} characters, or holds a control character.",
                    paramName);
            }

            if (!tools.Add(toolName))
            {
                throw new ArgumentException($"The recipe-excerpt allowlist names tool '{toolName}' twice.", paramName);
            }
        }

        return tools.Count == 0 ? Empty : new RecipeExcerptAllowlist(tools);
    }

    /// <summary>
    /// The allowlist a borrowed record is rendered with: only the tools this (the reader's) allowlist <em>and</em>
    /// <paramref name="ownerTools"/> (the grant's) both name. Store data that cannot be read names no tool.
    /// </summary>
    internal RecipeExcerptAllowlist IntersectWithGrant(IReadOnlyList<string>? ownerTools)
    {
        if (IsEmpty || ownerTools is null)
        {
            return Empty;
        }

        var shared = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var tool in ownerTools)
            {
                if (tool is not null && _tools.Contains(tool))
                {
                    shared.Add(tool);
                }
            }
        }
#pragma warning disable CA1031 // A store's list that cannot be read names no tool.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Empty;
        }

        return shared.Count == 0 ? Empty : new RecipeExcerptAllowlist(shared);
    }
}

using System.Text;

namespace AgentExperience.MicrosoftAgentFramework;

/// <summary>
/// Reduces a name the host did not choose as an identifier (an agent's name, the entry assembly's name) to one: lower
/// case, ASCII letters, digits, <c>.</c> and <c>_</c> kept, every run of other characters (dashes included) one
/// <c>-</c>, no separator at either end, at most <see cref="MaxLength"/> characters, never fusing two words at the cut.
/// </summary>
internal static class ExperienceNames
{
    /// <summary>The longest identifier this produces.</summary>
    internal const int MaxLength = 100;

    /// <summary>The reduced identifier, or <see langword="null"/> when <paramref name="name"/> keeps nothing.</summary>
    internal static string? Reduce(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var text = new StringBuilder(Math.Min(name.Length, MaxLength));
        var pendingSeparator = false;
        foreach (var character in name)
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '.' or '_')
            {
                var needed = pendingSeparator && text.Length > 0 ? 2 : 1;
                if (text.Length + needed > MaxLength)
                {
                    // Stop rather than fuse two words or cut the separator off its word.
                    break;
                }

                if (needed == 2)
                {
                    text.Append('-');
                }

                pendingSeparator = false;
                text.Append(char.ToLowerInvariant(character));
            }
            else
            {
                // A dash, and any run of other characters, dashes included, becomes one dash.
                pendingSeparator = true;
            }
        }

        var reduced = text.ToString().Trim('.', '_', '-');
        return reduced.Length == 0 ? null : reduced;
    }
}

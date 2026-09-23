using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentExperience.Sample.EndToEnd;

/// <summary>
/// Renders a captured run or a stored record as one searchable string, for the sample's own "this
/// value is not in here anywhere" checks.
/// </summary>
/// <remarks>
/// It is a search surface, never a storage format and never part of the transcript: the point is
/// that every field, however deeply nested, ends up in one string that can be scanned for the
/// secret tool argument's live value. Nothing is escaped away -- the encoder is left permissive on
/// purpose, so a value cannot hide behind <c>s</c>-style escaping of the characters a
/// <see cref="string.Contains(string, StringComparison)"/> would otherwise match.
/// </remarks>
internal static class SampleSerialization
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    /// <summary>Everything <paramref name="value"/> holds, as one string.</summary>
    /// <typeparam name="T">The object's static type; every public property is written.</typeparam>
    /// <param name="value">The object to flatten.</param>
    public static string Describe<T>(T value) => JsonSerializer.Serialize(value, Options);
}

using System.Text;
using System.Text.Json;
using AgentExperience.Abstractions;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// The stored form of a grant's <see cref="ExperienceGrant.RecipeExcerpts"/>: one JSON array of tool names, in
/// <c>experience_grants.recipe_excerpts</c> (<c>0031</c>). Read through <c>to_jsonb(g) -&gt;&gt; 'recipe_excerpts'</c>, so a
/// schema without the column reads as none.
/// </summary>
/// <remarks>
/// Parsing is strict and bounded, and a stored value it cannot read is <see langword="null"/>: the grant store turns
/// that into a loud decode failure, and the read path into "the owner named no tool". Never repaired into a wider list.
/// </remarks>
internal static class GrantRecipeExcerptsCodec
{
    /// <summary>
    /// Selects the permitting or listed grant's recipe-excerpt consent from the grant row aliased <c>g</c>, NULL on a
    /// schema that has no such column.
    /// </summary>
    internal const string Column = "to_jsonb(g) ->> 'recipe_excerpts'";

    /// <summary>A private copy of a caller's list, taken in one pass; a null stays null.</summary>
    internal static IReadOnlyList<string>? Snapshot(IReadOnlyList<string>? tools) => tools?.ToArray();

    /// <summary>Writes <paramref name="tools"/> as the stored JSON array. The caller has validated it.</summary>
    internal static string Serialize(IReadOnlyList<string> tools)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var tool in tools)
            {
                writer.WriteStringValue(tool);
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Reads a stored list, or <see langword="null"/> when it is not a non-empty array of distinct strings within
    /// <see cref="ExperienceGrant.MaxRecipeExcerptTools"/>.
    /// </summary>
    internal static IReadOnlyList<string>? Parse(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(stored);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() is 0 or > ExperienceGrant.MaxRecipeExcerptTools)
            {
                return null;
            }

            var tools = new List<string>(root.GetArrayLength());
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in root.EnumerateArray())
            {
                if (tool.ValueKind != JsonValueKind.String || !seen.Add(tool.GetString()!))
                {
                    return null;
                }

                tools.Add(tool.GetString()!);
            }

            return tools.AsReadOnly();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

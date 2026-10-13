using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework;

/// <summary>
/// Ready-made <see cref="ExperienceCaptureOptions.ClassifyToolResult"/> functions for tools that report failure in their
/// return value, such as an exit code or an HTTP status. Each returns an error class that is a single error-class token
/// (<c>exit 3</c>, <c>HTTP 503</c>, or <see cref="ExperienceCaptureOptions.ToolReportedErrorClass"/>), or
/// <see langword="null"/> when the call did not fail.
/// </summary>
/// <remarks>
/// <para>
/// Each reads only the tool's own raw result:
/// </para>
/// <list type="bullet">
/// <item><description>A JSON object -- a <see cref="JsonElement"/>, a string that parses as one, or any other object,
/// serialized the way capture serializes it (<see cref="AIJsonUtilities.DefaultOptions"/>) -- is read by its top-level
/// properties only, whatever its size, so a code quoted inside its output does not count. A JSON array counts as no
/// code.</description></item>
/// <item><description>Any other text (a <see cref="string"/>, a JSON string, a <see cref="TextContent"/> or a list of
/// them) is matched against the patterns given on each member, and the <b>last</b> match counts, so a final exit code
/// wins over an earlier step's. Text up to 1 MiB is read whole; of longer text, the first and last 64 KiB.</description></item>
/// </list>
/// <para>
/// Matching ignores case. With no tool names, a classifier applies to every tool's result; give the names of the tools
/// that report this way to leave every other tool's result alone (ordinal match on
/// <see cref="ExperienceToolResultContext.ToolName"/>). Set one through the one-call setup's capture hook:
/// <c>options.Capture = capture => capture.ClassifyToolResult = ExperienceToolResultClassifiers.ExitCode("run_tests");</c>
/// </para>
/// </remarks>
public static class ExperienceToolResultClassifiers
{
    private const int MaxWholeTextLength = 1024 * 1024;

    private const int EdgeLength = 64 * 1024;

    // A token's exit code has at most six digits (and an optional minus sign).
    private const int MaxExitCodeDigits = 6;

    private const RegexOptions Options =
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking;

    private static readonly Regex ExitText = new(
        @"\b(?:exit(?:ed)?(?:\s+with)?(?:\s+(?:code|status))?|return_?code)\s*[=:]?\s*(?<code>-?[0-9]+(?:\.[0-9]+)?)\b"
        + @"|""(?:exit_?code|return_?code)""\s*:\s*""?(?<code>-?[0-9]+(?:\.[0-9]+)?)\b",
        Options);

    private static readonly Regex HttpLeadingText = new(
        @"^\s*(?:HTTP(?:/[0-9](?:\.[0-9])?)?\s+|status(?:\s+code)?\s*[=:]?\s*)(?<code>[0-9]+)\b",
        Options);

    private static readonly Regex HttpPairText = new(
        @"""status(?:_?code)?""\s*:\s*""?(?<code>-?[0-9]+(?:\.[0-9]+)?)\b",
        Options);

    private static readonly Regex Numeric = new(
        @"^-?[0-9]+(?:\.[0-9]+)?(?:[eE][-+]?[0-9]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Spec Exit = new(
        ["exitCode", "exit_code", "returnCode", "return_code"],
        text => Last(ExitText, text),
        ExitClass);

    private static readonly Spec Http = new(
        ["status", "statusCode", "status_code"],
        text => HttpLeadingText.Match(text) is { Success: true } leading ? leading.Groups["code"].Value : Last(HttpPairText, text),
        HttpClass);

    /// <summary>
    /// Classifies a result by its exit code: <c>exit N</c> for a non-zero code N, <see langword="null"/> for zero or when
    /// the result names no exit code.
    /// </summary>
    /// <remarks>
    /// In text it finds <c>exit=N</c>, <c>exit N</c>, <c>exit code N</c>, <c>exit status N</c>,
    /// <c>exited with code N</c>, <c>returncode=N</c>, <c>"exitCode": N</c>, <c>"exit_code": N</c>, <c>"returnCode": N</c>
    /// or <c>"return_code": N</c>; in a JSON object, the first top-level <c>exitCode</c>, <c>exit_code</c>,
    /// <c>returnCode</c> or <c>return_code</c> property whose value is a number or a numeric string. A non-zero code that
    /// no error-class token can carry -- more than six digits, such as a Windows status code, or not an integer, such as
    /// <c>1.0</c> -- is classed <see cref="ExperienceCaptureOptions.ToolReportedErrorClass"/>.
    /// </remarks>
    /// <param name="toolNames">The tools it applies to; with none, every tool.</param>
    /// <returns>A <see cref="ExperienceCaptureOptions.ClassifyToolResult"/> function.</returns>
    /// <exception cref="ArgumentException">A tool name is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="toolNames"/> is <see langword="null"/>.</exception>
    public static Func<ExperienceToolResultContext, string?> ExitCode(params string[] toolNames) => Classifier(toolNames, Exit);

    /// <summary>
    /// Classifies a result by its HTTP status: <c>HTTP N</c> for a status N from 400 to 599, <see langword="null"/>
    /// otherwise, or when the result names none.
    /// </summary>
    /// <remarks>
    /// In text it finds a leading <c>HTTP N</c> (or <c>HTTP/1.1 N</c>) or <c>status N</c>, else the last
    /// <c>"status": N</c> or <c>"statusCode": N</c> pair; in a JSON object, the first top-level <c>status</c>,
    /// <c>statusCode</c> or <c>status_code</c> property whose value is a number or a numeric string.
    /// </remarks>
    /// <param name="toolNames">The tools it applies to; with none, every tool.</param>
    /// <returns>A <see cref="ExperienceCaptureOptions.ClassifyToolResult"/> function.</returns>
    /// <exception cref="ArgumentException">A tool name is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="toolNames"/> is <see langword="null"/>.</exception>
    public static Func<ExperienceToolResultContext, string?> HttpStatus(params string[] toolNames) => Classifier(toolNames, Http);

    private static Func<ExperienceToolResultContext, string?> Classifier(string[] toolNames, Spec spec)
    {
        ArgumentNullException.ThrowIfNull(toolNames);
        foreach (var name in toolNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(toolNames));
        }

        var tools = toolNames.Length == 0 ? null : new HashSet<string>(toolNames, StringComparer.Ordinal);
        return context =>
        {
            ArgumentNullException.ThrowIfNull(context);
            if (tools is not null && !tools.Contains(context.ToolName))
            {
                return null;
            }

            return Read(context.Result, spec) is { } code ? spec.Classify(code) : null;
        };
    }

    /// <summary>The numeric code a result names, as written: from a JSON object's properties, else from its text.</summary>
    private static string? Read(object? result, Spec spec)
    {
        switch (result)
        {
            case null:
                return null;
            case string text:
                return FromString(text, spec);
            case JsonElement element:
                return FromElement(element, spec);
            case TextContent content:
                return FromString(content.Text, spec);
            case IEnumerable<AIContent> contents:
                return FromString(string.Join("\n", contents.OfType<TextContent>().Select(content => content.Text)), spec);
            case AIContent:
                return null;
            default:
                JsonElement serialized;
                try
                {
                    serialized = JsonSerializer.SerializeToElement(result, AIJsonUtilities.DefaultOptions);
                }
                catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
                {
                    return null;
                }

                return FromElement(serialized, spec);
        }
    }

    private static string? FromElement(JsonElement element, Spec spec) => element.ValueKind switch
    {
        JsonValueKind.Object => FromObject(element, spec),
        JsonValueKind.String => FromString(element.GetString(), spec),
        _ => null,
    };

    private static string? FromString(string? text, Spec spec)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                return document.RootElement.ValueKind == JsonValueKind.Object ? FromObject(document.RootElement, spec) : null;
            }
            catch (JsonException)
            {
                // Not JSON after all: read it as text.
            }
        }

        return spec.FromText(text);
    }

    private static string? FromObject(JsonElement element, Spec spec)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!spec.Properties.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var value = property.Value;
            var text = value.ValueKind switch
            {
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.String => value.GetString()?.Trim(),
                _ => null,
            };
            if (text is not null && Numeric.IsMatch(text))
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>The code of the last match in the text read: all of it up to 1 MiB, else its first and last 64 KiB.</summary>
    private static string? Last(Regex pattern, string text)
    {
        string[] segments = text.Length <= MaxWholeTextLength ? [text] : [text[..EdgeLength], text[^EdgeLength..]];
        string? code = null;
        foreach (var segment in segments)
        {
            foreach (Match match in pattern.Matches(segment))
            {
                code = match.Groups["code"].Value;
            }
        }

        return code;
    }

    private static string? ExitClass(string code)
    {
        var negative = code.StartsWith('-');
        var unsigned = negative ? code[1..] : code;
        if (unsigned.AsSpan().IndexOfAnyExceptInRange('0', '9') >= 0)
        {
            // Not an integer (1.0, 1e0): zero is success, anything else a failure no token can name.
            return double.TryParse(code, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value == 0
                ? null
                : ExperienceCaptureOptions.ToolReportedErrorClass;
        }

        var digits = unsigned.TrimStart('0');
        if (digits.Length == 0)
        {
            return null;
        }

        return digits.Length > MaxExitCodeDigits
            ? ExperienceCaptureOptions.ToolReportedErrorClass
            : "exit " + (negative ? "-" : string.Empty) + digits;
    }

    private static string? HttpClass(string code) =>
        int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var status) && status is >= 400 and <= 599
            ? "HTTP " + status.ToString(CultureInfo.InvariantCulture)
            : null;

    private sealed record Spec(string[] Properties, Func<string, string?> FromText, Func<string, string?> Classify);
}

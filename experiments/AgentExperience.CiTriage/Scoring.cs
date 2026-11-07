using System.Text.Json;
using AgentExperience.CiTriage.GitHub;

namespace AgentExperience.CiTriage;

/// <summary>What the agent said: the failure's kind and, for <c>code</c>, the files most likely to need the fix.</summary>
public sealed record TriagePrediction(string Kind, IReadOnlyList<string> Files)
{
    public const int MaxFiles = 5;

    public bool IsFlaky => string.Equals(Kind, "flaky", StringComparison.Ordinal);

    public bool IsCode => string.Equals(Kind, "code", StringComparison.Ordinal);

    /// <summary>The ledger's spelling: <c>flaky</c>, or <c>code:a.cs,b.cs</c>.</summary>
    public string Describe() => IsFlaky ? "flaky" : "code:" + string.Join(',', Files);
}

/// <summary>Parsing the agent's JSON reply, and scoring a prediction against a label.</summary>
public static class Scoring
{
    /// <summary>
    /// Reads <c>{"kind": "code"|"flaky", "files": [...]}</c> from the reply: the outermost braces are taken, so a reply
    /// wrapped in prose or a code fence still parses. At most <see cref="TriagePrediction.MaxFiles"/> files are kept,
    /// normalised to repository-relative paths. <see langword="null"/> when there is no such object.
    /// </summary>
    public static TriagePrediction? Parse(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return null;
        }

        var start = reply.IndexOf('{', StringComparison.Ordinal);
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(reply[start..(end + 1)]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("kind", out var kindElement)
                || kindElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var kind = kindElement.GetString()!.Trim().ToLowerInvariant();
            if (kind is not ("code" or "flaky"))
            {
                return null;
            }

            var files = new List<string>();
            if (root.TryGetProperty("files", out var filesElement) && filesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in filesElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && Normalise(item.GetString()) is { } path && !files.Contains(path, StringComparer.Ordinal))
                    {
                        files.Add(path);
                    }

                    if (files.Count == TriagePrediction.MaxFiles)
                    {
                        break;
                    }
                }
            }

            return new TriagePrediction(kind, kind == "flaky" ? [] : files);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the prediction is right: a flaky failure when the agent says flaky; a code failure when it says code and
    /// at least one of its (at most five) files is among the fix files. Ambiguous, pending and unavailable failures are
    /// never scored.
    /// </summary>
    public static bool IsCorrect(FailureLabel label, TriagePrediction? prediction)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (prediction is null)
        {
            return false;
        }

        return label.Kind switch
        {
            LabelKind.Flaky => prediction.IsFlaky,
            LabelKind.Code => prediction.IsCode
                && prediction.Files.Take(TriagePrediction.MaxFiles).Any(file => label.FixFiles.Contains(file, StringComparer.Ordinal)),
            _ => throw new ArgumentException($"A {label.Name} failure is never scored.", nameof(label)),
        };
    }

    /// <summary>A repository-relative path: trimmed, without a leading <c>./</c> or <c>/</c>, with forward slashes.</summary>
    public static string? Normalise(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalised = path.Trim().Replace('\\', '/');
        while (normalised.StartsWith("./", StringComparison.Ordinal))
        {
            normalised = normalised[2..];
        }

        normalised = normalised.TrimStart('/');
        return normalised.Length == 0 ? null : normalised;
    }
}

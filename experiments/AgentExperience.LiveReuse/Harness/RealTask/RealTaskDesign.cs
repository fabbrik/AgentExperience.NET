using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace AgentExperience.LiveReuse.Harness;

/// <summary>
/// The real-task experiment's pre-registration (<c>preregistration.realtask.json</c>), embedded into the build: the
/// harness executes only values read from it and prints its git blob id in every report.
/// </summary>
public sealed record RealTaskDesign(
    string GitBlobId,
    string Version,
    string TaskSetDigest,
    string ControlLabel,
    string TreatmentLabel,
    string PlaceboLabel,
    string MismatchedLabel,
    int Repeats,
    float Temperature,
    long Seed,
    int LearningToolCalls,
    int EvaluationToolCalls,
    int MaxAttempts,
    double Alpha,
    int MaxExcludedServices,
    int AbortAfterRefusedTrials,
    IReadOnlyList<int> RefusedStatuses,
    int RetryMaxTries,
    IReadOnlyList<int> RetryDelaysMilliseconds,
    int DefaultMaxModelCalls,
    long DefaultMaxTotalTokens,
    int AmendmentCount)
{
    public const string ResourceName = "AgentExperience.LiveReuse.preregistration.realtask.json";

    /// <summary>Reads the embedded file.</summary>
    public static RealTaskDesign ReadEmbedded()
    {
        using var stream = typeof(RealTaskDesign).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new PreregistrationException("The real-task pre-registration is not embedded in this build.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Parse(memory.ToArray());
    }

    /// <summary>Parses <paramref name="bytes"/>; refuses a file missing a field the harness executes.</summary>
    public static RealTaskDesign Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(bytes).RootElement;
        }
        catch (JsonException ex)
        {
            throw new PreregistrationException("preregistration.realtask.json is not valid JSON: " + ex.Message);
        }

        var labels = Required(root, "labels");
        var sampling = Required(root, "sampling");
        var limits = Required(root, "limits");
        var gate = Required(root, "gate");
        var retry = Required(root, "retry");
        var refusal = Required(root, "providerRefusal");
        var budget = Required(root, "budgetDefaults");

        var design = new RealTaskDesign(
            LivePreregistration.ComputeGitBlobId(bytes),
            Text(root, "version"),
            Text(Required(root, "taskSet"), "digest"),
            Text(labels, "control"),
            Text(labels, "treatment"),
            Text(labels, "placebo"),
            Text(labels, "mismatched"),
            Int(root, "repeats"),
            (float)Number(sampling, "temperature"),
            (long)Number(sampling, "seed"),
            Int(limits, "learningToolCalls"),
            Int(limits, "evaluationToolCalls"),
            Int(limits, "maxAttempts"),
            Number(gate, "alpha"),
            Int(gate, "maxExcludedServices"),
            Int(refusal, "abortAfterConsecutiveTrials"),
            [.. Required(refusal, "httpStatuses").EnumerateArray().Select(item => item.GetInt32())],
            Int(retry, "maxTries"),
            [.. Required(retry, "delaysMilliseconds").EnumerateArray().Select(item => item.GetInt32())],
            Int(budget, "maxModelCalls"),
            (long)Number(budget, "maxTotalTokens"),
            Required(root, "amendments").GetArrayLength());

        if (design.Repeats is < 1 or > 10 || design.Alpha is <= 0 or >= 1 || design.MaxAttempts < 1
            || design.LearningToolCalls < 1 || design.EvaluationToolCalls < 1 || design.RetryMaxTries < 1
            || design.MaxExcludedServices < 0 || design.AbortAfterRefusedTrials < 1 || design.Temperature is < 0 or > 2)
        {
            throw new PreregistrationException("The real-task pre-registration's repeats, limits, alpha, temperature or exclusion limit are out of range.");
        }

        return design;
    }

    /// <summary>Refuses a task set that differs from the one the file locked.</summary>
    public void Check(RealTaskSet taskSet)
    {
        ArgumentNullException.ThrowIfNull(taskSet);
        if (!string.Equals(Version, RealTaskSet.Version, StringComparison.Ordinal))
        {
            throw new PreregistrationException($"The pre-registration is for '{Version}', this build's task set is '{RealTaskSet.Version}'.");
        }

        if (!string.Equals(TaskSetDigest, taskSet.Digest(), StringComparison.Ordinal))
        {
            throw new PreregistrationException("The task set differs from the one the pre-registration locked (its digest does not match; this build's is " + taskSet.Digest() + ").");
        }
    }

    private static JsonElement Required(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : throw new PreregistrationException($"preregistration.realtask.json has no '{name}'. Every field the harness executes is required.");

    private static string Text(JsonElement parent, string name) =>
        Required(parent, name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: > 0 } text
            ? text
            : throw new PreregistrationException($"preregistration.realtask.json '{name}' must be a non-empty string.");

    private static int Int(JsonElement parent, string name) =>
        Required(parent, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number)
            ? number
            : throw new PreregistrationException($"preregistration.realtask.json '{name}' must be an integer.");

    private static double Number(JsonElement parent, string name) =>
        Required(parent, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var number)
            ? number
            : throw new PreregistrationException($"preregistration.realtask.json '{name}' must be a number.");
}

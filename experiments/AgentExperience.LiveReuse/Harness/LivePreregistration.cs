using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentExperience.LiveReuse.Harness;

/// <summary>The pre-registration could not be read, or disagrees with what the harness is about to run.</summary>
public sealed class PreregistrationException(string message) : Exception(message);

/// <summary>One change made to the pre-registration after it was first committed.</summary>
public sealed record LiveAmendment(string Date, string Change, string Why, bool ResultsExisted, string ResultsChanged);

/// <summary>
/// The fields of <c>preregistration.json</c> the harness executes, with the git blob id of the exact bytes read.
/// </summary>
/// <remarks>
/// Every value the run depends on -- the instance count, the task set and its answer digest, the attempt limits, the
/// model settings, the budget defaults, the primary metric, alpha, the exclusion limit -- comes out of the file, and
/// <see cref="Check"/> refuses a run whose task set disagrees with it. The gate expression is printed from the file.
/// </remarks>
public sealed record LivePreregistration(
    string Version,
    string RegisteredAgainstCommit,
    string RegisteredOn,
    string Hypothesis,
    string TaskSetVersion,
    int Instances,
    string HiddenAssignmentSha256,
    string ControlLabel,
    string TreatmentLabel,
    string NegativeControlLabel,
    string PlaceboLabel,
    int LearningAttemptLimit,
    int EvaluationAttemptLimit,
    int ToolCallsPerAttempt,
    float Temperature,
    long Seed,
    int DefaultMaxModelCalls,
    long DefaultMaxTotalTokens,
    string PrimaryMetric,
    double Alpha,
    string GateExpression,
    int MaxExcludedInstances,
    IReadOnlyList<LiveAmendment> Amendments,
    string GitBlobId,
    int ByteCount)
{
    public const string ResourceName = "AgentExperience.LiveReuse.preregistration.json";

    /// <summary>The primary metric the harness knows how to gate on. The file must name it.</summary>
    public const string SupportedPrimaryMetric = "failed_attempts";

    /// <summary>Reads the pre-registration compiled into this assembly.</summary>
    public static LivePreregistration ReadEmbedded()
    {
        using var stream = typeof(LivePreregistration).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new PreregistrationException($"The embedded resource {ResourceName} is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Parse(buffer.ToArray());
    }

    /// <summary>Parses <paramref name="bytes"/>. Every field is required; nothing is defaulted.</summary>
    public static LivePreregistration Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            throw new PreregistrationException("preregistration.json is not valid JSON: " + ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            var conditions = Required(root, "conditions");
            var design = Required(root, "design");
            var limits = Required(design, "attemptLimits");
            var model = Required(root, "model");
            var budget = Required(root, "budgetDefaults");
            var test = Required(root, "statisticalTest");
            var gate = Required(root, "gate");
            var exclusions = Required(root, "exclusions");

            var amendments = Required(root, "amendments").EnumerateArray()
                .Select(amendment => new LiveAmendment(
                    String(amendment, "date"),
                    String(amendment, "change"),
                    String(amendment, "why"),
                    Required(amendment, "resultsExisted").GetBoolean(),
                    String(amendment, "resultsChanged")))
                .ToList();

            return new LivePreregistration(
                String(root, "preregistrationVersion"),
                String(root, "registeredAgainstCommit"),
                String(root, "registeredOn"),
                String(root, "hypothesis"),
                String(root, "taskSetVersion"),
                Required(root, "instances").GetInt32(),
                String(root, "hiddenAssignmentSha256"),
                String(conditions, "control"),
                String(conditions, "treatment"),
                String(conditions, "negativeControl"),
                String(conditions, "placebo"),
                Required(limits, "learning").GetInt32(),
                Required(limits, "evaluation").GetInt32(),
                Required(limits, "toolCallsPerAttempt").GetInt32(),
                Required(model, "temperature").GetSingle(),
                Required(model, "seed").GetInt64(),
                Required(budget, "maxModelCalls").GetInt32(),
                Required(budget, "maxTotalTokens").GetInt64(),
                String(root, "primaryMetric"),
                Required(test, "alpha").GetDouble(),
                String(gate, "expression"),
                Required(exclusions, "maxExcludedInstances").GetInt32(),
                amendments,
                ComputeGitBlobId(bytes),
                bytes.Length);
        }
    }

    /// <summary>How many amendments were made after results already existed.</summary>
    public int AmendmentsAfterResults => Amendments.Count(amendment => amendment.ResultsExisted);

    /// <summary>
    /// Refuses to run <paramref name="taskSet"/> unless it is the task set this file registered, with the answers this
    /// file locked, and unless the file's own values are ones the harness can execute.
    /// </summary>
    public void Check(MigrationTaskSet taskSet)
    {
        ArgumentNullException.ThrowIfNull(taskSet);

        if (!string.Equals(taskSet.Version, TaskSetVersion, StringComparison.Ordinal))
        {
            throw new PreregistrationException($"The task set is {taskSet.Version}; the pre-registration fixed {TaskSetVersion}.");
        }

        if (taskSet.Instances.Count != Instances)
        {
            throw new PreregistrationException($"The task set has {taskSet.Instances.Count} instances; the pre-registration fixed {Instances}.");
        }

        var digest = taskSet.AssignmentDigest();
        if (!string.Equals(digest, HiddenAssignmentSha256, StringComparison.Ordinal))
        {
            throw new PreregistrationException(
                $"The task set's hidden-strategy assignment digests to {digest}; the pre-registration locked {HiddenAssignmentSha256}.");
        }

        if (!string.Equals(PrimaryMetric, SupportedPrimaryMetric, StringComparison.Ordinal))
        {
            throw new PreregistrationException($"The pre-registration's primary metric is '{PrimaryMetric}'; this harness gates on '{SupportedPrimaryMetric}' only.");
        }

        if (LearningAttemptLimit < 1 || EvaluationAttemptLimit < 1 || ToolCallsPerAttempt < 1 || Alpha is <= 0 or >= 1 || MaxExcludedInstances < 0)
        {
            throw new PreregistrationException("The pre-registration's attempt limits, alpha or exclusion limit are out of range.");
        }
    }

    /// <summary>The git object id of <paramref name="bytes"/>: what <c>git hash-object</c> prints for the file.</summary>
    public static string ComputeGitBlobId(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var header = Encoding.ASCII.GetBytes("blob " + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\0");
#pragma warning disable CA5350 // SHA-1 is git's object identity, not a security control.
        return Convert.ToHexStringLower(SHA1.HashData([.. header, .. bytes]));
#pragma warning restore CA5350
    }

    private static JsonElement Required(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : throw new PreregistrationException($"preregistration.json has no '{name}'. Every field the harness executes is required.");

    private static string String(JsonElement parent, string name) =>
        Required(parent, name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: > 0 } text
            ? text
            : throw new PreregistrationException($"preregistration.json's '{name}' must be a non-empty string.");
}

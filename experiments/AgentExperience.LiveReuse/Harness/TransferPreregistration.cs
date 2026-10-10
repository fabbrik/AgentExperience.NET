using System.Text.Json;

namespace AgentExperience.LiveReuse.Harness;

/// <summary>
/// The fields of <c>preregistration.transfer.json</c> the transfer harness executes, with the git blob id of the exact
/// bytes read. Every value the run depends on comes out of the file, and <see cref="Check"/> refuses a run whose task
/// set disagrees with it.
/// </summary>
public sealed record TransferPreregistration(
    string Version,
    string RegisteredAgainstCommit,
    string RegisteredOn,
    bool LiveResultsExistedAtRegistration,
    string ResultsNote,
    string Hypothesis,
    string TaskSetVersion,
    int Clusters,
    int EvaluationInstances,
    int Distractors,
    string TraitAssignmentSha256,
    string TaskTextSha256,
    string ControlLabel,
    string TreatmentLabel,
    string PlaceboLabel,
    string MismatchedLabel,
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
    string ExclusionRule,
    string ConfirmatoryRunRule,
    IReadOnlyDictionary<string, string> OverallConclusion,
    IReadOnlyList<LiveAmendment> Amendments,
    string GitBlobId,
    int ByteCount)
{
    public const string FileName = "preregistration.transfer.json";

    public const string ResourceName = "AgentExperience.LiveReuse.preregistration.transfer.json";

    /// <summary>Reads the transfer pre-registration compiled into this assembly.</summary>
    public static TransferPreregistration ReadEmbedded()
    {
        using var stream = typeof(TransferPreregistration).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new PreregistrationException($"The embedded resource {ResourceName} is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Parse(buffer.ToArray());
    }

    /// <summary>
    /// The git blob of the file as first registered, when it has since been amended (<c>originalRegistrationGitBlobId</c>);
    /// <see langword="null"/> while it is as registered.
    /// </summary>
    public string? OriginalGitBlobId { get; init; }

    /// <summary>How many amendments were made after live results already existed.</summary>
    public int AmendmentsAfterResults => Amendments.Count(amendment => amendment.ResultsExisted);

    /// <summary>Parses <paramref name="bytes"/>. Every field is required; nothing is defaulted.</summary>
    public static TransferPreregistration Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            throw new PreregistrationException(FileName + " is not valid JSON: " + ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            try
            {
                return ParseRoot(root, bytes);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                // A field of the wrong JSON kind (a string where a number belongs, and so on).
                throw new PreregistrationException($"{FileName} has a field of the wrong kind: {ex.Message}");
            }
        }
    }

    private static TransferPreregistration ParseRoot(JsonElement root, byte[] bytes)
    {
        {
            var conditions = Required(root, "conditions");
            var design = Required(root, "design");
            var limits = Required(design, "attemptLimits");
            var model = Required(root, "model");
            var budget = Required(root, "budgetDefaults");
            var test = Required(root, "statisticalTest");
            var gate = Required(root, "gate");
            var exclusions = Required(root, "exclusions");
            var conclusion = Required(root, "overallConclusion");
            if (conclusion.ValueKind != JsonValueKind.Object)
            {
                throw new PreregistrationException(FileName + "'s 'overallConclusion' must be an object.");
            }

            var amendments = Required(root, "amendments").EnumerateArray()
                .Select(amendment => new LiveAmendment(
                    String(amendment, "date"),
                    String(amendment, "change"),
                    String(amendment, "why"),
                    Required(amendment, "resultsExisted").GetBoolean(),
                    String(amendment, "resultsChanged")))
                .ToList();

            return new TransferPreregistration(
                String(root, "preregistrationVersion"),
                String(root, "registeredAgainstCommit"),
                String(root, "registeredOn"),
                Required(root, "liveResultsExistedAtRegistration").GetBoolean(),
                String(root, "resultsNote"),
                String(root, "hypothesis"),
                String(root, "taskSetVersion"),
                Required(root, "clusters").GetInt32(),
                Required(root, "evaluationInstances").GetInt32(),
                Required(root, "distractors").GetInt32(),
                String(root, "traitAssignmentSha256"),
                String(root, "taskTextSha256"),
                String(conditions, "control"),
                String(conditions, "treatment"),
                String(conditions, "placebo"),
                String(conditions, "mismatchedControl"),
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
                String(exclusions, "rule"),
                String(root, "confirmatoryRunRule"),
                conclusion.EnumerateObject().ToDictionary(property => property.Name, property => String(conclusion, property.Name), StringComparer.Ordinal),
                amendments,
                LivePreregistration.ComputeGitBlobId(bytes),
                bytes.Length)
            {
                OriginalGitBlobId = root.TryGetProperty("originalRegistrationGitBlobId", out _) ? String(root, "originalRegistrationGitBlobId") : null,
            };
        }
    }

    /// <summary>
    /// Refuses to run <paramref name="taskSet"/> unless it is the task set this file registered, with the trait this
    /// file locked, and unless the file's own values are ones the harness can execute.
    /// </summary>
    public void Check(TransferTaskSet taskSet)
    {
        ArgumentNullException.ThrowIfNull(taskSet);

        if (!string.Equals(taskSet.Version, TaskSetVersion, StringComparison.Ordinal))
        {
            throw new PreregistrationException($"The task set is {taskSet.Version}; the transfer pre-registration fixed {TaskSetVersion}.");
        }

        if (taskSet.Clusters.Count != Clusters || taskSet.EvaluationInstances.Count != EvaluationInstances || taskSet.Distractors.Count != Distractors)
        {
            throw new PreregistrationException(
                $"The task set has {taskSet.Clusters.Count} clusters, {taskSet.EvaluationInstances.Count} instances and {taskSet.Distractors.Count} distractors; "
                + $"the transfer pre-registration fixed {Clusters}, {EvaluationInstances} and {Distractors}.");
        }

        var digest = taskSet.TraitDigest();
        if (!string.Equals(digest, TraitAssignmentSha256, StringComparison.Ordinal))
        {
            throw new PreregistrationException($"The task set's trait assignment digests to {digest}; the transfer pre-registration locked {TraitAssignmentSha256}.");
        }

        var texts = taskSet.TaskTextDigest();
        if (!string.Equals(texts, TaskTextSha256, StringComparison.Ordinal))
        {
            throw new PreregistrationException($"The task set's texts digest to {texts}; the transfer pre-registration locked {TaskTextSha256}.");
        }

        string[] labels = [ControlLabel, TreatmentLabel, PlaceboLabel, MismatchedLabel];
        if (labels.Distinct(StringComparer.Ordinal).Count() != labels.Length)
        {
            throw new PreregistrationException("The transfer pre-registration's four condition labels must be distinct.");
        }

        if (DefaultMaxModelCalls < 1 || DefaultMaxTotalTokens < 1)
        {
            throw new PreregistrationException("The transfer pre-registration's budget defaults must be at least 1.");
        }

        if (!string.Equals(PrimaryMetric, LivePreregistration.SupportedPrimaryMetric, StringComparison.Ordinal))
        {
            throw new PreregistrationException($"The transfer pre-registration's primary metric is '{PrimaryMetric}'; this harness gates on '{LivePreregistration.SupportedPrimaryMetric}' only.");
        }

        if (LearningAttemptLimit < 1 || EvaluationAttemptLimit < 1 || ToolCallsPerAttempt < 1 || Alpha is <= 0 or >= 1 || MaxExcludedInstances < 0)
        {
            throw new PreregistrationException("The transfer pre-registration's attempt limits, alpha or exclusion limit are out of range.");
        }

        // The conclusion names are registered, not invented by the harness: the file must name exactly the ones it can reach.
        var names = Enum.GetNames<TransferConclusion>();
        if (OverallConclusion.Count != names.Length || names.Any(name => !OverallConclusion.ContainsKey(name)))
        {
            throw new PreregistrationException("The transfer pre-registration's 'overallConclusion' does not register exactly the harness's Transfer verdicts.");
        }
    }

    private static JsonElement Required(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : throw new PreregistrationException($"{FileName} has no '{name}'. Every field the harness executes is required.");

    private static string String(JsonElement parent, string name) =>
        Required(parent, name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: > 0 } text
            ? text
            : throw new PreregistrationException($"{FileName}'s '{name}' must be a non-empty string.");
}

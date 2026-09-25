using System.Text.Json;
using System.Text.Json.Serialization;
using AgentExperience.Abstractions;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// The adapter-owned, versioned JSONB payload shape. Scope, status, confidence, counters, revision,
/// and timestamps live in their own columns; everything else lives here. Domain types carry no
/// version field -- this adapter maps them to and from explicit DTOs so a domain rename never
/// silently changes stored JSON.
/// </summary>
internal static class ExperiencePayload
{
    /// <summary>The payload version written by this adapter.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static string Serialize(ExperienceRecord record) =>
        JsonSerializer.Serialize(ToV1(record), SerializerOptions);

    public static PayloadV1 Deserialize(int version, string json)
    {
        if (version != CurrentVersion)
        {
            throw new ExperienceStoreException(
                $"Stored Experience Record has unsupported payload_version {version}; this adapter reads version {CurrentVersion}.");
        }

        try
        {
            return JsonSerializer.Deserialize<PayloadV1>(json, SerializerOptions)
                ?? throw new ExperienceStoreException("Stored Experience Record payload is null.");
        }
        catch (JsonException ex)
        {
            throw new ExperienceStoreException(
                $"Stored Experience Record payload could not be read as payload_version {CurrentVersion}.", ex);
        }
    }

    private static PayloadV1 ToV1(ExperienceRecord record) => new(
        record.TaskSummary,
        record.Attempts.Select(a => new AttemptV1(
            a.AttemptId,
            a.SequenceNumber,
            Utc(a.StartedAt),
            a.Duration,
            a.ToolCalls.Select(t => new ToolCallV1(
                t.ToolCallId,
                t.SequenceNumber,
                t.ToolName,
                t.Arguments,
                Utc(t.StartedAt),
                t.Duration,
                t.Result,
                t.Error)).ToList(),
            a.Result,
            a.Error)).ToList(),
        new OutcomeV1(
            record.Outcome.Status,
            record.Outcome.Evidence.Select(e => new EvidenceV1(
                e.EvidenceId,
                e.VerificationRoundId,
                e.ArtifactRevision,
                e.CheckId,
                e.Kind,
                e.Result,
                e.Producer,
                e.Detail,
                Utc(e.CapturedAt))).ToList(),
            record.Outcome.Reason,
            Utc(record.Outcome.EvaluatedAt)),
        record.CompletionScore,
        record.Reflection is null
            ? null
            : new ReflectionV1(
                record.Reflection.ReflectionId,
                record.Reflection.ExperienceRunId,
                record.Reflection.Lesson,
                record.Reflection.SuccessfulApproaches.ToList(),
                record.Reflection.FailedApproaches.ToList(),
                record.Reflection.Preconditions.ToList(),
                record.Reflection.Warnings.ToList(),
                record.Reflection.ReuseGuidance,
                record.Reflection.EvidenceIds.ToList(),
                record.Reflection.VerificationStatus,
                record.Reflection.CompletionScore,
                record.Reflection.VerificationRuleVersion,
                record.Reflection.Producer,
                Utc(record.Reflection.CreatedAt)),
        new EnvironmentV1(
            record.Environment.HostName,
            record.Environment.RuntimeVersion,
            record.Environment.OperatingSystem,
            record.Environment.ApplicationVersion,
            new Dictionary<string, string>(record.Environment.Metadata, StringComparer.Ordinal)),
        new ProvenanceV1(
            record.Provenance.Source,
            record.Provenance.SourceVersion,
            Utc(record.Provenance.RecordedAt),
            record.Provenance.CorrelationId),
        record.ClosedRoundId);

    /// <summary>Maps a stored payload plus its column values back to the domain record.</summary>
    public static ExperienceRecord ToRecord(
        PayloadV1 payload,
        Guid experienceId,
        Guid sourceRunId,
        Scope scope,
        string taskId,
        ExperienceStatus status,
        double reuseConfidence,
        int supportingValidations,
        int contradictions,
        long revision,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt) => new(
            experienceId,
            sourceRunId,
            scope,
            taskId,
            payload.TaskSummary,
            payload.Attempts.Select(a => new Attempt(
                a.AttemptId,
                a.SequenceNumber,
                Utc(a.StartedAt),
                a.Duration,
                a.ToolCalls.Select(t => new ToolCallRecord(
                    t.ToolCallId,
                    t.SequenceNumber,
                    t.ToolName,
                    NormalizeArguments(t.Arguments),
                    Utc(t.StartedAt),
                    t.Duration,
                    t.Result,
                    t.Error)).ToList(),
                a.Result,
                a.Error)).ToList(),
            new Outcome(
                payload.Outcome.Status,
                payload.Outcome.Evidence.Select(e => new Evidence(
                    e.EvidenceId,
                    e.VerificationRoundId,
                    e.ArtifactRevision,
                    e.CheckId,
                    e.Kind,
                    e.Result,
                    e.Producer,
                    e.Detail,
                    Utc(e.CapturedAt))).ToList(),
                payload.Outcome.Reason,
                Utc(payload.Outcome.EvaluatedAt)),
            payload.CompletionScore,
            payload.Reflection is null
                ? null
                : new Reflection(
                    payload.Reflection.ReflectionId,
                    payload.Reflection.ExperienceRunId,
                    payload.Reflection.Lesson,
                    payload.Reflection.SuccessfulApproaches,
                    payload.Reflection.FailedApproaches,
                    payload.Reflection.Preconditions,
                    payload.Reflection.Warnings,
                    payload.Reflection.ReuseGuidance,
                    payload.Reflection.EvidenceIds,
                    payload.Reflection.VerificationStatus,
                    payload.Reflection.CompletionScore,
                    payload.Reflection.VerificationRuleVersion,
                    payload.Reflection.Producer,
                    Utc(payload.Reflection.CreatedAt)),
            new EnvironmentFingerprint(
                payload.Environment.HostName,
                payload.Environment.RuntimeVersion,
                payload.Environment.OperatingSystem,
                payload.Environment.ApplicationVersion,
                payload.Environment.Metadata),
            new Provenance(
                payload.Provenance.Source,
                payload.Provenance.SourceVersion,
                Utc(payload.Provenance.RecordedAt),
                payload.Provenance.CorrelationId),
            status,
            reuseConfidence,
            supportingValidations,
            contradictions,
            revision,
            Utc(createdAt),
            Utc(updatedAt))
        {
            ClosedRoundId = payload.ClosedRoundId,
        };

    private static DateTimeOffset Utc(DateTimeOffset value) => value.ToUniversalTime();

    private static IReadOnlyDictionary<string, object?> NormalizeArguments(IReadOnlyDictionary<string, object?> arguments)
    {
        var normalized = new Dictionary<string, object?>(arguments.Count, StringComparer.Ordinal);
        foreach (var (key, value) in arguments)
        {
            normalized[key] = NormalizeValue(value);
        }

        return normalized;
    }

    /// <summary>
    /// Converts a deserialized <see cref="JsonElement"/> into plain CLR values: <see cref="string"/>,
    /// <see cref="bool"/>, <see cref="long"/> (integral numbers that fit), <see cref="double"/>,
    /// <see langword="null"/>, <see cref="Dictionary{TKey,TValue}"/> and <see cref="List{T}"/>.
    /// </summary>
    internal static object? NormalizeValue(object? value) => value is JsonElement element ? NormalizeElement(element) : value;

    private static object? NormalizeElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        // Cast each branch to object: a bare long/double conditional would promote integers to double.
        JsonValueKind.Number => element.TryGetInt64(out var integral) ? (object)integral : (object)element.GetDouble(),
        JsonValueKind.Array => element.EnumerateArray().Select(NormalizeElement).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => NormalizeElement(p.Value), StringComparer.Ordinal),
        _ => throw new ExperienceStoreException("Stored tool-call argument has an unsupported JSON kind."),
    };

    /// <summary>The version-1 payload.</summary>
    /// <remarks>
    /// <c>ClosedRoundId</c> was added within version 1 (story 6.6): it is optional, omitted when null, and
    /// absent from every payload written before it, which reads back as no closed round. An older reader
    /// ignores it.
    /// </remarks>
    internal sealed record PayloadV1(
        string? TaskSummary,
        IReadOnlyList<AttemptV1> Attempts,
        OutcomeV1 Outcome,
        double CompletionScore,
        ReflectionV1? Reflection,
        EnvironmentV1 Environment,
        ProvenanceV1 Provenance,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ClosedRoundId = null);

    internal sealed record AttemptV1(
        Guid AttemptId,
        int SequenceNumber,
        DateTimeOffset StartedAt,
        TimeSpan Duration,
        IReadOnlyList<ToolCallV1> ToolCalls,
        string? Result,
        string? Error);

    internal sealed record ToolCallV1(
        Guid ToolCallId,
        int SequenceNumber,
        string ToolName,
        IReadOnlyDictionary<string, object?> Arguments,
        DateTimeOffset StartedAt,
        TimeSpan Duration,
        string? Result,
        string? Error);

    internal sealed record OutcomeV1(
        TaskVerificationStatus Status,
        IReadOnlyList<EvidenceV1> Evidence,
        string? Reason,
        DateTimeOffset EvaluatedAt);

    internal sealed record EvidenceV1(
        Guid EvidenceId,
        Guid VerificationRoundId,
        string ArtifactRevision,
        string CheckId,
        string Kind,
        CheckResult Result,
        string Producer,
        string? Detail,
        DateTimeOffset CapturedAt);

    internal sealed record ReflectionV1(
        Guid ReflectionId,
        Guid ExperienceRunId,
        string Lesson,
        IReadOnlyList<string> SuccessfulApproaches,
        IReadOnlyList<string> FailedApproaches,
        IReadOnlyList<string> Preconditions,
        IReadOnlyList<string> Warnings,
        string? ReuseGuidance,
        IReadOnlyList<Guid> EvidenceIds,
        TaskVerificationStatus VerificationStatus,
        double CompletionScore,
        string VerificationRuleVersion,
        string Producer,
        DateTimeOffset CreatedAt);

    internal sealed record EnvironmentV1(
        string HostName,
        string RuntimeVersion,
        string OperatingSystem,
        string? ApplicationVersion,
        IReadOnlyDictionary<string, string> Metadata);

    internal sealed record ProvenanceV1(
        string Source,
        string? SourceVersion,
        DateTimeOffset RecordedAt,
        string? CorrelationId);
}

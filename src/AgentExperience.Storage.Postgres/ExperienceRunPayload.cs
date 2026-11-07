using System.Text.Json;
using System.Text.Json.Serialization;
using AgentExperience.Abstractions;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// The run store's versioned JSON payload for one completed <see cref="ExperienceRun"/>: everything but its ID and scope,
/// which live in their own columns. Its attempts, outcome, environment and provenance are written by
/// <see cref="ExperiencePayload"/>'s own mappings, so a captured run's attempts are stored exactly as a record's are, and a
/// field added there is added here. Compiled into the SQLite store from this same file.
/// </summary>
internal static class ExperienceRunPayload
{
    /// <summary>The payload version written by this adapter.</summary>
    public const int CurrentVersion = 1;

    public static string Serialize(ExperienceRun run) => JsonSerializer.Serialize(
        new RunPayloadV1(
            run.TaskId,
            ExperiencePayload.ToV1(run.Environment),
            ExperiencePayload.ToV1(run.Provenance),
            run.Attempts.Select(ExperiencePayload.ToV1).ToList(),
            run.ExecutionStatus ?? throw new ArgumentException("Only a completed run is stored.", nameof(run)),
            run.StartedAt.ToUniversalTime(),
            (run.EndedAt ?? throw new ArgumentException("Only a completed run is stored.", nameof(run))).ToUniversalTime(),
            run.TaskDescription,
            run.Outcome is { } outcome ? ExperiencePayload.ToV1(outcome) : null),
        ExperiencePayload.SerializerOptions);

    /// <summary>Maps a stored payload plus its ID and scope columns back to the run.</summary>
    public static ExperienceRun Deserialize(int version, string json, Guid runId, Scope scope)
    {
        if (version != CurrentVersion)
        {
            throw new ExperienceStoreException(
                $"Stored run has unsupported payload_version {version}; this adapter reads version {CurrentVersion}.");
        }

        RunPayloadV1 payload;
        try
        {
            payload = JsonSerializer.Deserialize<RunPayloadV1>(json, ExperiencePayload.SerializerOptions)
                ?? throw new ExperienceStoreException("Stored run payload is null.");
        }
        catch (JsonException ex)
        {
            throw new ExperienceStoreException($"Stored run payload could not be read as payload_version {CurrentVersion}.", ex);
        }

        return new ExperienceRun(
            runId,
            payload.TaskId,
            payload.TaskDescription,
            scope,
            ExperiencePayload.FromV1(payload.Environment),
            ExperiencePayload.FromV1(payload.Provenance),
            payload.Attempts.Select(ExperiencePayload.FromV1).ToList(),
            payload.ExecutionStatus,
            payload.Outcome is { } outcome ? ExperiencePayload.FromV1(outcome) : null,
            payload.StartedAt.ToUniversalTime(),
            payload.EndedAt.ToUniversalTime());
    }

    /// <summary>The version-1 run payload.</summary>
    internal sealed record RunPayloadV1(
        string TaskId,
        ExperiencePayload.EnvironmentV1 Environment,
        ExperiencePayload.ProvenanceV1 Provenance,
        IReadOnlyList<ExperiencePayload.AttemptV1> Attempts,
        RunExecutionStatus ExecutionStatus,
        DateTimeOffset StartedAt,
        DateTimeOffset EndedAt,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TaskDescription = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExperiencePayload.OutcomeV1? Outcome = null);
}

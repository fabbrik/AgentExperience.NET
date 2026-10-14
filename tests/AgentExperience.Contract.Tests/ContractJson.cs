using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// How the contract's JSON files are written and read: UTF-8, LF, two-space indentation, camelCase members, enums by
/// member name, and a stored record as its table columns plus its payload exactly as the PostgreSQL adapter maps it.
/// </summary>
internal static class ContractJson
{
    /// <summary>The options every fixture input is written and read with.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
    };

    /// <summary>Writes <paramref name="value"/> as a fixture file: indented, LF, one trailing line break.</summary>
    public static string Write<T>(T value) => EscapeInvisible(JsonSerializer.Serialize(value, Options)) + "\n";

    /// <summary>Writes a JSON node as a fixture file.</summary>
    public static string Write(JsonNode node) => EscapeInvisible(node.ToJsonString(Options)) + "\n";

    /// <summary>
    /// Escapes every invisible character -- control, format, and line or paragraph separator -- as <c>\uXXXX</c>, so a
    /// fixture never holds a character a reader cannot see. They can only occur inside JSON strings, where the escape
    /// means the same character.
    /// </summary>
    private static string EscapeInvisible(string json)
    {
        var builder = new StringBuilder(json.Length);
        foreach (var c in json)
        {
            var category = char.GetUnicodeCategory(c);
            var invisible = category is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                || (category == UnicodeCategory.Control && c != '\n');
            if (invisible)
            {
                builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>Reads a fixture file written by <see cref="Write{T}(T)"/>.</summary>
    public static T Read<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidOperationException($"A {typeof(T).Name} fixture is null.");

    /// <summary>
    /// <paramref name="record"/> as the table holds it: the record columns, named as in
    /// <c>agent_experience.experience_records</c>, and the payload column as <c>ExperiencePayload</c> serializes it.
    /// </summary>
    public static JsonObject ToStored(ExperienceRecord record) => new()
    {
        ["columns"] = new JsonObject
        {
            ["experience_id"] = record.ExperienceId.ToString("D", CultureInfo.InvariantCulture),
            ["source_run_id"] = record.SourceRunId.ToString("D", CultureInfo.InvariantCulture),
            ["tenant_id"] = record.Scope.TenantId,
            ["application_id"] = record.Scope.ApplicationId,
            ["project_id"] = record.Scope.ProjectId,
            ["team_id"] = record.Scope.TeamId,
            ["agent_id"] = record.Scope.AgentId,
            ["user_id"] = record.Scope.UserId,
            ["task_id"] = record.TaskId,
            ["status"] = record.Status.ToString(),
            ["reuse_confidence"] = record.ReuseConfidence,
            ["supporting_validations"] = record.SupportingValidations,
            ["contradictions"] = record.Contradictions,
            ["revision"] = record.Revision,
            ["created_at"] = Timestamp(record.CreatedAt),
            ["updated_at"] = Timestamp(record.UpdatedAt),
            ["payload_version"] = ExperiencePayload.CurrentVersion,
        },
        ["payload"] = JsonNode.Parse(ExperiencePayload.Serialize(record)),
    };

    /// <summary>
    /// Reads a stored record back exactly as the PostgreSQL adapter reads a row: the columns as they are, and the payload
    /// through <c>ExperiencePayload.Deserialize</c> and <c>ToRecord</c>.
    /// </summary>
    public static ExperienceRecord FromStored(JsonNode stored)
    {
        var columns = stored["columns"]!.AsObject();
        var payloadVersion = columns["payload_version"]!.GetValue<int>();
        var payload = ExperiencePayload.Deserialize(payloadVersion, stored["payload"]!.ToJsonString());

        return ExperiencePayload.ToRecord(
            payload,
            Guid.Parse(columns["experience_id"]!.GetValue<string>()),
            Guid.Parse(columns["source_run_id"]!.GetValue<string>()),
            new Scope(
                columns["tenant_id"]!.GetValue<string>(),
                columns["application_id"]!.GetValue<string>(),
                columns["project_id"]!.GetValue<string>(),
                columns["team_id"]?.GetValue<string>(),
                columns["agent_id"]?.GetValue<string>(),
                columns["user_id"]?.GetValue<string>()),
            columns["task_id"]!.GetValue<string>(),
            Status(columns["status"]!.GetValue<string>()),
            columns["reuse_confidence"]!.GetValue<double>(),
            columns["supporting_validations"]!.GetValue<int>(),
            columns["contradictions"]!.GetValue<int>(),
            columns["revision"]!.GetValue<long>(),
            ParseTimestamp(columns["created_at"]!.GetValue<string>()),
            ParseTimestamp(columns["updated_at"]!.GetValue<string>()));
    }

    /// <summary>An instant as the payload writes one: ISO 8601 in UTC with the offset <c>+00:00</c>.</summary>
    public static string Timestamp(DateTimeOffset value) => JsonSerializer.Serialize(value.ToUniversalTime()).Trim('"');

    private static DateTimeOffset ParseTimestamp(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>A status by its exact member name, as the store decodes it: no number, no other casing.</summary>
    public static ExperienceStatus Status(string text) =>
        Enum.TryParse<ExperienceStatus>(text, ignoreCase: false, out var status) && Enum.IsDefined(status) && status.ToString() == text
            ? status
            : throw new InvalidOperationException($"'{text}' is not an ExperienceStatus member name.");
}

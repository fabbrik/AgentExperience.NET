using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Verification;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// One encoding case: what kind of value <see cref="Input"/> is, whether the library accepts it, and, when it does, the
/// form the contract writes it in (<see langword="null"/> when refused).
/// </summary>
internal sealed record EncodingCase(string Kind, JsonNode Input, bool Valid, JsonNode? Canonical);

/// <summary>
/// The encoding family (contract 1.1): <c>fixtures/encoding/cases.json</c>. Values a port reads or derives on its own,
/// with no record around them, where its language's own parser or encoder differs from .NET's:
/// <list type="bullet">
/// <item><c>instant</c>: an ISO 8601 instant as a stored record carries one, read by the converter the payload reader
/// uses; its canonical form is the payload's (UTC, <c>+00:00</c>, trailing zeros of the fraction dropped).</item>
/// <item><c>duration</c>: a <c>TimeSpan</c> in the constant format, read the same way; canonical as the payload writes it.</item>
/// <item><c>non-blank</c>: a required check ID, accepted by verification when it is not blank. Every non-blank rule in
/// the contract (task IDs, check IDs, kinds, revisions) is .NET's <c>string.IsNullOrWhiteSpace</c>.</item>
/// <item><c>task-id</c>: a task ID, refused when blank by the agent integration's options.</item>
/// <item><c>experience-id</c>: the record ID derived from a run ID and a scope whose fields are given as UTF-16 code
/// units (an array of integers, or <see langword="null"/>), since a JSON string cannot carry a lone surrogate.</item>
/// </list>
/// </summary>
internal static class EncodingContract
{
    public const string Family = "encoding";

    public const string FileName = "cases.json";

    private static readonly string[] Instants =
    [
        "2026-03-02T09:00:00+00:00",
        "2026-03-02T09:00:00Z",
        "2026-03-02T09:00:00.1234567+00:00",
        "2026-03-02T09:00:00.5000000+01:30",
        "2026-03-02T23:59:59+14:00",
        "2026-03-02T23:59:59-14:00",
        "2026-03-02T24:00:00+00:00",
        "2026-03-02T09:60:00+00:00",
        "2026-03-02T09:00:60+00:00",
        "2026-03-02T09:00:00+14:01",
        "2026-03-02T09:00:00-14:01",
        "2026-03-02T09:00:00+15:00",
        "2026-03-02T09:00:00+01:60",
        "2026-03-02T09:00:00.12345678+00:00",
        "2026-03-02t09:00:00+00:00",
        "2026-03-02T09:00:00z",
        "2026-02-29T09:00:00+00:00",
        "2028-02-29T09:00:00+00:00",
        "2026-3-2T9:0:0+00:00",
    ];

    private static readonly string[] Durations =
    [
        "00:00:03.5000000",
        "00:00:03.5",
        "1.02:03:04",
        "-00:00:01",
        "23:59:59.9999999",
        "24:00:00",
        "00:60:00",
        "00:00:60",
        "1:2:3",
        "-1.02:03:04",
        "00:00:00.12345678",
        "10675199.02:48:05.4775807",
        "10675200.00:00:00",
    ];

    private static readonly string[] TaskIds = ["refund-stuck-pending", " ", "\u0085", "\u001C"];

    private static readonly string[] NonBlank =
    [
        "check",
        "",
        " ",
        "\t\n\v\f\r",
        "\u0085",
        "\u00A0",
        "\u2000",
        "\u2028",
        "\u3000",
        "\u001C",
        "\u001D",
        "\u001E",
        "\u001F",
        "\u200B",
        "\u180E",
    ];

    /// <summary>Every encoding case, the answers being the library's.</summary>
    public static IReadOnlyList<EncodingCase> Run() =>
    [
        .. Instants.Select(Instant),
        .. Durations.Select(Duration),
        .. NonBlank.Select(CheckId),
        .. TaskIds.Select(TaskId),
        .. ExperienceIds().Select(ExperienceId),
    ];

    /// <summary>Re-runs a case read from disk, so the checked-in answer is checked against its checked-in input.</summary>
    public static EncodingCase Rerun(EncodingCase read) => read.Kind switch
    {
        "instant" => Instant(read.Input.GetValue<string>()),
        "duration" => Duration(read.Input.GetValue<string>()),
        "non-blank" => CheckId(read.Input.GetValue<string>()),
        "task-id" => TaskId(read.Input.GetValue<string>()),
        "experience-id" => ExperienceId(read.Input),
        var other => throw new InvalidOperationException($"Unknown encoding kind '{other}'."),
    };

    private static EncodingCase Instant(string text)
    {
        // The converter the payload reader (ExperiencePayload) reads every instant with: System.Text.Json's own.
        DateTimeOffset value;
        try
        {
            value = JsonSerializer.Deserialize<DateTimeOffset>(JsonSerializer.Serialize(text));
        }
        catch (JsonException)
        {
            return new("instant", text, false, null);
        }

        return new("instant", text, true, ContractJson.Timestamp(value));
    }

    private static EncodingCase Duration(string text)
    {
        TimeSpan value;
        try
        {
            value = JsonSerializer.Deserialize<TimeSpan>(JsonSerializer.Serialize(text));
        }
        catch (JsonException)
        {
            return new("duration", text, false, null);
        }

        return new("duration", text, true, JsonSerializer.Serialize(value).Trim('"'));
    }

    private static EncodingCase CheckId(string text)
    {
        try
        {
            _ = VerificationAggregator.Aggregate(Fixed.Id(0xe0, 1), [], [new RequiredCheck(text, "integration-test")], null, "rev-1", Fixed.T0);
        }
        catch (ArgumentException)
        {
            return new("non-blank", text, false, null);
        }

        return new("non-blank", text, true, text);
    }

    /// <summary>
    /// A task ID as the agent integration's options validate one (<c>AgentExperienceOptions.TaskId</c>, the task a run is
    /// captured under): refused when blank. The record schema's <c>[^\s]</c> pattern is not this rule: under ECMA-262
    /// regular expressions U+0085 is not whitespace, so the schema accepts a task ID that is blank here.
    /// </summary>
    private static EncodingCase TaskId(string text)
    {
        var options = new AgentExperience.MicrosoftAgentFramework.AgentExperienceOptions
        {
            ResolveIdentity = (_, _) => ValueTask.FromResult<AgentExperience.MicrosoftAgentFramework.ExperienceIdentity?>(null),
            TaskId = text,
        };
        try
        {
            typeof(AgentExperience.MicrosoftAgentFramework.AgentExperienceOptions)
                .GetMethod("Validate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(options, null);
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is ArgumentException { ParamName: "TaskId" })
        {
            return new("task-id", text, false, null);
        }

        return new("task-id", text, true, text);
    }

    /// <summary>Run ID and scope inputs: a lone surrogate (high and low) in different fields, a pair, and their U+FFFD equivalents.</summary>
    private static IEnumerable<JsonNode> ExperienceIds()
    {
        var run = Fixed.Id(0xe1, 0).ToString("D", CultureInfo.InvariantCulture);
        yield return Input(run, Units("contract-tenant"), Units("contract-app"), Units("contract-project"), null, Units("refund-agent"), null);
        yield return Input(run, Units("t\uD800"), Units("a"), Units("p"), null, null, null);
        yield return Input(run, Units("t\uFFFD"), Units("a"), Units("p"), null, null, null);
        yield return Input(run, Units("t"), Units("a"), Units("p"), Units("\uDC00team"), null, Units("user\uD83E"));
        yield return Input(run, Units("t"), Units("a"), Units("p"), Units("\uFFFDteam"), null, Units("user\uFFFD"));
        yield return Input(run, Units("t"), Units("a"), Units("p"), null, Units("agent \uD83E\uDDFE"), null);
        yield return Input(run, Units("t\uDC00\uD800"), Units("a"), Units("p"), null, null, null);
        yield return Input(run, Units("t\uFFFD\uFFFD"), Units("a"), Units("p"), null, null, null);
        yield return Input(run, Units("t\uD83E"), Units("\uDDFEa"), Units("p"), null, null, null);
        yield return Input(run, Units("t\uFFFD"), Units("\uFFFDa"), Units("p"), null, null, null);
        yield return Input(run, Units("t"), Units("a"), Units("p"), Units(""), null, Units(""));
        yield return Input(run, Units("t"), Units("a"), Units("p"), null, Units(""), null);

        static JsonNode Input(string runId, int[] tenant, int[] application, int[] project, int[]? team, int[]? agent, int[]? user) => new JsonObject
        {
            ["runId"] = runId,
            ["scopeUtf16"] = new JsonObject
            {
                ["tenantId"] = Array(tenant),
                ["applicationId"] = Array(application),
                ["projectId"] = Array(project),
                ["teamId"] = Array(team),
                ["agentId"] = Array(agent),
                ["userId"] = Array(user),
            },
        };

        static JsonArray? Array(int[]? units) => units is null ? null : new JsonArray([.. units.Select(unit => (JsonNode)unit)]);

        static int[] Units(string text) => [.. text.Select(c => (int)c)];
    }

    /// <summary>
    /// The record ID for a run and scope. Always valid: the tenant, application and project are never null in a case,
    /// and every string derives an ID. A field is never paired across into the next, since each is encoded on its own.
    /// </summary>
    private static EncodingCase ExperienceId(JsonNode input)
    {
        var scopeUnits = input["scopeUtf16"]!;
        var scope = new Scope(
            Text(scopeUnits["tenantId"]) ?? throw new InvalidOperationException("An experience-id case has a null tenantId."),
            Text(scopeUnits["applicationId"]) ?? throw new InvalidOperationException("An experience-id case has a null applicationId."),
            Text(scopeUnits["projectId"]) ?? throw new InvalidOperationException("An experience-id case has a null projectId."),
            Text(scopeUnits["teamId"]),
            Text(scopeUnits["agentId"]),
            Text(scopeUnits["userId"]));

        var id = ExperienceFinalizationService.ExperienceIdFor(Guid.Parse(input["runId"]!.GetValue<string>()), scope);
        return new("experience-id", input.DeepClone(), true, id.ToString("D", CultureInfo.InvariantCulture));

        static string? Text(JsonNode? units) => units is null ? null : new string([.. units.AsArray().Select(unit => Unit(unit!.GetValue<int>()))]);

        static char Unit(int code) => code is >= 0 and <= 0xFFFF
            ? (char)code
            : throw new InvalidOperationException($"{code} is not a UTF-16 code unit (0 to 65535).");
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace AgentExperience.Storage.Postgres.Tests;

internal static class TestRecords
{
    /// <summary>A microsecond-precise UTC instant, storable in a timestamptz column without truncation.</summary>
    public static readonly DateTimeOffset ColumnTime = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero).AddTicks(1_234_560);

    /// <summary>A full 100 ns precision UTC instant for payload timestamps.</summary>
    public static readonly DateTimeOffset PayloadTime = new DateTimeOffset(2026, 9, 17, 9, 0, 0, TimeSpan.Zero).AddTicks(7_654_321);

    public static string NewTenant() => "tenant-" + Guid.NewGuid().ToString("N");

    public static Scope Scope(string tenant, string project = "project-1", string? team = null) =>
        new(tenant, "app-1", project, team, AgentId: null, UserId: null);

    public static AuthorizationContext Authorize(string tenant) =>
        new(tenant, "host-principal", ["experience:write"], ColumnTime);

    /// <summary>A data source pointing at a closed local port: any connection attempt fails fast.</summary>
    public static NpgsqlDataSource Unreachable() =>
        NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=nobody;Password=nothing;Database=none;Timeout=3;Pooling=false");

    public static ExperienceRecord Minimal(Scope scope, Guid? id = null, ExperienceStatus status = ExperienceStatus.Candidate, DateTimeOffset? createdAt = null) => new(
        ExperienceId: id ?? Guid.NewGuid(),
        SourceRunId: Guid.NewGuid(),
        Scope: scope,
        TaskId: "task-1",
        TaskSummary: null,
        Attempts: [],
        Outcome: new Outcome(TaskVerificationStatus.Unknown, [], null, PayloadTime),
        CompletionScore: 0,
        Reflection: null,
        Environment: new EnvironmentFingerprint("host", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
        Provenance: new Provenance("tests", null, PayloadTime, null),
        Status: status,
        ReuseConfidence: 0,
        SupportingValidations: 0,
        Contradictions: 0,
        Revision: 0,
        CreatedAt: createdAt ?? ColumnTime,
        UpdatedAt: createdAt ?? ColumnTime);

    /// <summary>A record with every optional part populated, including nested tool-call argument shapes.</summary>
    public static ExperienceRecord Full(Scope scope)
    {
        var evidenceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        return new ExperienceRecord(
            ExperienceId: Guid.NewGuid(),
            SourceRunId: runId,
            Scope: scope,
            TaskId: "support-ticket-resolution",
            TaskSummary: "Resolve refund ticket",
            Attempts:
            [
                new Attempt(
                    Guid.NewGuid(),
                    0,
                    PayloadTime,
                    TimeSpan.FromMilliseconds(1500.5),
                    [
                        new ToolCallRecord(
                            Guid.NewGuid(),
                            0,
                            "search_docs",
                            new Dictionary<string, object?>
                            {
                                ["query"] = "refund policy",
                                ["limit"] = 3L,
                                ["threshold"] = 0.75,
                                ["exact"] = true,
                                ["cursor"] = null,
                                ["filters"] = new Dictionary<string, object?> { ["lang"] = "en", ["tags"] = new List<object?> { "a", 2L, false } },
                            },
                            PayloadTime.AddTicks(1),
                            TimeSpan.FromTicks(123_456_789),
                            "3 documents",
                            null),
                        new ToolCallRecord(Guid.NewGuid(), 1, "update_ticket", new Dictionary<string, object?>(), PayloadTime.AddSeconds(1), TimeSpan.Zero, null, "System.TimeoutException"),
                    ],
                    null,
                    "could not update ticket"),
                new Attempt(Guid.NewGuid(), 1, PayloadTime.AddSeconds(2), TimeSpan.FromSeconds(2), [], "ticket updated", null),
            ],
            Outcome: new Outcome(
                TaskVerificationStatus.Verified,
                [new Evidence(evidenceId, Guid.NewGuid(), "rev-7", "unit-tests-pass", "TestResult", CheckResult.Pass, "ci", "42 of 42 passed", PayloadTime)],
                "all required checks passed",
                PayloadTime.AddMinutes(1)),
            CompletionScore: 1,
            Reflection: new Reflection(
                Guid.NewGuid(),
                runId,
                "Retry the ticket update after the lock clears.",
                ["retry after lock"],
                ["immediate update"],
                ["ticket system reachable"],
                ["lock duration unknown"],
                "Use for ticket-lock failures only.",
                [evidenceId],
                TaskVerificationStatus.Verified,
                1,
                "v1",
                "template-reflector/1.0",
                PayloadTime.AddMinutes(2)),
            Environment: new EnvironmentFingerprint("worker-01", "10.0.0", "linux-x64", "1.2.3", new Dictionary<string, string> { ["region"] = "us-east", ["az"] = "1b", ["a"] = "x" }),
            Provenance: new Provenance("AgentExperience.MicrosoftAgentFramework", "1.0.0", PayloadTime, "trace-123"),
            Status: ExperienceStatus.Validated,
            ReuseConfidence: 2d / 3d,
            SupportingValidations: 4,
            Contradictions: 1,
            Revision: 3,
            CreatedAt: ColumnTime,
            UpdatedAt: ColumnTime.AddSeconds(5));
    }

    /// <summary>
    /// Canonical JSON with object keys sorted recursively, so records compare deep-equal after JSON
    /// normalization regardless of dictionary key order or CLR numeric type.
    /// </summary>
    public static string Canonical(ExperienceRecord record) => Sort(JsonSerializer.SerializeToNode(record))?.ToJsonString() ?? "null";

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sort(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Sort).ToArray()),
        null => null,
        _ => JsonNode.Parse(node.ToJsonString()),
    };
}

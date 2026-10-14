using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// <c>contract/schema/experience-record.v1.json</c>, evaluated by a real JSON Schema (draft 2020-12) evaluator, and the
/// stored-format round trip every record in a fixture must survive.
/// </summary>
internal static class RecordSchema
{
    private static readonly Lazy<JsonSchema> Schema = new(() =>
        JsonSchema.FromText(ContractFiles.Read("schema/experience-record.v1.json") ?? throw new InvalidOperationException("The record schema is missing.")));

    /// <summary>
    /// Checks one stored record: it validates against the schema, and reading it the way the PostgreSQL adapter reads a
    /// row and writing it back gives exactly the same JSON.
    /// </summary>
    public static void Check(string where, JsonNode stored, List<string> problems)
    {
        using var document = JsonDocument.Parse(stored.ToJsonString());
        var results = Schema.Value.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!results.IsValid)
        {
            var errors = (results.Details ?? [])
                .Where(detail => detail.Errors is { Count: > 0 })
                .SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Value}"))
                .Take(10);
            problems.Add($"{where}: does not validate against experience-record.v1.json: {string.Join("; ", errors)}");
        }

        var roundTripped = ContractJson.ToStored(ContractJson.FromStored(stored));
        if (!JsonNode.DeepEquals(roundTripped, stored))
        {
            problems.Add($"{where}: does not survive the adapter's read and write unchanged.\n    was: {stored.ToJsonString()}\n    now: {roundTripped.ToJsonString()}");
        }
    }
}

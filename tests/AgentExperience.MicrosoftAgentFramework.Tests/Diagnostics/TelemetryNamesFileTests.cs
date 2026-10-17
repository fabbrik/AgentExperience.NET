using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Indexing;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Core.Retrieval;
using AgentExperience.Core.Feedback;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.MicrosoftAgentFramework.Diagnostics;

namespace AgentExperience.MicrosoftAgentFramework.Tests.Diagnostics;

/// <summary>
/// Story 23.5: <c>docs/telemetry-names.json</c> is generated from the .NET definitions and pins the names the Python and
/// Go ports may emit. It never updates itself: set <c>AGENTEXPERIENCE_TELEMETRY_NAMES_UPDATE=1</c> to rewrite it, then
/// read the diff and commit it on purpose. The ports' own tests load this file.
/// </summary>
public class TelemetryNamesFileTests
{
    private const string UpdateVariable = "AGENTEXPERIENCE_TELEMETRY_NAMES_UPDATE";

    private static readonly Type CoreHolder =
        typeof(DefaultSanitizer).Assembly.GetType("AgentExperience.Core.Diagnostics.ExperienceDiagnostics", throwOnError: true)!;

    private static readonly Type CoreOperationNames =
        typeof(DefaultSanitizer).Assembly.GetType("AgentExperience.Core.Diagnostics.ExperienceOperationNames", throwOnError: true)!;

    private static readonly Type CoreErrorClass =
        typeof(DefaultSanitizer).Assembly.GetType("AgentExperience.Core.Diagnostics.ExperienceOperationErrorClass", throwOnError: true)!;

    [Fact]
    public void The_names_file_matches_the_definitions()
    {
        var generated = Generate();
        var path = Path.Combine(DocsDirectory(), "telemetry-names.json");

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(path, generated, new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(path), $"docs/telemetry-names.json is missing; set {UpdateVariable}=1 to generate it.");
        Assert.Equal(generated, File.ReadAllText(path, Encoding.UTF8));
    }

    private static string Generate()
    {
        var coreOperations = new (string Constant, Type Outcomes)[]
        {
            ("CaptureStartRun", typeof(StartRunOutcome)),
            ("CaptureAppendAttempt", typeof(AppendAttemptOutcome)),
            ("CaptureCompleteRun", typeof(CompleteRunOutcome)),
            ("Verify", typeof(TaskVerificationStatus)),
            ("Reflect", typeof(TaskVerificationStatus)),
            ("Finalize", typeof(FinalizationOutcome)),
            ("LifecycleCommit", typeof(LifecycleTransitionOutcome)),
            ("ConfidenceApply", typeof(ConfidenceUpdateOutcome)),
            ("Retrieve", typeof(RetrievalOutcome)),
            ("Index", typeof(ExperienceIndexingOutcome)),
            ("Deindex", typeof(ExperienceDeindexingOutcome)),
            ("Reindex", typeof(ExperienceReindexOutcome)),
            ("ReuseFeedback", typeof(ExperienceReuseFeedbackOutcome)),
        };

        var coreSource = Str(CoreHolder, "SourceName");
        var adapterSource = InjectionDiagnostics.SourceName;
        var prefix = Str(CoreHolder, "SpanNamePrefix");

        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            json.WriteStartObject();
            json.WriteString("generatedFrom", "tests/AgentExperience.MicrosoftAgentFramework.Tests/Diagnostics/TelemetryNamesFileTests.cs");
            json.WriteStartArray("sources");
            json.WriteStringValue(coreSource);
            json.WriteStringValue(adapterSource);
            json.WriteEndArray();
            json.WriteString("spanNamePrefix", prefix);
            json.WriteString("faultedOutcome", Str(CoreHolder, "FaultedOutcome"));

            json.WriteStartArray("instruments");
            foreach (var (field, kind) in new[] { ("Operations", "counter"), ("Durations", "histogram"), ("Failures", "counter") })
            {
                var instrument = (Instrument)CoreHolder.GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                json.WriteStartObject();
                json.WriteString("name", instrument.Name);
                json.WriteString("kind", kind);
                json.WriteString("unit", instrument.Unit);
                json.WriteEndObject();
            }

            json.WriteEndArray();

            json.WriteStartArray("dimensions");
            foreach (var name in new[] { "OperationDimension", "OutcomeDimension", "ErrorClassDimension", "NestedDimension" })
            {
                json.WriteStringValue(Str(CoreHolder, name));
            }

            json.WriteEndArray();

            json.WriteStartArray("errorClasses");
            foreach (var name in Enum.GetNames(CoreErrorClass))
            {
                json.WriteStringValue(name);
            }

            json.WriteEndArray();

            var attributes = Constants(CoreHolder, "Attribute").Concat(Constants(typeof(InjectionDiagnostics), "Attribute"))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            json.WriteStartArray("attributes");
            foreach (var attribute in attributes)
            {
                json.WriteStringValue(attribute);
            }

            json.WriteEndArray();

            json.WriteStartArray("operations");
            foreach (var (constant, outcomes) in coreOperations)
            {
                Operation(json, Str(CoreOperationNames, constant), coreSource, true, Enum.GetNames(outcomes));
            }

            Operation(json, InjectionDiagnostics.Inject, adapterSource, true, Enum.GetNames<InjectionOutcome>());
            Operation(
                json,
                InjectionDiagnostics.RecordInjection,
                adapterSource,
                false,
                [
                    InjectionDiagnostics.RecordedOutcome,
                    InjectionDiagnostics.NotTrackedOutcome,
                    InjectionDiagnostics.NotRecordedOutcome,
                    InjectionDiagnostics.OutcomeUnknownOutcome,
                ]);
            json.WriteEndArray();

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void Operation(Utf8JsonWriter json, string operation, string source, bool span, string[] outcomes)
    {
        json.WriteStartObject();
        json.WriteString("operation", operation);
        json.WriteString("source", source);
        json.WriteBoolean("span", span);
        json.WriteStartArray("outcomes");
        foreach (var outcome in outcomes)
        {
            json.WriteStringValue(outcome);
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static string Str(Type holder, string name) =>
        (string)holder.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!.GetRawConstantValue()!;

    private static IEnumerable<string> Constants(Type holder, string suffix) =>
        holder.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string) && field.Name.EndsWith(suffix, StringComparison.Ordinal))
            .Select(field => (string)field.GetRawConstantValue()!);

    private static string DocsDirectory([CallerFilePath] string thisFile = "")
    {
        var docs = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "..", "docs"));
        Assert.True(File.Exists(Path.Combine(docs, "telemetry.md")), $"'{docs}' is not the docs directory.");
        return docs;
    }
}

using System.Collections;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;

namespace AgentExperience.Storage.InMemory;

/// <summary>
/// What the in-memory stores keep: a deep copy of what they were given, taken on write, with every collection a
/// read-only wrapper over a private array or dictionary, and the store-stamped timestamps normalized exactly as the
/// PostgreSQL store's <c>timestamptz</c> columns normalize them. A caller that mutates its own collections afterwards
/// changes nothing stored, and a snapshot handed back to one reader is safe to hand to every other.
/// </summary>
internal static class StoredSnapshots
{
    /// <summary>UTC, truncated to whole microseconds: what PostgreSQL's <c>timestamptz</c> keeps of an instant.</summary>
    public static DateTimeOffset Timestamp(DateTimeOffset value)
    {
        var utcTicks = value.UtcTicks;
        return new DateTimeOffset(utcTicks - (utcTicks % 10), TimeSpan.Zero);
    }

    /// <summary>
    /// A deep, read-only copy of a validated record, with <see cref="ExperienceRecord.CreatedAt"/> and
    /// <see cref="ExperienceRecord.UpdatedAt"/> normalized as the PostgreSQL store's columns are.
    /// </summary>
    public static ExperienceRecord Record(ExperienceRecord record) => record with
    {
        Attempts = List(record.Attempts, Attempt),
        Outcome = record.Outcome with { Evidence = List(record.Outcome.Evidence) },
        Reflection = record.Reflection is { } reflection ? Reflection(reflection) : null,
        Environment = record.Environment with { Metadata = Dictionary(record.Environment.Metadata) },
        Provenance = record.Provenance with { ExposedTo = List(record.Provenance.ExposedTo) },
        ProvenanceSignature = record.ProvenanceSignature is { } signature ? signature with { Value = signature.Value.ToArray() } : null,
        CreatedAt = Timestamp(record.CreatedAt),
        UpdatedAt = Timestamp(record.UpdatedAt),
    };

    /// <summary>A lifecycle event as stored: its <see cref="LifecycleEvent.OccurredAt"/> normalized, as the PostgreSQL store stores it.</summary>
    public static LifecycleEvent Event(LifecycleEvent lifecycleEvent) =>
        lifecycleEvent with { OccurredAt = Timestamp(lifecycleEvent.OccurredAt) };

    /// <summary>A deep, read-only copy of a validated feedback submission, its timestamps normalized.</summary>
    public static RecordedExperienceReuseFeedback Feedback(RecordedExperienceReuseFeedback feedback) => feedback with
    {
        EvidenceIds = List(feedback.EvidenceIds),
        Exposures = List(feedback.Exposures),
        AttributedAt = feedback.AttributedAt is { } attributedAt ? Timestamp(attributedAt) : null,
        ObservedAt = Timestamp(feedback.ObservedAt),
    };

    private static Attempt Attempt(Attempt attempt) => attempt with { ToolCalls = List(attempt.ToolCalls, ToolCall) };

    private static ToolCallRecord ToolCall(ToolCallRecord toolCall) => toolCall with { Arguments = Arguments(toolCall.Arguments) };

    private static Reflection Reflection(Reflection reflection) => reflection with
    {
        SuccessfulApproaches = List(reflection.SuccessfulApproaches),
        FailedApproaches = List(reflection.FailedApproaches),
        Preconditions = List(reflection.Preconditions),
        Warnings = List(reflection.Warnings),
        EvidenceIds = List(reflection.EvidenceIds),
    };

    private static ReadOnlyCollection<T> List<T>(IReadOnlyList<T> source, Func<T, T>? copy = null)
    {
        var items = new T[source.Count];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = copy is null ? source[i] : copy(source[i]);
        }

        return Array.AsReadOnly(items);
    }

    private static ReadOnlyDictionary<string, string> Dictionary(IReadOnlyDictionary<string, string> source) =>
        new(source.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    private static ReadOnlyDictionary<string, object?> Arguments(IEnumerable<KeyValuePair<string, object?>> source) =>
        new(source.ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal));

    /// <summary>
    /// One tool-call argument value, copied as deep as its shape allows: nested objects and lists become read-only
    /// copies, JSON values are cloned off their documents, and immutable values (strings, numbers, booleans) are kept.
    /// </summary>
    private static object? Value(object? value) => value switch
    {
        null or string => value,
        JsonElement element => element.Clone(),
        JsonNode node => node.DeepClone(),
        byte[] bytes => bytes.Clone(),
        IEnumerable<KeyValuePair<string, object?>> nested => Arguments(nested),
        IEnumerable sequence and not IDictionary => Array.AsReadOnly(sequence.Cast<object?>().Select(Value).ToArray()),
        _ => value,
    };
}

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AgentExperience.MicrosoftAgentFramework.Tests.Diagnostics;

/// <summary>One measurement a <see cref="TelemetryProbe"/> saw, flattened for assertions.</summary>
/// <param name="Instrument">The instrument's name.</param>
/// <param name="Meter">The meter that published it, which is the emitting assembly's source name.</param>
/// <param name="Value">The recorded value, widened to <see cref="double"/>.</param>
/// <param name="Tags">The measurement's dimensions.</param>
internal sealed record ProbedMeasurement(
    string Instrument,
    string Meter,
    double Value,
    IReadOnlyDictionary<string, object?> Tags);

/// <summary>
/// The host, in miniature, for the adapter's side of the story: it registers the listeners the
/// library is forbidden to create and collects what arrives.
/// </summary>
/// <remarks>
/// <b>It listens to <c>AgentExperience.*</c> only.</b> The content sweeps assert that no tag value on
/// any collected span carries captured text; run over every source in the process, they would be
/// asserting that about MAF's own <c>gen_ai</c> spans too, and a live MAF instrumentation that put the
/// prompt on its own span would fail this library's test for someone else's tags.
/// <see cref="EverySource"/> widens the span listener, and exactly one test uses it: the one whose
/// claim is that nothing agent-, model-, or tool-shaped comes from an <c>AgentExperience.*</c> source
/// while MAF's delegation runs.
/// </remarks>
internal sealed class TelemetryProbe : IDisposable
{
    /// <summary>The prefix a host subscribes with.</summary>
    internal const string SourcePrefix = "AgentExperience.";

    private readonly object _gate = new();
    private readonly List<Activity> _activities = [];
    private readonly List<ProbedMeasurement> _measurements = [];
    private readonly ActivityListener _activityListener;
    private readonly MeterListener _meterListener;

    private TelemetryProbe(bool everySource)
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => everySource || source.Name.StartsWith(SourcePrefix, StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                lock (_gate)
                {
                    Started?.Invoke(activity);
                }
            },
            ActivityStopped = activity =>
            {
                lock (_gate)
                {
                    _activities.Add(activity);
                }
            },
        };

        ActivitySource.AddActivityListener(_activityListener);

        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name.StartsWith(SourcePrefix, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };

        _meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) => Add(instrument, measurement, tags));
        _meterListener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) => Add(instrument, measurement, tags));
        _meterListener.Start();
    }

    /// <summary>Runs for every activity the moment it starts, whatever source it came from.</summary>
    internal Action<Activity>? Started { get; set; }

    /// <summary>Starts listening to this library's own sources and meters, which is what a host subscribes to.</summary>
    internal static TelemetryProbe Start() => new(everySource: false);

    /// <summary>Starts listening to every source in the process, for the non-duplication proof only.</summary>
    internal static TelemetryProbe EverySource() => new(everySource: true);

    /// <summary>Every finished activity, from every source, in the order it stopped.</summary>
    internal IReadOnlyList<Activity> Activities
    {
        get
        {
            lock (_gate)
            {
                return [.. _activities];
            }
        }
    }

    /// <summary>The finished activities this library emitted.</summary>
    internal IReadOnlyList<Activity> LibraryActivities =>
        [.. Activities.Where(activity => activity.Source.Name.StartsWith(SourcePrefix, StringComparison.Ordinal))];

    /// <summary>Every measurement, in the order it was recorded.</summary>
    internal IReadOnlyList<ProbedMeasurement> Measurements
    {
        get
        {
            lock (_gate)
            {
                return [.. _measurements];
            }
        }
    }

    /// <summary>The measurements recorded to one instrument.</summary>
    /// <param name="instrument">The instrument name.</param>
    internal IReadOnlyList<ProbedMeasurement> For(string instrument) =>
        [.. Measurements.Where(measurement => string.Equals(measurement.Instrument, instrument, StringComparison.Ordinal))];

    /// <summary>The measurements recorded to one instrument for one <c>operation</c> dimension value.</summary>
    /// <param name="instrument">The instrument name.</param>
    /// <param name="operation">The <c>operation</c> dimension value.</param>
    internal IReadOnlyList<ProbedMeasurement> For(string instrument, string operation) =>
        [.. For(instrument).Where(measurement =>
            measurement.Tags.TryGetValue("operation", out var value) && Equals(value, operation))];

    /// <summary>The measurements recorded to one instrument for one <c>operation</c>, split by whether another instrumented operation called it.</summary>
    /// <param name="instrument">The instrument name.</param>
    /// <param name="operation">The <c>operation</c> dimension value.</param>
    /// <param name="nested">The <c>nested</c> dimension value to match.</param>
    internal IReadOnlyList<ProbedMeasurement> For(string instrument, string operation, bool nested) =>
        [.. For(instrument, operation).Where(measurement =>
            measurement.Tags.TryGetValue("nested", out var value) && Equals(value, nested))];

    /// <summary>
    /// Every tag value, display name and status description on any span <em>this library</em> emitted.
    /// Scoped to the library on purpose: what MAF's own spans carry is MAF's business, and sweeping it
    /// here would make this library's content guarantee fail on someone else's tags.
    /// </summary>
    internal IReadOnlyList<string> EverySpanTagValue =>
        [.. LibraryActivities
            .SelectMany(activity => activity.TagObjects)
            .Select(tag => tag.Value?.ToString())
            .Concat(LibraryActivities.Select(activity => activity.StatusDescription))
            .Concat(LibraryActivities.Select(activity => activity.DisplayName))
            .Where(value => value is not null)
            .Select(value => value!)];

    /// <summary>
    /// Every attribute <em>key</em> on any span this library emitted, pinned exactly the way the
    /// metric dimension set is pinned. A marker sweep only proves that the content one drive happened
    /// to carry stayed off a span; an exact key set is what stops a new attribute carrying host free
    /// text from being added without anyone having to say what it carries.
    /// </summary>
    internal IReadOnlyList<string> EverySpanTagKey =>
        [.. LibraryActivities.SelectMany(activity => activity.TagObjects).Select(tag => tag.Key).Distinct(StringComparer.Ordinal)];

    /// <summary>Every dimension value on any collected measurement.</summary>
    internal IReadOnlyList<string> EveryMeasurementTagValue =>
        [.. Measurements
            .SelectMany(measurement => measurement.Tags.Values)
            .Select(value => value?.ToString())
            .Where(value => value is not null)
            .Select(value => value!)];

    /// <summary>Every dimension key on any collected measurement.</summary>
    internal IReadOnlyList<string> EveryMeasurementTagKey =>
        [.. Measurements.SelectMany(measurement => measurement.Tags.Keys).Distinct(StringComparer.Ordinal)];

    /// <summary>Unregisters both listeners.</summary>
    public void Dispose()
    {
        _meterListener.Dispose();
        _activityListener.Dispose();
    }

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copied = new Dictionary<string, object?>(tags.Length, StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            copied[tag.Key] = tag.Value;
        }

        lock (_gate)
        {
            _measurements.Add(new ProbedMeasurement(instrument.Name, instrument.Meter.Name, value, copied));
        }
    }
}

/// <summary>
/// Telemetry listeners are process-wide, so every test that registers one runs here, on its own,
/// rather than collecting whatever a concurrent test happened to emit.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryCollection
{
    /// <summary>The collection name to put on a test class with <see cref="CollectionAttribute"/>.</summary>
    public const string Name = "AgentExperience telemetry (process-wide listeners)";
}

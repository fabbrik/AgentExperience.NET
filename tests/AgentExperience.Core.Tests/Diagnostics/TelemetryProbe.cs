using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AgentExperience.Core.Tests.Diagnostics;

/// <summary>
/// One measurement a <see cref="TelemetryProbe"/> saw, flattened so a test can assert on the
/// instrument, the value, and the dimension keys and values without caring which numeric type the
/// instrument happened to be.
/// </summary>
/// <param name="Instrument">The instrument's name, e.g. <c>agentexperience.operation.count</c>.</param>
/// <param name="Meter">The meter that published the instrument, which is the emitting assembly's source name.</param>
/// <param name="Value">The recorded value, widened to <see cref="double"/>.</param>
/// <param name="Tags">The measurement's dimensions.</param>
internal sealed record ProbedMeasurement(
    string Instrument,
    string Meter,
    double Value,
    IReadOnlyDictionary<string, object?> Tags);

/// <summary>
/// A host, in miniature: it registers the <see cref="ActivityListener"/> and
/// <see cref="MeterListener"/> the library itself is forbidden to create, and collects everything
/// that arrives. Every telemetry assertion in this folder is made against what a real exporter would
/// have been handed, not against the library's internals.
/// </summary>
/// <remarks>
/// <para>
/// The two listeners are registered independently, so a test can subscribe to spans only, to
/// measurements only, or to neither -- which is how the "execution never depends on a listener" rows
/// of the story's matrix are actually exercised rather than assumed.
/// </para>
/// <para>
/// <b>By default it listens to <c>AgentExperience.*</c> only.</b> <see cref="EverySource"/> widens
/// the span listener to every source in the process, which is what proves the library adds no agent,
/// model, or tool spans of its own on top of the ones MAF already emits.
/// </para>
/// </remarks>
internal sealed class TelemetryProbe : IDisposable
{
    /// <summary>The prefix a host subscribes with (<c>AddSource("AgentExperience.*")</c>).</summary>
    internal const string SourcePrefix = "AgentExperience.";

    private readonly object _gate = new();
    private readonly List<Activity> _activities = [];
    private readonly List<ProbedMeasurement> _measurements = [];
    private readonly List<Instrument> _instruments = [];
    private readonly ActivityListener? _activityListener;
    private readonly MeterListener? _meterListener;

    private TelemetryProbe(bool spans, bool metrics, bool everySource, ActivitySamplingResult sampling)
    {
        if (spans)
        {
            _activityListener = new ActivityListener
            {
                ShouldListenTo = source => everySource || source.Name.StartsWith(SourcePrefix, StringComparison.Ordinal),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => sampling,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => sampling,
                ActivityStarted = activity =>
                {
                    lock (_gate)
                    {
                        Started?.Invoke(activity);
                    }
                },

                // Collected on stop, not on start: an operation's outcome tag is written just before
                // the span is disposed, so a probe that snapshotted it on start would assert on a span
                // nothing had finished filling in.
                ActivityStopped = activity =>
                {
                    lock (_gate)
                    {
                        _activities.Add(activity);
                    }
                },
            };

            ActivitySource.AddActivityListener(_activityListener);
        }

        if (metrics)
        {
            _meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (!instrument.Meter.Name.StartsWith(SourcePrefix, StringComparison.Ordinal))
                    {
                        return;
                    }

                    lock (_gate)
                    {
                        _instruments.Add(instrument);
                    }

                    listener.EnableMeasurementEvents(instrument);
                },
            };

            _meterListener.SetMeasurementEventCallback<long>(
                (instrument, measurement, tags, _) => Add(instrument, measurement, tags));
            _meterListener.SetMeasurementEventCallback<double>(
                (instrument, measurement, tags, _) => Add(instrument, measurement, tags));
            _meterListener.Start();
        }
    }

    /// <summary>Runs for every activity the moment it starts, before the operation it wraps has done anything.</summary>
    internal Action<Activity>? Started { get; set; }

    /// <summary>Spans and measurements from <c>AgentExperience.*</c>, which is what a host subscribes to.</summary>
    internal static TelemetryProbe All() => new(spans: true, metrics: true, everySource: false, ActivitySamplingResult.AllDataAndRecorded);

    /// <summary>Spans only: no <see cref="MeterListener"/> is registered at all.</summary>
    internal static TelemetryProbe SpansOnly() => new(spans: true, metrics: false, everySource: false, ActivitySamplingResult.AllDataAndRecorded);

    /// <summary>Measurements only: no <see cref="ActivityListener"/> is registered at all.</summary>
    internal static TelemetryProbe MetricsOnly() => new(spans: false, metrics: true, everySource: false, ActivitySamplingResult.AllDataAndRecorded);

    /// <summary>Spans from every source in the process, not just this library's.</summary>
    internal static TelemetryProbe EverySource() => new(spans: true, metrics: true, everySource: true, ActivitySamplingResult.AllDataAndRecorded);

    /// <summary>
    /// Listening, but declining every sample: <c>StartActivity</c> returns <see langword="null"/>, so
    /// every <c>?.SetTag</c> in the library is a no-op while the measurements still have to arrive.
    /// </summary>
    internal static TelemetryProbe Declining() => new(spans: true, metrics: true, everySource: false, ActivitySamplingResult.None);

    /// <summary>Every finished activity, in the order it stopped.</summary>
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

    /// <summary>The finished activities this library emitted, ignoring any the rest of the process produced.</summary>
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

    /// <summary>Every tag value written to any collected span, as strings, so a marker sweep can look at all of them at once.</summary>
    internal IReadOnlyList<string> EverySpanTagValue =>
        [.. Activities
            .SelectMany(activity => activity.TagObjects)
            .Select(tag => tag.Value?.ToString())
            .Concat(Activities.Select(activity => activity.StatusDescription))
            .Concat(Activities.Select(activity => activity.DisplayName))
            .Where(value => value is not null)
            .Select(value => value!)];

    /// <summary>Every dimension value written to any collected measurement, as strings.</summary>
    internal IReadOnlyList<string> EveryMeasurementTagValue =>
        [.. Measurements
            .SelectMany(measurement => measurement.Tags.Values)
            .Select(value => value?.ToString())
            .Where(value => value is not null)
            .Select(value => value!)];

    /// <summary>Every dimension key written to any collected measurement.</summary>
    internal IReadOnlyList<string> EveryMeasurementTagKey =>
        [.. Measurements.SelectMany(measurement => measurement.Tags.Keys).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Every attribute <em>key</em> written to any span this library emitted.
    /// </summary>
    /// <remarks>
    /// The marker sweep proves that no value <em>this drive produced</em> reached a span. That is a
    /// statement about the content the drive happened to carry, and it cannot catch an attribute that
    /// carries host free text the drive never poisoned. Pinning the key set exactly -- the way the
    /// metric dimension set is pinned -- does: a new attribute has to be added to the allow-list, on
    /// purpose, with whoever adds it having to say what it carries.
    /// </remarks>
    internal IReadOnlyList<string> EverySpanTagKey =>
        [.. LibraryActivities.SelectMany(activity => activity.TagObjects).Select(tag => tag.Key).Distinct(StringComparer.Ordinal)];

    /// <summary>Every instrument this probe's meter listener was offered, whether or not it enabled it.</summary>
    internal IReadOnlyList<Instrument> PublishedInstruments
    {
        get
        {
            lock (_gate)
            {
                return [.. _instruments];
            }
        }
    }

    /// <summary>Drains the measurement callbacks, then unregisters both listeners.</summary>
    public void Dispose()
    {
        _meterListener?.Dispose();
        _activityListener?.Dispose();
    }

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        // Copied out of the span before it goes away: the callback's tags are only valid for the
        // duration of the call.
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
/// Telemetry listeners are process-wide, so a probe registered by one test would otherwise collect
/// whatever a concurrently running test happened to emit. Every test that registers a probe -- and
/// every test that asserts nothing at all was emitted -- belongs to this collection, which xUnit runs
/// on its own.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryCollection
{
    /// <summary>The collection name to put on a test class with <see cref="CollectionAttribute"/>.</summary>
    public const string Name = "AgentExperience telemetry (process-wide listeners)";
}

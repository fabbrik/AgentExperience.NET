using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AgentExperience.Storage.Postgres.Tests.Diagnostics;

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
/// The host, in miniature, for the storage adapter's side of the telemetry contract: it registers the
/// listeners the library is forbidden to create and collects what arrives from <c>AgentExperience.*</c>.
/// A per-project copy, as the MAF adapter's test project keeps its own.
/// </summary>
internal sealed class TelemetryProbe : IDisposable
{
    /// <summary>The prefix a host subscribes with (<c>AddSource("AgentExperience.*")</c>).</summary>
    internal const string SourcePrefix = "AgentExperience.";

    private readonly object _gate = new();
    private readonly List<Activity> _activities = [];
    private readonly List<ProbedMeasurement> _measurements = [];
    private readonly ActivityListener? _activityListener;
    private readonly MeterListener? _meterListener;

    private TelemetryProbe(bool spans, bool metrics, ActivitySamplingResult sampling, bool throwing = false)
    {
        if (spans)
        {
            _activityListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name.StartsWith(SourcePrefix, StringComparison.Ordinal),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => sampling,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => sampling,
                ActivityStarted = _ =>
                {
                    if (throwing)
                    {
                        throw new ListenerFailureException();
                    }
                },

                // Collected on stop: the outcome tag is written just before the span is disposed.
                ActivityStopped = activity =>
                {
                    lock (_gate)
                    {
                        _activities.Add(activity);
                    }

                    if (throwing)
                    {
                        throw new ListenerFailureException();
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
                    if (instrument.Meter.Name.StartsWith(SourcePrefix, StringComparison.Ordinal))
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };

            _meterListener.SetMeasurementEventCallback<long>(
                (instrument, measurement, tags, _) => Add(instrument, measurement, tags, throwing));
            _meterListener.SetMeasurementEventCallback<double>(
                (instrument, measurement, tags, _) => Add(instrument, measurement, tags, throwing));
            _meterListener.Start();
        }
    }

    /// <summary>Spans and measurements from <c>AgentExperience.*</c>.</summary>
    internal static TelemetryProbe All() => new(spans: true, metrics: true, ActivitySamplingResult.AllDataAndRecorded);

    /// <summary>Measurements only: no <see cref="ActivityListener"/> is registered, so the source has no listener at all.</summary>
    internal static TelemetryProbe MetricsOnly() => new(spans: false, metrics: true, ActivitySamplingResult.AllDataAndRecorded);

    /// <summary>Listening, but declining every sample: no span is created while measurements still arrive.</summary>
    internal static TelemetryProbe Declining() => new(spans: true, metrics: true, ActivitySamplingResult.None);

    /// <summary>
    /// A hostile host: every span start, span stop and measurement callback throws
    /// <see cref="ListenerFailureException"/> after recording what it saw. Rule 6 says none of it may
    /// reach the caller.
    /// </summary>
    internal static TelemetryProbe Throwing() => new(spans: true, metrics: true, ActivitySamplingResult.AllDataAndRecorded, throwing: true);

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

    /// <summary>The finished spans with one name.</summary>
    /// <param name="spanName">The span name.</param>
    internal IReadOnlyList<Activity> Spans(string spanName) =>
        [.. Activities.Where(activity => string.Equals(activity.OperationName, spanName, StringComparison.Ordinal))];

    /// <summary>The measurements recorded to one instrument for one <c>operation</c> dimension value.</summary>
    /// <param name="instrument">The instrument name.</param>
    /// <param name="operation">The <c>operation</c> dimension value.</param>
    internal IReadOnlyList<ProbedMeasurement> For(string instrument, string operation) =>
        [.. Measurements.Where(measurement =>
            string.Equals(measurement.Instrument, instrument, StringComparison.Ordinal)
            && measurement.Tags.TryGetValue("operation", out var value)
            && Equals(value, operation))];

    /// <summary>Every tag value, display name and status description on any collected span, as strings.</summary>
    internal IReadOnlyList<string> EverySpanValue =>
        [.. Activities
            .SelectMany(activity => activity.TagObjects)
            .Select(tag => tag.Value?.ToString())
            .Concat(Activities.Select(activity => activity.StatusDescription))
            .Concat(Activities.Select(activity => activity.DisplayName))
            .Concat(Activities.Select(activity => activity.OperationName))
            .Where(value => value is not null)
            .Select(value => value!)];

    /// <summary>Every dimension value written to any collected measurement, as strings.</summary>
    internal IReadOnlyList<string> EveryMeasurementValue =>
        [.. Measurements
            .SelectMany(measurement => measurement.Tags.Values)
            .Select(value => value?.ToString())
            .Where(value => value is not null)
            .Select(value => value!)];

    /// <summary>Drains the measurement callbacks, then unregisters both listeners.</summary>
    public void Dispose()
    {
        _meterListener?.Dispose();
        _activityListener?.Dispose();
    }

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, bool throwing)
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

        if (throwing)
        {
            throw new ListenerFailureException();
        }
    }
}

/// <summary>What a <see cref="TelemetryProbe.Throwing"/> listener throws; it must never reach a caller.</summary>
internal sealed class ListenerFailureException() : Exception("a host telemetry listener failed");

/// <summary>
/// Telemetry listeners are process-wide, and <c>PostgresDeletionTests</c> erases records concurrently
/// in the shared collection, so a probe there would collect another test's spans. Every telemetry test
/// runs in this collection instead: on its own, after the parallel ones, with its own container.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresTelemetryCollection : ICollectionFixture<PostgresFixture>
{
    /// <summary>The collection name to put on a test class with <see cref="CollectionAttribute"/>.</summary>
    public const string Name = "Postgres telemetry (process-wide listeners)";
}

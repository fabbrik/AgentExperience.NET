using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using AgentExperience.Abstractions;

namespace AgentExperience.Storage.Postgres.Diagnostics;

/// <summary>
/// The storage adapter's one emission seam: the <see cref="ActivitySource"/> and <see cref="Meter"/>
/// named <c>AgentExperience.Storage.Postgres</c>, the same three instruments Core emits, and the three
/// operations this assembly owns -- erasure, the retention sweep, and the expired-grant purge.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why here, and not through Core's holder.</b> This package references
/// <c>AgentExperience.Abstractions</c> and nothing else of the library's, and erasure is deliberately a
/// capability of this adapter rather than of a port, so no Core service can wrap it. Reaching Core's
/// holder from here would mean a Core reference -- and with it Core's own dependencies -- in a storage
/// package, plus an <c>InternalsVisibleTo</c> grant into Core, which is the trade the MAF adapter's
/// holder already declined. <see cref="ActivitySource"/> and <see cref="Meter"/> are the BCL, so
/// emitting from here costs no reference at all. A host already subscribed with
/// <c>AddSource("AgentExperience.*")</c> and <c>AddMeter("AgentExperience.*")</c> receives these too.
/// </para>
/// <para>
/// <b>The wire names below are restated, not shared,</b> exactly as the MAF adapter restates them, and
/// for the same reason: the values are <see langword="const"/>, baked in at compile time, and the
/// packages ship independently. A test in this package's test project asserts every restated value,
/// and the classification table arm for arm, equal to Core's, so drift is a failing build.
/// </para>
/// <para>
/// <b>Nothing that was erased is ever a telemetry value.</b> An erasure's span carries the record ID
/// the caller passed -- written before the call runs, as every request identifier is, so a refusal
/// carries it too; for an erased record it is the ID the tombstone keeps -- and nothing else about it. A sweep and a purge carry a count and, for a sweep, whether it stopped
/// early. No scope identifier, task ID, grant ID, grant reason, recipient scope, administrator, record
/// content, or exception message is written, on the span or anywhere else.
/// </para>
/// <para>
/// <b>None of these operations is ever nested.</b> None calls another operation this assembly
/// instruments -- a sweep erases through the store's private erasure step, not through
/// <c>DeleteAsync</c> -- and nothing in the library calls them. Nesting does not cross assemblies, so
/// <c>nested</c> is a constant <see langword="false"/> rather than an ambient flag that could only ever
/// read <see langword="false"/>.
/// </para>
/// </remarks>
internal static class ErasureDiagnostics
{
    /// <summary>The <see cref="ActivitySource"/> and <see cref="Meter"/> name this assembly emits under.</summary>
    internal const string SourceName = "AgentExperience.Storage.Postgres";

    /// <summary>Erasing one record (<c>PostgresExperienceRecordStore.DeleteAsync</c>).</summary>
    internal const string Delete = "delete";

    /// <summary>One bounded retention-sweep batch (<c>PostgresExperienceRecordStore.SweepExpiredAsync</c>).</summary>
    internal const string RetentionSweep = "retention.sweep";

    /// <summary>One bounded expired-grant purge batch (<c>PostgresExperienceGrantStore.PurgeExpiredAsync</c>).</summary>
    internal const string GrantPurge = "grant.purge";

    /// <summary>How many records a sweep, or grants a purge, removed. A count: which ones stays on the database.</summary>
    internal const string ErasedCountAttribute = "agentexperience.erased_count";

    /// <summary>Whether a sweep stopped before the end of its batch, leaving records past the cutoff untouched.</summary>
    internal const string InterruptedAttribute = "agentexperience.interrupted";

    // ---------------------------------------------------------------------------------------------
    // Restated from Core. Every one of these is asserted equal to Core's own value by
    // AgentExperience.Storage.Postgres.Tests' diagnostics-agreement test.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The prefix an operation's span name carries.</summary>
    internal const string SpanNamePrefix = "agentexperience.";

    /// <summary>The <c>operation</c> metric dimension.</summary>
    internal const string OperationDimension = "operation";

    /// <summary>The <c>outcome</c> metric dimension.</summary>
    internal const string OutcomeDimension = "outcome";

    /// <summary>The <c>error.class</c> metric dimension.</summary>
    internal const string ErrorClassDimension = "error.class";

    /// <summary>The <c>nested</c> metric dimension.</summary>
    internal const string NestedDimension = "nested";

    /// <summary>The <c>operation</c> span attribute.</summary>
    internal const string OperationAttribute = "agentexperience.operation";

    /// <summary>The <c>outcome</c> span attribute.</summary>
    internal const string OutcomeAttribute = "agentexperience.outcome";

    /// <summary>The bounded failure classification, on the span as well as on the failure counter.</summary>
    internal const string ErrorClassAttribute = "agentexperience.error.class";

    /// <summary>The failing exception's type name, and only its type name.</summary>
    internal const string ErrorTypeAttribute = "error.type";

    /// <summary>The Experience Record an operation acted on.</summary>
    internal const string ExperienceIdAttribute = "agentexperience.experience_id";

    /// <summary>The <c>outcome</c> value every faulted path reports.</summary>
    internal const string FaultedOutcome = "Faulted";

    /// <summary>The name of the counter every instrumented operation increments exactly once.</summary>
    internal const string OperationCountInstrument = "agentexperience.operation.count";

    /// <summary>The name of the histogram every instrumented operation's wall-clock duration, in seconds, is recorded to.</summary>
    internal const string OperationDurationInstrument = "agentexperience.operation.duration";

    /// <summary>The name of the counter only a thrown operation increments.</summary>
    internal const string OperationFailuresInstrument = "agentexperience.operation.failures";

    /// <summary>Core's <c>ExperienceOperationErrorClass.Cancelled</c>, by name: the caller's own token was cancelled.</summary>
    internal const string Cancelled = "Cancelled";

    /// <summary>Core's <c>ExperienceOperationErrorClass.Timeout</c>, by name.</summary>
    internal const string Timeout = "Timeout";

    /// <summary>Core's <c>ExperienceOperationErrorClass.Infrastructure</c>, by name: the database is unreachable or failing.</summary>
    internal const string Infrastructure = "Infrastructure";

    /// <summary>Core's <c>ExperienceOperationErrorClass.Unexpected</c>, by name.</summary>
    internal const string Unexpected = "Unexpected";

    /// <summary>The assembly's informational version, carried by both the source and the meter.</summary>
    private static readonly string? InstrumentationVersion = typeof(ErasureDiagnostics).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private static readonly ActivitySource Source = new(SourceName, InstrumentationVersion);

    private static readonly Meter Meter = new(SourceName, InstrumentationVersion);

    private static readonly Counter<long> Operations = Meter.CreateCounter<long>(
        OperationCountInstrument,
        "{operation}",
        "Instrumented Experience operations, by operation and by the outcome they reached.");

    private static readonly Histogram<double> Durations = Meter.CreateHistogram<double>(
        OperationDurationInstrument,
        "s",
        "How long each instrumented Experience operation took, by operation and by the outcome it reached.");

    private static readonly Counter<long> Failures = Meter.CreateCounter<long>(
        OperationFailuresInstrument,
        "{failure}",
        "Instrumented Experience operations that threw, by operation and by bounded failure class.");

    /// <summary>The <c>nested</c> dimension every measurement from this assembly carries, boxed once.</summary>
    private static readonly object NotNested = false;

    /// <summary>
    /// Starts <paramref name="operation"/>: takes its start timestamp and opens its span when a listener
    /// wants one. The <see cref="ActivitySource.HasListeners"/> check comes first so an unsubscribed
    /// process does not even pay for composing the span name.
    /// </summary>
    /// <param name="operation">One of <see cref="Delete"/>, <see cref="RetentionSweep"/>, or <see cref="GrantPurge"/>.</param>
    /// <returns>The trace to tag, to report through, and to dispose when the operation ends.</returns>
    internal static ErasureTrace Start(string operation)
    {
        Activity? activity = null;

        try
        {
            if (Source.HasListeners())
            {
                // Created and started in two steps rather than through StartActivity, so a listener
                // whose ActivityStarted throws leaves this method holding the span it half-started.
                // Start() has already made it Activity.Current by then; without the reference there
                // would be no way to stop it, and every later span on this execution context would be
                // parented to an orphan.
                activity = Source.CreateActivity(SpanNamePrefix + operation, ActivityKind.Internal);
                activity?.SetTag(OperationAttribute, operation);
                activity?.Start();
            }
        }
        catch (Exception)
        {
            // Frozen rule 6: a listener that throws on start must not stop an erasure from running.
            // The span is abandoned -- stopped, so Activity.Current is restored -- and the operation
            // still reports its count and duration.
            try
            {
                activity?.Dispose();
            }
            catch (Exception)
            {
                // A listener that throws on stop as well is still not the caller's problem.
            }

            activity = null;
        }

        return new ErasureTrace(activity, Stopwatch.GetTimestamp(), operation);
    }

    /// <summary>Writes one span attribute, and never lets writing it become the caller's problem.</summary>
    /// <param name="trace">The trace <see cref="Start"/> produced.</param>
    /// <param name="name">The attribute name.</param>
    /// <param name="value">The attribute value, or <see langword="null"/> to write nothing at all.</param>
    internal static void Tag(in ErasureTrace trace, string name, object? value)
    {
        if (trace.Activity is not { } activity || value is null)
        {
            return;
        }

        try
        {
            activity.SetTag(name, value);
        }
        catch (Exception)
        {
            // Frozen rule 6: instrumentation failure is never propagated to the caller.
        }
    }

    /// <summary>
    /// Closes a span whose operation returned -- including a refusal, which is an answer rather than a
    /// failure and is therefore still <see cref="ActivityStatusCode.Ok"/>.
    /// </summary>
    /// <param name="trace">The trace <see cref="Start"/> produced.</param>
    /// <param name="outcome">The result's <see cref="ExperienceStoreOutcome"/>.</param>
    internal static void Succeeded(in ErasureTrace trace, ExperienceStoreOutcome outcome)
    {
        try
        {
            var name = outcome.ToString();
            trace.Activity?.SetTag(OutcomeAttribute, name);
            trace.Activity?.SetStatus(ActivityStatusCode.Ok);
            Record(trace.Operation, name, trace.StartTimestamp);
        }
        catch (Exception)
        {
            // Frozen rule 6: the erasure already happened, and telemetry must not report otherwise.
        }
    }

    /// <summary>
    /// Closes a span whose operation threw, handing telemetry the exception's type name and its bounded
    /// classification and nothing else.
    /// </summary>
    /// <param name="trace">The trace <see cref="Start"/> produced.</param>
    /// <param name="exception">The exception about to propagate unchanged to the caller.</param>
    /// <param name="cancellationToken">The caller's token. This adapter imposes no deadline of its own, so it is both the operation's token and the host's.</param>
    internal static void Faulted(in ErasureTrace trace, Exception exception, CancellationToken cancellationToken)
    {
        try
        {
            var errorClass = Classify(exception, cancellationToken, cancellationToken);
            var activity = trace.Activity;

            activity?.SetTag(ErrorTypeAttribute, exception.GetType().FullName);
            activity?.SetTag(ErrorClassAttribute, errorClass);
            activity?.SetTag(OutcomeAttribute, FaultedOutcome);
            activity?.SetStatus(ActivityStatusCode.Error);

            Record(trace.Operation, FaultedOutcome, trace.StartTimestamp);

            if (Failures.Enabled)
            {
                // Guarded on its own: this is the alertable signal, and a host listener that threw on
                // the count or the duration must not be able to suppress it.
                try
                {
                    Failures.Add(1, new TagList
                    {
                        { OperationDimension, trace.Operation },
                        { ErrorClassDimension, errorClass },
                        { NestedDimension, NotNested },
                    });
                }
                catch (Exception)
                {
                    // Frozen rule 6.
                }
            }
        }
        catch (Exception)
        {
            // Frozen rule 6, and here it matters most: this runs inside a `catch` that is about to
            // rethrow, so a throw from telemetry would replace the caller's own failure with ours.
        }
    }

    /// <summary>
    /// Core's classification, restated arm for arm and returned by name, because this assembly cannot
    /// see Core's enum. An <see cref="OperationCanceledException"/> the caller did not ask for is
    /// <see cref="Infrastructure"/>, not <see cref="Cancelled"/>.
    /// </summary>
    /// <param name="exception">The failure to classify.</param>
    /// <param name="operationToken">The token the operation was handed.</param>
    /// <param name="hostToken">The host's token. For every operation in this assembly it is the same token.</param>
    /// <returns>One of <see cref="Cancelled"/>, <see cref="Timeout"/>, <see cref="Infrastructure"/>, or <see cref="Unexpected"/>.</returns>
    internal static string Classify(
        Exception exception,
        CancellationToken operationToken,
        CancellationToken hostToken) => exception switch
    {
        OperationCanceledException when hostToken.IsCancellationRequested => Cancelled,
        OperationCanceledException when operationToken.IsCancellationRequested => Timeout,
        OperationCanceledException => Infrastructure,
        TimeoutException => Timeout,
        ExperienceStoreException => Infrastructure,
        _ => Unexpected,
    };

    /// <summary>
    /// Records one operation's count and duration. Both writes are guarded by the instrument's own
    /// <c>Enabled</c>, so an unsubscribed process composes no tags and records nothing.
    /// </summary>
    private static void Record(string operation, string outcome, long startTimestamp)
    {
        var counting = Operations.Enabled;
        var timing = Durations.Enabled;
        if (!counting && !timing)
        {
            return;
        }

        var tags = new TagList
        {
            { OperationDimension, operation },
            { OutcomeDimension, outcome },
            { NestedDimension, NotNested },
        };

        // Each write guarded on its own, so a host listener that throws on the count cannot also cost
        // the duration -- and, through Faulted, the failure counter after it.
        if (counting)
        {
            try
            {
                Operations.Add(1, tags);
            }
            catch (Exception)
            {
                // Frozen rule 6.
            }
        }

        if (timing)
        {
            try
            {
                Durations.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds, tags);
            }
            catch (Exception)
            {
                // Frozen rule 6.
            }
        }
    }
}

/// <summary>
/// One erasure operation in flight: its span if a listener wanted one, the timestamp its duration is
/// measured from, and which operation it is.
/// </summary>
/// <remarks>
/// A <see langword="readonly"/> <see langword="struct"/>, like Core's scope, so an operation nobody is
/// listening to costs no allocation here. Disposing it ends the span, which is why every wrapper holds
/// it in a <c>using</c>.
/// </remarks>
/// <param name="activity">The started span, or <see langword="null"/> when nobody is listening or a sampler declined it.</param>
/// <param name="startTimestamp">The <see cref="Stopwatch.GetTimestamp"/> taken when the operation began.</param>
/// <param name="operation">The <c>operation</c> value this trace reports under.</param>
internal readonly struct ErasureTrace(Activity? activity, long startTimestamp, string operation) : IDisposable
{
    /// <summary>The started span, or <see langword="null"/>.</summary>
    internal Activity? Activity { get; } = activity;

    /// <summary>The <see cref="Stopwatch.GetTimestamp"/> taken when the operation began.</summary>
    internal long StartTimestamp { get; } = startTimestamp;

    /// <summary>The <c>operation</c> value this trace reports under.</summary>
    internal string Operation { get; } = operation;

    /// <summary>Ends the span, on the faulted path as well as the returning one.</summary>
    public void Dispose()
    {
        try
        {
            Activity?.Dispose();
        }
        catch (Exception)
        {
            // Frozen rule 6: a listener's ActivityStopped callback that throws is not the caller's problem.
        }
    }
}

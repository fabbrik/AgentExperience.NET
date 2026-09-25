using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using AgentExperience.Abstractions;

namespace AgentExperience.Core.Diagnostics;

/// <summary>
/// Core's one emission seam: the <see cref="ActivitySource"/> and <see cref="Meter"/> named
/// <c>AgentExperience.Core</c>, the three instruments every instrumented operation writes to, and the
/// helpers that close a span and record its measurements. The library emits; the host exports.
/// </summary>
/// <remarks>
/// <para>
/// <b>The BCL and nothing else.</b> <see cref="ActivitySource"/> and <see cref="Meter"/> ship in the
/// shared framework of every target framework, so instrumenting Core costs no package reference and Core's
/// dependency boundary (AD-1) is unchanged. Nothing here constructs a tracer provider, a meter
/// provider, an exporter, an <see cref="ActivityListener"/>, or a <see cref="MeterListener"/>: a host
/// subscribes with <c>AddSource("AgentExperience.*")</c> and <c>AddMeter("AgentExperience.*")</c>, and
/// until it does, every call below is a handful of predictable branches that allocate no
/// <see cref="Activity"/> and record no measurement.
/// </para>
/// <para>
/// <b>Execution never depends on a listener.</b> <see cref="Start"/> opens no
/// <see cref="Activity"/> at all when nobody is listening or when a sampler declined, and every write
/// site is <c>activity?.SetTag(...)</c>; every metric write is guarded by the instrument's own
/// <c>Enabled</c>. An instrumented operation therefore returns exactly what the uninstrumented one
/// would, whatever is or is not subscribed.
/// </para>
/// <para>
/// <b>Exception objects never reach telemetry.</b> A failing span records the exception's type name
/// (<c>error.type</c>) and a four-valued <see cref="ExperienceOperationErrorClass"/>, never
/// <see cref="Exception.Message"/>, never <see cref="object.ToString"/>, and never through
/// <c>Activity.AddException</c> -- because a driver or HTTP client message can quote SQL text,
/// parameters, or caller data, which is exactly what must not leave the process through an exporter.
/// </para>
/// <para>
/// <b>Identifiers are span attributes; metrics carry four dimensions and no more.</b> Run, attempt,
/// record, event, feedback, and host-supplied correlation identifiers are unbounded, so they are
/// written to spans only. The instruments below accept exactly <c>operation</c>, <c>outcome</c>,
/// <c>error.class</c>, and <c>nested</c>, each of which is a closed set.
/// </para>
/// <para>
/// <b>Nesting is a dimension, not a reason to stop emitting.</b> Several operations are steps of a
/// larger one: a finalization verifies, reflects, commits the record's initial lifecycle event, and
/// indexes it; a reuse-feedback submission applies confidence evidence per exposed record. Each of
/// those inner calls goes through its own public, instrumented entry point, so it emits its own span
/// and its own measurements -- which is the only way a hung embedding provider inside a post-commit
/// hook can reach <c>agentexperience.operation.failures</c> at all. <see cref="NestedDimension"/>
/// then lets an operator ask the two different questions separately: <c>sum by (operation)</c> over
/// <c>nested=false</c> is what the host asked for, and the unfiltered sum is what the library did.
/// Spans need no such dimension, because a span already carries its parent.
/// </para>
/// <para>
/// <b>Nesting is read from ambient state, never from a parameter.</b> No public signature changes to
/// carry it: <see cref="Start"/> records the outermost call in an <see cref="AsyncLocal{T}"/> for the
/// duration of that call, and an operation is nested exactly when one was already in flight. The flag
/// flows across <c>await</c> boundaries with the execution context and is restored in the scope's
/// <c>Dispose</c>, on the faulted path as well as the returning one. It does not leak <em>out</em> of
/// an <c>async</c> method either: the state machine's own builder restores the execution context
/// around the synchronous part of the method, so two operations started side by side and awaited
/// together are each other's siblings rather than each other's parents.
/// </para>
/// </remarks>
internal static class ExperienceDiagnostics
{
    /// <summary>The <see cref="ActivitySource"/> and <see cref="Meter"/> name this assembly emits under. Assembly-scoped, so a text-only host can subscribe to Core without pulling the MAF adapter into its telemetry configuration.</summary>
    internal const string SourceName = "AgentExperience.Core";

    /// <summary>The prefix an operation's span name carries, so a span name is always derivable from its <c>operation</c> value.</summary>
    internal const string SpanNamePrefix = "agentexperience.";

    /// <summary>The <c>operation</c> metric dimension: one of <see cref="ExperienceOperationNames"/>.</summary>
    internal const string OperationDimension = "operation";

    /// <summary>The <c>outcome</c> metric dimension: an enum member name from the operation's own bounded outcome enum, or <see cref="FaultedOutcome"/>.</summary>
    internal const string OutcomeDimension = "outcome";

    /// <summary>The <c>error.class</c> metric dimension: an <see cref="ExperienceOperationErrorClass"/> member name.</summary>
    internal const string ErrorClassDimension = "error.class";

    /// <summary>
    /// The <c>nested</c> metric dimension: <see langword="false"/> when the host called the operation
    /// directly, <see langword="true"/> when another instrumented operation called it.
    /// </summary>
    internal const string NestedDimension = "nested";

    /// <summary>The <c>operation</c> span attribute, so a span can be sliced the same way its measurements are.</summary>
    internal const string OperationAttribute = "agentexperience.operation";

    /// <summary>The <c>outcome</c> span attribute. Documented content-free at every declaration site that produces one.</summary>
    internal const string OutcomeAttribute = "agentexperience.outcome";

    /// <summary>The bounded failure classification, on the span as well as on the failure counter.</summary>
    internal const string ErrorClassAttribute = "agentexperience.error.class";

    /// <summary>The failing exception's type name, and only its type name. Never a metric dimension: third-party type names are not a bounded set.</summary>
    internal const string ErrorTypeAttribute = "error.type";

    /// <summary>The captured run an operation acted on.</summary>
    internal const string RunIdAttribute = "agentexperience.run_id";

    /// <summary>The attempt an append acted on.</summary>
    internal const string AttemptIdAttribute = "agentexperience.attempt_id";

    /// <summary>The lifecycle or completion event an operation stamped.</summary>
    internal const string EventIdAttribute = "agentexperience.event_id";

    /// <summary>The Experience Record an operation acted on.</summary>
    internal const string ExperienceIdAttribute = "agentexperience.experience_id";

    /// <summary>The reflection an operation produced.</summary>
    internal const string ReflectionIdAttribute = "agentexperience.reflection_id";

    /// <summary>The reuse-feedback submission an operation recorded.</summary>
    internal const string FeedbackIdAttribute = "agentexperience.feedback_id";

    /// <summary>The host-supplied correlation identifier, echoed on every outcome including a timeout. Omitted entirely, never written as an empty string, when the host supplied none.</summary>
    internal const string CorrelationIdAttribute = "agentexperience.correlation_id";

    /// <summary>The <c>FinalizationStage</c> a finalization ended at. Documented content-free at its declaration site.</summary>
    internal const string StageAttribute = "agentexperience.stage";

    /// <summary>
    /// How confidence evidence that reached the store was admitted: a <c>ConfidenceEvidenceAdmission</c>
    /// member name (<c>Verified</c> or <c>HostTrusted</c>). A closed set, so an operator can find evidence the
    /// verification opt-out admitted without reading the ledger.
    /// </summary>
    internal const string AdmissionAttribute = "agentexperience.confidence.admission";

    /// <summary>Which independence check refused confidence evidence: an <c>IndependenceRefusal</c> member name. A closed set.</summary>
    internal const string RefusalAttribute = "agentexperience.independence.refusal";

    /// <summary>The <c>outcome</c> value every faulted path reports, so a thrown operation is still counted and timed alongside the ones that returned.</summary>
    internal const string FaultedOutcome = "Faulted";

    /// <summary>The name of the counter every instrumented operation increments exactly once.</summary>
    internal const string OperationCountInstrument = "agentexperience.operation.count";

    /// <summary>The name of the histogram every instrumented operation's wall-clock duration, in seconds, is recorded to.</summary>
    internal const string OperationDurationInstrument = "agentexperience.operation.duration";

    /// <summary>The name of the counter only a thrown operation increments, dimensioned by <see cref="ExperienceOperationErrorClass"/>.</summary>
    internal const string OperationFailuresInstrument = "agentexperience.operation.failures";

    /// <summary>
    /// The assembly's informational version, carried by both the source and the meter so a host can
    /// tell which build produced a signal.
    /// </summary>
    private static readonly string? InstrumentationVersion = typeof(ExperienceDiagnostics).Assembly
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

    /// <summary>
    /// The boxed <c>nested</c> dimension values. A <see cref="TagList"/> entry is an
    /// <see cref="object"/>, and .NET boxes a <see cref="bool"/> afresh every time, so the two
    /// possible values are boxed once here instead of on every measurement.
    /// </summary>
    private static readonly object Nested = true;

    private static readonly object NotNested = false;

    /// <summary>
    /// The outermost instrumented operation currently in flight on this execution context, or
    /// <see langword="null"/> when the next operation to start will be one the host called directly.
    /// </summary>
    /// <remarks>
    /// Written only by <see cref="Start"/>, and only by the outermost call: an operation that finds a
    /// value here is nested by definition and leaves it alone, which also means a deeply nested call
    /// costs no allocation. What it holds is the <em>host's</em> cancellation token, which is what
    /// keeps a deadline this library imposed on an inner step -- the post-commit indexing budget --
    /// from being reported as the caller having cancelled.
    /// </remarks>
    private static readonly AsyncLocal<HostCall?> InFlight = new();

    /// <summary>
    /// Starts <paramref name="operation"/>: marks it in flight, records whether it is nested, takes its
    /// start timestamp, and opens its span when a listener wants one. The
    /// <see cref="ActivitySource.HasListeners"/> check comes first so an unsubscribed process does not
    /// even pay for composing the span name.
    /// </summary>
    /// <param name="operation">One of <see cref="ExperienceOperationNames"/>.</param>
    /// <param name="cancellationToken">The token this operation was handed. For an outermost call it is the host's own; for a nested one it may be a token this library derived, which is why the host's is remembered separately.</param>
    /// <returns>The scope to tag, to report through, and to dispose when the operation ends.</returns>
    internal static ExperienceOperationScope Start(string operation, CancellationToken cancellationToken)
    {
        var ambient = InFlight.Value;
        var outermost = ambient is null;

        if (outermost)
        {
            ambient = new HostCall(cancellationToken);
            InFlight.Value = ambient;
        }

        Activity? activity = null;
        if (Source.HasListeners())
        {
            activity = Source.StartActivity(SpanNamePrefix + operation, ActivityKind.Internal);
            activity?.SetTag(OperationAttribute, operation);
        }

        return new ExperienceOperationScope(
            activity,
            Stopwatch.GetTimestamp(),
            nested: !outermost,
            cancellationToken,
            ambient!.Token,
            entered: outermost);
    }

    /// <summary>
    /// Ends the outermost operation's ambient mark, so the caller's execution context is left exactly
    /// as it was found. Called from <see cref="ExperienceOperationScope.Dispose"/>, on every path.
    /// </summary>
    internal static void Leave() => InFlight.Value = null;

    /// <summary>
    /// Writes one span attribute, and never lets writing it become the caller's problem.
    /// </summary>
    /// <remarks>
    /// Every tag site in the library goes through here rather than calling <c>SetTag</c> directly, so
    /// that frozen rule 6 -- "instrumentation failure is never propagated to the caller" -- holds by
    /// construction instead of by inspection. A listener's <c>ActivityStopped</c> callback, a sampler,
    /// or a future tag expression that threw would otherwise be able to turn an operation that already
    /// ran -- in the worst case one that already committed a durable write -- into an exception.
    /// </remarks>
    /// <param name="scope">The scope <see cref="Start"/> produced.</param>
    /// <param name="name">The attribute name. One of the <c>*Attribute</c> constants above.</param>
    /// <param name="value">The attribute value, or <see langword="null"/> to write nothing at all.</param>
    internal static void Tag(in ExperienceOperationScope scope, string name, object? value)
    {
        if (scope.Activity is not { } activity || value is null)
        {
            return;
        }

        try
        {
            activity.SetTag(name, value);
        }
        catch (Exception)
        {
            // Frozen rule 6. Telemetry is a side effect of the operation, never a precondition of it.
        }
    }

    /// <summary>
    /// Closes a span that returned -- including one that returned a <em>rejection</em>, which is a
    /// decision rather than a failure and is therefore still <see cref="ActivityStatusCode.Ok"/> --
    /// and records its count and duration under the outcome it reached.
    /// </summary>
    /// <remarks>
    /// Called from <em>outside</em> the wrapper's guarded region, and guarded again here, so an
    /// already-returned operation can never be reported to its caller as having thrown.
    /// </remarks>
    /// <param name="scope">The scope <see cref="Start"/> produced.</param>
    /// <param name="operation">One of <see cref="ExperienceOperationNames"/>.</param>
    /// <param name="outcome">The operation's own outcome enum member name, verbatim.</param>
    internal static void Succeeded(in ExperienceOperationScope scope, string operation, string outcome)
    {
        try
        {
            scope.Activity?.SetTag(OutcomeAttribute, outcome);
            scope.Activity?.SetStatus(ActivityStatusCode.Ok);
            Record(operation, outcome, scope.StartTimestamp, scope.Nested);
        }
        catch (Exception)
        {
            // Frozen rule 6.
        }
    }

    /// <summary>
    /// Closes a span whose operation threw, classifying the exception into the four bounded values an
    /// operator can alert on. The exception object itself is never handed to telemetry: only its type
    /// name and its classification are written.
    /// </summary>
    /// <param name="scope">The scope <see cref="Start"/> produced.</param>
    /// <param name="operation">One of <see cref="ExperienceOperationNames"/>.</param>
    /// <param name="exception">The exception about to propagate unchanged to the caller.</param>
    internal static void Faulted(in ExperienceOperationScope scope, string operation, Exception exception)
    {
        try
        {
            var errorClass = Classify(exception, scope.OperationToken, scope.HostToken);
            var activity = scope.Activity;

            activity?.SetTag(ErrorTypeAttribute, exception.GetType().FullName);
            activity?.SetTag(ErrorClassAttribute, Name(errorClass));
            activity?.SetTag(OutcomeAttribute, FaultedOutcome);
            activity?.SetStatus(ActivityStatusCode.Error);

            Record(operation, FaultedOutcome, scope.StartTimestamp, scope.Nested);

            if (Failures.Enabled)
            {
                Failures.Add(1, new TagList
                {
                    { OperationDimension, operation },
                    { ErrorClassDimension, Name(errorClass) },
                    { NestedDimension, scope.Nested ? Nested : NotNested },
                });
            }
        }
        catch (Exception)
        {
            // Frozen rule 6, and here it matters most: this runs inside a `catch` that is about to
            // rethrow, so a throw from telemetry would replace the caller's own failure with ours.
        }
    }

    /// <summary>
    /// Classifies a failure into the four values that may become a metric dimension.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An <see cref="OperationCanceledException"/> the caller did not ask for is
    /// <see cref="ExperienceOperationErrorClass.Infrastructure"/> rather than
    /// <see cref="ExperienceOperationErrorClass.Cancelled"/>, mirroring the storage adapter's own
    /// infrastructure-failure rule: a driver-side or HTTP-client timeout surfaces as a cancellation
    /// with the caller's token untouched, and reporting it as "the caller cancelled" would hide a
    /// dependency that is down.
    /// </para>
    /// <para>
    /// <b>Two tokens, because a cancelled token is not automatically the caller's.</b> An inner step
    /// can be handed a token this library derived -- the post-commit indexing hook runs on a linked
    /// source with the library's own budget on it -- and that token being cancelled means a deadline
    /// expired, not that anyone gave up. A hung embedding provider is the case that matters: reporting
    /// it as <see cref="ExperienceOperationErrorClass.Cancelled"/>, the one class documented as
    /// normally not alertable, is precisely how the vector channel dies without paging anybody. So
    /// only the <em>host's</em> token makes a cancellation the caller's; this operation's own token
    /// makes it a <see cref="ExperienceOperationErrorClass.Timeout"/>. For an operation the host
    /// called directly the two tokens are the same, and this collapses to the rule above.
    /// </para>
    /// </remarks>
    /// <param name="exception">The failure to classify.</param>
    /// <param name="operationToken">The token this operation itself was handed.</param>
    /// <param name="hostToken">The token the outermost operation was handed, which is the host's own.</param>
    /// <returns>The bounded classification.</returns>
    internal static ExperienceOperationErrorClass Classify(
        Exception exception,
        CancellationToken operationToken,
        CancellationToken hostToken) => exception switch
    {
        OperationCanceledException when hostToken.IsCancellationRequested => ExperienceOperationErrorClass.Cancelled,
        OperationCanceledException when operationToken.IsCancellationRequested => ExperienceOperationErrorClass.Timeout,
        OperationCanceledException => ExperienceOperationErrorClass.Infrastructure,
        TimeoutException => ExperienceOperationErrorClass.Timeout,
        ExperienceStoreException => ExperienceOperationErrorClass.Infrastructure,
        _ => ExperienceOperationErrorClass.Unexpected,
    };

    /// <summary>
    /// The member name of <paramref name="errorClass"/> as a compile-time constant, so a dimension
    /// value costs no allocation and cannot drift from the enum it names.
    /// </summary>
    internal static string Name(ExperienceOperationErrorClass errorClass) => errorClass switch
    {
        ExperienceOperationErrorClass.Cancelled => nameof(ExperienceOperationErrorClass.Cancelled),
        ExperienceOperationErrorClass.Timeout => nameof(ExperienceOperationErrorClass.Timeout),
        ExperienceOperationErrorClass.Infrastructure => nameof(ExperienceOperationErrorClass.Infrastructure),
        _ => nameof(ExperienceOperationErrorClass.Unexpected),
    };

    /// <summary>
    /// Records one operation's count and duration. Both writes are guarded by the instrument's own
    /// <c>Enabled</c>, so an unsubscribed process composes no tags and records nothing.
    /// </summary>
    private static void Record(string operation, string outcome, long startTimestamp, bool nested)
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
            { NestedDimension, nested ? Nested : NotNested },
        };

        if (counting)
        {
            Operations.Add(1, tags);
        }

        if (timing)
        {
            Durations.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds, tags);
        }
    }

    /// <summary>The outermost instrumented operation on one execution context: what makes everything under it nested, and whose cancellation token is the host's.</summary>
    /// <param name="token">The token the host handed the outermost operation.</param>
    private sealed class HostCall(CancellationToken token)
    {
        /// <summary>The token the host handed the outermost operation.</summary>
        internal CancellationToken Token { get; } = token;
    }
}

/// <summary>
/// One instrumented operation in flight: its span if a listener wanted one, the timestamp its duration
/// is measured from, whether another instrumented operation called it, and the two tokens its failure
/// classification is decided by.
/// </summary>
/// <remarks>
/// A <see langword="struct"/>, so an operation nobody is listening to costs no allocation here at all,
/// and <see langword="readonly"/>, so nothing about a live operation can be changed after it started.
/// Disposing it ends the span and the ambient mark together, which is why every wrapper holds it in a
/// <c>using</c> rather than closing it by hand on each path.
/// </remarks>
/// <param name="activity">The started span, or <see langword="null"/> when nobody is listening or a sampler declined it.</param>
/// <param name="startTimestamp">The <see cref="Stopwatch.GetTimestamp"/> taken when the operation began.</param>
/// <param name="nested">Whether another instrumented operation was already in flight when this one started.</param>
/// <param name="operationToken">The token this operation itself was handed.</param>
/// <param name="hostToken">The token the outermost operation was handed.</param>
/// <param name="entered">Whether this operation is the outermost one, and so the one that must clear the ambient mark.</param>
internal readonly struct ExperienceOperationScope(
    Activity? activity,
    long startTimestamp,
    bool nested,
    CancellationToken operationToken,
    CancellationToken hostToken,
    bool entered) : IDisposable
{
    /// <summary>The started span, or <see langword="null"/> when nobody is listening or a sampler declined it.</summary>
    internal Activity? Activity { get; } = activity;

    /// <summary>The <see cref="Stopwatch.GetTimestamp"/> taken when the operation began.</summary>
    internal long StartTimestamp { get; } = startTimestamp;

    /// <summary>Whether another instrumented operation called this one.</summary>
    internal bool Nested { get; } = nested;

    /// <summary>The token this operation itself was handed, which may be one this library derived.</summary>
    internal CancellationToken OperationToken { get; } = operationToken;

    /// <summary>The token the host handed the outermost operation.</summary>
    internal CancellationToken HostToken { get; } = hostToken;

    private bool Entered { get; } = entered;

    /// <summary>
    /// Ends the span and, for an outermost operation, the ambient mark that made everything under it
    /// nested. Runs on the faulted path as well as the returning one, so a throw cannot leave a stale
    /// mark behind for the next operation on this execution context to read.
    /// </summary>
    public void Dispose()
    {
        Activity?.Dispose();

        if (Entered)
        {
            ExperienceDiagnostics.Leave();
        }
    }
}

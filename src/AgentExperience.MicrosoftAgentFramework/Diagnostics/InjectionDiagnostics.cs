using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using AgentExperience.Abstractions;
using AgentExperience.Core.Diagnostics;

namespace AgentExperience.MicrosoftAgentFramework.Diagnostics;

/// <summary>
/// The MAF adapter's one emission seam: the <see cref="ActivitySource"/> and <see cref="Meter"/>
/// named <c>AgentExperience.MicrosoftAgentFramework</c>, the same three instruments Core emits, and
/// the one operation this assembly owns -- <c>inject</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Assembly-scoped names, on purpose.</b> Core emits under <c>AgentExperience.Core</c> and this
/// adapter under its own name, so a text-only host can subscribe to Core without pulling the MAF
/// adapter into its telemetry configuration, and a host that wants both pays nothing for the second
/// name: <c>AddSource("AgentExperience.*")</c> and <c>AddMeter("AgentExperience.*")</c> take them
/// together.
/// </para>
/// <para>
/// <b>The wire names below are restated, not shared.</b> Core's holder is <see langword="internal"/>
/// to Core, and it stays that way: granting this assembly access to <em>every</em> Core internal to
/// reach a dozen strings would be a large, permanent coupling bought for a small convenience -- and it
/// would not even buy what it looks like it buys, because the values are <see langword="const"/> and
/// are therefore baked into this assembly at compile time. Core and the adapter ship as independent
/// packages and can be mixed at different versions, so the two tables can drift either way. They are
/// duplicated here deliberately, and a test asserts the two tables agree, which makes that drift
/// visible as a failing build rather than as two dashboards that quietly stop lining up.
/// </para>
/// <para>
/// <b>One span per injection, and no second pipeline.</b> This is the <em>only</em> place the adapter
/// starts an activity. The capture wrapper deliberately opens none around <c>RunAsync</c> or
/// <c>RunStreamingAsync</c> -- it continues to <em>read</em> <see cref="Activity.Current"/> so a
/// captured run can carry the host's trace ID as its provenance correlation, and nothing more. MAF's
/// own agent, model, and tool spans are MAF's to emit; duplicating them here would double every
/// operator's trace for no added fact.
/// </para>
/// <para>
/// <b><c>inject</c> is never a nested operation.</b> Core's holder reads an ambient flag to tell an
/// operation the host called directly from one another instrumented operation called. This assembly
/// emits exactly one operation, and nothing in this library calls it -- <c>ProvideAIContextAsync</c>
/// is invoked by the agent pipeline, which is the host. So <c>nested</c> is a constant
/// <see langword="false"/> here rather than an ambient flag that could only ever read
/// <see langword="false"/>: a dimension a test can pin is worth more than a mechanism with an
/// unreachable branch. The dimension is present at all so that a host summing across both meters does
/// not have to special-case which one a series came from.
/// </para>
/// <para>
/// <b>The retrieval this injection performs reports itself to Core's meter, not to this one.</b> It is
/// a <c>retrieve</c> on <c>AgentExperience.Core</c>, and Core sees it as a call from its own host --
/// which, from Core's side, the adapter is. The two assemblies ship as independent packages with
/// independently named meters and no shared ambient state, and inventing a process-wide slot to link
/// them would be the same trade the <c>InternalsVisibleTo</c> grant was reverted for. An operator who
/// sums <c>nested=false</c> across both meters therefore sees the injection and the retrieval inside
/// it; the Core meter alone still answers "what was asked of Core".
/// </para>
/// <para>
/// <b>The injected block is never a telemetry value.</b> Neither the Historical Reference text, nor a
/// record's lesson, nor the task text the retrieval matched on is ever written to a span or a
/// measurement. An injection reports its bounded <c>InjectionOutcome</c>, the host's own correlation
/// identifier, and how many ranked records were left out -- a count, never the reasons' content.
/// </para>
/// </remarks>
internal static class InjectionDiagnostics
{
    /// <summary>The <see cref="ActivitySource"/> and <see cref="Meter"/> name this assembly emits under.</summary>
    internal const string SourceName = "AgentExperience.MicrosoftAgentFramework";

    /// <summary>The only <c>operation</c> value this assembly emits. Core owns thirteen, and the storage adapter's erasure paths three.</summary>
    internal const string Inject = "inject";

    /// <summary>How many ranked records this injection left out. A count: the omission reasons themselves stay on the typed result.</summary>
    internal const string OmittedCountAttribute = "agentexperience.omitted_count";

    // ---------------------------------------------------------------------------------------------
    // Restated from Core. Every one of these is asserted equal to Core's own value by
    // AgentExperience.MicrosoftAgentFramework.Tests' diagnostics-agreement test.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The prefix an operation's span name carries.</summary>
    internal const string SpanNamePrefix = "agentexperience.";

    /// <summary>The <c>operation</c> metric dimension.</summary>
    internal const string OperationDimension = "operation";

    /// <summary>The <c>outcome</c> metric dimension.</summary>
    internal const string OutcomeDimension = "outcome";

    /// <summary>The <c>error.class</c> metric dimension.</summary>
    internal const string ErrorClassDimension = "error.class";

    /// <summary>The <c>nested</c> metric dimension: whether another instrumented operation called this one.</summary>
    internal const string NestedDimension = "nested";

    /// <summary>The <c>operation</c> span attribute.</summary>
    internal const string OperationAttribute = "agentexperience.operation";

    /// <summary>The <c>outcome</c> span attribute.</summary>
    internal const string OutcomeAttribute = "agentexperience.outcome";

    /// <summary>The bounded failure classification, on the span as well as on the failure counter.</summary>
    internal const string ErrorClassAttribute = "agentexperience.error.class";

    /// <summary>The failing exception's type name, and only its type name.</summary>
    internal const string ErrorTypeAttribute = "error.type";

    /// <summary>The host-supplied correlation identifier, echoed on every outcome including a timeout.</summary>
    internal const string CorrelationIdAttribute = "agentexperience.correlation_id";

    /// <summary>The <c>outcome</c> value every faulted path reports.</summary>
    internal const string FaultedOutcome = "Faulted";

    /// <summary>The name of the counter every instrumented operation increments exactly once.</summary>
    internal const string OperationCountInstrument = "agentexperience.operation.count";

    /// <summary>The name of the histogram every instrumented operation's wall-clock duration, in seconds, is recorded to.</summary>
    internal const string OperationDurationInstrument = "agentexperience.operation.duration";

    /// <summary>The name of the counter only a thrown operation increments.</summary>
    internal const string OperationFailuresInstrument = "agentexperience.operation.failures";

    /// <summary>
    /// The assembly's informational version, carried by both the source and the meter so a host can
    /// tell which build of the adapter produced a signal.
    /// </summary>
    private static readonly string? InstrumentationVersion = typeof(InjectionDiagnostics).Assembly
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
    /// The <c>nested</c> dimension every measurement from this assembly carries. Boxed once: a
    /// <see cref="TagList"/> entry is an <see cref="object"/>, and .NET boxes a <see cref="bool"/>
    /// afresh every time.
    /// </summary>
    /// <remarks>
    /// Always <see langword="false"/>, and by construction rather than by luck: <c>inject</c> is the
    /// only operation this assembly emits, and the only thing that calls it is the agent pipeline.
    /// </remarks>
    private static readonly object NotNested = false;

    /// <summary>
    /// Starts the span for one injection, or returns a trace carrying no span at all when no listener
    /// wants it. The <see cref="ActivitySource.HasListeners"/> check comes first so an unsubscribed
    /// process does not even pay for composing the span name.
    /// </summary>
    /// <returns>The trace to thread through the call and hand back to <see cref="Succeeded"/> or <see cref="Faulted"/>.</returns>
    internal static InjectionTrace Start()
    {
        if (!Source.HasListeners())
        {
            return new InjectionTrace(null, Stopwatch.GetTimestamp());
        }

        var activity = Source.StartActivity(SpanNamePrefix + Inject, ActivityKind.Internal);
        activity?.SetTag(OperationAttribute, Inject);
        return new InjectionTrace(activity, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Writes one span attribute, and never lets writing it become the invocation's problem. The same
    /// rule Core applies: telemetry is a side effect of the operation, never a precondition of it.
    /// </summary>
    /// <param name="trace">The trace <see cref="Start"/> produced.</param>
    /// <param name="name">The attribute name.</param>
    /// <param name="value">The attribute value, or <see langword="null"/> to write nothing at all.</param>
    internal static void Tag(InjectionTrace trace, string name, object? value)
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
    /// Closes a span whose injection returned -- including one that injected nothing, which is a
    /// decision rather than a failure and is therefore still <see cref="ActivityStatusCode.Ok"/>.
    /// </summary>
    /// <param name="trace">The trace <see cref="Start"/> produced.</param>
    /// <param name="outcome">The <c>InjectionOutcome</c> member name, verbatim.</param>
    internal static void Succeeded(InjectionTrace trace, string outcome)
    {
        trace.Reported = true;

        try
        {
            trace.Activity?.SetTag(OutcomeAttribute, outcome);
            trace.Activity?.SetStatus(ActivityStatusCode.Ok);
            Record(outcome, trace.StartTimestamp);
        }
        catch (Exception)
        {
            // Frozen rule 6.
        }
    }

    /// <summary>
    /// Closes a span whose injection threw. Only cancellation of the invocation itself escapes
    /// <c>ProvideAIContextAsync</c>, so in practice this classifies that -- but it classifies whatever
    /// arrives, and it hands telemetry the exception's type name and nothing else.
    /// </summary>
    /// <param name="trace">The trace <see cref="Start"/> produced.</param>
    /// <param name="exception">The exception about to propagate unchanged to the caller.</param>
    /// <param name="cancellationToken">The invocation's token, which is what distinguishes <see cref="ExperienceOperationErrorClass.Cancelled"/> from <see cref="ExperienceOperationErrorClass.Infrastructure"/>.</param>
    internal static void Faulted(InjectionTrace trace, Exception exception, CancellationToken cancellationToken)
    {
        trace.Reported = true;

        try
        {
            // The same token twice: this adapter imposes no deadline of its own on an injection, so
            // the token it was handed is the host's. Core's holder, which does impose one on its
            // post-commit hooks, is what the two-token form of this rule exists for.
            Failed(trace, Classify(exception, cancellationToken, cancellationToken), exception.GetType().FullName);
        }
        catch (Exception)
        {
            // Frozen rule 6, and here it matters most: this runs inside a `catch` that is about to
            // rethrow, so a throw from telemetry would replace the caller's own failure with ours.
        }
    }

    /// <summary>
    /// Closes an injection that reached neither <see cref="Succeeded"/> nor <see cref="Faulted"/>, so
    /// that "every injection is counted exactly once" is enforced rather than merely intended.
    /// </summary>
    /// <remarks>
    /// Nothing in the provider reaches here today: every exit runs through the single report site. It
    /// exists because a future early <c>return</c> added to the injection body would otherwise emit a
    /// span with no outcome, no count, and no duration -- a silent hole in exactly the operation an
    /// operator alerts on, and one no assertion about the paths that <em>do</em> report could catch.
    /// An injection that got here is a bug in this library rather than a failure of a dependency, which
    /// is why it is classified <see cref="ExperienceOperationErrorClass.Unexpected"/> and carries no
    /// <c>error.type</c>: no exception was involved.
    /// </remarks>
    /// <param name="trace">The trace <see cref="Start"/> produced.</param>
    internal static void Closed(InjectionTrace trace)
    {
        if (trace.Reported)
        {
            return;
        }

        trace.Reported = true;

        try
        {
            Failed(trace, ExperienceOperationErrorClass.Unexpected, errorType: null);
        }
        catch (Exception)
        {
            // Frozen rule 6.
        }
    }

    /// <summary>
    /// The same classification Core applies, restated here rather than shared, because Core's holder is
    /// internal to Core and an emission seam is per-assembly by design. An
    /// <see cref="OperationCanceledException"/> the caller did not ask for is
    /// <see cref="ExperienceOperationErrorClass.Infrastructure"/>, not
    /// <see cref="ExperienceOperationErrorClass.Cancelled"/>; one that a deadline this library imposed
    /// caused is <see cref="ExperienceOperationErrorClass.Timeout"/>. A test asserts arm for arm that
    /// this table and Core's agree.
    /// </summary>
    /// <param name="exception">The failure to classify.</param>
    /// <param name="operationToken">The token this operation itself was handed.</param>
    /// <param name="hostToken">The token the outermost operation was handed, which is the host's own. For an injection the two are the same token.</param>
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

    /// <summary>The member name of <paramref name="errorClass"/> as a compile-time constant, so a dimension value costs no allocation.</summary>
    /// <param name="errorClass">The classification to name.</param>
    /// <returns>The enum member name.</returns>
    internal static string Name(ExperienceOperationErrorClass errorClass) => errorClass switch
    {
        ExperienceOperationErrorClass.Cancelled => nameof(ExperienceOperationErrorClass.Cancelled),
        ExperienceOperationErrorClass.Timeout => nameof(ExperienceOperationErrorClass.Timeout),
        ExperienceOperationErrorClass.Infrastructure => nameof(ExperienceOperationErrorClass.Infrastructure),
        _ => nameof(ExperienceOperationErrorClass.Unexpected),
    };

    /// <summary>Writes the failing span's attributes, its count, its duration, and the failure counter.</summary>
    private static void Failed(InjectionTrace trace, ExperienceOperationErrorClass errorClass, string? errorType)
    {
        var activity = trace.Activity;

        if (errorType is not null)
        {
            activity?.SetTag(ErrorTypeAttribute, errorType);
        }

        activity?.SetTag(ErrorClassAttribute, Name(errorClass));
        activity?.SetTag(OutcomeAttribute, FaultedOutcome);
        activity?.SetStatus(ActivityStatusCode.Error);

        Record(FaultedOutcome, trace.StartTimestamp);

        if (Failures.Enabled)
        {
            Failures.Add(1, new TagList
            {
                { OperationDimension, Inject },
                { ErrorClassDimension, Name(errorClass) },
                { NestedDimension, NotNested },
            });
        }
    }

    /// <summary>
    /// Records the injection's count and duration. Both writes are guarded by the instrument's own
    /// <c>Enabled</c>, so an unsubscribed process composes no tags and records nothing.
    /// </summary>
    private static void Record(string outcome, long startTimestamp)
    {
        var counting = Operations.Enabled;
        var timing = Durations.Enabled;
        if (!counting && !timing)
        {
            return;
        }

        var tags = new TagList
        {
            { OperationDimension, Inject },
            { OutcomeDimension, outcome },
            { NestedDimension, NotNested },
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
}

/// <summary>
/// One injection's span, if any, the timestamp its duration is measured from, and whether its outcome
/// has been reported yet.
/// </summary>
/// <remarks>
/// Created per invocation and threaded through the call rather than stashed on the provider, because a
/// single <c>ExperienceContextProvider</c> serves every concurrent invocation of the agent it is
/// attached to. It is a class rather than a struct so that <see cref="Reported"/> -- which
/// <c>ProvideAIContextAsync</c>'s <c>finally</c> reads to enforce that every injection is counted
/// exactly once -- is the same flag the report site set, whichever frame set it.
/// </remarks>
/// <param name="activity">The started span, or <see langword="null"/> when nobody is listening or a sampler declined it.</param>
/// <param name="startTimestamp">The <see cref="Stopwatch.GetTimestamp"/> taken when the injection began.</param>
internal sealed class InjectionTrace(Activity? activity, long startTimestamp)
{
    /// <summary>The started span, or <see langword="null"/> when nobody is listening or a sampler declined it.</summary>
    internal Activity? Activity { get; } = activity;

    /// <summary>The <see cref="Stopwatch.GetTimestamp"/> taken when the injection began.</summary>
    internal long StartTimestamp { get; } = startTimestamp;

    /// <summary>Whether this injection's outcome has already been reported. Set by every close.</summary>
    internal bool Reported { get; set; }
}

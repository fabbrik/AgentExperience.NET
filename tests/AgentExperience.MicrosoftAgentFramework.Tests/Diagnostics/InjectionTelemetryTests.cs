using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentExperience.Core.Diagnostics;
using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework.Diagnostics;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework.Tests.Diagnostics;

/// <summary>
/// The adapter's side of the story: one <c>inject</c> span and one count/duration pair per
/// invocation, the host's own correlation ID and an omission <em>count</em> on the span, nothing of
/// the block that was injected anywhere -- and, the negative claim that matters most, no span at all
/// around the agent's own delegation.
/// </summary>
[Collection(TelemetryCollection.Name)]
public class InjectionTelemetryTests
{
    private const string CountInstrument = "agentexperience.operation.count";
    private const string DurationInstrument = "agentexperience.operation.duration";
    private const string FailuresInstrument = "agentexperience.operation.failures";
    private const string InjectSpan = "agentexperience.inject";
    private const string AdapterSource = "AgentExperience.MicrosoftAgentFramework";

    /// <summary>A string that cannot occur by accident, planted in the lesson the block would carry.</summary>
    private const string Marker = "W2-CANARY-71bc-DO-NOT-EXPORT";

    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");

    private static readonly AuthorizationContext Authorization = new("tenant-1", "host", ["experience:read"], DateTimeOffset.UnixEpoch);

    /// <summary>
    /// The name of the control span the stub agent opens inside its own <c>RunAsync</c> body.
    /// </summary>
    private const string DelegationControlSpan = "tests.delegation_control";

    /// <summary>
    /// A test-owned source under the prefix a host subscribes to, so a span it starts is collected by
    /// exactly the filter the library's own spans go through. Nothing in the library emits from it.
    /// </summary>
    private static readonly ActivitySource Control = new("AgentExperience.Tests.DelegationControl");

    [Fact]
    public async Task An_injection_reports_its_outcome_correlation_id_and_omitted_count()
    {
        using var probe = TelemetryProbe.Start();
        var harness = new Harness { Limits = ExperienceInjectionLimits.Default with { MaxRecords = 1 } };
        harness.World.Publish(InjectionRecords.Record(InjectionRecords.Id(1), TestScope), relevance: 1d);
        harness.World.Publish(InjectionRecords.Record(InjectionRecords.Id(2), TestScope), relevance: 0.5d);

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        var reported = Assert.Single(harness.Results);
        Assert.Equal(InjectionOutcome.Injected, reported.Outcome);
        Assert.Single(reported.Omitted);

        var span = Assert.Single(probe.LibraryActivities, activity => activity.OperationName == InjectSpan);
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
        Assert.Equal("inject", span.GetTagItem("agentexperience.operation"));
        Assert.Equal(nameof(InjectionOutcome.Injected), span.GetTagItem("agentexperience.outcome"));
        Assert.Equal("corr-1", span.GetTagItem("agentexperience.correlation_id"));

        // A count, not the reasons: how many records were left out is a number an operator can graph,
        // while why each one was left out belongs to the typed result.
        Assert.Equal(1, span.GetTagItem("agentexperience.omitted_count"));

        var counted = Assert.Single(probe.For(CountInstrument, "inject"));
        Assert.Equal(AdapterSource, counted.Meter);
        Assert.Equal("inject", counted.Tags["operation"]);
        Assert.Equal(nameof(InjectionOutcome.Injected), counted.Tags["outcome"]);
        Assert.Equal(1d, counted.Value);

        // An injection is never nested: this adapter emits one operation and the only thing that calls
        // it is the agent pipeline, which is the host. The dimension is still written, so a host
        // summing across both meters does not have to special-case which one a series came from.
        Assert.Equal(false, counted.Tags["nested"]);
        Assert.Single(probe.For(CountInstrument, "inject", nested: false));
        Assert.Empty(probe.For(CountInstrument, "inject", nested: true));

        var timed = Assert.Single(probe.For(DurationInstrument, "inject"));
        Assert.True(timed.Value >= 0d);
        Assert.Equal(nameof(InjectionOutcome.Injected), timed.Tags["outcome"]);

        Assert.Empty(probe.For(FailuresInstrument, "inject"));
    }

    [Fact]
    public async Task An_injection_that_injects_nothing_is_a_decision_not_a_failure()
    {
        using var probe = TelemetryProbe.Start();
        var harness = new Harness();

        // Nothing published, so retrieval completes with no records at all.
        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        Assert.Equal(InjectionOutcome.NothingToInject, Assert.Single(harness.Results).Outcome);

        var span = Assert.Single(probe.LibraryActivities, activity => activity.OperationName == InjectSpan);
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
        Assert.Equal(nameof(InjectionOutcome.NothingToInject), span.GetTagItem("agentexperience.outcome"));
        Assert.Equal(nameof(InjectionOutcome.NothingToInject), Assert.Single(probe.For(CountInstrument, "inject")).Tags["outcome"]);
        Assert.Empty(probe.For(FailuresInstrument, "inject"));
    }

    [Fact]
    public async Task A_host_opt_out_still_reports_an_outcome()
    {
        using var probe = TelemetryProbe.Start();
        var harness = new Harness { Resolve = _ => null };

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        Assert.Equal(nameof(InjectionOutcome.Skipped), Assert.Single(probe.For(CountInstrument, "inject")).Tags["outcome"]);
    }

    [Fact]
    public async Task Caller_cancellation_is_classified_and_propagates_unchanged()
    {
        using var probe = TelemetryProbe.Start();
        using var cancellation = new CancellationTokenSource();

        // The invocation is cancelled before it starts, so retrieval -- the one call in this provider
        // whose cancellation is allowed out -- throws on the caller's own token. Everything else the
        // provider can catch is already a reported InjectionOutcome, which is why this is the only
        // faulted path it has.
        await cancellation.CancelAsync();

        var harness = new Harness();
        harness.World.Publish(InjectionRecords.Record(InjectionRecords.Id(1), TestScope));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Agent().RunAsync("refund ticket stuck on a lock", cancellationToken: cancellation.Token));

        var span = Assert.Single(probe.LibraryActivities, activity => activity.OperationName == InjectSpan);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(nameof(ExperienceOperationErrorClass.Cancelled), span.GetTagItem("agentexperience.error.class"));
        Assert.Equal("System.OperationCanceledException", span.GetTagItem("error.type"));
        Assert.Equal("Faulted", span.GetTagItem("agentexperience.outcome"));

        var failure = Assert.Single(probe.For(FailuresInstrument, "inject"));
        Assert.Equal("inject", failure.Tags["operation"]);
        Assert.Equal(nameof(ExperienceOperationErrorClass.Cancelled), failure.Tags["error.class"]);
        Assert.Equal(false, failure.Tags["nested"]);

        // Faulted is still counted and timed, and the host callback never ran.
        Assert.Equal("Faulted", Assert.Single(probe.For(CountInstrument, "inject")).Tags["outcome"]);
        Assert.Single(probe.For(DurationInstrument, "inject"));
        Assert.Empty(harness.Results);
    }

    /// <summary>
    /// The four outcomes an operator would actually alert on. Every one of them could emit no span
    /// close, no count and no duration without a single test noticing.
    /// </summary>
    /// <param name="outcome">The <c>InjectionOutcome</c> to drive.</param>
    [Theory]
    [InlineData(nameof(InjectionOutcome.RetrievalTimedOut))]
    [InlineData(nameof(InjectionOutcome.RetrievalDenied))]
    [InlineData(nameof(InjectionOutcome.RetrievalFailed))]
    [InlineData(nameof(InjectionOutcome.Failed))]
    public async Task Every_reported_outcome_closes_its_span_and_records_its_measurements(string outcome)
    {
        using var probe = TelemetryProbe.Start();

        var released = new TaskCompletionSource();
        var harness = Harnessed(outcome, released);
        harness.World.Publish(InjectionRecords.Record(InjectionRecords.Id(1), TestScope));

        var response = await harness.Agent().RunAsync("refund ticket stuck on a lock");
        released.TrySetResult();

        // The invocation ran regardless: nothing about a failing injection reaches the agent.
        Assert.NotNull(response);
        Assert.Equal(outcome, Assert.Single(harness.Results).Outcome.ToString());

        var span = Assert.Single(probe.LibraryActivities, activity => activity.OperationName == InjectSpan);

        // A reported outcome is a decision, however unwelcome, so the span is Ok and the failure
        // counter is untouched -- the operator alerts on the outcome dimension, not on error.class.
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
        Assert.Equal(outcome, span.GetTagItem("agentexperience.outcome"));
        Assert.Equal(outcome, Assert.Single(probe.For(CountInstrument, "inject")).Tags["outcome"]);
        Assert.Equal(outcome, Assert.Single(probe.For(DurationInstrument, "inject")).Tags["outcome"]);
        Assert.Empty(probe.For(FailuresInstrument, "inject"));

        // And the host's correlation identifier is there for all of them but the one whose request
        // never resolved -- which is the point of reading it off the request rather than off a result
        // these outcomes may not have.
        if (outcome != nameof(InjectionOutcome.Failed))
        {
            Assert.Equal("corr-1", span.GetTagItem("agentexperience.correlation_id"));
        }
    }

    [Fact]
    public async Task Span_attributes_are_only_the_documented_keys()
    {
        using var probe = TelemetryProbe.Start();

        var harness = new Harness { Limits = ExperienceInjectionLimits.Default with { MaxRecords = 1 } };
        harness.World.Publish(InjectionRecords.Record(InjectionRecords.Id(1), TestScope), relevance: 1d);
        harness.World.Publish(InjectionRecords.Record(InjectionRecords.Id(2), TestScope), relevance: 0.5d);
        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        // Plus an outcome that carries an InjectionFailure -- whose Reason is host- and driver-derived
        // free text, and is exactly the kind of value an attribute added in good faith would carry.
        var failing = new Harness { World = { SearchThrows = new ExperienceStoreException("Host=db;Password=hunter2") } };
        failing.World.Publish(InjectionRecords.Record(InjectionRecords.Id(1), TestScope));
        await failing.Agent().RunAsync("refund ticket stuck on a lock");
        Assert.NotNull(Assert.Single(failing.Results).Failure);

        // Plus a faulted injection, so error.type and error.class have been written too.
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var faulting = new Harness();
        faulting.World.Publish(InjectionRecords.Record(InjectionRecords.Id(1), TestScope));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => faulting.Agent().RunAsync("refund ticket stuck on a lock", cancellationToken: cancellation.Token));

        // Exactly these, no more. The marker sweep can only prove that the content one run happened to
        // carry stayed off a span; an exact key set is what makes adding an attribute that carries
        // host free text a deliberate, reviewed act.
        Assert.Equal(
            new[]
            {
                "agentexperience.correlation_id",
                "agentexperience.error.class",
                "agentexperience.omitted_count",
                "agentexperience.operation",
                "agentexperience.outcome",
                "error.type",
            },
            probe.EverySpanTagKey.Order(StringComparer.Ordinal));
    }

    /// <summary>Builds the harness that drives one of the four alertable outcomes.</summary>
    private static Harness Harnessed(string outcome, TaskCompletionSource released) => outcome switch
    {
        nameof(InjectionOutcome.RetrievalTimedOut) => new Harness
        {
            // A real clock and a short budget, so retrieval reaches its own timeout rather than a
            // frozen provider's timer that never fires.
            Clock = TimeProvider.System,
            Policy = RetrievalPolicy.Default with { Timeout = TimeSpan.FromMilliseconds(20) },
            World = { SearchDelay = token => released.Task.WaitAsync(token) },
        },

        nameof(InjectionOutcome.RetrievalDenied) => new Harness
        {
            // The resolver asks in a scope the host-established authorization does not cover, so
            // retrieval refuses before either channel is touched.
            Asking = new AuthorizationContext("tenant-2", "host", ["experience:read"], DateTimeOffset.UnixEpoch),
        },

        nameof(InjectionOutcome.RetrievalFailed) => new Harness
        {
            World = { SearchThrows = new ExperienceStoreException("the database is unreachable") },
        },

        nameof(InjectionOutcome.Failed) => new Harness
        {
            Resolve = _ => throw new InvalidOperationException("the host's resolver threw"),
        },

        _ => throw new InvalidOperationException($"No harness for '{outcome}'."),
    };

    [Fact]
    public async Task Injection_telemetry_never_contains_the_block_it_injected()
    {
        using var probe = TelemetryProbe.Start();
        var harness = new Harness();
        harness.World.Publish(InjectionRecords.Record(InjectionRecords.Id(1), TestScope, lesson: $"Check the {Marker} table first."));

        await harness.Agent().RunAsync($"a refund ticket about {Marker}");

        // The marker really did reach the model, so the sweep below is about something.
        var injected = harness.InjectedText();
        Assert.NotNull(injected);
        Assert.Contains(Marker, injected, StringComparison.Ordinal);

        Assert.NotEmpty(probe.EverySpanTagValue);
        Assert.NotEmpty(probe.EveryMeasurementTagValue);

        foreach (var value in probe.EverySpanTagValue.Concat(probe.EveryMeasurementTagValue))
        {
            Assert.DoesNotContain(Marker, value, StringComparison.Ordinal);
        }

        Assert.All(probe.LibraryActivities, activity =>
        {
            Assert.Empty(activity.Events);
            Assert.Null(activity.StatusDescription);
        });
    }

    [Fact]
    public async Task Metric_dimensions_are_only_operation_outcome_error_class_and_nested()
    {
        using var probe = TelemetryProbe.Start();
        using var cancellation = new CancellationTokenSource();

        var injecting = new Harness();
        injecting.World.Publish(InjectionRecords.Record(InjectionRecords.Id(1), TestScope));
        await injecting.Agent().RunAsync("refund ticket stuck on a lock");

        await cancellation.CancelAsync();
        var faulting = new Harness();
        faulting.World.Publish(InjectionRecords.Record(InjectionRecords.Id(1), TestScope));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => faulting.Agent().RunAsync("refund ticket stuck on a lock", cancellationToken: cancellation.Token));

        var keys = probe.EveryMeasurementTagKey;

        Assert.Contains("operation", keys);
        Assert.Contains("outcome", keys);
        Assert.Contains("error.class", keys);
        Assert.Contains("nested", keys);

        // Exactly these, so a fifth dimension has to be added here on purpose. The probe subscribes to
        // AgentExperience.* rather than to this adapter alone, so this pins Core's retrieval
        // measurements as well as the adapter's own -- the two meters must not drift apart.
        Assert.Equal(new[] { "error.class", "nested", "operation", "outcome" }, keys.Order(StringComparer.Ordinal));

        // The correlation ID is on the span and only on the span: it is the host's string, so it is
        // exactly the kind of value that must never become a metric label.
        Assert.DoesNotContain("corr-1", probe.EveryMeasurementTagValue);
        Assert.Contains("corr-1", probe.EverySpanTagValue);
    }

    [Fact]
    public async Task Library_adds_no_agent_model_or_tool_spans()
    {
        // The one test that watches every source in the process: its claim is that nothing this
        // library emits is agent-, model-, or tool-shaped, which means looking at everything.
        using var probe = TelemetryProbe.EverySource();

        var delegating = false;
        var duringDelegation = new List<string>();
        probe.Started = activity =>
        {
            if (delegating && activity.Source.Name.StartsWith(TelemetryProbe.SourcePrefix, StringComparison.Ordinal))
            {
                duringDelegation.Add(activity.OperationName);
            }
        };

        var capture = new InMemoryExperienceCaptureService(
            new DefaultSanitizer(SanitizationOptions.Empty),
            new CaptureLimits(10, 10, 1_000, 1_000));

        var inner = new WindowedAgent(open => delegating = open);
        var wrapped = inner.AsBuilder().UseExperienceCapture(capture, new ExperienceCaptureOptions
        {
            NewId = Guid.NewGuid,
            ResolveRun = context => new ExperienceRunDescriptor(context.Messages.Last().Text, TestScope, "captured by tests"),
            FinalizationTimeout = TimeSpan.FromSeconds(5),

            // Tool-call capture installs MAF's own function-invocation middleware, which needs a
            // function-invoking chat client underneath. This stub agent is not one, and tool calls are
            // beside the point here: what matters is what happens around the delegation.
            CaptureToolCalls = false,
        }).Build();

        var response = await wrapped.RunAsync("do the thing");

        // The wrapper did its job...
        Assert.Equal("windowed agent reply", response.Text);
        Assert.True(inner.Ran);

        // ...and every span this library emitted is one of its own operations, named from the frozen
        // table. Nothing agent-, model-, or tool-shaped: MAF owns those and they are not duplicated.
        var frozen = new[]
        {
            "agentexperience.capture.start_run",
            "agentexperience.capture.append_attempt",
            "agentexperience.capture.complete_run",
            "agentexperience.verify",
            "agentexperience.reflect",
            "agentexperience.finalize",
            "agentexperience.lifecycle.commit",
            "agentexperience.confidence.apply",
            "agentexperience.retrieve",
            "agentexperience.index",
            "agentexperience.deindex",
            "agentexperience.reindex",
            "agentexperience.reuse_feedback",
            InjectSpan,
        };

        // The control span is under the same prefix on purpose -- that is what makes it a control for
        // the window below -- so it is excluded here by source rather than by name.
        var emitted = probe.LibraryActivities
            .Where(activity => activity.Source.Name != Control.Name)
            .ToList();

        Assert.NotEmpty(emitted);
        Assert.All(emitted, activity =>
            Assert.True(frozen.Contains(activity.OperationName, StringComparer.Ordinal), $"'{activity.OperationName}' is not in the frozen operation table."));

        // Exactly the operations the wrapped run performed, each exactly once. The frozen-name check
        // above is a set membership test, so a span opened around the delegation that <em>reused</em> a
        // frozen name would satisfy it: this counts them instead. One invocation, one captured run.
        Assert.Equal(
            new[]
            {
                "agentexperience.capture.append_attempt",
                "agentexperience.capture.complete_run",
                "agentexperience.capture.start_run",
            },
            emitted.Select(activity => activity.OperationName).Order(StringComparer.Ordinal));

        // And the delegation itself -- the window in which the wrapped agent's own RunAsync body runs
        // -- produced none of this library's spans. Capture brackets the run; it never wraps it in a
        // span.
        //
        // The window has a positive control: the agent opens one span of its own, from a source under
        // the same prefix a host subscribes to, while its body is running. So the window really does
        // catch what is started inside it, and "empty except the control" is a claim that can fail --
        // which it did not when the window could never be non-empty at all.
        Assert.Equal([DelegationControlSpan], duringDelegation);
    }

    /// <summary>
    /// Matrix row 15's other half. The capture wrapper reads <see cref="Activity.Current"/> to stamp
    /// the run's provenance correlation, and this is the one row where the adapter's new
    /// <c>inject</c> span could plausibly have changed existing behaviour: it opens and closes an
    /// activity on the very path that harvest runs on.
    /// </summary>
    [Fact]
    public async Task Capture_still_harvests_the_ambient_trace_id_while_injection_runs()
    {
        using var probe = TelemetryProbe.Start();

        var harness = new Harness();
        harness.World.Publish(InjectionRecords.Record(InjectionRecords.Id(1), TestScope));

        var capture = new InMemoryExperienceCaptureService(
            new DefaultSanitizer(SanitizationOptions.Empty),
            new CaptureLimits(10, 10, 1_000, 1_000));

        var runId = Guid.NewGuid();
        var wrapped = harness.Agent().AsBuilder().UseExperienceCapture(capture, new ExperienceCaptureOptions
        {
            NewId = () => runId,
            ResolveRun = context => new ExperienceRunDescriptor(context.Messages.Last().Text, TestScope, "captured by tests"),
            FinalizationTimeout = TimeSpan.FromSeconds(5),
            CaptureToolCalls = false,
        }).Build();

        using var ambient = Control.StartActivity("host.invocation", ActivityKind.Internal);
        Assert.NotNull(ambient);
        Assert.Same(ambient, Activity.Current);

        await wrapped.RunAsync("refund ticket stuck on a lock");

        // The injection really did happen on this path, so the harvest below ran with the inject span
        // having been opened and closed underneath it.
        Assert.Single(probe.LibraryActivities, activity => activity.OperationName == InjectSpan);

        Assert.True(capture.TryGetRun(runId, out var run));
        Assert.Equal(ambient.TraceId.ToHexString(), run!.Provenance.CorrelationId);

        // And the caller's ambient activity is exactly the one it started: the inject span was closed
        // behind it, not left current.
        Assert.Same(ambient, Activity.Current);
    }

    /// <summary>
    /// Every arm of the adapter's classification table. It is restated rather than shared with Core,
    /// and replacing the whole switch with <c>=&gt; Cancelled</c> used to leave every adapter test
    /// passing -- a dead copy whose own doc comment warned of exactly the drift nothing checked.
    /// </summary>
    /// <param name="kind">Which failure to classify.</param>
    /// <param name="hostCancelled">Whether the token the host handed the outermost operation was cancelled.</param>
    /// <param name="operationCancelled">Whether the token this operation itself was handed was cancelled.</param>
    /// <param name="expected">The bounded class the adapter must report.</param>
    [Theory]
    [InlineData("cancelled-by-caller", true, true, nameof(ExperienceOperationErrorClass.Cancelled))]
    [InlineData("cancelled-by-a-library-budget", false, true, nameof(ExperienceOperationErrorClass.Timeout))]
    [InlineData("cancelled-by-nobody", false, false, nameof(ExperienceOperationErrorClass.Infrastructure))]
    [InlineData("timeout", false, false, nameof(ExperienceOperationErrorClass.Timeout))]
    [InlineData("store", false, false, nameof(ExperienceOperationErrorClass.Infrastructure))]
    [InlineData("anything-else", false, false, nameof(ExperienceOperationErrorClass.Unexpected))]
    public void The_adapter_classification_table(string kind, bool hostCancelled, bool operationCancelled, string expected)
    {
        // Two tokens, because a cancelled token is not automatically the caller's: an inner step can
        // run on a deadline this library imposed, and a deadline expiring is a Timeout rather than
        // somebody giving up. An injection always passes the same token for both -- this adapter
        // imposes no budget of its own -- but the table it applies has to be Core's, arm for arm.
        using var host = new CancellationTokenSource();
        using var operationCancellation = new CancellationTokenSource();
        if (hostCancelled)
        {
            host.Cancel();
        }

        if (operationCancelled)
        {
            operationCancellation.Cancel();
        }

        var failure = Failure(kind, operationCancellation.Token);
        var classified = InjectionDiagnostics.Classify(failure, operationCancellation.Token, host.Token);

        Assert.Equal(expected, classified.ToString());
        Assert.Equal(expected, InjectionDiagnostics.Name(classified));
    }

    /// <summary>
    /// The adapter's failure path carries whatever arrives, not only the cancellation that is the one
    /// thing able to escape the injection body today.
    /// </summary>
    [Fact]
    public void A_faulted_injection_records_whatever_class_the_failure_falls_into()
    {
        using var probe = TelemetryProbe.Start();

        var trace = InjectionDiagnostics.Start();
        InjectionDiagnostics.Faulted(trace, new TimeoutException("the bound was exceeded"), CancellationToken.None);
        trace.Activity?.Dispose();

        var span = Assert.Single(probe.LibraryActivities, activity => activity.OperationName == InjectSpan);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(nameof(ExperienceOperationErrorClass.Timeout), span.GetTagItem("agentexperience.error.class"));
        Assert.Equal("System.TimeoutException", span.GetTagItem("error.type"));

        Assert.Equal(
            nameof(ExperienceOperationErrorClass.Timeout),
            Assert.Single(probe.For(FailuresInstrument, "inject")).Tags["error.class"]);
        Assert.Equal("Faulted", Assert.Single(probe.For(CountInstrument, "inject")).Tags["outcome"]);
        Assert.Single(probe.For(DurationInstrument, "inject"));
    }

    /// <summary>
    /// "Report is the one place <c>inject</c> is counted" is enforced, not merely intended. A future
    /// early <c>return</c> in the injection body would otherwise emit a span with no outcome, no count
    /// and no duration -- a silent hole in the operation an operator alerts on.
    /// </summary>
    [Fact]
    public void An_injection_that_reported_nothing_is_still_counted()
    {
        using var probe = TelemetryProbe.Start();

        var unreported = InjectionDiagnostics.Start();
        InjectionDiagnostics.Closed(unreported);
        unreported.Activity?.Dispose();

        var span = Assert.Single(probe.LibraryActivities, activity => activity.OperationName == InjectSpan);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("Faulted", span.GetTagItem("agentexperience.outcome"));

        // An exit with no outcome is this library's bug, not a dependency's, and no exception was
        // involved -- so the class is Unexpected and there is no error.type to report.
        Assert.Equal(nameof(ExperienceOperationErrorClass.Unexpected), span.GetTagItem("agentexperience.error.class"));
        Assert.Null(span.GetTagItem("error.type"));

        Assert.Single(probe.For(CountInstrument, "inject"));
        Assert.Single(probe.For(DurationInstrument, "inject"));
        Assert.Single(probe.For(FailuresInstrument, "inject"));
    }

    [Fact]
    public async Task An_injection_that_reported_its_outcome_is_not_counted_twice()
    {
        using var probe = TelemetryProbe.Start();
        var harness = new Harness();
        harness.World.Publish(InjectionRecords.Record(InjectionRecords.Id(1), TestScope));

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        // The finally's invariant check ran on a trace that had already reported, and did nothing:
        // one injection, one count, one duration, and no failure at all.
        Assert.Equal(nameof(InjectionOutcome.Injected), Assert.Single(probe.For(CountInstrument, "inject")).Tags["outcome"]);
        Assert.Single(probe.For(DurationInstrument, "inject"));
        Assert.Empty(probe.For(FailuresInstrument, "inject"));
    }

    private static Exception Failure(string kind, CancellationToken cancellationToken) => kind switch
    {
        "cancelled-by-caller" or "cancelled-by-a-library-budget" => new OperationCanceledException("a cancelled token", cancellationToken),
        "cancelled-by-nobody" => new OperationCanceledException("a client-side timeout, arriving as a cancellation nobody asked for"),
        "timeout" => new TimeoutException("the call exceeded its bound"),
        "store" => new ExperienceStoreException("the database is unreachable"),
        _ => new InvalidOperationException("something nobody classified"),
    };

    /// <summary>
    /// A minimal <see cref="AIAgent"/> that reports when its own <c>RunAsync</c> body is executing, so
    /// a listener can tell "around the delegation" from "during the delegation".
    /// </summary>
    private sealed class WindowedAgent(Action<bool> window) : AIAgent
    {
        /// <summary>Whether the agent's body ever ran, so an empty delegation window cannot pass by never opening.</summary>
        public bool Ran { get; private set; }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
            new(new WindowedSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
            new(session.StateBag.Serialize());

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
            new(new WindowedSession());

        protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        {
            window(true);
            try
            {
                Ran = true;

                // The window's positive control: one span, from a source under the prefix a host
                // subscribes to, started while the delegation is in flight. Without it "the window was
                // empty" is a sentence that cannot be false.
                using (Control.StartActivity(DelegationControlSpan, ActivityKind.Internal))
                {
                    await Task.Yield();
                }

                return new AgentResponse(new ChatMessage(ChatRole.Assistant, "windowed agent reply"));
            }
            finally
            {
                window(false);
            }
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            window(true);
            Ran = true;
            await Task.Yield();
            yield return new AgentResponseUpdate(ChatRole.Assistant, "windowed");
            window(false);
        }

        private sealed class WindowedSession : AgentSession;
    }

    /// <summary>The same shape the injection tests use: a real agent, a real retrieval service, a fake world.</summary>
    private sealed class Harness
    {
        private readonly List<ExperienceInjectionResult> _results = [];

        public FakeExperienceWorld World { get; } = new();

        public RecordingChatClient Client { get; } = new();

        public TimeProvider Clock { get; init; } = new FrozenTimeProvider(InjectionRecords.Now);

        /// <summary>The retrieval policy the provider's own retrieval service runs under, so a test can give it a real, short timeout.</summary>
        public RetrievalPolicy Policy { get; init; } = RetrievalPolicy.Default;

        /// <summary>The authorization the default resolver asks with, so a test can ask in a scope the host does not cover.</summary>
        public AuthorizationContext Asking { get; init; } = Authorization;

        public ExperienceInjectionLimits Limits { get; init; } = ExperienceInjectionLimits.Default;

        public Func<ExperienceInjectionContext, RetrieveExperienceRequest?>? Resolve { get; init; }

        public Func<ExperienceInjectionDecisionContext, InjectionDecision>? Decide { get; init; }

        public IReadOnlyList<ExperienceInjectionResult> Results
        {
            get
            {
                lock (_results)
                {
                    return [.. _results];
                }
            }
        }

        public ChatClientAgent Agent() => new(Client, new ChatClientAgentOptions { AIContextProviders = [Provider()] });

        public string? InjectedText() => Client.LastMessages
            ?.FirstOrDefault(m => m.AdditionalProperties?.ContainsKey(ExperienceContextProvider.HistoricalReferenceKey) == true)
            ?.Text;

        public ExperienceContextProvider Provider() => new(
            new ExperienceRetrievalService(World, Policy, RankingWeights.Default, Clock),
            World,
            new ExperienceInjectionOptions
            {
                ResolveRequest = Resolve ?? (context => new RetrieveExperienceRequest(
                    Asking,
                    TestScope,
                    context.Messages.LastOrDefault(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text))?.Text
                        ?? "refund ticket stuck on a lock",
                    CorrelationId: "corr-1")),
                Limits = Limits,
                DecideInjection = Decide,
                TimeProvider = Clock,
                OnContextInjected = result =>
                {
                    lock (_results)
                    {
                        _results.Add(result);
                    }
                },
            });
    }
}

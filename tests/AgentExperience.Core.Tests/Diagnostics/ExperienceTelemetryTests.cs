using System.Diagnostics;
using AgentExperience.Core.Diagnostics;
using AgentExperience.Core.Feedback;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Indexing;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Core.Retrieval;

namespace AgentExperience.Core.Tests.Diagnostics;

/// <summary>
/// What a host actually receives when it subscribes to <c>AgentExperience.*</c>: one span and one
/// count/duration pair per instrumented operation, a failure counter that only a thrown operation
/// moves, four metric dimensions and no more, a pinned set of span attributes, and -- the point of
/// the whole story -- not one byte of captured content anywhere in any of them.
/// </summary>
/// <remarks>
/// Listeners are process-wide, so this class runs in the telemetry collection, on its own. Every test
/// asserts against the listener's own collected data rather than against the library's internals: if
/// an exporter would not see it, neither does a test here.
/// </remarks>
[Collection(TelemetryCollection.Name)]
public class ExperienceTelemetryTests
{
    private const string CountInstrument = "agentexperience.operation.count";
    private const string DurationInstrument = "agentexperience.operation.duration";
    private const string FailuresInstrument = "agentexperience.operation.failures";

    private const string OperationDimension = "operation";
    private const string OutcomeDimension = "outcome";
    private const string ErrorClassDimension = "error.class";
    private const string NestedDimension = "nested";

    private const string OperationAttribute = "agentexperience.operation";
    private const string OutcomeAttribute = "agentexperience.outcome";
    private const string ErrorClassAttribute = "agentexperience.error.class";
    private const string ErrorTypeAttribute = "error.type";
    private const string StageAttribute = "agentexperience.stage";
    private const string CorrelationIdAttribute = "agentexperience.correlation_id";

    private const string CoreSource = "AgentExperience.Core";

    private const string Faulted = "Faulted";

    /// <summary>
    /// The frozen operation table: the <c>operation</c> dimension value, the span name it must carry,
    /// and the outcome a fully successful drive of the loop reaches for it. Restated here as literals
    /// rather than read from the library, so a rename in the library is a failing test rather than a
    /// silently re-labelled dashboard.
    /// </summary>
    private static readonly (string Operation, string SpanName, string Outcome)[] FrozenTable =
    [
        ("capture.start_run", "agentexperience.capture.start_run", nameof(StartRunOutcome.Started)),
        ("capture.append_attempt", "agentexperience.capture.append_attempt", nameof(AppendAttemptOutcome.Recorded)),
        ("capture.complete_run", "agentexperience.capture.complete_run", nameof(CompleteRunOutcome.Recorded)),
        ("verify", "agentexperience.verify", nameof(TaskVerificationStatus.Verified)),
        ("reflect", "agentexperience.reflect", nameof(TaskVerificationStatus.Verified)),
        ("finalize", "agentexperience.finalize", nameof(FinalizationOutcome.Validated)),
        ("lifecycle.commit", "agentexperience.lifecycle.commit", nameof(LifecycleTransitionOutcome.Committed)),
        ("confidence.apply", "agentexperience.confidence.apply", nameof(ConfidenceUpdateOutcome.Applied)),
        ("retrieve", "agentexperience.retrieve", nameof(RetrievalOutcome.Completed)),
        ("index", "agentexperience.index", nameof(ExperienceIndexingOutcome.Indexed)),
        ("deindex", "agentexperience.deindex", nameof(ExperienceDeindexingOutcome.Removed)),
        ("reindex", "agentexperience.reindex", nameof(ExperienceReindexOutcome.Completed)),
        ("reuse_feedback", "agentexperience.reuse_feedback", nameof(ExperienceReuseFeedbackOutcome.Recorded)),
    ];

    /// <summary>
    /// Every span attribute this library is allowed to write, and nothing else. This is the span-side
    /// counterpart of the exact metric-dimension set: the marker sweep can only prove that the content
    /// one drive happened to carry stayed off a span, whereas an exact key set is what makes adding an
    /// attribute that carries host free text a deliberate, reviewed act.
    /// </summary>
    private static readonly string[] AllowedSpanAttributes =
    [
        ErrorClassAttribute,
        CorrelationIdAttribute,
        "agentexperience.attempt_id",
        "agentexperience.event_id",
        "agentexperience.experience_id",
        OperationAttribute,
        OutcomeAttribute,
        "agentexperience.reflection_id",
        "agentexperience.run_id",
        StageAttribute,
        ErrorTypeAttribute,
        "agentexperience.feedback_id",
    ];

    /// <summary>
    /// Where each documented identifier attribute must actually appear. Deleting the library's
    /// <c>SetTag</c> calls used to leave every test passing, because the attributes were swept for the
    /// marker and never asserted present.
    /// </summary>
    private static readonly (string SpanName, string Attribute)[] DocumentedIdentifiers =
    [
        ("agentexperience.capture.start_run", "agentexperience.run_id"),
        ("agentexperience.capture.append_attempt", "agentexperience.run_id"),
        ("agentexperience.capture.append_attempt", "agentexperience.attempt_id"),
        ("agentexperience.capture.complete_run", "agentexperience.run_id"),
        ("agentexperience.capture.complete_run", "agentexperience.event_id"),
        ("agentexperience.reflect", "agentexperience.run_id"),
        ("agentexperience.reflect", "agentexperience.reflection_id"),
        ("agentexperience.finalize", "agentexperience.run_id"),
        ("agentexperience.finalize", "agentexperience.experience_id"),
        ("agentexperience.finalize", StageAttribute),
        ("agentexperience.lifecycle.commit", "agentexperience.experience_id"),
        ("agentexperience.lifecycle.commit", "agentexperience.event_id"),
        ("agentexperience.confidence.apply", "agentexperience.experience_id"),
        ("agentexperience.confidence.apply", "agentexperience.event_id"),
        ("agentexperience.retrieve", CorrelationIdAttribute),
        ("agentexperience.index", "agentexperience.experience_id"),
        ("agentexperience.deindex", "agentexperience.experience_id"),
        ("agentexperience.reuse_feedback", "agentexperience.feedback_id"),
        ("agentexperience.reuse_feedback", "agentexperience.run_id"),
    ];

    // ---------------------------------------------------------------------------------------------
    // What a successful operation emits
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Record_outcome_reaches_the_counter_and_histogram()
    {
        using var probe = TelemetryProbe.All();
        var loop = new ExperienceLoop();

        var results = await loop.DriveAsync();

        // The loop really did run end to end; otherwise an operation with no measurement would be
        // indistinguishable from an operation that never happened.
        Assert.Equal(FinalizationOutcome.Validated, results.Finalized.Outcome);
        Assert.Equal(RetrievalOutcome.Completed, results.Retrieved.Outcome);
        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, results.Feedback.Outcome);
        Assert.Equal(ExperienceDeindexingOutcome.Removed, results.Deindexed.Outcome);

        foreach (var (operation, spanName, outcome) in FrozenTable)
        {
            var counted = probe.For(CountInstrument, operation);
            var timed = probe.For(DurationInstrument, operation);

            Assert.NotEmpty(counted);
            Assert.NotEmpty(timed);

            // Every measurement for this operation carries the operation's own outcome enum member
            // name, verbatim -- not a description, not a lower-cased alias, not "ok".
            Assert.All(counted, measurement =>
            {
                Assert.Equal(1d, measurement.Value);
                Assert.Equal(outcome, Assert.IsType<string>(measurement.Tags[OutcomeDimension]));
                Assert.Equal(CoreSource, measurement.Meter);
            });

            Assert.All(timed, measurement =>
            {
                Assert.True(measurement.Value >= 0d, $"'{operation}' recorded a negative duration.");
                Assert.Equal(outcome, Assert.IsType<string>(measurement.Tags[OutcomeDimension]));
            });

            var spans = probe.LibraryActivities.Where(activity => activity.OperationName == spanName).ToList();
            Assert.NotEmpty(spans);
            Assert.All(spans, span =>
            {
                Assert.Equal(ActivityStatusCode.Ok, span.Status);
                Assert.Equal(operation, span.GetTagItem(OperationAttribute));
                Assert.Equal(outcome, span.GetTagItem(OutcomeAttribute));
            });
        }

        // Nothing threw, so the failure counter was never touched at all.
        Assert.Empty(probe.For(FailuresInstrument));
    }

    [Fact]
    public async Task Every_library_span_is_named_from_the_frozen_table()
    {
        using var probe = TelemetryProbe.All();

        await new ExperienceLoop().DriveAsync();

        var allowed = FrozenTable.Select(entry => entry.SpanName).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(probe.LibraryActivities);
        Assert.All(probe.LibraryActivities, activity =>
            Assert.True(allowed.Contains(activity.OperationName), $"'{activity.OperationName}' is not in the frozen operation table."));
    }

    /// <summary>
    /// The exact call table one drive of the loop produces, nesting included. Several operations are
    /// steps of a larger one -- a finalization verifies, reflects, commits the record's initial
    /// lifecycle event and indexes it; a reuse-feedback submission applies confidence evidence per
    /// exposed record; a one-record index runs a one-record re-index pass -- and every one of them is
    /// emitted, because an inner step that emits nothing is an inner step whose failures reach no
    /// alert. <c>nested</c> is what keeps the two questions apart, so this pins both halves: how many
    /// calls the host made, and how many the library made under them.
    /// </summary>
    [Fact]
    public async Task One_call_to_an_operation_is_one_count_and_one_duration()
    {
        using var probe = TelemetryProbe.All();
        var loop = new ExperienceLoop();

        var results = await loop.DriveAsync();
        Assert.Equal(FinalizationOutcome.Validated, results.Finalized.Outcome);

        // "operation nested=x" -> how many counts carried it. Written out in full rather than as a
        // per-operation total, so that moving a call between the direct and the nested column is a
        // failing test rather than an invisible re-labelling of somebody's dashboard.
        var expected = new[]
        {
            "capture.append_attempt nested=False x2",
            "capture.complete_run nested=False x1",
            "capture.start_run nested=False x1",
            "confidence.apply nested=False x1",
            "confidence.apply nested=True x1",
            "deindex nested=False x1",
            "finalize nested=False x1",
            "index nested=False x1",
            "index nested=True x1",
            "lifecycle.commit nested=False x1",
            "lifecycle.commit nested=True x1",
            "reflect nested=True x1",
            "reindex nested=False x1",
            "reindex nested=True x2",
            "retrieve nested=False x1",
            "reuse_feedback nested=False x1",
            "verify nested=False x1",
            "verify nested=True x1",
        };

        Assert.Equal(expected, Calls(probe, CountInstrument));

        // The histogram is written from the same tag list as the counter, so one call is one count and
        // one duration under the same dimensions -- not merely the same number of them.
        Assert.Equal(expected, Calls(probe, DurationInstrument));

        // `reflect` is no longer an exception to anything. The reflector is an injected port called
        // from inside a finalization, and it reports exactly what the private-sibling siblings report:
        // one nested operation, which is the real call graph.
        Assert.Empty(probe.For(CountInstrument, "reflect", nested: false));
        Assert.Single(probe.For(CountInstrument, "reflect", nested: true));
    }

    /// <summary>
    /// The <c>nested</c> dimension itself: the same operation, called by the host and called by
    /// another instrumented operation, is one series an operator can split. Without it, the only way
    /// to stop a finalization from counting as seven operations was to stop emitting six of them.
    /// </summary>
    [Fact]
    public async Task A_direct_call_is_not_nested_and_the_same_operation_inside_finalize_is()
    {
        using var probe = TelemetryProbe.All();
        var loop = new ExperienceLoop();

        var results = await loop.DriveAsync();
        Assert.Equal(FinalizationOutcome.Validated, results.Finalized.Outcome);

        // Every operation the drive calls directly is exactly that -- whatever else it does inside.
        foreach (var operation in new[] { "capture.start_run", "finalize", "retrieve", "reuse_feedback", "deindex" })
        {
            Assert.NotEmpty(probe.For(CountInstrument, operation, nested: false));
            Assert.Empty(probe.For(CountInstrument, operation, nested: true));
        }

        // And the four the drive calls both ways are on both sides of the dimension: one direct call
        // from the test, one from inside the operation that contains it.
        foreach (var operation in new[] { "verify", "lifecycle.commit", "index", "confidence.apply" })
        {
            Assert.Single(probe.For(CountInstrument, operation, nested: false));
            Assert.Single(probe.For(CountInstrument, operation, nested: true));
        }

        // The nested ones really are children of the operation that called them, so the trace says the
        // same thing the dimension does -- which is why `nested` is a metric dimension only.
        var finalize = Assert.Single(probe.LibraryActivities, a => a.OperationName == "agentexperience.finalize");
        var nested = probe.LibraryActivities
            .Where(a => a.Parent is not null && a.Parent.Id == finalize.Id)
            .Select(a => a.OperationName)
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            new[]
            {
                "agentexperience.index",
                "agentexperience.lifecycle.commit",
                "agentexperience.reflect",
                "agentexperience.verify",
            },
            nested);
    }

    [Fact]
    public async Task The_span_carries_the_host_correlation_id_and_omits_it_when_there_is_none()
    {
        using var probe = TelemetryProbe.SpansOnly();
        var loop = new ExperienceLoop();
        await loop.DriveAsync();

        var withId = Assert.Single(probe.LibraryActivities, a => a.OperationName == "agentexperience.retrieve");
        Assert.Equal(ExperienceLoop.CorrelationId, withId.GetTagItem(CorrelationIdAttribute));

        await loop.Retrieval.RetrieveAsync(
            new RetrieveExperienceRequest(ExperienceLoop.Authorization, ExperienceLoop.Scope, "a task with no correlation id"));

        var withoutId = probe.LibraryActivities.Last(a => a.OperationName == "agentexperience.retrieve");

        // Omitted, not blank: an absent attribute and an empty one do not mean the same thing.
        Assert.Null(withoutId.GetTagItem(CorrelationIdAttribute));
        Assert.DoesNotContain(withoutId.TagObjects, tag => tag.Key == CorrelationIdAttribute);
    }

    /// <summary>
    /// Matrix row 11: a retrieval that ran out of time is a returned decision, and the correlation ID
    /// is on its span -- which is the whole point of reading the identifier off the <em>request</em>
    /// rather than off a result that a slow or throwing retrieval may never produce.
    /// </summary>
    [Fact]
    public async Task A_timed_out_retrieval_still_echoes_the_correlation_id()
    {
        using var probe = TelemetryProbe.All();

        var released = new TaskCompletionSource();
        var candidates = new SlowCandidateSource(released);
        var retrieval = new ExperienceRetrievalService(
            candidates,
            RetrievalPolicy.Default with { Timeout = TimeSpan.FromMilliseconds(20) },
            RankingWeights.Default,
            TimeProvider.System);

        var result = await retrieval.RetrieveAsync(new RetrieveExperienceRequest(
            ExperienceLoop.Authorization,
            ExperienceLoop.Scope,
            "a task whose candidate source never answers",
            RequiredEnvironmentAttributes: null,
            CorrelationId: ExperienceLoop.CorrelationId));

        released.TrySetResult();

        Assert.Equal(RetrievalOutcome.TimedOut, result.Outcome);

        var span = Assert.Single(probe.LibraryActivities, a => a.OperationName == "agentexperience.retrieve");
        Assert.Equal(ExperienceLoop.CorrelationId, span.GetTagItem(CorrelationIdAttribute));

        // A timeout is a decision, not a failure: Ok status, the real outcome name, no failure count.
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
        Assert.Equal(nameof(RetrievalOutcome.TimedOut), span.GetTagItem(OutcomeAttribute));
        Assert.Equal(
            nameof(RetrievalOutcome.TimedOut),
            Assert.Single(probe.For(CountInstrument, "retrieve")).Tags[OutcomeDimension]);
        Assert.Empty(probe.For(FailuresInstrument));

        // ...and it is on the span only, never on a measurement: the host owns that string.
        Assert.DoesNotContain(ExperienceLoop.CorrelationId, probe.EveryMeasurementTagValue);
    }

    /// <summary>
    /// A retrieval that threw carries the correlation ID too. Before it was read from the request,
    /// exactly the span an operator opens first -- the one that failed -- was the one that could not
    /// say which invocation it belonged to.
    /// </summary>
    [Fact]
    public async Task A_thrown_retrieval_still_echoes_the_correlation_id()
    {
        using var probe = TelemetryProbe.All();
        var loop = new ExperienceLoop();

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.Retrieval.RetrieveAsync(
            new RetrieveExperienceRequest(
                ExperienceLoop.Authorization,
                ExperienceLoop.Scope,
                "a task the caller gave up on",
                RequiredEnvironmentAttributes: null,
                CorrelationId: ExperienceLoop.CorrelationId),
            cancellation.Token));

        var span = Assert.Single(probe.LibraryActivities, a => a.OperationName == "agentexperience.retrieve");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(ExperienceLoop.CorrelationId, span.GetTagItem(CorrelationIdAttribute));
    }

    // ---------------------------------------------------------------------------------------------
    // The span attribute set
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Span_attributes_are_only_the_documented_keys()
    {
        using var probe = TelemetryProbe.All();
        var loop = new ExperienceLoop();

        await loop.DriveAsync();

        // Plus one thrown operation, so error.type and error.class have been written by the time the
        // keys are collected and the exact set below is not an accident of the happy path.
        loop.Store.Throws = () => new ExperienceStoreException("the database is unreachable");
        await Assert.ThrowsAsync<ExperienceStoreException>(() => loop.Lifecycle.CommitAsync(
            ExperienceLoop.Authorization,
            new CommitLifecycleTransitionRequest(
                Guid.NewGuid(), Guid.NewGuid(), ExperienceLoop.Scope, ExperienceStatus.Candidate,
                ExperienceStatus.Validated, "initial", "tests", ExperienceLoop.Now, 0),
            CancellationToken.None));

        // Exactly these, no more: a new attribute has to be added here on purpose.
        Assert.Equal(
            AllowedSpanAttributes.Order(StringComparer.Ordinal),
            probe.EverySpanTagKey.Order(StringComparer.Ordinal));

        // And every documented identifier is really written where it is documented, with a value.
        // Without this half, deleting every identifier SetTag call in the library passes.
        foreach (var (spanName, attribute) in DocumentedIdentifiers)
        {
            var span = probe.LibraryActivities.FirstOrDefault(activity => activity.OperationName == spanName);
            Assert.True(span is not null, $"No '{spanName}' span was emitted at all.");

            var value = span!.GetTagItem(attribute) as string;
            Assert.True(
                !string.IsNullOrWhiteSpace(value),
                $"'{spanName}' carries no '{attribute}'.");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // A decision is not a failure
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rejection_outcomes_do_not_increment_failures()
    {
        using var probe = TelemetryProbe.All();
        var loop = new ExperienceLoop();

        // A retrieval refused before either channel is touched. RetrievalOutcome has no
        // "NoCandidates" member (the spec's example names one that does not exist); Denied and Failed
        // are the two returned-rather-than-thrown refusals this service actually produces.
        var denied = await loop.Retrieval.RetrieveAsync(new RetrieveExperienceRequest(
            new AuthorizationContext("tenant-2", "other-principal", ["experience:read"], ExperienceLoop.Now),
            ExperienceLoop.Scope,
            "a task in a scope the host does not cover"));

        loop.Candidates.Outcome = ExperienceStoreOutcome.Denied;
        var failed = await loop.Retrieval.RetrieveAsync(new RetrieveExperienceRequest(
            ExperienceLoop.Authorization,
            ExperienceLoop.Scope,
            "a task whose candidate source refuses to answer"));

        // A transition the table refuses: Core never calls the store at all.
        var refused = await loop.Lifecycle.CommitAsync(
            ExperienceLoop.Authorization,
            new CommitLifecycleTransitionRequest(
                EventId: Guid.NewGuid(),
                ExperienceId: Guid.NewGuid(),
                Scope: ExperienceLoop.Scope,
                PriorStatus: ExperienceStatus.Revoked,
                CurrentStatus: ExperienceStatus.Validated,
                Reason: "a revoked record cannot be validated again",
                Producer: "tests",
                OccurredAt: ExperienceLoop.Now,
                ExpectedRevision: 3),
            CancellationToken.None);

        Assert.Equal(RetrievalOutcome.Denied, denied.Outcome);
        Assert.Equal(RetrievalOutcome.Failed, failed.Outcome);
        Assert.Equal(LifecycleTransitionOutcome.TransitionNotAllowed, refused.Outcome);

        // The decisions are counted and timed under their own outcome names...
        Assert.Contains(probe.For(CountInstrument, "retrieve"), m => Equals(m.Tags[OutcomeDimension], nameof(RetrievalOutcome.Denied)));
        Assert.Contains(probe.For(CountInstrument, "retrieve"), m => Equals(m.Tags[OutcomeDimension], nameof(RetrievalOutcome.Failed)));
        Assert.Contains(probe.For(DurationInstrument, "lifecycle.commit"), m => Equals(m.Tags[OutcomeDimension], nameof(LifecycleTransitionOutcome.TransitionNotAllowed)));

        // ...their spans are Ok, because a refusal is an answer...
        Assert.All(probe.LibraryActivities, activity => Assert.Equal(ActivityStatusCode.Ok, activity.Status));

        // ...and nothing anywhere was recorded as a failure.
        Assert.Empty(probe.For(FailuresInstrument));
        Assert.All(probe.Measurements, m => Assert.False(m.Tags.ContainsKey(ErrorClassDimension)));
    }

    /// <summary>
    /// The <c>outcome</c> dimension is the operation's own enum member, on the paths that did
    /// <em>not</em> succeed as well as on the ones that did. Verified only on success, three
    /// hardcoded success literals in place of <c>result.Outcome.ToString()</c> went undetected.
    /// </summary>
    /// <param name="operation">The operation whose rejection is driven.</param>
    [Theory]
    [InlineData("capture.start_run")]
    [InlineData("capture.append_attempt")]
    [InlineData("capture.complete_run")]
    [InlineData("verify")]
    [InlineData("reflect")]
    [InlineData("finalize")]
    [InlineData("lifecycle.commit")]
    [InlineData("confidence.apply")]
    [InlineData("retrieve")]
    [InlineData("index")]
    [InlineData("deindex")]
    [InlineData("reindex")]
    [InlineData("reuse_feedback")]
    public async Task A_rejection_is_reported_under_its_own_outcome_name(string operation)
    {
        var loop = new ExperienceLoop();

        // Arranged before anything is listening, so the only measurements collected are the rejection's.
        var arranged = await loop.DriveAsync();

        using var probe = TelemetryProbe.All();
        var rejected = await RejectAsync(loop, arranged, operation);

        var success = FrozenTable.Single(entry => entry.Operation == operation).Outcome;
        Assert.NotEqual(success, rejected);

        var counted = Assert.Single(probe.For(CountInstrument, operation));
        Assert.Equal(rejected, Assert.IsType<string>(counted.Tags[OutcomeDimension]));
        Assert.Equal(rejected, Assert.Single(probe.For(DurationInstrument, operation)).Tags[OutcomeDimension]);

        var span = Assert.Single(probe.LibraryActivities, a => a.OperationName == "agentexperience." + operation);
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
        Assert.Equal(rejected, span.GetTagItem(OutcomeAttribute));

        // A decision, however unwelcome, is never a failure.
        Assert.Empty(probe.For(FailuresInstrument));
    }

    // ---------------------------------------------------------------------------------------------
    // Failures
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Matrix rows 6-10, per operation. Covering only <c>lifecycle.commit</c> left twelve of thirteen
    /// fault paths entirely unexercised: making <c>Faulted</c> a no-op everywhere else passed.
    /// </summary>
    /// <param name="operation">The operation to make throw.</param>
    /// <param name="kind">How to make it throw. <c>port-*</c> kinds script an exception instance into a port.</param>
    /// <param name="expectedErrorClass">The bounded class the failure must be reported under.</param>
    [Theory]
    [InlineData("capture.start_run", "argument", nameof(ExperienceOperationErrorClass.Unexpected))]
    [InlineData("capture.append_attempt", "argument", nameof(ExperienceOperationErrorClass.Unexpected))]
    [InlineData("capture.append_attempt", "cancelled", nameof(ExperienceOperationErrorClass.Cancelled))]
    [InlineData("capture.complete_run", "cancelled", nameof(ExperienceOperationErrorClass.Cancelled))]
    [InlineData("verify", "argument", nameof(ExperienceOperationErrorClass.Unexpected))]
    [InlineData("verify", "cancelled", nameof(ExperienceOperationErrorClass.Cancelled))]
    [InlineData("reflect", "argument", nameof(ExperienceOperationErrorClass.Unexpected))]
    [InlineData("reflect", "cancelled", nameof(ExperienceOperationErrorClass.Cancelled))]
    [InlineData("finalize", "argument", nameof(ExperienceOperationErrorClass.Unexpected))]
    [InlineData("finalize", "cancelled", nameof(ExperienceOperationErrorClass.Cancelled))]
    [InlineData("finalize", "port-cancelled-by-nobody", nameof(ExperienceOperationErrorClass.Infrastructure))]
    [InlineData("lifecycle.commit", "cancelled", nameof(ExperienceOperationErrorClass.Cancelled))]
    [InlineData("lifecycle.commit", "port-store", nameof(ExperienceOperationErrorClass.Infrastructure))]
    [InlineData("lifecycle.commit", "port-cancelled-by-nobody", nameof(ExperienceOperationErrorClass.Infrastructure))]
    [InlineData("lifecycle.commit", "port-timeout", nameof(ExperienceOperationErrorClass.Timeout))]
    [InlineData("lifecycle.commit", "port-unexpected", nameof(ExperienceOperationErrorClass.Unexpected))]
    [InlineData("confidence.apply", "cancelled", nameof(ExperienceOperationErrorClass.Cancelled))]
    [InlineData("confidence.apply", "port-store", nameof(ExperienceOperationErrorClass.Infrastructure))]
    [InlineData("confidence.apply", "port-timeout", nameof(ExperienceOperationErrorClass.Timeout))]
    [InlineData("confidence.apply", "port-unexpected", nameof(ExperienceOperationErrorClass.Unexpected))]
    [InlineData("retrieve", "argument", nameof(ExperienceOperationErrorClass.Unexpected))]
    [InlineData("retrieve", "cancelled", nameof(ExperienceOperationErrorClass.Cancelled))]
    [InlineData("index", "argument", nameof(ExperienceOperationErrorClass.Unexpected))]
    [InlineData("index", "cancelled", nameof(ExperienceOperationErrorClass.Cancelled))]
    [InlineData("index", "port-cancelled-by-nobody", nameof(ExperienceOperationErrorClass.Infrastructure))]
    [InlineData("deindex", "argument", nameof(ExperienceOperationErrorClass.Unexpected))]
    [InlineData("reindex", "argument", nameof(ExperienceOperationErrorClass.Unexpected))]
    [InlineData("reindex", "cancelled", nameof(ExperienceOperationErrorClass.Cancelled))]
    [InlineData("reindex", "port-cancelled-by-nobody", nameof(ExperienceOperationErrorClass.Infrastructure))]
    [InlineData("reuse_feedback", "cancelled", nameof(ExperienceOperationErrorClass.Cancelled))]
    [InlineData("reuse_feedback", "port-store", nameof(ExperienceOperationErrorClass.Infrastructure))]
    [InlineData("reuse_feedback", "port-timeout", nameof(ExperienceOperationErrorClass.Timeout))]
    [InlineData("reuse_feedback", "port-unexpected", nameof(ExperienceOperationErrorClass.Unexpected))]
    public async Task Error_classification_table(string operation, string kind, string expectedErrorClass)
    {
        var loop = new ExperienceLoop();

        // Arranged unlistened, so the only telemetry collected below belongs to the failure.
        var arranged = await loop.DriveAsync();

        using var cancellation = new CancellationTokenSource();
        if (kind == "cancelled")
        {
            await cancellation.CancelAsync();
        }

        var scripted = Scripted(kind);
        Arm(loop, kind, scripted);

        using var probe = TelemetryProbe.All();

        var propagated = await Assert.ThrowsAnyAsync<Exception>(
            () => FaultAsync(loop, arranged, operation, kind, cancellation.Token));

        if (scripted is not null)
        {
            // The wrapper observed the failure and rethrew the very same object: no wrapping, no
            // re-creation, no swallowing.
            Assert.Same(scripted, propagated);
        }

        var failure = Assert.Single(probe.For(FailuresInstrument, operation));
        Assert.Equal(1d, failure.Value);
        Assert.Equal(expectedErrorClass, Assert.IsType<string>(failure.Tags[ErrorClassDimension]));

        // A faulted operation is still counted and timed, under the literal "Faulted".
        Assert.Equal(Faulted, Assert.Single(probe.For(CountInstrument, operation)).Tags[OutcomeDimension]);
        Assert.Single(probe.For(DurationInstrument, operation));

        var span = Assert.Single(probe.LibraryActivities, a => a.OperationName == "agentexperience." + operation);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(expectedErrorClass, span.GetTagItem(ErrorClassAttribute));
        Assert.Equal(propagated.GetType().FullName, span.GetTagItem(ErrorTypeAttribute));
        Assert.Equal(Faulted, span.GetTagItem(OutcomeAttribute));
    }

    /// <summary>
    /// The identifiers a failing span carries. They are read from the request, before the call, so a
    /// span that records a throw can still say what it was operating on.
    /// </summary>
    [Fact]
    public async Task A_faulted_span_still_identifies_what_it_was_operating_on()
    {
        using var probe = TelemetryProbe.SpansOnly();
        var loop = new ExperienceLoop();

        var experienceId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        loop.Store.Throws = () => new ExperienceStoreException("the database is unreachable");

        await Assert.ThrowsAsync<ExperienceStoreException>(() => loop.Lifecycle.CommitAsync(
            ExperienceLoop.Authorization,
            new CommitLifecycleTransitionRequest(
                eventId, experienceId, ExperienceLoop.Scope, ExperienceStatus.Candidate,
                ExperienceStatus.Validated, "initial", "tests", ExperienceLoop.Now, 0),
            CancellationToken.None));

        var span = Assert.Single(probe.LibraryActivities);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(experienceId.ToString("D"), span.GetTagItem("agentexperience.experience_id"));
        Assert.Equal(eventId.ToString("D"), span.GetTagItem("agentexperience.event_id"));
    }

    /// <summary>
    /// A finalization that threw says which stage was in flight. Written on success only, an operator
    /// looking at the one span that mattered could not tell whether the record had been written before
    /// the call stopped.
    /// </summary>
    [Fact]
    public async Task A_faulted_finalization_carries_the_stage_it_reached()
    {
        var loop = new ExperienceLoop();
        var arranged = await loop.DriveAsync();

        using var probe = TelemetryProbe.All();

        // The store cancels for its own reasons at the CreateRecord stage: the caller never asked, so
        // the stage the span reports is the one the body had actually got to.
        loop.Store.Throws = () => new OperationCanceledException("a driver-side timeout nobody asked for");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.Finalization.FinalizeAsync(
            FinalizeRequest(arranged.RunId),
            CancellationToken.None));

        var span = Assert.Single(probe.LibraryActivities, a => a.OperationName == "agentexperience.finalize");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(nameof(FinalizationStage.CreateRecord), span.GetTagItem(StageAttribute));
        Assert.Equal(arranged.RunId.ToString("D"), span.GetTagItem("agentexperience.run_id"));
        Assert.Equal(
            nameof(ExperienceOperationErrorClass.Infrastructure),
            Assert.Single(probe.For(FailuresInstrument, "finalize")).Tags[ErrorClassDimension]);
    }

    [Fact]
    public async Task A_failing_span_carries_the_exception_type_but_nothing_the_exception_said()
    {
        using var probe = TelemetryProbe.All();
        var loop = new ExperienceLoop();

        const string secret = "connection string: Host=db;Password=hunter2";
        loop.Store.Throws = () => new ExperienceStoreException(secret);

        await Assert.ThrowsAsync<ExperienceStoreException>(() => loop.Lifecycle.CommitAsync(
            ExperienceLoop.Authorization,
            new CommitLifecycleTransitionRequest(
                Guid.NewGuid(), Guid.NewGuid(), ExperienceLoop.Scope, ExperienceStatus.Candidate,
                ExperienceStatus.Validated, "initial", "tests", ExperienceLoop.Now, 0),
            CancellationToken.None));

        Assert.Equal("AgentExperience.Abstractions.ExperienceStoreException", Assert.Single(probe.LibraryActivities).GetTagItem(ErrorTypeAttribute));
        Assert.DoesNotContain(probe.EverySpanTagValue, value => value.Contains("hunter2", StringComparison.Ordinal));
        Assert.DoesNotContain(probe.EveryMeasurementTagValue, value => value.Contains("hunter2", StringComparison.Ordinal));

        // And no exception event was attached to the span either: Activity.AddException would have
        // carried the message onto it as an event rather than as a tag.
        Assert.Empty(Assert.Single(probe.LibraryActivities).Events);
    }

    /// <summary>
    /// A hung embedding provider inside the post-commit indexing hook is the failure this whole
    /// dimension exists for. The hook runs on a budget this library imposed, not on the caller's
    /// token, so the wrapper must report the budget expiring as a
    /// <see cref="ExperienceOperationErrorClass.Timeout"/> -- never as
    /// <see cref="ExperienceOperationErrorClass.Cancelled"/>, the one class whose own documentation
    /// says it is normally not alertable -- and it must report it <em>at all</em>, which routing the
    /// hook past the instrumented entry point silently stopped it doing.
    /// </summary>
    [Fact]
    public async Task A_library_imposed_indexing_budget_is_never_reported_as_caller_cancellation()
    {
        using var probe = TelemetryProbe.All();

        var gate = new TaskCompletionSource();
        var generator = new FakeEmbeddingGenerator { Gate = gate };
        var index = new FakeEmbeddingIndex();
        var indexing = new ExperienceIndexingService(index, generator);
        var store = new LoopRecordStore();
        var lifecycle = new ExperienceLifecycleService(store, indexing);
        var capture = new InMemoryExperienceCaptureService(
            new DefaultSanitizer(SanitizationOptions.Empty),
            new CaptureLimits(8, 8, 1_000, 1_000));
        var finalization = new ExperienceFinalizationService(
            capture,
            new DefaultExperienceReflector(),
            store,
            lifecycle,
            indexing,
            indexingTimeout: TimeSpan.FromMilliseconds(30));

        var runId = Guid.NewGuid();
        capture.StartRun(
            runId,
            "task-1",
            "a task",
            ExperienceLoop.Scope,
            new EnvironmentFingerprint("host-1", "net10.0", "linux", "1.0.0", new Dictionary<string, string>()),
            new Provenance("tests", "1.0.0", ExperienceLoop.Now, null),
            ExperienceLoop.Now);
        await capture.AppendAttemptAsync(
            runId,
            new AppendAttemptRequest(Guid.NewGuid(), ExperienceLoop.Now, TimeSpan.FromSeconds(1), [], "done", null));
        await capture.CompleteRunAsync(runId, Guid.NewGuid(), RunExecutionStatus.Completed, ExperienceLoop.Now.AddSeconds(3));

        var experienceId = ExperienceFinalizationService.ExperienceIdFor(runId, ExperienceLoop.Scope);
        index.Records[experienceId] = new FakeEmbeddingIndex.Row(1, "a retrieval summary");

        var finalized = await finalization.FinalizeAsync(FinalizeRequest(runId), CancellationToken.None);
        gate.TrySetResult();

        // The record is durable and the hung provider was abandoned on this library's own budget...
        Assert.Equal(FinalizationOutcome.Validated, finalized.Outcome);
        Assert.Equal(ExperienceIndexingOutcome.IndexFailed, finalized.Indexing!.Outcome);

        // ...the failure reached the counter, as a nested operation, which is the signal a host pages
        // on. Routing the hook past the wrapper removed it entirely: not misclassified, uncounted.
        var failures = probe.For(FailuresInstrument);
        Assert.NotEmpty(failures);
        Assert.All(failures, failure =>
        {
            Assert.Equal(true, failure.Tags[NestedDimension]);
            Assert.Equal(nameof(ExperienceOperationErrorClass.Timeout), failure.Tags[ErrorClassDimension]);
        });

        Assert.Single(probe.For(FailuresInstrument, "index", nested: true));
        Assert.Single(probe.For(FailuresInstrument, "reindex", nested: true));

        // ...and nothing anywhere was reported as the caller having cancelled, because the caller
        // never did. The finalization it happened inside succeeded, and is counted as a success.
        Assert.DoesNotContain(
            nameof(ExperienceOperationErrorClass.Cancelled),
            probe.EveryMeasurementTagValue);
        Assert.Equal(
            nameof(FinalizationOutcome.Validated),
            Assert.Single(probe.For(CountInstrument, "finalize")).Tags[OutcomeDimension]);
    }

    /// <summary>
    /// The other half of the same signal: a provider that throws rather than hangs is a
    /// <em>returned</em> failure, so it never touches the failure counter -- but the nested <c>index</c>
    /// is still counted and timed under the outcome it reached, which is the only way an operator sees
    /// that the vector channel stopped working behind a finalization that keeps reporting success.
    /// </summary>
    [Fact]
    public async Task A_throwing_embedding_provider_inside_the_post_commit_hook_is_still_a_nested_operation()
    {
        var loop = new ExperienceLoop();

        using var probe = TelemetryProbe.All();
        loop.Generator.Throws = FakeEmbeddingGenerator.ThrownException;

        var results = await loop.DriveAsync();

        // The record was written and the caller was told finalization worked, because it did: an
        // embedding is derived data and a provider being down never fails a canonical write.
        Assert.Equal(FinalizationOutcome.Validated, results.Finalized.Outcome);
        Assert.Equal(ExperienceIndexingOutcome.ProviderFailed, results.Finalized.Indexing!.Outcome);

        var hook = Assert.Single(probe.For(CountInstrument, "index", nested: true));
        Assert.Equal(nameof(ExperienceIndexingOutcome.ProviderFailed), hook.Tags[OutcomeDimension]);
        Assert.Single(probe.For(DurationInstrument, "index", nested: true));

        // A reported outcome is a decision, not a throw, so the failure counter stays untouched --
        // and the operator's alert is the ProviderFailed rate, which now exists.
        Assert.Empty(probe.For(FailuresInstrument));
    }

    // ---------------------------------------------------------------------------------------------
    // Execution never depends on a listener
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task No_listener_produces_identical_results()
    {
        var unlistened = new ExperienceLoop();
        var observedWithoutListener = new List<Activity?>();
        unlistened.Store.OnCall = () => observedWithoutListener.Add(Activity.Current);

        Assert.Null(Activity.Current);
        var without = await unlistened.DriveAsync();
        Assert.Null(Activity.Current);

        // Nothing was listening, so nothing was created -- observed from the bottom of the call stack,
        // inside the store, not from out here.
        Assert.NotEmpty(observedWithoutListener);
        Assert.All(observedWithoutListener, Assert.Null);

        var listened = new ExperienceLoop();
        var observedWithListener = new List<Activity?>();
        listened.Store.OnCall = () => observedWithListener.Add(Activity.Current);

        LoopResults with;
        using (var probe = TelemetryProbe.All())
        {
            with = await listened.DriveAsync();

            // The check above is not vacuous: with a listener the very same observation point sees a
            // live span.
            Assert.Contains(observedWithListener, activity => activity is not null);
            Assert.NotEmpty(probe.LibraryActivities);
            Assert.NotEmpty(probe.Measurements);
        }

        // And the span was closed behind us, leaving the caller's ambient activity exactly as it was.
        Assert.Null(Activity.Current);

        // The whole result of every operation, compared structurally rather than field by hand. A
        // hand-picked subset is what let rewriting the retrieval result under a listener pass: the
        // listener-independence claim is this story's headline invariant and nothing about a result
        // may differ, not just the fields somebody thought to list.
        Assert.Equal(Normalize(without), Normalize(with));
    }

    [Fact]
    public async Task An_activity_listener_alone_produces_spans_and_no_measurements()
    {
        using var spansOnly = TelemetryProbe.SpansOnly();

        var results = await new ExperienceLoop().DriveAsync();

        Assert.Equal(FinalizationOutcome.Validated, results.Finalized.Outcome);
        Assert.NotEmpty(spansOnly.LibraryActivities);
        Assert.Empty(spansOnly.Measurements);

        // The positive control the emptiness above needs. A probe with no MeterListener collects no
        // measurements whatever the library does, so on its own that assertion cannot fail. With a
        // MeterListener registered, the very same drive of the very same instruments does produce
        // them -- so the emptiness is the absent listener, not an instrument nothing ever writes to.
        using var full = TelemetryProbe.All();
        var again = await new ExperienceLoop().DriveAsync();

        Assert.Equal(FinalizationOutcome.Validated, again.Finalized.Outcome);
        Assert.NotEmpty(full.Measurements);
        Assert.NotEmpty(full.LibraryActivities);
    }

    [Fact]
    public async Task A_meter_listener_alone_produces_measurements_and_no_spans()
    {
        var loop = new ExperienceLoop();
        var observed = new List<Activity?>();
        loop.Store.OnCall = () => observed.Add(Activity.Current);

        using (var metricsOnly = TelemetryProbe.MetricsOnly())
        {
            var results = await loop.DriveAsync();

            Assert.Equal(FinalizationOutcome.Validated, results.Finalized.Outcome);
            Assert.NotEmpty(metricsOnly.Measurements);

            // Not "the probe collected no spans" -- which a probe with no ActivityListener cannot fail
            // to report -- but "the library started none", observed from the bottom of the call stack
            // where an Activity would have been ambient had one been created.
            Assert.NotEmpty(observed);
            Assert.All(observed, Assert.Null);
            Assert.Empty(metricsOnly.Activities);
        }

        // The positive control: the same observation point, with an ActivityListener registered, sees
        // a live span. So the nulls above are the absent listener and not a dead observation hook.
        var listened = new ExperienceLoop();
        var withListener = new List<Activity?>();
        listened.Store.OnCall = () => withListener.Add(Activity.Current);

        using var full = TelemetryProbe.All();
        await listened.DriveAsync();

        Assert.Contains(withListener, activity => activity is not null);
        Assert.NotEmpty(full.LibraryActivities);
    }

    [Fact]
    public async Task A_sampler_that_declines_still_records_measurements()
    {
        using var probe = TelemetryProbe.Declining();

        var results = await new ExperienceLoop().DriveAsync();

        // StartActivity returned null for every operation, so every ?.SetTag was a no-op...
        Assert.Empty(probe.Activities);

        // ...and neither the measurements nor the results noticed.
        Assert.NotEmpty(probe.For(CountInstrument));
        Assert.Equal(FinalizationOutcome.Validated, results.Finalized.Outcome);
        Assert.Equal(RetrievalOutcome.Completed, results.Retrieved.Outcome);
    }

    [Fact]
    public async Task The_meter_publishes_exactly_the_three_frozen_instruments()
    {
        using var probe = TelemetryProbe.All();

        await new ExperienceLoop().DriveAsync();

        var published = probe.PublishedInstruments
            .Where(instrument => instrument.Meter.Name == CoreSource)
            .ToList();

        Assert.Equal(
            new[] { CountInstrument, DurationInstrument, FailuresInstrument },
            published.Select(instrument => instrument.Name).Order(StringComparer.Ordinal));

        // Per-operation instrument names are deliberately not used: cardinality is identical either
        // way, and one closed dimension cannot drift where fourteen instrument names can.
        Assert.Equal("s", published.Single(instrument => instrument.Name == DurationInstrument).Unit);
        Assert.Equal("{operation}", published.Single(instrument => instrument.Name == CountInstrument).Unit);
        Assert.Equal("{failure}", published.Single(instrument => instrument.Name == FailuresInstrument).Unit);
    }

    // ---------------------------------------------------------------------------------------------
    // The content guarantee
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Telemetry_never_contains_captured_content()
    {
        using var probe = TelemetryProbe.All();
        var loop = new ExperienceLoop();

        var results = await loop.DriveAsync();

        // First prove the marker really did travel the whole loop. Without this the sweep below would
        // pass just as happily against a loop that captured nothing at all.
        var record = Assert.IsType<ExperienceRecord>(results.Finalized.Record);
        Assert.Contains(ExperienceLoop.Marker, record.TaskSummary!, StringComparison.Ordinal);
        Assert.Contains(record.Attempts, attempt => attempt.Result?.Contains(ExperienceLoop.Marker, StringComparison.Ordinal) == true);
        Assert.Contains(record.Attempts, attempt => attempt.Error?.Contains(ExperienceLoop.Marker, StringComparison.Ordinal) == true);
        Assert.Contains(
            record.Attempts.SelectMany(attempt => attempt.ToolCalls),
            call => call.Result?.Contains(ExperienceLoop.Marker, StringComparison.Ordinal) == true);
        Assert.Contains(ExperienceLoop.Marker, record.Reflection!.FailedApproaches[0], StringComparison.Ordinal);
        Assert.Contains(ExperienceLoop.Marker, loop.Generator.Requests[0], StringComparison.Ordinal);

        // And that it reached the four operations that handle no captured text at all, through the
        // free-form fields they do carry. Without these, five of thirteen operations were swept for a
        // marker that was never anywhere near them.
        Assert.Contains(ExperienceLoop.Marker, results.Verified.Outcome.Evidence[0].Detail!, StringComparison.Ordinal);
        Assert.Contains(ExperienceLoop.Marker, results.Verified.Outcome.Evidence[0].Producer, StringComparison.Ordinal);
        Assert.Contains(ExperienceLoop.Marker, results.Transitioned.Event!.Reason, StringComparison.Ordinal);
        Assert.Contains(ExperienceLoop.Marker, results.Transitioned.Event!.Producer, StringComparison.Ordinal);
        Assert.Contains(ExperienceLoop.Marker, results.Confidence.Event!.Reason, StringComparison.Ordinal);
        Assert.Contains(ExperienceLoop.Marker, loop.Generator.Requests[^1], StringComparison.Ordinal);
        Assert.Contains(ExperienceLoop.Marker, loop.FeedbackStore.Submissions[0].Rationale!, StringComparison.Ordinal);

        // Now sweep every value an exporter would ever see.
        Assert.NotEmpty(probe.EverySpanTagValue);
        Assert.NotEmpty(probe.EveryMeasurementTagValue);

        foreach (var value in probe.EverySpanTagValue.Concat(probe.EveryMeasurementTagValue))
        {
            Assert.DoesNotContain(ExperienceLoop.Marker, value, StringComparison.Ordinal);
        }

        // Including the parts of a span that are not tags at all.
        Assert.All(probe.Activities, activity =>
        {
            Assert.DoesNotContain(ExperienceLoop.Marker, activity.DisplayName, StringComparison.Ordinal);
            Assert.Null(activity.StatusDescription);
            Assert.Empty(activity.Events);
            Assert.Empty(activity.Baggage);
        });
    }

    [Fact]
    public async Task Metric_dimensions_are_only_operation_outcome_error_class_and_nested()
    {
        using var probe = TelemetryProbe.All();

        // A full successful drive, then a thrown operation, so every dimension the library can write
        // has actually been written by the time the keys are collected.
        var loop = new ExperienceLoop();
        await loop.DriveAsync();

        loop.Store.Throws = () => new ExperienceStoreException("the database is unreachable");
        await Assert.ThrowsAsync<ExperienceStoreException>(() => loop.Lifecycle.CommitAsync(
            ExperienceLoop.Authorization,
            new CommitLifecycleTransitionRequest(
                Guid.NewGuid(), Guid.NewGuid(), ExperienceLoop.Scope, ExperienceStatus.Candidate,
                ExperienceStatus.Validated, "initial", "tests", ExperienceLoop.Now, 0),
            CancellationToken.None));

        var keys = probe.EveryMeasurementTagKey;

        // All four appear, so the exact assertion below cannot pass by emitting nothing...
        Assert.Contains(OperationDimension, keys);
        Assert.Contains(OutcomeDimension, keys);
        Assert.Contains(ErrorClassDimension, keys);
        Assert.Contains(NestedDimension, keys);

        // ...and nothing else does. No run ID, no record ID, no correlation ID, no reason. A fifth
        // dimension has to be added here, on purpose, by whoever adds it -- which is the point: every
        // dimension is a cardinality multiplier on every series, and an unbounded one is a bill.
        Assert.Equal(
            new[] { ErrorClassDimension, NestedDimension, OperationDimension, OutcomeDimension },
            keys.Order(StringComparer.Ordinal));

        // `nested` is a bool and takes both values in this drive, so it is a dimension rather than a
        // constant that happens to be attached to everything.
        Assert.All(probe.Measurements, measurement => Assert.IsType<bool>(measurement.Tags[NestedDimension]));
        Assert.Contains(probe.Measurements, measurement => Equals(measurement.Tags[NestedDimension], true));
        Assert.Contains(probe.Measurements, measurement => Equals(measurement.Tags[NestedDimension], false));

        // Every dimension value is a closed-set member too: an operation from the frozen table, an
        // outcome or "Faulted", or an error class.
        var operations = FrozenTable.Select(entry => entry.Operation).ToHashSet(StringComparer.Ordinal);
        Assert.All(probe.Measurements, measurement =>
            Assert.True(
                operations.Contains(Assert.IsType<string>(measurement.Tags[OperationDimension])),
                $"'{measurement.Tags[OperationDimension]}' is not in the frozen operation table."));

        Assert.All(
            probe.For(FailuresInstrument),
            measurement => Assert.True(Enum.TryParse<ExperienceOperationErrorClass>(
                Assert.IsType<string>(measurement.Tags[ErrorClassDimension]), out _)));
    }

    // ---------------------------------------------------------------------------------------------
    // Drivers
    // ---------------------------------------------------------------------------------------------

    private static FinalizeExperienceRequest FinalizeRequest(Guid runId) => new(
        RunId: runId,
        Authorization: ExperienceLoop.Authorization,
        ClosedRound: ExperienceLoop.Round,
        RequiredChecks: [new RequiredCheck("tests")],
        Evidence: [ExperienceLoop.Evidence()],
        CurrentArtifactRevision: ExperienceLoop.ArtifactRevision,
        StorageDecision: StorageDecision.Permit,
        FinalizedAt: ExperienceLoop.Now.AddMinutes(1));

    /// <summary>The exception a <c>port-*</c> kind scripts into a port, or <see langword="null"/> when the kind scripts none.</summary>
    private static Exception? Scripted(string kind) => kind switch
    {
        "port-store" => new ExperienceStoreException("the database is unreachable"),
        "port-timeout" => new TimeoutException("the statement exceeded its bound"),
        "port-cancelled-by-nobody" => new OperationCanceledException("a driver-side timeout, arriving as a cancellation nobody asked for"),
        "port-unexpected" => new InvalidOperationException("something nobody classified"),
        _ => null,
    };

    /// <summary>Arms every port the loop owns with <paramref name="scripted"/>, so whichever one the operation reaches throws it.</summary>
    private static void Arm(ExperienceLoop loop, string kind, Exception? scripted)
    {
        if (scripted is null)
        {
            return;
        }

        _ = kind;
        loop.Store.Throws = () => scripted;
        loop.FeedbackStore.Throws = () => scripted;
        loop.Index.ScanThrows = scripted;
        loop.Index.WriteThrows = scripted;
        loop.Index.RemoveThrows = scripted;
        loop.Candidates.Throws = () => scripted;
        loop.Generator.Throws = scripted;
    }

    /// <summary>Calls <paramref name="operation"/> in a way that makes it throw.</summary>
    private static Task FaultAsync(
        ExperienceLoop loop,
        LoopResults arranged,
        string operation,
        string kind,
        CancellationToken cancellationToken)
    {
        var missing = Guid.NewGuid();

        return (operation, kind) switch
        {
            ("capture.start_run", _) => Task.Run(
                () => loop.Capture.StartRun(
                    Guid.NewGuid(),
                    "task-1",
                    null,
                    scope: null!,
                    new EnvironmentFingerprint("host-1", "net10.0", "linux", "1.0.0", new Dictionary<string, string>()),
                    new Provenance("tests", "1.0.0", ExperienceLoop.Now, null),
                    ExperienceLoop.Now),
                CancellationToken.None),

            ("capture.append_attempt", "argument") => loop.Capture.AppendAttemptAsync(arranged.RunId, request: null!),
            ("capture.append_attempt", _) => loop.Capture.AppendAttemptAsync(
                arranged.RunId,
                new AppendAttemptRequest(Guid.NewGuid(), ExperienceLoop.Now, TimeSpan.FromSeconds(1), [], "done", null),
                cancellationToken),

            ("capture.complete_run", _) => loop.Capture.CompleteRunAsync(
                arranged.RunId, Guid.NewGuid(), RunExecutionStatus.Completed, ExperienceLoop.Now, cancellationToken),

            ("verify", "argument") => Task.Run(
                () => VerificationAggregator.Aggregate(
                    evidence: null!, [], ExperienceLoop.Round, ExperienceLoop.ArtifactRevision, ExperienceLoop.Now),
                CancellationToken.None),
            ("verify", _) => Task.Run(
                () => VerificationAggregator.Aggregate(
                    [ExperienceLoop.Evidence()],
                    [new RequiredCheck("tests")],
                    ExperienceLoop.Round,
                    ExperienceLoop.ArtifactRevision,
                    ExperienceLoop.Now,
                    cancellationToken),
                CancellationToken.None),

            ("reflect", "argument") => new DefaultExperienceReflector().ReflectAsync(request: null!),
            ("reflect", _) => new DefaultExperienceReflector().ReflectAsync(
                new ReflectionRequest(
                    arranged.Finalized.Record is null
                        ? throw new InvalidOperationException("The arranged drive produced no record.")
                        : Run(loop, arranged.RunId),
                    arranged.Verified,
                    Guid.NewGuid(),
                    ExperienceLoop.Now),
                cancellationToken),

            ("finalize", "argument") => loop.Finalization.FinalizeAsync(request: null!),
            ("finalize", _) => loop.Finalization.FinalizeAsync(FinalizeRequest(arranged.RunId), cancellationToken),

            ("lifecycle.commit", _) => loop.Lifecycle.CommitAsync(
                ExperienceLoop.Authorization,
                new CommitLifecycleTransitionRequest(
                    Guid.NewGuid(), arranged.ExperienceId, ExperienceLoop.Scope, ExperienceStatus.Validated,
                    ExperienceStatus.Reinforced, "again", "tests", ExperienceLoop.Now, 1),
                cancellationToken),

            ("confidence.apply", _) => loop.Lifecycle.ApplyEvidenceAsync(
                ExperienceLoop.Authorization,
                new ApplyConfidenceEvidenceRequest(
                    Guid.NewGuid(),
                    arranged.ExperienceId,
                    ExperienceLoop.Scope,
                    Guid.NewGuid(),
                    ConfidenceEvidenceKind.Supporting,
                    ConfidenceEvidenceSource.Machine,
                    arranged.RunId,
                    ExperienceLoop.Round.RoundId,
                    "a later run reused it",
                    "tests",
                    ExperienceLoop.Now),
                cancellationToken),

            ("retrieve", "argument") => loop.Retrieval.RetrieveAsync(request: null!),
            ("retrieve", _) => loop.Retrieval.RetrieveAsync(
                new RetrieveExperienceRequest(ExperienceLoop.Authorization, ExperienceLoop.Scope, "a task"), cancellationToken),

            ("index", "argument") => loop.Indexing.IndexAsync(ExperienceLoop.Authorization, scope: null!, missing),
            ("index", _) => loop.Indexing.IndexAsync(ExperienceLoop.Authorization, ExperienceLoop.Scope, arranged.ExperienceId, cancellationToken),

            ("deindex", _) => loop.Indexing.RemoveAsync(ExperienceLoop.Authorization, scope: null!, missing),

            ("reindex", "argument") => loop.Indexing.ReindexAsync(ExperienceLoop.Authorization, request: null!),
            ("reindex", _) => loop.Indexing.ReindexAsync(
                ExperienceLoop.Authorization,
                new ReindexExperienceRequest(ExperienceLoop.Scope, [arranged.ExperienceId], Limit: 1),
                cancellationToken),

            ("reuse_feedback", _) => loop.FeedbackService.RecordAsync(
                ExperienceLoop.Authorization,
                new ExperienceReuseFeedback(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    ExperienceLoop.Scope,
                    [arranged.ExperienceId],
                    TaskVerificationStatus.Verified,
                    new ReuseMeasure("task-success", 1),
                    ExperienceLoop.Now),
                cancellationToken),

            _ => throw new InvalidOperationException($"No fault driver for '{operation}'/'{kind}'."),
        };
    }

    /// <summary>Calls <paramref name="operation"/> in a way that makes it return a rejection rather than its success outcome.</summary>
    private static async Task<string> RejectAsync(ExperienceLoop loop, LoopResults arranged, string operation)
    {
        var outsider = new AuthorizationContext("tenant-2", "other-principal", ["experience:write"], ExperienceLoop.Now);
        var missing = Guid.NewGuid();

        switch (operation)
        {
            case "capture.start_run":
                // The run ID is already taken, so this is a second start for the same run.
                return loop.Capture.StartRun(
                    arranged.RunId,
                    "task-1",
                    null,
                    ExperienceLoop.Scope,
                    new EnvironmentFingerprint("host-1", "net10.0", "linux", "1.0.0", new Dictionary<string, string>()),
                    new Provenance("tests", "1.0.0", ExperienceLoop.Now, null),
                    ExperienceLoop.Now).Outcome.ToString();

            case "capture.append_attempt":
                // The run was completed by the arranged drive, so it accepts no further attempts.
                return (await loop.Capture.AppendAttemptAsync(
                    arranged.RunId,
                    new AppendAttemptRequest(Guid.NewGuid(), ExperienceLoop.Now, TimeSpan.FromSeconds(1), [], "late", null)))
                    .Outcome.ToString();

            case "capture.complete_run":
                return (await loop.Capture.CompleteRunAsync(
                    arranged.RunId, Guid.NewGuid(), RunExecutionStatus.Completed, ExperienceLoop.Now.AddSeconds(4)))
                    .Outcome.ToString();

            case "verify":
                return VerificationAggregator.Aggregate(
                    [ExperienceLoop.Evidence(CheckResult.Fail)],
                    [new RequiredCheck("tests")],
                    ExperienceLoop.Round,
                    ExperienceLoop.ArtifactRevision,
                    ExperienceLoop.Now).Outcome.Status.ToString();

            case "reflect":
                return (await new DefaultExperienceReflector().ReflectAsync(new ReflectionRequest(
                    Run(loop, arranged.RunId),
                    VerificationAggregator.Aggregate(
                        [ExperienceLoop.Evidence(CheckResult.Fail)],
                        [new RequiredCheck("tests")],
                        ExperienceLoop.Round,
                        ExperienceLoop.ArtifactRevision,
                        ExperienceLoop.Now),
                    Guid.NewGuid(),
                    ExperienceLoop.Now))).VerificationStatus.ToString();

            case "finalize":
                // The run was finalized by the arranged drive; a second call writes nothing.
                return (await loop.Finalization.FinalizeAsync(FinalizeRequest(arranged.RunId))).Outcome.ToString();

            case "lifecycle.commit":
                return (await loop.Lifecycle.CommitAsync(
                    ExperienceLoop.Authorization,
                    new CommitLifecycleTransitionRequest(
                        Guid.NewGuid(), arranged.ExperienceId, ExperienceLoop.Scope, ExperienceStatus.Revoked,
                        ExperienceStatus.Validated, "a revoked record cannot be validated again", "tests",
                        ExperienceLoop.Now, 3),
                    CancellationToken.None)).Outcome.ToString();

            case "confidence.apply":
                return (await loop.Lifecycle.ApplyEvidenceAsync(
                    ExperienceLoop.Authorization,
                    new ApplyConfidenceEvidenceRequest(
                        Guid.NewGuid(),
                        missing,
                        ExperienceLoop.Scope,
                        Guid.NewGuid(),
                        ConfidenceEvidenceKind.Supporting,
                        ConfidenceEvidenceSource.Machine,
                        arranged.RunId,
                        ExperienceLoop.Round.RoundId,
                        "about a record that does not exist",
                        "tests",
                        ExperienceLoop.Now),
                    CancellationToken.None)).Outcome.ToString();

            case "retrieve":
                return (await loop.Retrieval.RetrieveAsync(new RetrieveExperienceRequest(
                    outsider, ExperienceLoop.Scope, "a task in a scope the host does not cover"))).Outcome.ToString();

            case "index":
                return (await loop.Indexing.IndexAsync(ExperienceLoop.Authorization, ExperienceLoop.Scope, missing)).Outcome.ToString();

            case "deindex":
                return (await loop.Indexing.RemoveAsync(ExperienceLoop.Authorization, ExperienceLoop.Scope, missing)).Outcome.ToString();

            case "reindex":
                return (await loop.Indexing.ReindexAsync(
                    outsider, new ReindexExperienceRequest(ExperienceLoop.Scope, [arranged.ExperienceId], Limit: 1))).Outcome.ToString();

            case "reuse_feedback":
                return (await loop.FeedbackService.RecordAsync(
                    outsider,
                    new ExperienceReuseFeedback(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        ExperienceLoop.Scope,
                        [arranged.ExperienceId],
                        TaskVerificationStatus.Verified,
                        new ReuseMeasure("task-success", 1),
                        ExperienceLoop.Now),
                    CancellationToken.None)).Outcome.ToString();

            default:
                throw new InvalidOperationException($"No rejection driver for '{operation}'.");
        }
    }

    /// <summary>
    /// Every call one instrument recorded, as <c>"&lt;operation&gt; nested=&lt;bool&gt; x&lt;count&gt;"</c>
    /// lines in a stable order, so a whole call table is one assertion with a readable diff.
    /// </summary>
    /// <param name="probe">The probe that collected the drive.</param>
    /// <param name="instrument">The instrument to tabulate.</param>
    /// <returns>One line per operation and nesting combination.</returns>
    private static IReadOnlyList<string> Calls(TelemetryProbe probe, string instrument) =>
        [.. probe.For(instrument)
            .GroupBy(
                measurement => $"{measurement.Tags[OperationDimension]} nested={measurement.Tags[NestedDimension]}",
                StringComparer.Ordinal)
            .Select(group => $"{group.Key} x{group.Count()}")
            .Order(StringComparer.Ordinal)];

    private static ExperienceRun Run(ExperienceLoop loop, Guid runId)
    {
        Assert.True(loop.Capture.TryGetRun(runId, out var run));
        return run!;
    }

    /// <summary>
    /// One drive's results with the parts that cannot be equal between two drives -- freshly generated
    /// identifiers and measured elapsed times -- normalized away, so everything else is compared.
    /// </summary>
    private static string Normalize(LoopResults results)
    {
        var text = results.ToString();

        // Every GUID in the drive is freshly generated per run, or derived from one that is.
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            "<id>");

        // Retrieval reports how long it took, which is wall clock and never repeats.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"Elapsed = [^,}]*", "Elapsed = <elapsed>");

        return text;
    }

    /// <summary>A candidate source that does not answer until it is released, so retrieval hits its own timeout.</summary>
    private sealed class SlowCandidateSource(TaskCompletionSource released) : IExperienceCandidateSource
    {
        public async Task<ExperienceCandidateSearchResult> SearchAsync(
            AuthorizationContext authorization,
            ExperienceCandidateQuery query,
            CancellationToken cancellationToken)
        {
            await released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new ExperienceCandidateSearchResult(ExperienceStoreOutcome.Found, [], []);
        }
    }
}

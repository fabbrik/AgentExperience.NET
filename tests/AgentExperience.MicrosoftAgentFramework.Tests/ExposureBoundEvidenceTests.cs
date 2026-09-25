using System.Globalization;
using AgentExperience.Core.Confidence;
using AgentExperience.Core.Feedback;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Core.Reflections;
using AgentExperience.Core.Retrieval;
using AgentExperience.Core.Verification;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// Story 7.3 (KL-11), end to end through the adapter: the context provider injects a lesson into a captured
/// invocation, capture records that exposure on the run, finalization carries it onto the run's record, and
/// feedback about reusing the lesson is admitted for that run -- and refused for a real, finalized run in the
/// same scope that was never given it.
/// </summary>
/// <remarks>
/// Everything between the invocations is the real implementation: capture, the context provider, session
/// tracking, finalization, verification, the lifecycle service and the feedback service. Only the record
/// store, the search index and the feedback ledger are in-memory doubles.
/// </remarks>
public class ExposureBoundEvidenceTests
{
    private const string TaskId = "triage-refund";
    private const string ArtifactRevision = "rev-1";

    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");
    private static readonly AuthorizationContext Authorization = new("tenant-1", "host", ["experience:read", "experience:write"], DateTimeOffset.UnixEpoch);
    private static readonly byte[] Key = [.. Enumerable.Range(0, 32).Select(value => (byte)(value * 11))];

    private static readonly SanitizationOptions Sanitization = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        ["ToolResult"] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "value" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal),
            MaxDepth: 2,
            MaxFieldCount: 5,
            MaxValueLength: 10_000,
            MaxFieldNameLength: 100),
    });

    [Fact]
    public async Task Inject_capture_finalize_then_feedback_is_admitted_for_the_exposed_run_and_refused_for_one_that_never_saw_the_lesson()
    {
        var loop = new Loop();

        // ---- Run A: the lesson. Captured and finalized, no injection. --------------------------------------
        var lesson = await loop.RunWithoutInjectionAsync();
        Assert.Equal(ExperienceStatus.Validated, lesson.Status);
        Assert.Empty(lesson.Provenance.ExposedTo);
        Assert.Equal(ExperienceRecordOrigin.Finalized, lesson.Origin);
        loop.World.Index(lesson);

        // ---- Run B: the lesson is injected into a captured invocation, which is then finalized. -----------
        var session = await loop.InjectingAgent.CreateSessionAsync();
        var runB = await loop.RunInjectingAsync(session);

        var injection = Assert.Single(loop.Injections);
        Assert.Equal(InjectionOutcome.Injected, injection.Outcome);
        Assert.Equal([lesson.ExperienceId], injection.InjectedExperienceIds);
        Assert.Empty(loop.Failures);

        var recordB = loop.RecordOf(runB);
        Assert.Equal(ExperienceRecordOrigin.Finalized, recordB.Origin);
        var exposure = Assert.Single(recordB.Provenance.ExposedTo);
        Assert.Equal(lesson.ExperienceId, exposure.ExperienceId);
        Assert.Equal(loop.World.Stored[lesson.ExperienceId].Revision, exposure.Revision);

        // ---- Run C: real, finalized in the same scope with a closed round -- and never given the lesson. ----
        var recordC = await loop.RunWithoutInjectionAsync();
        Assert.Empty(recordC.Provenance.ExposedTo);

        // ---- Feedback about run B, from a comparative evaluation over run B's own round: admitted. ----------
        var admitted = await loop.Feedback.RecordAsync(Authorization, loop.ComparativeFeedback(recordB, lesson.ExperienceId), CancellationToken.None);
        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, admitted.Outcome);
        Assert.Equal(ReuseAttributionSource.ComparativeEvaluation, admitted.AttributionSource);
        var applied = Assert.Single(admitted.Exposures);
        Assert.Equal(ExperienceExposureDisposition.EvidenceApplied, applied.Disposition);
        Assert.True(applied.Counted);

        // Once per key: the same run and round again, under a fresh feedback ID, is recorded and not counted.
        var again = await loop.Feedback.RecordAsync(Authorization, loop.ComparativeFeedback(recordB, lesson.ExperienceId), CancellationToken.None);
        Assert.Equal(ExperienceExposureDisposition.EvidenceApplied, Assert.Single(again.Exposures).Disposition);
        Assert.False(Assert.Single(again.Exposures).Counted);

        // A human reviewer's token about run B lands too, as a separate key.
        var token = loop.Issuer.Issue(Authorization, TestScope, runB, ConfidenceEvidenceKind.Supporting, [lesson.ExperienceId]);
        var human = await loop.Lifecycle.ApplyEvidenceAsync(
            Authorization,
            new ApplyConfidenceEvidenceRequest(
                Guid.NewGuid(), lesson.ExperienceId, TestScope, Guid.NewGuid(), ConfidenceEvidenceKind.Supporting,
                ConfidenceEvidenceSource.Human, runB, null, "a reviewer read run B", "review", InjectionRecords.Now,
                AssessmentToken: token.Token),
            CancellationToken.None);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, human.Outcome);
        Assert.True(human.Counted);
        Assert.Equal(ConfidenceEvidenceAdmission.Verified, human.Update!.Admission);

        // ---- The same about run C: refused, before the ledger and on the confidence path. -----------------
        var refused = await loop.Feedback.RecordAsync(Authorization, loop.ComparativeFeedback(recordC, lesson.ExperienceId), CancellationToken.None);
        Assert.Equal(ReuseAttributionSource.None, refused.AttributionSource);
        Assert.Equal(ExperienceReuseBenefit.Unknown, refused.Benefit);
        Assert.Contains("exposed to", refused.Reason!, StringComparison.Ordinal);

        var direct = await loop.Lifecycle.ApplyEvidenceAsync(
            Authorization,
            new ApplyConfidenceEvidenceRequest(
                Guid.NewGuid(), lesson.ExperienceId, TestScope, Guid.NewGuid(), ConfidenceEvidenceKind.Supporting,
                ConfidenceEvidenceSource.Machine, recordC.SourceRunId, recordC.ClosedRoundId, "claimed reuse in run C", "tests", InjectionRecords.Now),
            CancellationToken.None);
        Assert.Equal(ConfidenceUpdateOutcome.Unverified, direct.Outcome);
        Assert.Equal(IndependenceRefusal.NotExposed, direct.Refusal);

        // Initial 1, run B's round, run B's reviewer: nothing from run C.
        Assert.Equal(3, loop.World.Stored[lesson.ExperienceId].SupportingValidations);
    }

    [Fact]
    public async Task A_later_run_in_the_same_session_is_not_credited_with_what_earlier_turns_delivered()
    {
        var loop = new Loop();
        var lesson = await loop.RunWithoutInjectionAsync();
        loop.World.Index(lesson);

        var session = await loop.InjectingAgent.CreateSessionAsync();
        var runB = await loop.RunInjectingAsync(session);
        Assert.Equal(InjectionOutcome.Injected, loop.Injections[^1].Outcome);

        // Run D is a new run on the same session. The session already holds the lesson, so it is not injected
        // again. The session account lives in host session storage, unauthenticated, so what it says earlier
        // turns delivered is not the library's statement about run D: run D is exposed to nothing.
        var runD = await loop.RunInjectingAsync(session);
        Assert.NotEqual(runB, runD);
        Assert.Empty(loop.Injections[^1].InjectedExperienceIds);
        Assert.Empty(loop.Failures);
        Assert.Empty(loop.RecordOf(runD).Provenance.ExposedTo);

        var refused = await loop.Feedback.RecordAsync(Authorization, loop.ComparativeFeedback(loop.RecordOf(runD), lesson.ExperienceId), CancellationToken.None);
        Assert.Equal(ReuseAttributionSource.None, refused.AttributionSource);
    }

    [Fact]
    public async Task A_capture_service_that_fails_to_record_exposure_is_reported_and_costs_the_invocation_nothing()
    {
        var loop = new Loop();
        var lesson = await loop.RunWithoutInjectionAsync();
        loop.World.Index(lesson);

        loop.Capture.ThrowOnRecordExposure = true;
        var session = await loop.InjectingAgent.CreateSessionAsync();
        var run = await loop.RunInjectingAsync(session);

        // The model still got the lesson.
        Assert.Equal(InjectionOutcome.Injected, loop.Injections[^1].Outcome);

        var failure = Assert.Single(loop.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.RecordExposure, failure.Stage);
        Assert.Equal(run, failure.RunId);
        Assert.IsType<InvalidOperationException>(failure.Exception);

        // And the run is honestly exposed to nothing, so evidence about it is refused.
        var record = loop.RecordOf(run);
        Assert.Empty(record.Provenance.ExposedTo);
        var refused = await loop.Feedback.RecordAsync(Authorization, loop.ComparativeFeedback(record, lesson.ExperienceId), CancellationToken.None);
        Assert.Equal(ReuseAttributionSource.None, refused.AttributionSource);
    }

    [Fact]
    public async Task A_capture_service_that_does_not_record_exposure_is_reported_once_and_the_injection_still_happens()
    {
        var loop = new Loop();
        var lesson = await loop.RunWithoutInjectionAsync();
        loop.World.Index(lesson);

        loop.Capture.ForcedExposureOutcome = RecordExposureOutcome.NotSupported;
        var session = await loop.InjectingAgent.CreateSessionAsync();
        var run = await loop.RunInjectingAsync(session);

        Assert.Equal(InjectionOutcome.Injected, loop.Injections[^1].Outcome);
        var failure = Assert.Single(loop.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.RecordExposure, failure.Stage);
        Assert.Equal(run, failure.RunId);
        Assert.Contains(nameof(RecordExposureOutcome.NotSupported), failure.Reason, StringComparison.Ordinal);
        Assert.Empty(loop.RecordOf(run).Provenance.ExposedTo);
    }

    [Fact]
    public async Task An_uncaptured_agent_running_inside_a_captured_invocation_does_not_expose_the_outer_run()
    {
        var loop = new Loop();
        var lesson = await loop.RunWithoutInjectionAsync();
        loop.World.Index(lesson);

        // The outer agent is captured and injects nothing; its model calls an inner agent that injects the
        // lesson but is not captured. The inner block reaches the inner model only, never the outer run's.
        var inner = new ChatClientAgent(new RecordingChatClient(), new ChatClientAgentOptions { AIContextProviders = [loop.Provider()] });
        var outer = loop.CapturedAgent(new ChatClientAgent(new NestingChatClient(inner), new ChatClientAgentOptions()));

        var count = loop.Finalized.Count;
        var session = await outer.CreateSessionAsync();
        await outer.RunAsync("delegate the refund ticket", session);

        Assert.Equal(InjectionOutcome.Injected, loop.Injections[^1].Outcome);
        var outcome = Assert.Single(loop.Finalized.Skip(count));
        Assert.Empty(loop.World.Stored[outcome.ExperienceId!.Value].Provenance.ExposedTo);
        Assert.Empty(loop.Failures);
    }

    [Fact]
    public async Task A_provider_used_without_capture_records_nothing_anywhere()
    {
        var loop = new Loop();
        var lesson = await loop.RunWithoutInjectionAsync();
        loop.World.Index(lesson);
        var before = loop.Capture.RecordedExposures.Count;

        var agent = new ChatClientAgent(new RecordingChatClient(), new ChatClientAgentOptions { AIContextProviders = [loop.Provider()] });
        await agent.RunAsync("another refund ticket");

        Assert.Equal(InjectionOutcome.Injected, loop.Injections[^1].Outcome);
        Assert.Equal(before, loop.Capture.RecordedExposures.Count);
    }

    /// <summary>The real services over in-memory doubles, and two agents: one that injects, one that does not.</summary>
    private sealed class Loop
    {
        private readonly Dictionary<Guid, Guid> _rounds = [];

        public Loop()
        {
            var independence = new ExperienceIndependenceOptions { AssessmentTokenKey = Key };
            Capture = new RecordingCaptureService(
                new InMemoryExperienceCaptureService(new DefaultSanitizer(Sanitization), new CaptureLimits(10, 50, 10_000, 10_000)));
            Lifecycle = new ExperienceLifecycleService(World, indexingService: null, independence, Capture);
            Finalization = new ExperienceFinalizationService(Capture, new DefaultExperienceReflector(), World, Lifecycle);
            Feedback = new ExperienceReuseFeedbackService(new Ledger(), Lifecycle);
            Issuer = new AssessmentTokenIssuer(independence);

            PlainAgent = Captured(new ChatClientAgent(new RecordingChatClient(), new ChatClientAgentOptions()));
            InjectingAgent = Captured(new ChatClientAgent(new RecordingChatClient(), new ChatClientAgentOptions { AIContextProviders = [Provider()] }));
        }

        public FakeExperienceWorld World { get; } = new();

        public RecordingCaptureService Capture { get; }

        public ExperienceLifecycleService Lifecycle { get; }

        public ExperienceFinalizationService Finalization { get; }

        public ExperienceReuseFeedbackService Feedback { get; }

        public AssessmentTokenIssuer Issuer { get; }

        public AIAgent PlainAgent { get; }

        public AIAgent InjectingAgent { get; }

        public List<ExperienceInjectionResult> Injections { get; } = [];

        public List<ExperienceCaptureFailure> Failures { get; } = [];

        public List<FinalizeExperienceResult> Finalized { get; } = [];

        public ExperienceContextProvider Provider() => new(
            new ExperienceRetrievalService(World, RetrievalPolicy.Default, RankingWeights.Default, new FrozenTimeProvider(InjectionRecords.Now)),
            World,
            new ExperienceInjectionOptions
            {
                ResolveRequest = _ => new RetrieveExperienceRequest(Authorization, TestScope, TaskId, CorrelationId: "exposure"),
                OnContextInjected = Injections.Add,
            });

        public async Task<ExperienceRecord> RunWithoutInjectionAsync()
        {
            var count = Finalized.Count;
            await PlainAgent.RunAsync("refund ticket stuck on a lock");
            var outcome = Assert.Single(Finalized.Skip(count));
            Assert.Equal(FinalizationOutcome.Validated, outcome.Outcome);
            return World.Stored[outcome.ExperienceId!.Value];
        }

        public async Task<Guid> RunInjectingAsync(AgentSession session)
        {
            var count = Finalized.Count;
            await InjectingAgent.RunAsync("another refund ticket stuck on a lock", session);
            var outcome = Assert.Single(Finalized.Skip(count));
            Assert.True(outcome.IsDurable);
            Assert.True(session.StateBag.TryGetValue<string>(ExperienceCaptureAgentBuilderExtensions.RunIdStateKey, out var text));
            return Guid.Parse(text!, CultureInfo.InvariantCulture);
        }

        public ExperienceRecord RecordOf(Guid runId) => World.Stored[ExperienceFinalizationService.ExperienceIdFor(runId, TestScope)];

        public ExperienceReuseFeedback ComparativeFeedback(ExperienceRecord run, Guid lesson) => new(
            FeedbackId: Guid.NewGuid(),
            RunId: run.SourceRunId,
            Scope: TestScope,
            ExposedExperienceIds: [lesson],
            RunOutcome: TaskVerificationStatus.Verified,
            Measure: new ReuseMeasure("tool-calls", 2),
            ObservedAt: InjectionRecords.Now,
            ComparativeEvaluation: new ComparativeEvaluationResult(
                "comparator",
                run.SourceRunId,
                run.ClosedRoundId!.Value,
                ExperienceReuseBenefit.Improved,
                [lesson],
                [new Evidence(Guid.NewGuid(), run.ClosedRoundId!.Value, ArtifactRevision, "comparison", "Comparison", CheckResult.Pass, "comparator", null, InjectionRecords.Now)],
                "fewer tool calls with the lesson than without it",
                InjectionRecords.Now));

        public AIAgent CapturedAgent(ChatClientAgent agent) => Captured(agent);

        private AIAgent Captured(ChatClientAgent agent) => agent
            .AsBuilder()
            .UseExperienceCapture(Capture, new ExperienceCaptureOptions
            {
                ResolveRun = _ => new ExperienceRunDescriptor(TaskId, TestScope, "Triage a refund ticket"),
                CaptureToolCalls = false,
                FinalizationService = Finalization,
                ResolveFinalization = context => Request(context.Run.RunId),
                OnRunFinalized = Finalized.Add,
                OnCaptureFailure = Failures.Add,
            })
            .Build();

        private FinalizeExperienceRequest Request(Guid runId)
        {
            Guid round;
            lock (_rounds)
            {
                if (!_rounds.TryGetValue(runId, out round))
                {
                    round = Guid.NewGuid();
                    _rounds[runId] = round;
                }
            }

            return new FinalizeExperienceRequest(
                RunId: runId,
                Authorization: Authorization,
                ClosedRound: new ClosedVerificationRound(round, ArtifactRevision),
                RequiredChecks: [new RequiredCheck("tests", "TestResult")],
                Evidence: [new Evidence(Guid.NewGuid(), round, ArtifactRevision, "tests", "TestResult", CheckResult.Pass, "ci", null, InjectionRecords.Now)],
                CurrentArtifactRevision: ArtifactRevision,
                StorageDecision: StorageDecision.Permit,
                FinalizedAt: InjectionRecords.Now);
        }
    }

    /// <summary>A chat client that, like a model calling an agent-as-a-tool, runs another agent before it answers.</summary>
    private sealed class NestingChatClient(AIAgent inner) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var answer = await inner.RunAsync("the nested question", cancellationToken: cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, answer.Text));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>A feedback ledger that records each submission once, keyed by its ID.</summary>
    private sealed class Ledger : IExperienceReuseFeedbackStore
    {
        private readonly Dictionary<Guid, RecordedExperienceReuseFeedback> _rows = [];

        public Task<ExperienceReuseFeedbackStoreResult> RecordAsync(
            AuthorizationContext authorization,
            RecordedExperienceReuseFeedback feedback,
            CancellationToken cancellationToken)
        {
            lock (_rows)
            {
                return Task.FromResult(_rows.TryAdd(feedback.FeedbackId, feedback)
                    ? new ExperienceReuseFeedbackStoreResult(ExperienceReuseFeedbackStoreOutcome.Recorded, feedback, [])
                    : new ExperienceReuseFeedbackStoreResult(ExperienceReuseFeedbackStoreOutcome.AlreadyRecorded, _rows[feedback.FeedbackId], []));
            }
        }
    }
}

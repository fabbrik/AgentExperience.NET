using System.Globalization;
using AgentExperience.Abstractions;
using AgentExperience.Core.Capture;
using AgentExperience.Core.Feedback;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Retrieval;
using AgentExperience.Core.Verification;
using AgentExperience.MicrosoftAgentFramework;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.Sample.EndToEnd.Fixtures;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.Sample.EndToEnd;

/// <summary>
/// A stage of the sample did not end the way the sample says it does. It is thrown rather than
/// printed, so the sample can never narrate a step that did not happen.
/// </summary>
internal sealed class SampleStageFailedException(string message) : Exception(message);

/// <summary>
/// The sample's seven stages: capture a run whose first approach is wrong and whose second is
/// right, verify it from evidence, reflect and persist it, then let a second run retrieve it, be
/// given it as a Historical Reference, and record that exposure.
/// </summary>
/// <remarks>
/// <para>
/// Everything between the stages is the real implementation: capture, sanitization, the
/// verification aggregator, the default reflector, lifecycle, finalization, retrieval, ranking, the
/// final eligibility re-check, the payload writer, and reuse feedback. What is a fixture is named
/// as one -- the model client, the clock, the identifier source, the environment fingerprint, and,
/// in the default mode, the three storage ports.
/// </para>
/// <para>
/// It returns a <see cref="SampleTranscript"/> rather than only printing, so the sample's own tests
/// assert the stages and their outcomes without parsing standard output. What the stages report is
/// read back out of the loop's own state -- the capture snapshot, the record the store returns, the
/// text run B's model was handed -- rather than restated from what the sample asked for.
/// </para>
/// </remarks>
public sealed class SampleRun
{
    private const string TaskId = "refund-ticket-triage";
    private const string TaskText = "Refund ticket RF-4821 is stuck on a database lock.";
    private const string ArtifactRevision = "refund-pipeline@rev-7";
    private const string CheckId = "refund-check";
    private const string EvidenceProducer = "sample-ci";

    // Host-established, the way a host establishes them: from its own bookkeeping, never from
    // anything an agent produced. Fixed values here so the transcript is the same on every run.
    private static readonly Guid FirstRoundId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid ClosedRoundId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid FirstEvidenceId = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid SecondEvidenceId = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly Guid FeedbackId = Guid.Parse("55555555-5555-4555-8555-555555555555");

    private static readonly Scope SampleScope = new("contoso", "support-desk", "refunds");

    private static readonly AuthorizationContext Authorization = new(
        TenantId: "contoso",
        PrincipalId: "sample-host",
        Roles: ["experience:read", "experience:write"],
        IssuedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static readonly EnvironmentFingerprint SampleEnvironment = new(
        HostName: "sample-host",
        RuntimeVersion: "net10.0",
        OperatingSystem: "sample-os",
        ApplicationVersion: "1.0.0-sample",
        Metadata: new Dictionary<string, string>(StringComparer.Ordinal) { ["Fixture"] = "deterministic" });

    private static readonly RequiredCheck[] RequiredChecks = [new RequiredCheck(CheckId, "ToolExitCode")];

    private readonly SampleStorageMode _mode;
    private readonly IExperienceCaptureService _capture;
    private readonly ExperienceFinalizationService _finalization;
    private readonly ExperienceRetrievalService _retrieval;
    private readonly IExperienceRecordStore _store;
    private readonly ExperienceReuseFeedbackService _feedback;
    private readonly SteppingTimeProvider _clock;
    private readonly DeterministicIds _ids;

    /// <summary>Creates a run over the ports and fixtures <paramref name="services"/> was composed with.</summary>
    /// <param name="services">The composed container. See <see cref="SampleHost"/> for what goes into it.</param>
    /// <param name="mode">Which ports were registered, for the transcript's header.</param>
    public SampleRun(IServiceProvider services, SampleStorageMode mode)
    {
        ArgumentNullException.ThrowIfNull(services);

        _mode = mode;
        _capture = services.GetRequiredService<IExperienceCaptureService>();
        _finalization = services.GetRequiredService<ExperienceFinalizationService>();
        _retrieval = services.GetRequiredService<ExperienceRetrievalService>();
        _store = services.GetRequiredService<IExperienceRecordStore>();
        _feedback = services.GetRequiredService<ExperienceReuseFeedbackService>();
        _clock = services.GetRequiredService<SteppingTimeProvider>();
        _ids = services.GetRequiredService<DeterministicIds>();
    }

    /// <summary>Run A exactly as capture holds it once both attempts are in and the run is complete.</summary>
    internal ExperienceRun? CapturedRunA { get; private set; }

    /// <summary>The Experience Record read back out of the store after finalization wrote it.</summary>
    internal ExperienceRecord? PersistedRecord { get; private set; }

    /// <summary>Run B's agent session, so a caller can read the capture middleware's own state bag itself.</summary>
    internal AgentSession? RunBSession { get; private set; }

    /// <summary>The Historical Reference block run B's model was actually handed, verbatim.</summary>
    internal string? InjectedBlock { get; private set; }

    /// <summary>Executes the seven stages in order and returns what each one produced.</summary>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <exception cref="SampleStageFailedException">A stage did not end the way the sample narrates it.</exception>
    public async Task<SampleTranscript> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var stages = new List<SampleStage>(7);

        // ---- Stages 1 and 2: one Experience Run, two attempts. --------------------------------
        // The capture contract models a second try at the same task as a second AppendAttemptAsync
        // on the same run, and the host drives it directly here so the contract underneath the
        // adapter is visible. Since story 4.6 the adapter can do the same thing: an invocation whose
        // ExperienceRunDescriptor carries ContinuesRunId appends its attempt to that run, and
        // ShouldCompleteRun decides which invocation closes it. Run B below uses
        // UseExperienceCapture in its ordinary, single-invocation form.
        var runAId = _ids.Next();
        var runStartedAt = _clock.GetUtcNow();
        var started = _capture.StartRun(
            runAId,
            TaskId,
            TaskText,
            SampleScope,
            SampleEnvironment,
            new Provenance("AgentExperience.Sample.EndToEnd", "1.0.0", runStartedAt, CorrelationId: "sample-run-a"),
            runStartedAt);

        if (started.Outcome != StartRunOutcome.Started)
        {
            throw new SampleStageFailedException($"Stage 1 could not open the Experience Run: StartRun returned {started.Outcome}.");
        }

        var wrong = await InvokeAttemptAsync(SampleTools.WrongStrategy, cancellationToken).ConfigureAwait(false);
        var firstAttemptId = _ids.Next();
        var appendedWrong = await _capture.AppendAttemptAsync(
            runAId,
            new AppendAttemptRequest(
                AttemptId: firstAttemptId,
                StartedAt: wrong.StartedAt,
                Duration: wrong.Duration,
                ToolCalls: wrong.ToolCalls,
                Result: null,
                Error: string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} exited {1} under strategy '{2}'; the refund was not posted.",
                    SampleTools.RefundCheckToolName,
                    wrong.ExitCode,
                    SampleTools.WrongStrategy)),
            cancellationToken).ConfigureAwait(false);

        Require(appendedWrong.Outcome == AppendAttemptOutcome.Recorded, 1, $"AppendAttemptAsync returned {appendedWrong.Outcome}.");

        // The wrong approach is what makes the record worth keeping, so the sample checks that the
        // check really refused it rather than assuming the fixture behaved.
        Require(wrong.ExitCode != 0, 1, $"attempt 1's check exited {wrong.ExitCode}, so it did not take the wrong approach.");

        stages.Add(new SampleStage(
            1,
            "capture",
            "Run A opens; attempt 1 takes the wrong approach.",
            [$"StartRunOutcome.{started.Outcome}", $"AppendAttemptOutcome.{appendedWrong.Outcome}"],
            [
                $"run {runAId:D}, attempt {firstAttemptId:D}",
                $"the agent called {SampleTools.RefundCheckToolName}(strategy: {SampleTools.WrongStrategy}) and it exited {wrong.ExitCode.ToString(CultureInfo.InvariantCulture)}",
                CapturedToolCallLine(runAId, attemptIndex: 0),
            ]));

        var right = await InvokeAttemptAsync(SampleTools.CorrectStrategy, cancellationToken).ConfigureAwait(false);
        var secondAttemptId = _ids.Next();
        var appendedRight = await _capture.AppendAttemptAsync(
            runAId,
            new AppendAttemptRequest(
                AttemptId: secondAttemptId,
                StartedAt: right.StartedAt,
                Duration: right.Duration,
                ToolCalls: right.ToolCalls,
                Result: right.AgentText,
                Error: null),
            cancellationToken).ConfigureAwait(false);

        Require(appendedRight.Outcome == AppendAttemptOutcome.Recorded, 2, $"AppendAttemptAsync returned {appendedRight.Outcome}.");
        Require(right.ExitCode == 0, 2, $"attempt 2's check exited {right.ExitCode}, so it did not take the correct approach.");

        var completed = await _capture.CompleteRunAsync(
            runAId,
            _ids.Next(),
            RunExecutionStatus.Completed,
            _clock.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);

        Require(completed.Outcome == CompleteRunOutcome.Recorded, 2, $"CompleteRunAsync returned {completed.Outcome}.");

        // Read the run back out of capture rather than narrating the two appends the sample just
        // made: the attempt count and the execution status the stage prints are then facts about
        // what capture holds.
        Require(_capture.TryGetRun(runAId, out var capturedRunA), 2, "the completed run could not be read back from capture.");
        CapturedRunA = capturedRunA;
        Require(
            capturedRunA!.ExecutionStatus == RunExecutionStatus.Completed,
            2,
            $"the run capture holds reports RunExecutionStatus.{capturedRunA.ExecutionStatus?.ToString() ?? "(none)"}.");
        Require(capturedRunA.Attempts.Count == 2, 2, $"capture holds {capturedRunA.Attempts.Count.ToString(CultureInfo.InvariantCulture)} attempt(s) on run A, not two.");
        Require(FailedAttemptCount(capturedRunA) == 1, 2, $"capture holds {FailedAttemptCount(capturedRunA).ToString(CultureInfo.InvariantCulture)} failed attempt(s) on run A, not one.");

        stages.Add(new SampleStage(
            2,
            "capture",
            "Attempt 2 takes the correct approach; the run completes.",
            [$"AppendAttemptOutcome.{appendedRight.Outcome}", $"CompleteRunOutcome.{completed.Outcome}"],
            [
                $"attempt {secondAttemptId:D}, on the same run {runAId:D}",
                $"the agent called {SampleTools.RefundCheckToolName}(strategy: {SampleTools.CorrectStrategy}) and it exited {right.ExitCode.ToString(CultureInfo.InvariantCulture)}",
                $"read back from capture: RunExecutionStatus.{capturedRunA.ExecutionStatus}, {capturedRunA.Attempts.Count.ToString(CultureInfo.InvariantCulture)} attempts captured, {FailedAttemptCount(capturedRunA).ToString(CultureInfo.InvariantCulture)} of them failed",
            ]));

        // ---- Stage 3: verify, from the host's evidence, against the host's closed round. -------
        var firstRoundEvidence = TaskCheckEvaluators.ExitCode(
            FirstEvidenceId, CheckId, FirstRoundId, ArtifactRevision, EvidenceProducer, _clock.GetUtcNow(), wrong.ExitCode);
        var closedRoundEvidence = TaskCheckEvaluators.ExitCode(
            SecondEvidenceId, CheckId, ClosedRoundId, ArtifactRevision, EvidenceProducer, _clock.GetUtcNow(), right.ExitCode);

        Evidence[] allEvidence = [firstRoundEvidence, closedRoundEvidence];
        var closedRound = new ClosedVerificationRound(ClosedRoundId, ArtifactRevision);

        // Two further aggregations of the same evidence, so the stage can say what actually happens
        // instead of implying one answer and printing another.
        //
        //  * `unclosedVerdict` is the aggregate when the host has closed nothing. That is the state
        //    round 1 is really in, and the aggregator answers Unknown for it -- never Failed.
        //  * `firstRoundClosedVerdict` is the aggregate had the host closed round 1 instead. It is
        //    obtained by *constructing* a closed round for that ID, which is a different question
        //    from the one above and has a different answer.
        var unclosedVerdict = VerificationAggregator.Aggregate(
            allEvidence, RequiredChecks, closedRound: null, ArtifactRevision, _clock.GetUtcNow());

        var firstRoundClosedVerdict = VerificationAggregator.Aggregate(
            allEvidence, RequiredChecks, new ClosedVerificationRound(FirstRoundId, ArtifactRevision), ArtifactRevision, _clock.GetUtcNow());

        var verdict = VerificationAggregator.Aggregate(
            allEvidence, RequiredChecks, closedRound, ArtifactRevision, _clock.GetUtcNow());

        Require(verdict.Outcome.Status == TaskVerificationStatus.Verified, 3, $"the closed round aggregated to {verdict.Outcome.Status}.");
        Require(firstRoundEvidence.Result == CheckResult.Fail, 3, $"attempt 1's evidence is CheckResult.{firstRoundEvidence.Result}, so the two attempts do not contrast.");
        Require(closedRoundEvidence.Result == CheckResult.Pass, 3, $"attempt 2's evidence is CheckResult.{closedRoundEvidence.Result}.");

        stages.Add(new SampleStage(
            3,
            "verify",
            "Attempt 1's evidence sits in a round the host left open; attempt 2's is in the round the host closed.",
            [$"TaskVerificationStatus.{verdict.Outcome.Status}"],
            [
                $"completion score {verdict.CompletionScore.ToString("F3", CultureInfo.InvariantCulture)} under rule {verdict.RuleVersion}, required checks: {CheckId}",
                $"attempt 1 evidence: CheckResult.{firstRoundEvidence.Result} in round {FirstRoundId:D}",
                $"attempt 2 evidence: CheckResult.{closedRoundEvidence.Result} in the closed round {ClosedRoundId:D}, revision {ArtifactRevision}",
                $"with no round closed at all, the same evidence aggregates to TaskVerificationStatus.{unclosedVerdict.Outcome.Status} -- an open round is unresolved, never failed",
                $"had the host closed round {FirstRoundId:D} instead, it would have been TaskVerificationStatus.{firstRoundClosedVerdict.Outcome.Status}: inside one round a Fail on a required check dominates a later Pass",
                "trust boundary: which round is closed is the host's claim, not the library's finding. The",
                "aggregator cannot tell a genuine fix-and-rerun -- what this sample is staging -- from a host",
                "parking a failure in a round it simply never closes. Closing a round is an assertion you are",
                "accountable for, and the lifecycle log is where it is auditable.",
            ]));

        // ---- Stage 4: reflect and persist. ----------------------------------------------------
        var finalized = await _finalization.FinalizeAsync(
            new FinalizeExperienceRequest(
                RunId: runAId,
                Authorization: Authorization,
                ClosedRound: closedRound,
                RequiredChecks: RequiredChecks,
                Evidence: allEvidence,
                CurrentArtifactRevision: ArtifactRevision,
                StorageDecision: StorageDecision.Permit,
                FinalizedAt: _clock.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);

        if (finalized.Outcome == FinalizationOutcome.AlreadyFinalized)
        {
            // Deterministic identifiers are what make the transcript assertable, and an Experience
            // Record's ID is derived from its run's -- so a durable store this sample has already
            // written to answers the second run with a replay. That is the store behaving correctly,
            // and the sample says so rather than narrating a stage that did not happen.
            throw new SampleStageFailedException(
                $"Stage 4 found run {runAId:D} already finalized as experience {finalized.ExperienceId:D}. The sample's identifiers are fixtures, so every run asks to finalize the same run ID; point {SampleHost.PostgresEnvironmentVariable} at a database this sample has not written to yet, or drop its schema, and run it again.");
        }

        Require(finalized.Outcome == FinalizationOutcome.Validated, 4, $"FinalizeAsync returned {finalized.Outcome} at stage {finalized.Stage}.");
        Require(finalized.Record is not null, 4, "FinalizeAsync produced no record.");
        Require(finalized.Reflection is not null, 4, "FinalizeAsync produced no reflection.");

        var reflection = finalized.Reflection!;

        // "Persisted" is read back from the store, not taken from what finalization returned, so the
        // stage reports a record the store actually holds under the sample's own authorization.
        var readBack = await _store.GetAsync(Authorization, SampleScope, finalized.Record!.ExperienceId, cancellationToken)
            .ConfigureAwait(false);
        Require(
            readBack.Outcome == ExperienceStoreOutcome.Found && readBack.Record is not null,
            4,
            $"the store answered {readBack.Outcome} when the record finalization reported durable was read back.");

        var record = readBack.Record!;
        PersistedRecord = record;

        // The premise of the whole sample: the record keeps the failed approach next to the one that
        // worked. Without both, there is nothing for a later run to learn from and the narration
        // above would be describing something else.
        Require(record.Attempts.Count == 2, 4, $"the persisted record kept {record.Attempts.Count} attempt(s), not both.");
        Require(record.Attempts[0].Error is not null && record.Attempts[0].Result is null, 4, "the persisted record's first attempt is not a failure.");
        Require(record.Attempts[1].Error is null && record.Attempts[1].Result is not null, 4, "the persisted record's second attempt is not a success.");
        Require(reflection.FailedApproaches.Count > 0, 4, "the reflection recorded no failed approach, so the contrast was lost.");
        Require(reflection.SuccessfulApproaches.Count > 0, 4, "the reflection recorded no successful approach.");
        RequireNoSecretIn(SampleSerialization.Describe(record), 4, "the persisted Experience Record");

        stages.Add(new SampleStage(
            4,
            "finalize",
            "The verified run is reflected on and persisted as one Experience Record.",
            [$"FinalizationOutcome.{finalized.Outcome}", $"IsDurable={finalized.IsDurable.ToString().ToLowerInvariant()}"],
            [
                $"experience {record.ExperienceId:D}, ExperienceStatus.{record.Status}, revision {record.Revision.ToString(CultureInfo.InvariantCulture)} (read back from the store)",
                $"reuse confidence {record.ReuseConfidence.ToString("F3", CultureInfo.InvariantCulture)}, {record.Attempts.Count.ToString(CultureInfo.InvariantCulture)} attempts kept: {AttemptShapeLine(record)}",
                $"lesson: {reflection.Lesson}",
                $"failed approaches recorded: {reflection.FailedApproaches.Count.ToString(CultureInfo.InvariantCulture)}; successful: {reflection.SuccessfulApproaches.Count.ToString(CultureInfo.InvariantCulture)}",
            ]));

        // ---- Stage 5: a second run asks the same task, and retrieval finds the record. ---------
        var retrieved = await _retrieval.RetrieveAsync(
            new RetrieveExperienceRequest(Authorization, SampleScope, TaskText, CorrelationId: "sample-run-b"),
            cancellationToken).ConfigureAwait(false);

        Require(retrieved.Outcome == RetrievalOutcome.Completed, 5, $"RetrieveAsync returned {retrieved.Outcome}.");
        Require(retrieved.Records.Count > 0, 5, "retrieval ranked no records, so there was nothing to inject.");

        stages.Add(new SampleStage(
            5,
            "retrieve",
            "Run B asks the same task; the stored record is found.",
            [$"RetrievalOutcome.{retrieved.Outcome}", $"candidates ranked: {retrieved.Records.Count.ToString(CultureInfo.InvariantCulture)}"],
            [
                $"eligible statuses: {string.Join(", ", ExperienceRetrievalService.EligibleStatuses)}; confidence floor {_retrieval.Policy.MinimumConfidence.ToString("F2", CultureInfo.InvariantCulture)}",
                $"top candidate: experience {retrieved.Records[0].Record.ExperienceId:D}, ExperienceStatus.{retrieved.Records[0].Record.Status}",
                retrieved.TextOnly
                    ? "text-only retrieval: no vector channel is configured, which is a supported deployment"
                    : "hybrid retrieval: both channels contributed",
            ]));

        // ---- Stages 6 and 7: injection into run B, then the exposure it produced. --------------
        var injectionLimits = ExperienceInjectionLimits.Default with { EligibilityCheckTimeout = TimeSpan.FromSeconds(30) };
        ExperienceInjectionResult? injection = null;
        var captureFailures = new List<ExperienceCaptureFailure>();

        // The scripted answer below is a fixed string. It is NOT derived from the injected block,
        // and this client would answer the same way with nothing injected at all -- see
        // ScriptedChatClient's remarks. Run B demonstrates that the record reaches the model's
        // context, never that the model made any use of it.
        var runBClient = new ScriptedChatClient(call: null, "Waiting for the ledger lock before retrying the refund.");

        var runBAgent = new ChatClientAgent(
            runBClient,
            new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions { Tools = [SampleTools.RefundCheck] },
                AIContextProviders =
                [
                    new ExperienceContextProvider(
                        _retrieval,
                        _store,
                        new ExperienceInjectionOptions
                        {
                            ResolveRequest = _ => new RetrieveExperienceRequest(Authorization, SampleScope, TaskText, CorrelationId: "sample-run-b"),
                            Limits = injectionLimits,
                            OnContextInjected = result => injection = result,
                            TimeProvider = _clock,
                        }),
                ],
            })
            .AsBuilder()
            // Capture is the outermost layer, so this must be the first Use on the builder. Run B is
            // wrapped with it for the reason stage 7 needs: the run ID reuse feedback is recorded
            // against comes from the session state capture writes, never from agent output.
            .UseExperienceCapture(_capture, new ExperienceCaptureOptions
            {
                ResolveRun = _ => new ExperienceRunDescriptor(TaskId, SampleScope, TaskText),
                Environment = SampleEnvironment,
                NewId = _ids.Next,
                TimeProvider = _clock,
                FinalizationTimeout = TimeSpan.FromSeconds(30),
                OnCaptureFailure = captureFailures.Add,
            })
            .Build();

        var session = await runBAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        RunBSession = session;
        await runBAgent.RunAsync(TaskText, session, cancellationToken: cancellationToken).ConfigureAwait(false);

        Require(captureFailures.Count == 0, 6, $"capture reported {captureFailures.Count} failure(s), the first at stage {captureFailures.FirstOrDefault()?.Stage}.");
        Require(injection is not null, 6, "the context provider never reported an injection result.");
        Require(injection!.Outcome == InjectionOutcome.Injected, 6, $"the context provider returned {injection.Outcome}.");

        // What the block says is read out of the text the model was handed, never restated from the
        // injection result, so the stage cannot claim a property the payload does not have.
        var block = HistoricalReferenceIn(runBClient.FirstTurnMessages);
        Require(block is not null, 6, "no Historical Reference block reached run B's model, so there was nothing to describe.");
        InjectedBlock = block;
        RequireNoSecretIn(block!, 6, "the injected Historical Reference block");

        var blockShape = BlockShape(block!, record);
        Require(blockShape.CarriesLesson, 6, "the injected block does not carry the record's lesson.");
        Require(blockShape.NamesSource, 6, "the injected block does not name the record it came from.");
        Require(blockShape.NamesConfidence, 6, "the injected block does not state the record's confidence.");
        Require(blockShape.NamesApplicability, 6, "the injected block does not state how it was ranked.");
        Require(blockShape.NamesApproach, 6, "the injected block does not name the tools the verified attempt used.");
        Require(!blockShape.RepeatsCapturedResult, 6, "the injected block repeats a raw captured result, which it must never do.");
        Require(!blockShape.RepeatsToolArguments, 6, "the injected block repeats a captured tool argument, which it must never do.");

        if (!session.StateBag.TryGetValue<string>(ExperienceCaptureAgentBuilderExtensions.RunIdStateKey, out var runBText)
            || !Guid.TryParse(runBText, out var runBId))
        {
            throw new SampleStageFailedException(
                $"Stage 6 found no run ID under the session state key '{ExperienceCaptureAgentBuilderExtensions.RunIdStateKey}', so stage 7 would have had to invent one.");
        }

        stages.Add(new SampleStage(
            6,
            "inject",
            "The record is injected into run B as a labelled Historical Reference.",
            [$"InjectionOutcome.{injection.Outcome}", $"injected: {string.Join(", ", injection.InjectedExperienceIds.Select(id => id.ToString("D")))}"],
            [
                $"byte budget used: {injection.PayloadBytes.ToString(CultureInfo.InvariantCulture)} of {injectionLimits.MaxBytes.ToString(CultureInfo.InvariantCulture)}; record limit {injectionLimits.MaxRecords.ToString(CultureInfo.InvariantCulture)}",
                $"omitted: {injection.Omitted.Count.ToString(CultureInfo.InvariantCulture)}; excluded before ranking: {injection.Excluded.Count.ToString(CultureInfo.InvariantCulture)}; truncated search: {injection.Truncated.ToString().ToLowerInvariant()}",
                $"read out of the {Utf8Length(block!).ToString(CultureInfo.InvariantCulture)} bytes run B's model was handed: lesson {Present(blockShape.CarriesLesson)}, source {Named(blockShape.NamesSource)}, confidence {Named(blockShape.NamesConfidence)}, applicability {Named(blockShape.NamesApplicability)}, approach {Named(blockShape.NamesApproach)}",
                $"the approach is the verified attempt's tool names in order and nothing else: raw captured result {Present(blockShape.RepeatsCapturedResult)}, captured tool argument {Present(blockShape.RepeatsToolArguments)}",
                $"run B's own Experience Run is {runBId:D}, read from the session state key '{ExperienceCaptureAgentBuilderExtensions.RunIdStateKey}'",
            ]));

        var submitted = new ExperienceReuseFeedback(
            FeedbackId: FeedbackId,
            RunId: runBId,
            Scope: SampleScope,
            ExposedExperienceIds: injection.InjectedExperienceIds,
            RunOutcome: TaskVerificationStatus.Unknown,
            Measure: new ReuseMeasure("ExposedRecords", injection.InjectedCount),
            ObservedAt: _clock.GetUtcNow(),
            ClaimedBenefit: ExperienceReuseBenefit.Unknown);

        var recorded = await _feedback.RecordAsync(Authorization, submitted, cancellationToken).ConfigureAwait(false);

        Require(recorded.Outcome == ExperienceReuseFeedbackOutcome.Recorded, 7, $"RecordAsync returned {recorded.Outcome}.");

        stages.Add(new SampleStage(
            7,
            "feedback",
            "The exposure is recorded against run B, and nothing is claimed about it.",
            [
                $"ExperienceReuseFeedbackOutcome.{recorded.Outcome}",
                $"Outcome: {recorded.Outcome}{SampleTranscript.FieldSeparator}Benefit: {recorded.Benefit}{SampleTranscript.FieldSeparator}nothing moved",
            ],
            [
                $"feedback {recorded.FeedbackId:D} against run {submitted.RunId:D}",
                $"ReuseAttributionSource.{recorded.AttributionSource}; {string.Join(", ", recorded.Exposures.Select(e => $"experience {e.ExperienceId:D}: ExperienceExposureDisposition.{e.Disposition}, counted={e.Counted.ToString().ToLowerInvariant()}"))}",
                "exposure is not attribution: the run saw the record and the run ended, and nothing in those two facts",
                "attributes one to the other. Moving a confidence score needs a human assessment or a comparative",
                "evaluation, and the sample performed neither, so it submits neither.",
            ]));

        return new SampleTranscript(_mode, stages, runAId, runBId, record.ExperienceId, submitted);
    }

    /// <summary>What one attempt of run A did: its tool calls, its exit code, and how long it took.</summary>
    private sealed record AttemptObservation(
        DateTimeOffset StartedAt,
        TimeSpan Duration,
        IReadOnlyList<RawToolCall> ToolCalls,
        int ExitCode,
        string AgentText);

    /// <summary>What the injected block was found to contain, each answer read out of the block itself.</summary>
    private sealed record InjectedBlockShape(
        bool CarriesLesson,
        bool NamesSource,
        bool NamesConfidence,
        bool NamesApplicability,
        bool NamesApproach,
        bool RepeatsCapturedResult,
        bool RepeatsToolArguments);

    /// <summary>
    /// Runs one attempt: a real <see cref="ChatClientAgent"/> over the scripted model client, with
    /// the sample's own function middleware buffering the tool calls MAF makes.
    /// </summary>
    private async Task<AttemptObservation> InvokeAttemptAsync(string strategy, CancellationToken cancellationToken)
    {
        var recorder = new AttemptToolRecorder(_clock, _ids.Next);
        var agent = new ChatClientAgent(
            new ScriptedChatClient(
                SampleTools.CallFor(strategy),
                string.Format(CultureInfo.InvariantCulture, "Ran the refund check for {0} under the {1} strategy.", SampleTools.TicketId, strategy)),
            new ChatClientAgentOptions { ChatOptions = new ChatOptions { Tools = [SampleTools.RefundCheck] } })
            .AsBuilder()
            .Use(recorder.InvokeAsync)
            .Build();

        var startedAt = _clock.GetUtcNow();
        var startTimestamp = _clock.GetTimestamp();
        var response = await agent.RunAsync(TaskText, cancellationToken: cancellationToken).ConfigureAwait(false);
        var duration = _clock.GetElapsedTime(startTimestamp);

        if (SampleTools.ExitCodeOf(recorder.LastResult) is not { } exitCode)
        {
            throw new SampleStageFailedException(
                $"The scripted agent did not call {SampleTools.RefundCheckToolName} under strategy '{strategy}', so the attempt has no exit code to verify.");
        }

        return new AttemptObservation(startedAt, duration, recorder.Calls, exitCode, response.Text);
    }

    /// <summary>
    /// Renders one captured attempt's first tool call straight out of the capture service's own
    /// snapshot, so what the transcript shows is what was stored -- including the redaction. The
    /// claim about the secret is checked against the snapshot before it is printed.
    /// </summary>
    private string CapturedToolCallLine(Guid runId, int attemptIndex)
    {
        if (!_capture.TryGetRun(runId, out var run) || run.Attempts.Count <= attemptIndex)
        {
            throw new SampleStageFailedException($"Stage 1 could not read attempt {attemptIndex.ToString(CultureInfo.InvariantCulture)} back out of capture.");
        }

        var call = run.Attempts[attemptIndex].ToolCalls.FirstOrDefault()
            ?? throw new SampleStageFailedException("Stage 1 captured no tool call, so there is nothing to show redacted.");

        var arguments = string.Join(
            ", ",
            call.Arguments.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}=\"{pair.Value}\""));

        // The sample never prints "it was redacted" without looking. The secret's own value must be
        // absent from the whole captured run, and the secret field must be present and empty: an
        // argument that was dropped rather than redacted would be a different claim.
        RequireNoSecretIn(SampleSerialization.Describe(run), 1, "the captured Experience Run");
        Require(
            call.Arguments.TryGetValue(SampleTools.SecretArgumentName, out var secret) && secret is string { Length: 0 },
            1,
            $"the captured tool call does not carry '{SampleTools.SecretArgumentName}' as an empty value, so the sample cannot say it was redacted.");

        return $"captured as {call.ToolName}({arguments}) -- '{SampleTools.SecretArgumentName}' is empty because the sanitization policy classifies it as a secret";
    }

    /// <summary>The Historical Reference block out of the messages run B's model was handed, if one arrived.</summary>
    private static string? HistoricalReferenceIn(IReadOnlyList<ChatMessage> messages) => messages
        .Select(message => message.Text)
        .FirstOrDefault(text => text.Contains(HistoricalReferenceWriter.BlockBegin, StringComparison.Ordinal));

    /// <summary>Reads back, out of the block itself, what the sample is about to say the block contains.</summary>
    private static InjectedBlockShape BlockShape(string block, ExperienceRecord record) => new(
        CarriesLesson: record.Reflection is { Lesson.Length: > 0 } reflection && block.Contains(reflection.Lesson, StringComparison.Ordinal),
        NamesSource: block.Contains($"Source: experience {record.ExperienceId:D}", StringComparison.Ordinal),
        NamesConfidence: block.Contains("Confidence: ", StringComparison.Ordinal),
        NamesApplicability: block.Contains("Applicability (as ranked at retrieval)", StringComparison.Ordinal),
        NamesApproach: record.Attempts
            .OrderBy(attempt => attempt.SequenceNumber)
            .LastOrDefault() is { Error: null } winning
            && winning.ToolCalls.Count > 0
            && block.Contains(
                "Approach: " + HistoricalReferenceWriter.ApproachPrefix
                    + string.Join(HistoricalReferenceWriter.ApproachSeparator, winning.ToolCalls.OrderBy(call => call.SequenceNumber).Select(call => call.ToolName)) + ".",
                StringComparison.Ordinal),
        RepeatsCapturedResult: record.Attempts.Any(attempt =>
            attempt.Result is { Length: > 0 } result && block.Contains(result, StringComparison.Ordinal)),

        // The approach carries names. An argument value -- the free-form half of a tool call, and the
        // half a secret lives in -- must not ride along with them.
        RepeatsToolArguments: record.Attempts
            .SelectMany(attempt => attempt.ToolCalls)
            .SelectMany(call => call.Arguments.Values)
            .OfType<string>()
            .Any(value => value.Length > 0 && block.Contains(value, StringComparison.Ordinal)));

    private static int FailedAttemptCount(ExperienceRun run) => run.Attempts.Count(attempt => attempt.Error is not null);

    private static string AttemptShapeLine(ExperienceRecord record) => string.Join(
        ", ",
        record.Attempts.Select(attempt => string.Format(
            CultureInfo.InvariantCulture,
            "#{0} {1}",
            attempt.SequenceNumber,
            attempt.Error is null ? "succeeded" : "failed")));

    private static string Present(bool value) => value ? "present" : "absent";

    private static string Named(bool value) => value ? "named" : "missing";

    private static int Utf8Length(string text) => System.Text.Encoding.UTF8.GetByteCount(text);

    /// <summary>
    /// Refuses to continue if the live secret value appears anywhere in <paramref name="text"/>. The
    /// sample's most reputation-critical claim is that the token never reaches a record, so it is
    /// checked rather than asserted in prose.
    /// </summary>
    private static void RequireNoSecretIn(string text, int stage, string what)
    {
        if (text.Contains(SampleTools.SecretArgumentValue, StringComparison.Ordinal))
        {
            throw new SampleStageFailedException(string.Format(
                CultureInfo.InvariantCulture,
                "Stage {0} found the secret tool argument's live value in {1}. Nothing is printed: the sanitization policy did not redact what the sample says it redacts.",
                stage,
                what));
        }
    }

    private static void Require(bool condition, int stage, string what)
    {
        if (!condition)
        {
            throw new SampleStageFailedException($"Stage {stage.ToString(CultureInfo.InvariantCulture)} did not happen as the sample narrates it: {what}");
        }
    }
}

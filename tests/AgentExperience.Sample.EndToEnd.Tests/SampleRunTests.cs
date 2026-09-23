using System.Diagnostics;
using System.Text.Json;
using AgentExperience.Abstractions;
using AgentExperience.MicrosoftAgentFramework;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.Sample.EndToEnd.Fixtures;

namespace AgentExperience.Sample.EndToEnd.Tests;

/// <summary>
/// Holds the sample to the story's frozen narrative: the seven stages, in order, with the library's
/// own outcome values; a run that really did fail once and then succeed; a secret that really is
/// absent; and a report that claims nothing the sample did not measure.
/// </summary>
/// <remarks>
/// Every test here runs the sample in its default mode: no Docker, no PostgreSQL, no model
/// credentials, and no network, which is also how CI runs it. Where a fact can be read out of the
/// sample's own stores rather than off its printed lines, it is: a transcript line is evidence that
/// the sample said something, never that it did it.
/// </remarks>
public class SampleRunTests
{
    /// <summary>
    /// The stage names the story's frozen narrative fixes, restated here on purpose.
    /// </summary>
    /// <remarks>
    /// The expectation lives in the test, not on the production transcript type. Asserting a
    /// production constant against production output proves only that one file agrees with itself:
    /// renaming a stage in both places used to pass.
    /// </remarks>
    private static readonly string[] FrozenStageNames =
        ["capture", "capture", "verify", "finalize", "retrieve", "inject", "feedback"];

    [Fact]
    public async Task Sample_runs_all_seven_stages_in_order()
    {
        var transcript = (await SampleFacts.RunAsync()).Transcript;

        Assert.Equal(SampleStorageMode.InMemory, transcript.Mode);
        Assert.Equal(FrozenStageNames, transcript.Stages.Select(stage => stage.Name));
        Assert.Equal(Enumerable.Range(1, 7), transcript.Stages.Select(stage => stage.Number));

        // The frozen narrative's "printed outcome" column, stage by stage.
        Assert.Equal(["StartRunOutcome.Started", "AppendAttemptOutcome.Recorded"], transcript.Stages[0].Outcomes);
        Assert.Equal(["AppendAttemptOutcome.Recorded", "CompleteRunOutcome.Recorded"], transcript.Stages[1].Outcomes);
        Assert.Equal(["TaskVerificationStatus.Verified"], transcript.Stages[2].Outcomes);
        Assert.Contains(transcript.Stages[2].Details, detail => detail.StartsWith("completion score 1.000", StringComparison.Ordinal));
        Assert.Equal(["FinalizationOutcome.Validated", "IsDurable=true"], transcript.Stages[3].Outcomes);
        Assert.Equal(["RetrievalOutcome.Completed", "candidates ranked: 1"], transcript.Stages[4].Outcomes);
        Assert.Equal(
            ["InjectionOutcome.Injected", $"injected: {transcript.ExperienceId:D}"],
            transcript.Stages[5].Outcomes);
        Assert.Equal(
            ["ExperienceReuseFeedbackOutcome.Recorded", "Outcome: Recorded | Benefit: Unknown | nothing moved"],
            transcript.Stages[6].Outcomes);

        // Stage 6 must report the byte budget even though one small record can never exhaust it.
        Assert.Contains(transcript.Stages[5].Details, detail => detail.StartsWith("byte budget used: ", StringComparison.Ordinal));
    }

    /// <summary>
    /// The premise of the whole sample, checked against the stores rather than the narration: run A
    /// really contains one failed attempt and one successful one, and the record kept both.
    /// </summary>
    [Fact]
    public async Task Run_A_really_failed_once_and_then_succeeded()
    {
        var execution = await SampleFacts.RunAsync();
        var captured = execution.CapturedRun();
        var record = execution.PersistedRecord();

        Assert.Equal(RunExecutionStatus.Completed, captured.ExecutionStatus);
        Assert.Equal(2, captured.Attempts.Count);
        Assert.Equal(1, captured.Attempts.Count(attempt => attempt.Error is not null));

        // Attempt 1 failed, with no result; attempt 2 succeeded, with no error. Making attempt 1 pass
        // leaves the transcript saying "takes the wrong approach" next to CheckResult.Pass, and this
        // is what refuses it.
        Assert.NotNull(captured.Attempts[0].Error);
        Assert.Null(captured.Attempts[0].Result);
        Assert.Null(captured.Attempts[1].Error);
        Assert.NotNull(captured.Attempts[1].Result);

        // The two attempts differ by the one argument the sample says they differ by, read off the
        // captured tool calls rather than off the sentence that describes them.
        Assert.Equal(SampleTools.WrongStrategy, StrategyOf(captured.Attempts[0]));
        Assert.Equal(SampleTools.CorrectStrategy, StrategyOf(captured.Attempts[1]));

        // And the record the store holds carries the same shape, which is what a later run learns from.
        Assert.Equal(2, record.Attempts.Count);
        Assert.NotNull(record.Attempts[0].Error);
        Assert.Null(record.Attempts[1].Error);
        Assert.NotNull(record.Reflection);
        Assert.NotEmpty(record.Reflection!.FailedApproaches);
        Assert.NotEmpty(record.Reflection.SuccessfulApproaches);
        Assert.Equal(TaskVerificationStatus.Verified, record.Reflection.VerificationStatus);

        // The transcript agrees with all of that rather than asserting it independently.
        Assert.Contains(
            execution.Transcript.Stages[3].Details,
            detail => detail.StartsWith("failed approaches recorded: 1; successful: 1", StringComparison.Ordinal));
    }

    /// <summary>
    /// The sample's most reputation-critical claim: the live secret tool argument reaches nothing.
    /// </summary>
    /// <remarks>
    /// Moving <c>apiToken</c> from the sanitization policy's secret list to its allowed list used to
    /// print the live token straight into the transcript, one line above a sentence saying it had
    /// been redacted, with every test still green.
    /// </remarks>
    [Fact]
    public async Task Secret_tool_argument_reaches_neither_the_transcript_the_capture_nor_the_record()
    {
        var execution = await SampleFacts.RunAsync();
        var secret = SampleTools.SecretArgumentValue;

        Assert.DoesNotContain(secret, execution.Transcript.Render(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, Flatten(execution.CapturedRun()), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, Flatten(execution.PersistedRecord()), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, execution.InjectedBlock(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, Flatten(execution.LedgerRow()), StringComparison.Ordinal);

        // Redacted, not merely dropped: the field is captured, and captured empty. A policy that
        // omitted the argument entirely would satisfy the assertions above while making the
        // transcript's explanation of *why* it is empty wrong.
        var arguments = execution.CapturedRun().Attempts[0].ToolCalls[0].Arguments;
        Assert.True(arguments.TryGetValue(SampleTools.SecretArgumentName, out var captured));
        Assert.Equal(string.Empty, Assert.IsType<string>(captured));

        // The non-secret arguments are still there, so "redaction" is not passing by capturing nothing.
        Assert.Equal(SampleTools.TicketId, Assert.IsType<string>(arguments["ticketId"]));
    }

    /// <summary>
    /// Frozen rule 8: run B's run ID comes from the capture middleware's session state, never from
    /// agent output.
    /// </summary>
    /// <remarks>
    /// The test reads the state bag itself rather than comparing a printed ID against itself.
    /// Replacing the sample's session-state lookup with a fresh identifier from the counter used to
    /// pass, because every value in sight came from the same substitution.
    /// </remarks>
    [Fact]
    public async Task Sample_run_id_comes_from_capture_state_not_agent_output()
    {
        var execution = await SampleFacts.RunAsync();
        var transcript = execution.Transcript;

        // The independent read: what capture actually wrote under its own key.
        var fromState = execution.RunIdFromSessionState();

        Assert.NotEqual(Guid.Empty, fromState);
        Assert.NotEqual(transcript.RunAId, fromState);
        Assert.Equal(fromState, transcript.RunBId);
        Assert.Equal(fromState, transcript.SubmittedFeedback.RunId);
        Assert.Equal(fromState, execution.LedgerRow().RunId);

        // And the value is nowhere in what the model produced, so it cannot have come from there.
        Assert.DoesNotContain(fromState.ToString("D"), execution.InjectedBlock(), StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            transcript.Stages[5].Details,
            detail => detail.Contains(ExperienceCaptureAgentBuilderExtensions.RunIdStateKey, StringComparison.Ordinal)
                && detail.Contains(fromState.ToString("D"), StringComparison.Ordinal));
    }

    /// <summary>
    /// Frozen rule 5, read out of the ledger the sample wrote to rather than off the line that
    /// describes it. Deleting the <c>RecordAsync</c> call entirely and rendering stage 7 from
    /// literals used to pass every test.
    /// </summary>
    [Fact]
    public async Task Sample_records_feedback_without_claiming_benefit()
    {
        var execution = await SampleFacts.RunAsync();
        var transcript = execution.Transcript;
        var submitted = transcript.SubmittedFeedback;

        Assert.Equal(ExperienceReuseBenefit.Unknown, submitted.ClaimedBenefit);
        Assert.Null(submitted.HumanAssessment);
        Assert.Null(submitted.ComparativeEvaluation);

        // The row the ledger holds: one submission, for the run the middleware opened, exposing the
        // record that was injected, claiming nothing and attributing nothing.
        var row = execution.LedgerRow();
        Assert.Equal(submitted.FeedbackId, row.FeedbackId);
        Assert.Equal(transcript.RunBId, row.RunId);
        Assert.Equal(ExperienceReuseBenefit.Unknown, row.ClaimedBenefit);
        Assert.Equal(ExperienceReuseBenefit.Unknown, row.Benefit);
        Assert.Equal(ReuseAttributionSource.None, row.AttributionSource);
        Assert.Null(row.ReviewerIdentity);
        Assert.Null(row.EvaluatorId);
        Assert.Null(row.AssessmentId);
        Assert.Null(row.VerificationRoundId);
        Assert.Null(row.AttributedAt);
        Assert.Empty(row.EvidenceIds);

        var exposure = Assert.Single(row.Exposures);
        Assert.Equal(transcript.ExperienceId, exposure.ExperienceId);
        Assert.False(exposure.Attributed);
        Assert.Null(exposure.EvidenceId);

        // And nothing moved: the record the store holds after stage 7 is, field for field, the record
        // stage 4 read before it. Exposure alone changes no confidence, no counter, no revision, and
        // not even the record's UpdatedAt.
        var before = execution.PersistedRecord();
        var after = execution.RecordAfterFeedback();
        Assert.Equal(before.ReuseConfidence, after.ReuseConfidence);
        Assert.Equal(before.SupportingValidations, after.SupportingValidations);
        Assert.Equal(before.Contradictions, after.Contradictions);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.Status, after.Status);

        var stage = transcript.Stages[6];
        Assert.Contains("Benefit: Unknown | nothing moved", string.Join("\n", stage.Outcomes), StringComparison.Ordinal);
        Assert.Contains(stage.Details, detail => detail.Contains("ExperienceExposureDisposition.ExposureOnly", StringComparison.Ordinal)
            && detail.Contains("counted=false", StringComparison.Ordinal));
    }

    /// <summary>
    /// Stage 6 says what the injected block carries; this checks it against the block run B's model
    /// was actually handed.
    /// </summary>
    [Fact]
    public async Task Injected_block_carries_the_lesson_and_the_approach_but_not_the_raw_captured_result()
    {
        var execution = await SampleFacts.RunAsync();
        var block = execution.InjectedBlock();
        var record = execution.PersistedRecord();

        Assert.Contains(record.Reflection!.Lesson, block, StringComparison.Ordinal);
        Assert.Contains($"Source: experience {record.ExperienceId:D}", block, StringComparison.Ordinal);
        Assert.Contains("Confidence: ", block, StringComparison.Ordinal);
        Assert.Contains("Applicability (as ranked at retrieval)", block, StringComparison.Ordinal);

        // Story 4.6: the verified attempt's tool names, in order, and derived from the record's own
        // attempts rather than from the reflection's prose.
        var winning = record.Attempts.OrderBy(attempt => attempt.SequenceNumber).Last();
        Assert.Null(winning.Error);
        Assert.Contains(
            "Approach: " + HistoricalReferenceWriter.ApproachPrefix
                + string.Join(HistoricalReferenceWriter.ApproachSeparator, winning.ToolCalls.OrderBy(call => call.SequenceNumber).Select(call => call.ToolName)) + ".",
            block,
            StringComparison.Ordinal);

        foreach (var attempt in record.Attempts.Where(attempt => attempt.Result is { Length: > 0 }))
        {
            Assert.DoesNotContain(attempt.Result!, block, StringComparison.Ordinal);
        }

        // Names cross; the arguments they were called with do not.
        foreach (var value in record.Attempts.SelectMany(a => a.ToolCalls).SelectMany(c => c.Arguments.Values).OfType<string>().Where(v => v.Length > 0))
        {
            Assert.DoesNotContain(value, block, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Matrix row 10: the sample behaves identically with a telemetry listener attached.
    /// </summary>
    /// <remarks>
    /// The claim is printed in the sample's README, and it holds today only because story 4.1
    /// measures duration with <c>Stopwatch.GetTimestamp()</c> rather than through the injected
    /// <see cref="TimeProvider"/>. That is one line away from silently breaking determinism, which
    /// is exactly why it is worth a test.
    /// </remarks>
    [Fact]
    public async Task Sample_renders_the_same_transcript_with_a_telemetry_listener_attached()
    {
        var withoutListener = (await SampleFacts.RunAsync()).Transcript.Render();

        var activities = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("AgentExperience.", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _ => Interlocked.Increment(ref activities),
        };
        ActivitySource.AddActivityListener(listener);

        var withListener = (await SampleFacts.RunAsync()).Transcript.Render();

        // The listener really was subscribed -- otherwise this asserts nothing at all.
        Assert.True(activities > 0, "No AgentExperience activity was observed, so the listener proved nothing.");
        Assert.Equal(withoutListener, withListener);
        Assert.Equal(GoldenTranscriptTests.ReadGolden(), withListener);
    }

    [Fact]
    public async Task Sample_is_deterministic()
    {
        var first = (await SampleFacts.RunAsync()).Transcript;
        var second = (await SampleFacts.RunAsync()).Transcript;

        // The rendered transcript is every identifier, timestamp, score, and byte count the sample
        // prints, so comparing it compares all of them at once -- and it is literally the stdout
        // that `dotnet run` twice must produce byte for byte.
        Assert.Equal(first.Render(), second.Render());

        Assert.Equal(first.RunAId, second.RunAId);
        Assert.Equal(first.RunBId, second.RunBId);
        Assert.Equal(first.ExperienceId, second.ExperienceId);
        Assert.Equal(first.SubmittedFeedback.ObservedAt, second.SubmittedFeedback.ObservedAt);
    }

    /// <summary>The strategy argument of an attempt's first captured tool call.</summary>
    private static string StrategyOf(Attempt attempt) =>
        Assert.IsType<string>(Assert.Single(attempt.ToolCalls).Arguments["strategy"]);

    /// <summary>Everything an object holds, as one searchable string.</summary>
    private static string Flatten<T>(T value) => JsonSerializer.Serialize(
        value,
        new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

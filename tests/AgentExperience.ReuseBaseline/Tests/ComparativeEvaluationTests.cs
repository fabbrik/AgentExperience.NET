using AgentExperience.Abstractions;
using AgentExperience.Core.Capture;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Feedback;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Retrieval;
using AgentExperience.Core.Sanitization;
using AgentExperience.ReuseBaseline.Experiment;
using AgentExperience.Sample.EndToEnd.Doubles;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.ReuseBaseline.Tests;

/// <summary>
/// The comparative-evaluation path, exercised against synthetic evidence only.
/// </summary>
/// <remarks>
/// <para>
/// Frozen rule 11: the reference experiment submits no <see cref="ComparativeEvaluationResult"/> and
/// no <see cref="HumanReuseAssessment"/>. Fabricating either from a scripted run would move a real
/// confidence score on the strength of a script. The path still has to be exercised, so it is
/// exercised here, where the evidence is openly synthetic and nothing published depends on it.
/// </para>
/// <para>
/// What the tests are about is the split at
/// <c>ExperienceReuseFeedbackService.cs:832-920</c>: which failures cost the whole submission
/// (fatal, nothing written) and which cost only the attribution (degrading, the exposure is still
/// recorded with benefit Unknown). That split is the reason a bad attribution cannot take a true
/// fact about a run down with it.
/// </para>
/// </remarks>
public class ComparativeEvaluationTests
{
    private static readonly Scope TestScope = new("reuse-baseline", "incident-desk", "settlement");

    private static readonly AuthorizationContext Authorization =
        new("reuse-baseline", "reuse-baseline-harness", ["experience:read", "experience:write"], DateTimeOffset.UnixEpoch);

    private static readonly Guid ExposedId = TrialIdentities.Derive("synthetic", 0, "experience", 0);
    private static readonly Guid RunId = TrialIdentities.Derive("synthetic", 0, "run", 0);
    private static readonly Guid RoundId = TrialIdentities.Derive("synthetic", 0, "closed-round", 0);
    private static readonly DateTimeOffset At = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_valid_comparative_result_is_accepted_as_machine_attribution()
    {
        var recorded = await RecordAsync(Feedback(Comparative()));

        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, recorded.Outcome);
        Assert.Equal(ReuseAttributionSource.ComparativeEvaluation, recorded.AttributionSource);
        Assert.Equal(ExperienceReuseBenefit.Improved, recorded.Benefit);
    }

    [Fact]
    public async Task A_valid_result_about_a_run_the_library_never_finalized_costs_the_attribution()
    {
        // Story 6.6: the run and the round are half of the machine independence key, so a result naming
        // a run with no finalized record in the scope is not attribution. The exposure is still recorded.
        var ledger = new InMemoryReuseFeedbackStore();

        var recorded = await RecordAsync(Feedback(Comparative()), ledger, finalizeRun: false);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, recorded.Outcome);
        Assert.Equal(ReuseAttributionSource.None, recorded.AttributionSource);
        Assert.Contains("RunId", recorded.Reason!, StringComparison.Ordinal);
        Assert.Single(ledger.Rows);
    }

    [Fact]
    public async Task A_result_about_a_different_run_is_fatal_and_nothing_is_written()
    {
        var ledger = new InMemoryReuseFeedbackStore();

        var recorded = await RecordAsync(
            Feedback(Comparative() with { RunId = TrialIdentities.Derive("synthetic", 99, "run", 0) }),
            ledger);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Invalid, recorded.Outcome);
        Assert.Contains(recorded.Errors, error => error.Path.Contains("RunId", StringComparison.Ordinal));
        Assert.Empty(ledger.Rows);
    }

    [Fact]
    public async Task Carrying_both_a_comparative_result_and_a_human_assessment_is_fatal()
    {
        var ledger = new InMemoryReuseFeedbackStore();

        var recorded = await RecordAsync(
            Feedback(Comparative()) with
            {
                HumanAssessment = new HumanReuseAssessment(
                    AssessmentId: TrialIdentities.Derive("synthetic", 0, "assessment", 0),
                    Benefit: ExperienceReuseBenefit.Improved,
                    AttributedExperienceIds: [ExposedId],
                    Rationale: "synthetic",
                    AssessedAt: At),
            },
            ledger);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Invalid, recorded.Outcome);
        Assert.Empty(ledger.Rows);
    }

    [Fact]
    public async Task Attributing_a_record_the_run_was_never_exposed_to_is_fatal()
    {
        var ledger = new InMemoryReuseFeedbackStore();

        var recorded = await RecordAsync(
            Feedback(Comparative() with { AttributedExperienceIds = [TrialIdentities.Derive("synthetic", 0, "experience", 1)] }),
            ledger);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Invalid, recorded.Outcome);
        Assert.Empty(ledger.Rows);
    }

    [Fact]
    public async Task Evidence_from_another_verification_round_costs_the_attribution_and_not_the_exposure()
    {
        var ledger = new InMemoryReuseFeedbackStore();
        var otherRound = TrialIdentities.Derive("synthetic", 1, "closed-round", 0);

        var recorded = await RecordAsync(
            Feedback(Comparative() with { Evidence = [Evidence(otherRound)] }),
            ledger);

        // Degrading, not fatal: the exposure is still a true fact about the run.
        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, recorded.Outcome);
        Assert.Equal(ReuseAttributionSource.None, recorded.AttributionSource);
        Assert.Equal(ExperienceReuseBenefit.Unknown, recorded.Benefit);
        Assert.Contains("VerificationRoundId", recorded.Reason!, StringComparison.Ordinal);
        Assert.Single(ledger.Rows);
    }

    [Fact]
    public async Task A_result_that_carries_no_evidence_costs_the_attribution_and_not_the_exposure()
    {
        var ledger = new InMemoryReuseFeedbackStore();

        var recorded = await RecordAsync(Feedback(Comparative() with { Evidence = [] }), ledger);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, recorded.Outcome);
        Assert.Equal(ReuseAttributionSource.None, recorded.AttributionSource);
        Assert.Single(ledger.Rows);
    }

    [Fact]
    public async Task An_attribution_of_Unknown_is_not_an_attribution()
    {
        var recorded = await RecordAsync(Feedback(Comparative() with { Benefit = ExperienceReuseBenefit.Unknown }));

        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, recorded.Outcome);
        Assert.Equal(ReuseAttributionSource.None, recorded.AttributionSource);
    }

    [Fact]
    public async Task The_reference_experiment_submits_no_comparative_result_and_no_human_assessment()
    {
        var result = await ExperimentFacts.ReferenceAsync();

        // Counted out of the ledger's own rows. An earlier version compared against a compile-time
        // literal that nothing incremented, so it could not fail and would have kept passing if a
        // later change started submitting comparative results.
        Assert.NotEmpty(result.LedgerRows);
        Assert.Equal(0, result.LedgerRows.Count(row => row.AttributionSource != ReuseAttributionSource.None));
        Assert.Equal(0, result.ComparativeResultsSubmitted);
        Assert.Equal(0, result.HumanAssessmentsSubmitted);

        // Read out of what the ledger path actually reported for each trial, not from the count above.
        foreach (var trial in result.Trials.Where(trial => trial.FeedbackId is not null))
        {
            Assert.Contains("attribution None", trial.FeedbackOutcome, StringComparison.Ordinal);
            Assert.Contains("benefit Unknown", trial.FeedbackOutcome, StringComparison.Ordinal);
        }

        // And every trial that made no submission says why, rather than being silently absent.
        Assert.All(
            result.Trials.Where(trial => trial.FeedbackId is null),
            trial => Assert.StartsWith("none:", trial.FeedbackOutcome, StringComparison.Ordinal));
    }

    private static ComparativeEvaluationResult Comparative() => new(
        EvaluatorId: "synthetic-comparative-evaluator",
        RunId: RunId,
        VerificationRoundId: RoundId,
        Benefit: ExperienceReuseBenefit.Improved,
        AttributedExperienceIds: [ExposedId],
        Evidence: [Evidence(RoundId)],
        Summary: "Synthetic evidence. Nothing observed here; this exists to exercise the validation rules.",
        EvaluatedAt: At);

    private static ExperienceRecord FinalizedRunRecord() => new(
        ExperienceId: ExperienceFinalizationService.ExperienceIdFor(RunId, TestScope),
        SourceRunId: RunId,
        Scope: TestScope,
        TaskId: "synthetic-task",
        TaskSummary: null,
        Attempts: [],
        Outcome: new Outcome(TaskVerificationStatus.Unknown, [], "synthetic", At),
        CompletionScore: 0,
        Reflection: null,
        Environment: new EnvironmentFingerprint("synthetic", "net10.0", "linux", null, new Dictionary<string, string>()),
        Provenance: new Provenance("synthetic", null, At, null) { ExposedTo = [new RunExposure(ExposedId, 0)] },
        Status: ExperienceStatus.Candidate,
        ReuseConfidence: 0,
        SupportingValidations: 0,
        Contradictions: 0,
        Revision: 0,
        CreatedAt: At,
        UpdatedAt: At)
    {
        ClosedRoundId = RoundId,
        Origin = ExperienceRecordOrigin.Finalized,
    };

    private static Evidence Evidence(Guid roundId) => new(
        EvidenceId: TrialIdentities.Derive("synthetic", 0, "evidence", 0),
        VerificationRoundId: roundId,
        ArtifactRevision: ReuseBaselineExperiment.ArtifactRevision,
        CheckId: ReuseBaselineExperiment.CheckId,
        Kind: "ToolExitCode",
        Result: CheckResult.Pass,
        Producer: "synthetic",
        Detail: null,
        CapturedAt: At);

    private static ExperienceReuseFeedback Feedback(ComparativeEvaluationResult comparative) => new(
        FeedbackId: TrialIdentities.Derive("synthetic", 0, "feedback", 0),
        RunId: RunId,
        Scope: TestScope,
        ExposedExperienceIds: [ExposedId],
        RunOutcome: TaskVerificationStatus.Verified,
        Measure: new ReuseMeasure("failed_attempts", 0d),
        ObservedAt: At,
        ClaimedBenefit: ExperienceReuseBenefit.Unknown,
        HumanAssessment: null,
        ComparativeEvaluation: comparative,
        TrialLabel: "memory-enabled");

    private static async Task<ExperienceReuseFeedbackResult> RecordAsync(
        ExperienceReuseFeedback feedback,
        InMemoryReuseFeedbackStore? ledger = null,
        bool finalizeRun = true)
    {
        var records = new InMemoryRecordStore();
        if (finalizeRun)
        {
            // The evaluated run, as finalization would have left it: a record derived from it in this
            // scope, carrying the round it closed. Without it the run is not one the library knows.
            var created = await records.CreateAsync(Authorization, FinalizedRunRecord(), CancellationToken.None);
            Assert.Equal(ExperienceStoreOutcome.Created, created.Outcome);
        }

        var services = new ServiceCollection();

        services.AddSingleton<IExperienceRecordStore>(records);
        services.AddSingleton<IExperienceCandidateSource>(new InMemoryCandidateSource(records));
        services.AddSingleton<IExperienceReuseFeedbackStore>(ledger ?? new InMemoryReuseFeedbackStore());
        services.AddAgentExperienceCore(
            new SanitizationOptions(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)),
            new CaptureLimits(4, 4, 100, 100));
        services.AddAgentExperienceReuseFeedback();
        services.AddAgentExperienceRetrieval(RetrievalPolicy.Default);

        await using var provider = services.BuildServiceProvider();

        return await provider.GetRequiredService<ExperienceReuseFeedbackService>()
            .RecordAsync(Authorization, feedback, CancellationToken.None);
    }
}

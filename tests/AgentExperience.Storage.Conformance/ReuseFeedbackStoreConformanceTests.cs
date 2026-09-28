using static AgentExperience.Storage.Conformance.ConformanceData;

namespace AgentExperience.Storage.Conformance;

/// <summary>
/// The behaviour every <see cref="IExperienceReuseFeedbackStore"/> must show, observed through the port alone: a
/// submission is stored and returned exactly as given; the feedback ID is the idempotency key, so an identical
/// resubmission is
/// <see cref="ExperienceReuseFeedbackStoreOutcome.AlreadyRecorded"/>, and the same ID with any different stored
/// field is <see cref="ExperienceReuseFeedbackStoreOutcome.Conflict"/> in any scope, returning the stored submission
/// only where the caller has authority over it; plus the documented refusals and unwrapped cancellation. A subclass
/// supplies the store through <see cref="CreateStore"/>.
/// </summary>
public abstract class ReuseFeedbackStoreConformanceTests
{
    private readonly Lazy<IExperienceReuseFeedbackStore> _store;

    protected ReuseFeedbackStoreConformanceTests()
    {
        _store = new Lazy<IExperienceReuseFeedbackStore>(CreateStore);
    }

    /// <summary>The store under test.</summary>
    protected IExperienceReuseFeedbackStore Store => _store.Value;

    /// <summary>Creates the store under test. Called at most once per test.</summary>
    protected abstract IExperienceReuseFeedbackStore CreateStore();

    /// <summary>
    /// Whether the store under test seals a submission's rationale at rest (crypto-shredding), which the contract
    /// leaves out. A sealing store may hand the stored submission back with its rationale sealed, and cannot compare
    /// a sealed rationale when deciding a replay, so for such a store the suite does not assert either.
    /// </summary>
    protected virtual bool SealsRationale => false;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_first_submission_is_Recorded_and_an_identical_one_AlreadyRecorded_both_returning_exactly_what_was_submitted(bool attributed)
    {
        var tenant = NewTenant();
        var feedback = attributed ? AttributedFeedback(Scope(tenant)) : Feedback(Scope(tenant), [Guid.NewGuid(), Guid.NewGuid()]);

        var first = await Store.RecordAsync(Authorize(tenant), feedback, CancellationToken.None);
        var replay = await Store.RecordAsync(Authorize(tenant), feedback, CancellationToken.None);
        var replayAgain = await Store.RecordAsync(Authorize(tenant), feedback, CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackStoreOutcome.Recorded, first.Outcome);
        Assert.Empty(first.Errors);
        Assert.Equal(Json(feedback), Json(first.Feedback));
        Assert.Equal(ExperienceReuseFeedbackStoreOutcome.AlreadyRecorded, replay.Outcome);
        AssertStoredAs(feedback, replay.Feedback);
        Assert.Equal(ExperienceReuseFeedbackStoreOutcome.AlreadyRecorded, replayAgain.Outcome);
    }

    public static TheoryData<string> Differences =>
    [
        "trial-label",
        "run",
        "run-outcome",
        "measure-kind",
        "measure-value",
        "observed-at",
        "claimed-benefit",
        "benefit",
        "attribution-source",
        "evaluator",
        "round",
        "rationale",
        "attributed-at",
        "evidence-ids",
        "exposure-added",
        "exposure-attributed",
        "exposure-evidence-id",
        "scope",
    ];

    [Theory]
    [MemberData(nameof(Differences))]
    public async Task The_same_feedback_id_with_different_content_is_Conflict_returns_the_stored_submission_and_rewrites_nothing(string difference)
    {
        if (difference == "rationale" && SealsRationale)
        {
            return;
        }

        var tenant = NewTenant();
        var feedback = AttributedFeedback(Scope(tenant));
        Assert.Equal(
            ExperienceReuseFeedbackStoreOutcome.Recorded,
            (await Store.RecordAsync(Authorize(tenant), feedback, CancellationToken.None)).Outcome);

        var attributedExposure = feedback.Exposures[0];
        var plainExposure = feedback.Exposures[1];
        var diverged = difference switch
        {
            "trial-label" => feedback with { TrialLabel = "memory-disabled" },
            "run" => feedback with { RunId = Guid.NewGuid() },
            "run-outcome" => feedback with { RunOutcome = TaskVerificationStatus.Failed },
            "measure-kind" => feedback with { Measure = feedback.Measure with { Kind = "tool-calls" } },
            "measure-value" => feedback with { Measure = feedback.Measure with { Value = 0 } },
            "observed-at" => feedback with { ObservedAt = feedback.ObservedAt.AddSeconds(1) },
            "claimed-benefit" => feedback with { ClaimedBenefit = ExperienceReuseBenefit.Unknown },
            "benefit" => feedback with { Benefit = ExperienceReuseBenefit.Harmed },
            "attribution-source" => Feedback(feedback.Scope, [.. feedback.Exposures.Select(e => e.ExperienceId)], feedback.FeedbackId) with
            {
                RunId = feedback.RunId,
                ClaimedBenefit = feedback.ClaimedBenefit,
            },
            "evaluator" => feedback with { EvaluatorId = "another-evaluator" },
            "round" => feedback with { VerificationRoundId = Guid.NewGuid() },
            "rationale" => feedback with { Rationale = feedback.Rationale + " (revised)" },
            "attributed-at" => feedback with { AttributedAt = feedback.AttributedAt!.Value.AddSeconds(1) },
            "evidence-ids" => feedback with { EvidenceIds = [.. feedback.EvidenceIds, Guid.NewGuid()] },
            "exposure-added" => feedback with
            {
                Exposures = [.. feedback.Exposures.Append(new ExperienceReuseExposure(Guid.NewGuid(), false, null)).OrderBy(e => e.ExperienceId)],
            },
            "exposure-attributed" => feedback with
            {
                Exposures = [attributedExposure, plainExposure with { Attributed = true, EvidenceId = Guid.NewGuid() }],
            },
            "exposure-evidence-id" => feedback with
            {
                Exposures = [attributedExposure with { EvidenceId = Guid.NewGuid() }, plainExposure],
            },
            _ => feedback with { Scope = Scope(tenant, project: "project-2") },
        };

        var conflict = await Store.RecordAsync(Authorize(tenant), diverged, CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackStoreOutcome.Conflict, conflict.Outcome);

        // The caller may see the stored submission's own scope, so it is handed back -- the stored one, not its own.
        AssertStoredAs(feedback, conflict.Feedback);

        // Nothing was rewritten: the original still replays as itself.
        var replay = await Store.RecordAsync(Authorize(tenant), feedback, CancellationToken.None);
        Assert.Equal(ExperienceReuseFeedbackStoreOutcome.AlreadyRecorded, replay.Outcome);
        AssertStoredAs(feedback, replay.Feedback);
    }

    [Fact]
    public async Task A_feedback_id_stored_in_another_tenant_is_Conflict_and_reveals_nothing()
    {
        var tenant = NewTenant();
        var foreignTenant = NewTenant();
        var feedback = Feedback(Scope(tenant), [Guid.NewGuid()]);
        Assert.Equal(
            ExperienceReuseFeedbackStoreOutcome.Recorded,
            (await Store.RecordAsync(Authorize(tenant), feedback, CancellationToken.None)).Outcome);

        var foreign = await Store.RecordAsync(
            Authorize(foreignTenant), feedback with { Scope = Scope(foreignTenant) }, CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackStoreOutcome.Conflict, foreign.Outcome);
        Assert.Null(foreign.Feedback);
    }

    [Fact]
    public async Task A_submission_outside_the_authorization_is_Denied_and_records_nothing()
    {
        var tenant = NewTenant();
        var feedback = Feedback(Scope(tenant), [Guid.NewGuid()]);

        var denied = await Store.RecordAsync(Authorize(NewTenant()), feedback, CancellationToken.None);
        var afterwards = await Store.RecordAsync(Authorize(tenant), feedback, CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackStoreOutcome.Denied, denied.Outcome);
        Assert.Null(denied.Feedback);
        Assert.Equal(ExperienceReuseFeedbackStoreOutcome.Recorded, afterwards.Outcome);
    }

    [Theory]
    [InlineData("measure-not-finite")]
    [InlineData("measure-kind-blank")]
    [InlineData("benefit-without-attribution")]
    public async Task A_malformed_submission_is_Invalid_and_records_nothing(string malformation)
    {
        var tenant = NewTenant();
        var valid = Feedback(Scope(tenant), [Guid.NewGuid()]);
        var feedback = malformation switch
        {
            "measure-not-finite" => valid with { Measure = new ReuseMeasure("task-success", double.NaN) },
            "measure-kind-blank" => valid with { Measure = new ReuseMeasure(" ", 1) },
            _ => valid with { Benefit = ExperienceReuseBenefit.Improved },
        };

        var result = await Store.RecordAsync(Authorize(tenant), feedback, CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackStoreOutcome.Invalid, result.Outcome);
        Assert.NotEmpty(result.Errors);
        Assert.Null(result.Feedback);

        // Nothing was stored under that ID: the valid submission is still a first submission.
        Assert.Equal(
            ExperienceReuseFeedbackStoreOutcome.Recorded,
            (await Store.RecordAsync(Authorize(tenant), valid, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_submission_with_a_cancelled_token_throws_an_unwrapped_OperationCanceledException_and_records_nothing()
    {
        var tenant = NewTenant();
        var feedback = Feedback(Scope(tenant), [Guid.NewGuid()]);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.RecordAsync(Authorize(tenant), feedback, cancelled.Token));

        Assert.Equal(
            ExperienceReuseFeedbackStoreOutcome.Recorded,
            (await Store.RecordAsync(Authorize(tenant), feedback, CancellationToken.None)).Outcome);
    }

    /// <summary>
    /// Asserts <paramref name="returned"/> is exactly <paramref name="submitted"/>, apart from a rationale a sealing
    /// store (see <see cref="SealsRationale"/>) may hand back sealed.
    /// </summary>
    private void AssertStoredAs(RecordedExperienceReuseFeedback submitted, RecordedExperienceReuseFeedback? returned)
    {
        var stored = Assert.IsType<RecordedExperienceReuseFeedback>(returned);
        Assert.Equal(Json(SealsRationale ? submitted with { Rationale = stored.Rationale } : submitted), Json(stored));
    }
}

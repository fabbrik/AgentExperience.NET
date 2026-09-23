using AgentExperience.Abstractions;
using AgentExperience.ReuseBaseline.Experiment;
using AgentExperience.ReuseBaseline.Harness;

namespace AgentExperience.ReuseBaseline.Tests;

/// <summary>
/// What the reference experiment actually wrote to the reuse-feedback ledger, read back out of the
/// ledger.
/// </summary>
/// <remarks>
/// <para>
/// Nothing used to read these rows. Labelling every one of them with the wrong condition passed the
/// whole suite, while the pre-registration and the report both claimed <c>TrialLabel</c> carries the
/// condition -- and the comparative-result count was compared against a literal nothing increments,
/// so it could not fail and would have kept passing if a later change started submitting them.
/// </para>
/// <para>
/// Every assertion here starts from <see cref="ExperimentResult.LedgerRows"/>, which is the ledger's
/// own account rather than the harness's tally of what it believes it sent.
/// </para>
/// </remarks>
public class LedgerTests
{
    [Fact]
    public async Task Every_row_carries_its_trials_condition_as_its_TrialLabel()
    {
        var result = await ExperimentFacts.ReferenceAsync();
        var design = result.Preregistration.Design;
        var byRunId = result.Trials.ToDictionary(trial => trial.RunId);

        Assert.NotEmpty(result.LedgerRows);

        foreach (var row in result.LedgerRows)
        {
            var trial = byRunId[row.RunId];

            // The label is the condition's, and it is the label the pre-registration fixed for it --
            // not merely some non-empty string, and not the other condition's.
            Assert.Equal(design.LabelFor(trial.Condition), row.TrialLabel);
            Assert.Equal(design.Conditions.MemoryEnabled, row.TrialLabel);
            Assert.NotEqual(design.Conditions.MemoryDisabled, row.TrialLabel);
            Assert.Equal(TrialCondition.MemoryEnabled, trial.Condition);
        }

        // And every label the ledger holds is the memory-enabled one, because a memory-disabled
        // trial has no exposure to submit.
        Assert.Equal([design.Conditions.MemoryEnabled], result.LedgerRows.Select(row => row.TrialLabel).Distinct());
    }

    [Fact]
    public async Task Every_row_carries_the_primary_metric_and_its_trials_own_value_for_it()
    {
        var result = await ExperimentFacts.ReferenceAsync();
        var design = result.Preregistration.Design;
        var byRunId = result.Trials.ToDictionary(trial => trial.RunId);

        foreach (var row in result.LedgerRows)
        {
            var trial = byRunId[row.RunId];

            Assert.Equal(design.PrimaryMetric, row.Measure.Kind);
            Assert.Equal((double)trial.Metrics.FailedAttempts!.Value, row.Measure.Value);

            // The exposures are the trial's own, in the same set.
            Assert.Equal(
                [.. trial.ExposedExperienceIds.Order()],
                [.. row.Exposures.Select(exposure => exposure.ExperienceId).Order()]);

            Assert.Equal(trial.FeedbackId, row.FeedbackId);
            Assert.Equal(ExperienceReuseBenefit.Unknown, row.ClaimedBenefit);
        }
    }

    [Fact]
    public async Task The_rows_are_one_per_exposed_trial_and_none_for_the_rest()
    {
        var result = await ExperimentFacts.ReferenceAsync();

        var exposedTrials = result.Trials.Where(trial => trial.ExposedExperienceIds.Count > 0).ToList();

        Assert.Equal(6, exposedTrials.Count);
        Assert.Equal(exposedTrials.Count, result.LedgerRows.Count);
        Assert.Equal(result.LedgerRows.Count, result.FeedbackSubmissions);
        Assert.Equal(
            [.. exposedTrials.Select(trial => trial.RunId).Order()],
            [.. result.LedgerRows.Select(row => row.RunId).Order()]);
    }

    /// <summary>
    /// No row carries an attribution of any kind, counted from the rows rather than compared against
    /// a literal.
    /// </summary>
    [Fact]
    public async Task No_row_carries_a_comparative_result_or_a_human_assessment()
    {
        foreach (var result in new[]
        {
            await ExperimentFacts.ReferenceAsync(),
            await ExperimentFacts.NegativeControlAsync(),
            await ExperimentFacts.WrongStrategyAsync(),
        })
        {
            Assert.NotEmpty(result.LedgerRows);

            Assert.All(result.LedgerRows, row =>
            {
                Assert.Equal(ReuseAttributionSource.None, row.AttributionSource);
                Assert.Equal(ExperienceReuseBenefit.Unknown, row.Benefit);
                Assert.Null(row.EvaluatorId);
                Assert.Null(row.AssessmentId);
                Assert.Null(row.ReviewerIdentity);
                Assert.Empty(row.EvidenceIds);
            });

            Assert.Equal(0, result.ComparativeResultsSubmitted);
            Assert.Equal(0, result.HumanAssessmentsSubmitted);
        }
    }

    /// <summary>
    /// And the counter is one that can move: a ledger holding a comparative row reports it.
    /// </summary>
    /// <remarks>
    /// The count is a property over the stored rows, so this exercises the same expression the
    /// report prints. Without it, "0" would be a number no test could distinguish from a constant.
    /// </remarks>
    [Fact]
    public async Task The_comparative_count_is_one_that_could_have_been_non_zero()
    {
        var result = await ExperimentFacts.ReferenceAsync();
        var row = result.LedgerRows[0];

        var withAttribution = result with
        {
            LedgerRows =
            [
                row with
                {
                    AttributionSource = ReuseAttributionSource.ComparativeEvaluation,
                    EvaluatorId = "synthetic-comparative-evaluator",
                },
                .. result.LedgerRows.Skip(1),
            ],
        };

        Assert.Equal(1, withAttribution.ComparativeResultsSubmitted);

        var withAssessment = result with
        {
            LedgerRows =
            [
                row with
                {
                    AttributionSource = ReuseAttributionSource.HumanAssessment,
                    AssessmentId = Guid.NewGuid(),
                },
                .. result.LedgerRows.Skip(1),
            ],
        };

        Assert.Equal(1, withAssessment.HumanAssessmentsSubmitted);
    }
}

using AgentExperience.ReuseBaseline.Experiment;
using AgentExperience.ReuseBaseline.Harness;

namespace AgentExperience.ReuseBaseline.Tests;

/// <summary>
/// No two trials share a run identifier, a closed verification round, or a feedback identifier.
/// </summary>
/// <remarks>
/// This is why the 4.2 sample's counter-based identifier source is not reused. It restarts at one
/// for every container it is registered in, and the harness builds one container per trial -- so
/// twelve trials would take twelve identical run identifiers and the experiment would be one trial
/// replayed eleven times with the same numbers.
/// </remarks>
public class TrialIdentityTests
{
    [Fact]
    public async Task Every_trial_has_its_own_run_round_and_feedback_identifier()
    {
        var result = await ExperimentFacts.ReferenceAsync();

        Assert.Equal(result.Trials.Count, result.Trials.Select(trial => trial.RunId).Distinct().Count());
        Assert.Equal(result.Trials.Count, result.Trials.Select(trial => trial.VerificationRoundId).Distinct().Count());

        var feedbackIds = result.Trials.Where(trial => trial.FeedbackId is not null).Select(trial => trial.FeedbackId!.Value).ToList();
        Assert.NotEmpty(feedbackIds);
        Assert.Equal(feedbackIds.Count, feedbackIds.Distinct().Count());

        // A run identifier is never a round identifier, either.
        var everything = result.Trials.SelectMany(trial => new[] { trial.RunId, trial.VerificationRoundId }).ToList();
        Assert.Equal(everything.Count, everything.Distinct().Count());
        Assert.DoesNotContain(Guid.Empty, everything);
    }

    [Fact]
    public void Identifiers_are_derived_so_a_collision_is_impossible_by_construction()
    {
        var ids = new List<Guid>();

        foreach (var arm in new[] { "reference", "negative-control" })
        {
            for (var index = 0; index < 64; index++)
            {
                var trial = new TrialIdentities(arm, index);
                ids.Add(trial.RunId);
                ids.Add(trial.OpenRoundId);
                ids.Add(trial.ClosedRoundId);
                ids.Add(trial.FeedbackId);

                for (var sequence = 0; sequence < 16; sequence++)
                {
                    ids.Add(trial.Next());
                }
            }
        }

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.NotEqual(Guid.Empty, id));

        // Well-formed version-4 variant-1 GUIDs, so they are valid identifiers everywhere they land.
        Assert.All(ids, id => Assert.Equal('4', id.ToString("D")[14]));
    }

    [Fact]
    public void The_same_arm_and_index_derive_the_same_identifiers_so_the_run_is_reproducible()
    {
        Assert.Equal(new TrialIdentities("reference", 5).RunId, new TrialIdentities("reference", 5).RunId);
        Assert.NotEqual(new TrialIdentities("reference", 5).RunId, new TrialIdentities("negative-control", 5).RunId);
        Assert.NotEqual(new TrialIdentities("reference", 5).RunId, new TrialIdentities("reference", 6).RunId);
    }

    [Fact]
    public async Task The_two_arms_share_no_identifier()
    {
        var reference = await ExperimentFacts.ReferenceAsync();
        var control = await ExperimentFacts.NegativeControlAsync();

        Assert.Empty(reference.Trials.Select(trial => trial.RunId)
            .Intersect(control.Trials.Select(trial => trial.RunId)));
    }
}

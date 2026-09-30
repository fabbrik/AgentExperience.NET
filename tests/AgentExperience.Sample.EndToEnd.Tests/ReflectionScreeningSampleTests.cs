using System.Text.Json;
using AgentExperience.Abstractions;
using AgentExperience.Core.Reflections;
using AgentExperience.Core.Verification;

namespace AgentExperience.Sample.EndToEnd.Tests;

/// <summary>
/// Story 14.1: finalization screens every reflection, the default reflector's included, and the default
/// reflector's output must pass unchanged. Over the sample's own run this recomputes what the default
/// reflector wrote and pins the stored reflection to it, byte for byte.
/// </summary>
public class ReflectionScreeningSampleTests
{
    [Fact]
    public async Task The_stored_reflection_is_the_default_reflectors_output_byte_for_byte()
    {
        var execution = await SampleFacts.RunAsync();
        var run = execution.CapturedRun();
        var stored = execution.PersistedRecord();
        var reflection = stored.Reflection ?? throw new InvalidOperationException("The sample stored no reflection.");

        // The evaluation finalization computed, recomputed from what the record keeps of it: the evidence
        // of the closed round, the checks it satisfied, and the finalization time.
        var evidence = stored.Outcome.Evidence;
        var revision = Assert.Single(evidence.Select(item => item.ArtifactRevision).Distinct());
        var evaluation = VerificationAggregator.Aggregate(
            run.RunId,
            evidence,
            [.. evidence.Select(item => new RequiredCheck(item.CheckId, item.Kind)).Distinct()],
            new ClosedVerificationRound(stored.ClosedRoundId!.Value, revision),
            revision,
            stored.CreatedAt);
        Assert.Equal(stored.Outcome.Status, evaluation.Outcome.Status);

        var expected = await new DefaultExperienceReflector().ReflectAsync(
            new ReflectionRequest(run, evaluation, reflection.ReflectionId, reflection.CreatedAt));

        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(expected), JsonSerializer.SerializeToUtf8Bytes(reflection));
    }
}

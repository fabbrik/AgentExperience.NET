using System.Diagnostics;
using AgentExperience.Core.Retrieval;

namespace AgentExperience.Core.Tests.Diagnostics;

/// <summary>
/// A throwing <see cref="IEnvironmentCompatibilityScorer"/> is a returned <see cref="RetrievalOutcome.Failed"/>
/// on the retrieve span, and neither the preferred attributes nor the record's environment reach telemetry.
/// </summary>
[Collection(TelemetryCollection.Name)]
public class EnvironmentScorerTelemetryTests
{
    private const string Secret = "preferred-value-that-must-not-leak";

    [Fact]
    public async Task A_throwing_scorer_is_a_Failed_span_outcome_with_no_attribute_content_anywhere()
    {
        using var probe = TelemetryProbe.All();

        var retrieval = new ExperienceRetrievalService(
            new OneRecordSource(),
            RetrievalPolicy.Default,
            RankingWeights.Default,
            TimeProvider.System,
            embeddingIndex: null,
            embeddingGenerator: null,
            new ThrowingScorer());

        var result = await retrieval.RetrieveAsync(
            new RetrieveExperienceRequest(ExperienceLoop.Authorization, ExperienceLoop.Scope, "a task")
            {
                PreferredEnvironmentAttributes = new Dictionary<string, string> { ["secret-key"] = Secret },
            });

        Assert.Equal(RetrievalOutcome.Failed, result.Outcome);

        var span = Assert.Single(probe.LibraryActivities, a => a.OperationName == "agentexperience.retrieve");
        Assert.Equal(nameof(RetrievalOutcome.Failed), span.GetTagItem("agentexperience.outcome"));
        Assert.Equal(ActivityStatusCode.Ok, span.Status); // a returned decision, not a thrown operation

        var spanValues = probe.LibraryActivities
            .SelectMany(activity => activity.TagObjects)
            .Select(tag => $"{tag.Key}={tag.Value}")
            .ToArray();
        Assert.DoesNotContain(spanValues, value => value.Contains(Secret, StringComparison.Ordinal) || value.Contains("secret-key", StringComparison.Ordinal));
        Assert.DoesNotContain(probe.EveryMeasurementTagValue, value => value.Contains(Secret, StringComparison.Ordinal));
    }

    private sealed class ThrowingScorer : IEnvironmentCompatibilityScorer
    {
        public double Score(EnvironmentFingerprint recordEnvironment, IReadOnlyDictionary<string, string> preferredAttributes) =>
            throw new InvalidOperationException(Secret);
    }

    private sealed class OneRecordSource : IExperienceCandidateSource
    {
        public Task<ExperienceCandidateSearchResult> SearchAsync(
            AuthorizationContext authorization,
            ExperienceCandidateQuery query,
            CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            var record = new ExperienceRecord(
                Guid.NewGuid(), Guid.NewGuid(), query.Scope, "task", "summary", [],
                new Outcome(TaskVerificationStatus.Verified, [], null, now), 1, null,
                new EnvironmentFingerprint("host", "10.0.0", "linux-x64", null, new Dictionary<string, string> { ["secret-key"] = Secret }),
                new Provenance("tests", null, now, null),
                ExperienceStatus.Validated, 0.9, 1, 0, 1, now, now);
            return Task.FromResult(new ExperienceCandidateSearchResult(ExperienceStoreOutcome.Found, [new ExperienceCandidate(record, 1d)], []));
        }
    }
}

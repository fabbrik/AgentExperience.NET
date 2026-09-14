using System.Text.Json;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Proves <see cref="IExperienceReflector"/> is a stable, replaceable port (AC4): a test-only
/// alternate implementation with entirely different wording satisfies the same structured-content
/// contract as <see cref="DefaultExperienceReflector"/> for the same run and evaluation. Also proves
/// a <see cref="Reflection"/> serializes to JSON with every structured section and no reasoning or
/// chain-of-thought field (AC5).
/// </summary>
public class ReflectorContractTests
{
    private static readonly string[] ForbiddenPropertyNameFragments = ["Reasoning", "Thought", "ChainOfThought", "Confidence"];

    private static readonly string[] ExpectedJsonSections =
    [
        nameof(Reflection.ReflectionId),
        nameof(Reflection.ExperienceRunId),
        nameof(Reflection.Lesson),
        nameof(Reflection.SuccessfulApproaches),
        nameof(Reflection.FailedApproaches),
        nameof(Reflection.Preconditions),
        nameof(Reflection.Warnings),
        nameof(Reflection.ReuseGuidance),
        nameof(Reflection.EvidenceIds),
        nameof(Reflection.VerificationStatus),
        nameof(Reflection.CompletionScore),
        nameof(Reflection.VerificationRuleVersion),
        nameof(Reflection.Producer),
        nameof(Reflection.CreatedAt),
    ];

    /// <summary>
    /// A deliberately different, test-only reflector: terse wording and no attempt descriptions
    /// beyond sequence numbers. It honors the contract clauses this test class exercises; it is not a
    /// complete implementation (e.g. it lists only the application version as a precondition and does
    /// not reject a run-outcome status mismatch).
    /// </summary>
    private sealed class TerseExperienceReflector : IExperienceReflector
    {
        public Task<Reflection> ReflectAsync(ReflectionRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Run is null || request.Evaluation is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.Run.ExecutionStatus is null)
            {
                throw new ArgumentException("Run in progress.", nameof(request));
            }

            cancellationToken.ThrowIfCancellationRequested();

            var outcome = request.Evaluation.Outcome;
            var verified = outcome.Status == TaskVerificationStatus.Verified;
            var ordered = request.Run.Attempts.OrderBy(a => a.SequenceNumber).ToList();
            var final = ordered.LastOrDefault();

            var warnings = new List<string>();
            var preconditions = new List<string>();
            if (!verified)
            {
                warnings.Add($"Not a validated procedure ({outcome.Status}).");
            }

            var appVersion = request.Run.Environment.ApplicationVersion;
            preconditions.Add($"Application version: {(string.IsNullOrWhiteSpace(appVersion) ? "unknown" : appVersion)}");
            if (string.IsNullOrWhiteSpace(appVersion))
            {
                warnings.Add("Application version unknown.");
            }

            return Task.FromResult(new Reflection(
                request.ReflectionId,
                request.Run.RunId,
                Lesson: $"{request.Run.TaskId}: {outcome.Status}",
                SuccessfulApproaches: verified && final is { Error: null } ? [$"#{final.SequenceNumber}"] : [],
                FailedApproaches: ordered.Where(a => a.Error is not null).Select(a => $"#{a.SequenceNumber}").ToArray(),
                Preconditions: preconditions,
                Warnings: warnings,
                ReuseGuidance: verified ? "Re-check before reuse." : "Not a validated procedure; do not reuse.",
                EvidenceIds: outcome.Evidence.Select(e => e.EvidenceId).Distinct().ToArray(),
                VerificationStatus: outcome.Status,
                CompletionScore: request.Evaluation.CompletionScore,
                VerificationRuleVersion: request.Evaluation.RuleVersion,
                Producer: "Tests.TerseExperienceReflector/0.1",
                CreatedAt: request.CreatedAt));
        }
    }

    private static IExperienceReflector CreateReflector(string implementation) => implementation switch
    {
        "default" => new DefaultExperienceReflector(),
        _ => new TerseExperienceReflector(),
    };

    private static VerificationResult CreateEvaluation(string kind) => kind switch
    {
        "verified" => ReflectionFixtures.Verified(),
        "failed" => ReflectionFixtures.Failed(),
        _ => ReflectionFixtures.Unknown(),
    };

    [Theory]
    [InlineData("default", "verified")]
    [InlineData("default", "failed")]
    [InlineData("default", "unknown")]
    [InlineData("alternate", "verified")]
    [InlineData("alternate", "failed")]
    [InlineData("alternate", "unknown")]
    public async Task Any_implementation_of_the_port_satisfies_the_structured_content_contract(string implementation, string evaluationKind)
    {
        IExperienceReflector reflector = CreateReflector(implementation);
        var evaluation = CreateEvaluation(evaluationKind);
        var run = ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts(), ReflectionFixtures.Environment(applicationVersion: null));
        var request = ReflectionFixtures.Request(run, evaluation);

        var reflection = await reflector.ReflectAsync(request);

        // Identity and time come from the caller.
        Assert.Equal(request.ReflectionId, reflection.ReflectionId);
        Assert.Equal(request.CreatedAt, reflection.CreatedAt);
        Assert.Equal(run.RunId, reflection.ExperienceRunId);

        // The evaluation basis is copied exactly.
        Assert.Equal(evaluation.Outcome.Evidence.Select(e => e.EvidenceId).Distinct(), reflection.EvidenceIds);
        Assert.Equal(evaluation.Outcome.Status, reflection.VerificationStatus);
        Assert.Equal(evaluation.CompletionScore, reflection.CompletionScore);
        Assert.Equal(evaluation.RuleVersion, reflection.VerificationRuleVersion);
        Assert.False(string.IsNullOrWhiteSpace(reflection.Producer));

        // Structured content.
        Assert.False(string.IsNullOrWhiteSpace(reflection.Lesson));
        Assert.NotNull(reflection.SuccessfulApproaches);
        Assert.NotNull(reflection.FailedApproaches);
        Assert.Contains(reflection.Preconditions, p => p == "Application version: unknown");
        Assert.NotEmpty(reflection.Warnings);
        Assert.Contains(reflection.Warnings, w => w.Contains("Application version", StringComparison.Ordinal) && w.Contains("unknown", StringComparison.Ordinal));

        if (reflection.VerificationStatus != TaskVerificationStatus.Verified)
        {
            Assert.Empty(reflection.SuccessfulApproaches);
            Assert.Contains(reflection.Warnings, w => w.Contains("not a validated procedure", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("not", reflection.ReuseGuidance, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("validated procedure", reflection.ReuseGuidance, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("default")]
    [InlineData("alternate")]
    public async Task Any_implementation_rejects_an_in_progress_run_and_honors_cancellation(string implementation)
    {
        var reflector = CreateReflector(implementation);
        var inProgress = ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts(), executionStatus: null);
        await Assert.ThrowsAsync<ArgumentException>(() => reflector.ReflectAsync(ReflectionFixtures.Request(inProgress, ReflectionFixtures.Verified())));
        await Assert.ThrowsAsync<ArgumentNullException>(() => reflector.ReflectAsync(null!));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var request = ReflectionFixtures.Request(ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts()), ReflectionFixtures.Verified());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reflector.ReflectAsync(request, cts.Token));
    }

    [Fact]
    public async Task A_reflection_round_trips_through_System_Text_Json_with_every_section_present_and_no_reasoning_field()
    {
        var attempts = new[]
        {
            ReflectionFixtures.Attempt(0, null, "compile error"),
            ReflectionFixtures.Attempt(1, "superseded", null),
            ReflectionFixtures.Attempt(2, "green", null),
        };
        var run = ReflectionFixtures.Run(attempts, ReflectionFixtures.Environment(applicationVersion: null));
        var reflection = await new DefaultExperienceReflector().ReflectAsync(ReflectionFixtures.Request(run, ReflectionFixtures.Verified()));

        // Every section is populated, so the round trip exercises real content in each.
        Assert.NotEmpty(reflection.SuccessfulApproaches);
        Assert.NotEmpty(reflection.FailedApproaches);
        Assert.NotEmpty(reflection.Preconditions);
        Assert.NotEmpty(reflection.Warnings);
        Assert.NotEmpty(reflection.EvidenceIds);
        Assert.NotNull(reflection.ReuseGuidance);

        var json = JsonSerializer.Serialize(reflection);
        var roundTripped = JsonSerializer.Deserialize<Reflection>(json);
        Assert.NotNull(roundTripped);
        Assert.Equal(json, JsonSerializer.Serialize(roundTripped));
        Assert.Equal(reflection.SuccessfulApproaches, roundTripped.SuccessfulApproaches);
        Assert.Equal(reflection.EvidenceIds, roundTripped.EvidenceIds);

        using var document = JsonDocument.Parse(json);
        var propertyNames = document.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(ExpectedJsonSections.Order(StringComparer.Ordinal), propertyNames.Order(StringComparer.Ordinal));

        foreach (var name in propertyNames.Concat(typeof(Reflection).GetProperties().Select(p => p.Name)))
        {
            foreach (var forbidden in ForbiddenPropertyNameFragments)
            {
                Assert.False(
                    name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"Reflection exposes '{name}', which matches forbidden reasoning field fragment '{forbidden}'.");
            }
        }
    }
}

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
/// Story 2.3, the end-to-end proof that the loop closes: a first invocation is captured and
/// finalized into a durable, Validated Experience Record, and a second invocation of the same task
/// receives that record's own lesson as a Historical Reference.
/// </summary>
/// <remarks>
/// Everything between the two invocations is the real implementation -- capture, sanitization,
/// verification, reflection, lifecycle, finalization, eligibility, ranking, the final re-check, and
/// the payload writer. Only the record store and the search index are in-memory doubles, and only
/// because the database-backed versions of both are proven in the PostgreSQL test projects. The
/// explicit <c>Index</c> call between the two runs stands in for the storage adapter's own indexing.
/// </remarks>
public class ExperienceLoopClosureTests
{
    private const string TaskId = "triage-refund";
    private const string ArtifactRevision = "rev-1";

    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");
    private static readonly AuthorizationContext Authorization = new("tenant-1", "host", ["experience:read", "experience:write"], DateTimeOffset.UnixEpoch);
    private static readonly ClosedVerificationRound Round = new(Guid.Parse("66666666-0000-0000-0000-000000000001"), ArtifactRevision);

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
    public async Task A_finalized_run_becomes_the_next_invocations_Historical_Reference()
    {
        var world = new FakeExperienceWorld();
        var capture = new InMemoryExperienceCaptureService(new DefaultSanitizer(Sanitization), new CaptureLimits(10, 50, 10_000, 10_000));
        var finalization = new ExperienceFinalizationService(capture, new DefaultExperienceReflector(), world, new ExperienceLifecycleService(world));

        // ---- Invocation 1: captured, then finalized into a durable Validated record. --------------
        var finalized = new List<FinalizeExperienceResult>();
        var failures = new List<ExperienceCaptureFailure>();
        var firstAgent = new ChatClientAgent(new RecordingChatClient(), new ChatClientAgentOptions())
            .AsBuilder()
            .UseExperienceCapture(capture, new ExperienceCaptureOptions
            {
                ResolveRun = _ => new ExperienceRunDescriptor(TaskId, TestScope, "Triage a refund ticket"),
                CaptureToolCalls = false,
                FinalizationService = finalization,
                ResolveFinalization = context => new FinalizeExperienceRequest(
                    RunId: context.Run.RunId,
                    Authorization: Authorization,
                    ClosedRound: Round,
                    RequiredChecks: [new RequiredCheck("tests", "TestResult")],
                    Evidence:
                    [
                        new Evidence(
                            EvidenceId: Guid.Parse("77777777-0000-0000-0000-000000000001"),
                            VerificationRoundId: Round.RoundId,
                            ArtifactRevision: ArtifactRevision,
                            CheckId: "tests",
                            Kind: "TestResult",
                            Result: CheckResult.Pass,
                            Producer: "ci",
                            Detail: "raw-evidence-detail-must-never-be-injected",
                            CapturedAt: InjectionRecords.Now),
                    ],
                    CurrentArtifactRevision: ArtifactRevision,
                    StorageDecision: StorageDecision.Permit,
                    FinalizedAt: InjectionRecords.Now),
                OnRunFinalized = finalized.Add,
                OnCaptureFailure = failures.Add,
            })
            .Build();

        await firstAgent.RunAsync("refund ticket stuck on a lock");

        Assert.Empty(failures);
        var outcome = Assert.Single(finalized);
        Assert.Equal(FinalizationOutcome.Validated, outcome.Outcome);
        var record = world.Stored[outcome.ExperienceId!.Value];
        Assert.Equal(ExperienceStatus.Validated, record.Status);
        Assert.NotNull(record.Reflection);

        // The storage adapter would index the committed record here; the double is told to.
        world.Index(record);

        // ---- Invocation 2: the same task, a fresh agent, and the lesson comes back. ---------------
        var injections = new List<ExperienceInjectionResult>();
        var model = new RecordingChatClient();
        var secondAgent = new ChatClientAgent(model, new ChatClientAgentOptions
        {
            AIContextProviders =
            [
                new ExperienceContextProvider(
                    new ExperienceRetrievalService(world, RetrievalPolicy.Default, RankingWeights.Default, new FrozenTimeProvider(InjectionRecords.Now)),
                    world,
                    new ExperienceInjectionOptions
                    {
                        ResolveRequest = _ => new RetrieveExperienceRequest(Authorization, TestScope, TaskId, CorrelationId: "loop"),
                        OnContextInjected = injections.Add,
                    }),
            ],
        });

        var response = await secondAgent.RunAsync("another refund ticket stuck on a lock");

        Assert.Equal("Hello, world", response.Text);

        var injection = Assert.Single(injections);
        Assert.Equal(InjectionOutcome.Injected, injection.Outcome);
        Assert.Equal([record.ExperienceId], injection.InjectedExperienceIds);

        var injected = Assert.Single(
            model.LastMessages!,
            m => m.AdditionalProperties?.ContainsKey(ExperienceContextProvider.HistoricalReferenceKey) == true);

        // The record's own lesson, its source, its confidence, and its applicability -- nothing raw.
        Assert.Contains(record.Reflection!.Lesson, injected.Text, StringComparison.Ordinal);
        Assert.Contains($"Task '{TaskId}' verified", injected.Text, StringComparison.Ordinal);
        Assert.Contains($"Source: experience {record.ExperienceId:D}", injected.Text, StringComparison.Ordinal);
        Assert.Contains($"source run {record.SourceRunId:D}", injected.Text, StringComparison.Ordinal);
        Assert.Contains("Confidence: 0.667 (status Validated)", injected.Text, StringComparison.Ordinal);
        Assert.Contains("Applicability (as ranked at retrieval): score ", injected.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-evidence-detail-must-never-be-injected", injected.Text, StringComparison.Ordinal);

        // Reference material in the user role, marked but not trusted -- a System-role block would
        // read as a host instruction, which is exactly what this is not.
        Assert.Equal(ChatRole.User, injected.Role);
        Assert.Equal(true, injected.AdditionalProperties![ExperienceContextProvider.HistoricalReferenceKey]);
        Assert.DoesNotContain(model.LastMessages!, m => m.Role == ChatRole.System);
    }
}

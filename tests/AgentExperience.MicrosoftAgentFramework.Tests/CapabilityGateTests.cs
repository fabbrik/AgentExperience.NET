using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// Story 10.4: the capability gate on injection. One test per I/O matrix row, each through a real
/// <see cref="ChatClientAgent"/> and a real <see cref="ExperienceRetrievalService"/> over the fake world,
/// plus the acceptance criterion that a gated record is never injected nor recorded as a run exposure. A gated
/// borrowed record still has a grant access row: the re-read before the gate is the store's disclosure.
/// </summary>
public class CapabilityGateTests
{
    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");
    private static readonly AuthorizationContext Authorization = new("tenant-1", "host", ["experience:read"], DateTimeOffset.UnixEpoch);

    // ---- Not configured -------------------------------------------------------------------------------

    [Fact]
    public async Task With_no_receiving_agent_declared_the_block_and_the_omissions_are_unchanged()
    {
        var plain = new Harness();
        var declaredNull = new Harness { ReceivingAgent = null };
        foreach (var harness in new[] { plain, declaredNull })
        {
            harness.World.Publish(Verified(InjectionRecords.Id(1), "read_ledger", "delete_everything"), relevance: 1d);
            harness.World.Publish(Verified(InjectionRecords.Id(2), "read_ledger"), relevance: 0.5d);
            await harness.Agent().RunAsync("refund ticket stuck on a lock");
        }

        Assert.NotNull(plain.InjectedText());
        Assert.Equal(plain.InjectedText(), declaredNull.InjectedText());
        Assert.Equal(plain.Last.InjectedExperienceIds, declaredNull.Last.InjectedExperienceIds);
        Assert.Equal(plain.Last.Omitted, declaredNull.Last.Omitted);
        Assert.Equal(plain.Last.PayloadBytes, declaredNull.Last.PayloadBytes);
        Assert.Equal([InjectionRecords.Id(1), InjectionRecords.Id(2)], plain.Last.InjectedExperienceIds);
    }

    // ---- The tool check ------------------------------------------------------------------------------

    [Fact]
    public async Task A_record_whose_approach_tools_are_all_available_is_injected()
    {
        var harness = new Harness { ReceivingAgent = new ReceivingAgentCapabilities { AvailableTools = Set("a", "b", "c") } };
        harness.World.Publish(Verified(InjectionRecords.Id(1), "a", "b"));

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        Assert.Equal(InjectionOutcome.Injected, harness.Last.Outcome);
        Assert.Equal([InjectionRecords.Id(1)], harness.Last.InjectedExperienceIds);
        Assert.Empty(harness.Last.Omitted);
        Assert.Contains("called these tools, in order: a -> b.", harness.InjectedText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_record_whose_approach_calls_a_missing_tool_is_omitted_as_tool_unavailable_and_names_no_tool()
    {
        var harness = new Harness { ReceivingAgent = new ReceivingAgentCapabilities { AvailableTools = Set("a", "b") } };
        harness.World.Publish(Verified(InjectionRecords.Id(1), "a", "d"), relevance: 1d);
        harness.World.Publish(Verified(InjectionRecords.Id(2), "b"), relevance: 0.5d);

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        Assert.Equal([InjectionRecords.Id(2)], harness.Last.InjectedExperienceIds);
        var omission = Assert.Single(harness.Last.Omitted);
        Assert.Equal(new OmittedExperience(InjectionRecords.Id(1), InjectionOmissionReason.ToolUnavailable), omission);
        Assert.Null(omission.Detail);
        Assert.DoesNotContain(InjectionRecords.Id(1).ToString("D"), harness.InjectedText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_names_compare_ordinally_whatever_comparer_the_host_set_uses()
    {
        var harness = new Harness
        {
            ReceivingAgent = new ReceivingAgentCapabilities { AvailableTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A" } },
        };
        harness.World.Publish(Verified(InjectionRecords.Id(1), "a"));

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        Assert.Equal(InjectionOmissionReason.ToolUnavailable, Assert.Single(harness.Last.Omitted).Reason);

        // And the risk classes: a case-insensitive host dictionary declaring "A" does not declare "a", which
        // is therefore Critical.
        var risk = new Harness
        {
            ReceivingAgent = new ReceivingAgentCapabilities
            {
                MaxRiskClass = ToolRiskClass.High,
                ToolRiskClasses = new Dictionary<string, ToolRiskClass>(StringComparer.OrdinalIgnoreCase) { ["A"] = ToolRiskClass.Low },
            },
        };
        risk.World.Publish(Verified(InjectionRecords.Id(1), "a"));

        await risk.Agent().RunAsync("refund ticket stuck on a lock");

        Assert.Equal(InjectionOmissionReason.RiskClassExceeded, Assert.Single(risk.Last.Omitted).Reason);
    }

    [Fact]
    public async Task A_recorded_call_with_a_null_tool_name_is_unavailable_and_critical_never_a_failure()
    {
        var id = InjectionRecords.Id(1);

        var tools = new Harness { ReceivingAgent = new ReceivingAgentCapabilities { AvailableTools = Set("a") } };
        tools.World.Publish(Verified(id, "a", null!));
        await tools.Agent().RunAsync("refund ticket stuck on a lock");
        Assert.Equal(InjectionOutcome.NothingToInject, tools.Last.Outcome);
        Assert.Equal(new OmittedExperience(id, InjectionOmissionReason.ToolUnavailable), Assert.Single(tools.Last.Omitted));

        var high = new Harness { ReceivingAgent = new ReceivingAgentCapabilities { MaxRiskClass = ToolRiskClass.High } };
        high.World.Publish(Verified(id, [null!]));
        await high.Agent().RunAsync("refund ticket stuck on a lock");
        Assert.Equal(InjectionOutcome.NothingToInject, high.Last.Outcome);
        Assert.Equal(new OmittedExperience(id, InjectionOmissionReason.RiskClassExceeded), Assert.Single(high.Last.Omitted));

        var critical = new Harness { ReceivingAgent = new ReceivingAgentCapabilities { MaxRiskClass = ToolRiskClass.Critical } };
        critical.World.Publish(Verified(id, [null!]));
        await critical.Agent().RunAsync("refund ticket stuck on a lock");
        Assert.Equal(InjectionOutcome.Injected, critical.Last.Outcome);
        Assert.Equal([id], critical.Last.InjectedExperienceIds);
    }

    [Fact]
    public async Task The_gate_checks_only_the_tools_the_approach_line_would_carry_up_to_its_cap()
    {
        // The twenty-first call is past MaxApproachToolNames, so the line never names it and neither does the gate.
        var tools = Enumerable.Range(0, HistoricalReferenceWriter.MaxApproachToolNames).Select(i => $"t{i}").ToArray();
        var harness = new Harness { ReceivingAgent = new ReceivingAgentCapabilities { AvailableTools = Set(tools) } };
        harness.World.Publish(Verified(InjectionRecords.Id(1), [.. tools, "beyond_the_cap"]));

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        Assert.Equal([InjectionRecords.Id(1)], harness.Last.InjectedExperienceIds);
        Assert.Contains(HistoricalReferenceWriter.ApproachClamped, harness.InjectedText(), StringComparison.Ordinal);
    }

    // ---- The risk check ------------------------------------------------------------------------------

    [Fact]
    public async Task A_record_whose_tools_are_within_the_maximum_risk_class_is_injected()
    {
        var harness = new Harness
        {
            ReceivingAgent = new ReceivingAgentCapabilities
            {
                MaxRiskClass = ToolRiskClass.Medium,
                ToolRiskClasses = Classes(("a", ToolRiskClass.Low), ("b", ToolRiskClass.Medium)),
            },
        };
        harness.World.Publish(Verified(InjectionRecords.Id(1), "a", "b"));

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        Assert.Equal([InjectionRecords.Id(1)], harness.Last.InjectedExperienceIds);
        Assert.Empty(harness.Last.Omitted);
    }

    [Fact]
    public async Task A_record_with_a_tool_above_the_maximum_risk_class_is_omitted_as_risk_class_exceeded()
    {
        var harness = new Harness
        {
            ReceivingAgent = new ReceivingAgentCapabilities
            {
                MaxRiskClass = ToolRiskClass.Medium,
                ToolRiskClasses = Classes(("a", ToolRiskClass.Low), ("b", ToolRiskClass.High)),
            },
        };
        harness.World.Publish(Verified(InjectionRecords.Id(1), "a", "b"));

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        Assert.Equal(InjectionOutcome.NothingToInject, harness.Last.Outcome);
        Assert.Null(harness.InjectedText());
        Assert.Equal(new OmittedExperience(InjectionRecords.Id(1), InjectionOmissionReason.RiskClassExceeded), Assert.Single(harness.Last.Omitted));
    }

    [Fact]
    public async Task A_tool_with_no_declared_class_counts_as_critical()
    {
        var harness = new Harness
        {
            ReceivingAgent = new ReceivingAgentCapabilities
            {
                MaxRiskClass = ToolRiskClass.High,
                ToolRiskClasses = Classes(("b", ToolRiskClass.Low)),
            },
        };
        harness.World.Publish(Verified(InjectionRecords.Id(1), "a", "b"));

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        Assert.Equal(InjectionOmissionReason.RiskClassExceeded, Assert.Single(harness.Last.Omitted).Reason);
    }

    [Fact]
    public async Task A_record_failing_both_checks_is_omitted_as_tool_unavailable()
    {
        var harness = new Harness
        {
            ReceivingAgent = new ReceivingAgentCapabilities
            {
                AvailableTools = Set("a"),
                MaxRiskClass = ToolRiskClass.Low,
                ToolRiskClasses = Classes(("a", ToolRiskClass.Critical)),
            },
        };
        harness.World.Publish(Verified(InjectionRecords.Id(1), "a", "missing"));

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        Assert.Equal(InjectionOmissionReason.ToolUnavailable, Assert.Single(harness.Last.Omitted).Reason);
    }

    // ---- Nothing to gate ------------------------------------------------------------------------------

    [Theory]
    [InlineData(TaskVerificationStatus.Unknown)]
    [InlineData(TaskVerificationStatus.Failed)]
    public async Task A_record_with_no_approach_passes_the_gate(TaskVerificationStatus verification)
    {
        var harness = new Harness
        {
            ReceivingAgent = new ReceivingAgentCapabilities
            {
                AvailableTools = Set(),
                MaxRiskClass = ToolRiskClass.Low,
            },
        };
        harness.World.Publish(InjectionRecords.Record(InjectionRecords.Id(1), TestScope, attempts: Attempts("unlisted_tool"), verification: verification));

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        Assert.Equal([InjectionRecords.Id(1)], harness.Last.InjectedExperienceIds);
        Assert.DoesNotContain("Approach:", harness.InjectedText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_verified_record_whose_final_attempt_called_no_tool_passes_an_empty_tool_set()
    {
        var harness = new Harness { ReceivingAgent = new ReceivingAgentCapabilities { AvailableTools = Set(), MaxRiskClass = ToolRiskClass.Low } };
        harness.World.Publish(Verified(InjectionRecords.Id(1)));

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        Assert.Equal([InjectionRecords.Id(1)], harness.Last.InjectedExperienceIds);
        Assert.Contains(HistoricalReferenceWriter.NoToolsUsed, harness.InjectedText(), StringComparison.Ordinal);
    }

    // ---- One check set -------------------------------------------------------------------------------

    [Fact]
    public async Task With_only_a_maximum_risk_class_only_the_risk_check_runs()
    {
        // No AvailableTools: "b" is not declared available, and that alone would fail a tool check.
        var permitted = new Harness
        {
            ReceivingAgent = new ReceivingAgentCapabilities
            {
                MaxRiskClass = ToolRiskClass.Medium,
                ToolRiskClasses = Classes(("a", ToolRiskClass.Low), ("b", ToolRiskClass.Medium)),
            },
        };
        permitted.World.Publish(Verified(InjectionRecords.Id(1), "a", "b"));
        await permitted.Agent().RunAsync("refund ticket stuck on a lock");
        Assert.Equal([InjectionRecords.Id(1)], permitted.Last.InjectedExperienceIds);

        var denied = new Harness
        {
            ReceivingAgent = new ReceivingAgentCapabilities
            {
                MaxRiskClass = ToolRiskClass.Low,
                ToolRiskClasses = Classes(("a", ToolRiskClass.Low), ("b", ToolRiskClass.Medium)),
            },
        };
        denied.World.Publish(Verified(InjectionRecords.Id(1), "a", "b"));
        await denied.Agent().RunAsync("refund ticket stuck on a lock");
        Assert.Equal(InjectionOmissionReason.RiskClassExceeded, Assert.Single(denied.Last.Omitted).Reason);
    }

    [Fact]
    public async Task With_only_available_tools_the_risk_classes_are_not_consulted()
    {
        var harness = new Harness { ReceivingAgent = new ReceivingAgentCapabilities { AvailableTools = Set("a") } };
        harness.World.Publish(Verified(InjectionRecords.Id(1), "a"));

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        // "a" has no declared class, which would count as Critical under a risk check.
        Assert.Equal([InjectionRecords.Id(1)], harness.Last.InjectedExperienceIds);
    }

    // ---- Placement -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_gated_record_is_never_shown_to_the_hosts_decision()
    {
        var asked = new List<Guid>();
        var harness = new Harness
        {
            ReceivingAgent = new ReceivingAgentCapabilities { AvailableTools = Set("a") },
            Decide = context =>
            {
                asked.Add(context.Current.ExperienceId);
                return InjectionDecision.Permit;
            },
        };
        harness.World.Publish(Verified(InjectionRecords.Id(1), "b"), relevance: 1d);
        harness.World.Publish(Verified(InjectionRecords.Id(2), "a"), relevance: 0.5d);

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        Assert.Equal([InjectionRecords.Id(2)], asked);
        Assert.Equal([InjectionRecords.Id(2)], harness.Last.InjectedExperienceIds);
    }

    [Fact]
    public async Task The_gate_checks_the_record_as_the_final_re_read_returns_it()
    {
        var harness = new Harness { ReceivingAgent = new ReceivingAgentCapabilities { AvailableTools = Set("a") } };
        var id = InjectionRecords.Id(1);
        harness.World.Publish(Verified(id, "a"));

        // Indexed with an approach the agent can carry out; stored, by injection time, with one it cannot.
        harness.World.Store(Verified(id, "b") with { Revision = 2 });

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        Assert.Equal(InjectionOmissionReason.ToolUnavailable, Assert.Single(harness.Last.Omitted).Reason);
    }

    // ---- Gated with a session ------------------------------------------------------------------------

    [Fact]
    public async Task A_gated_record_is_not_charged_to_the_session_nor_tracked_as_delivered()
    {
        var harness = new Harness { ReceivingAgent = new ReceivingAgentCapabilities { AvailableTools = Set("a") } };
        harness.World.Publish(Verified(InjectionRecords.Id(1), "b"));

        var agent = harness.Agent();
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync("refund ticket stuck on a lock", session);
        await agent.RunAsync("refund ticket stuck on a lock", session);

        // Not AlreadyDelivered on the second turn: it was never delivered, so it is gated again.
        Assert.All(harness.Results, result =>
        {
            Assert.Equal(InjectionOutcome.NothingToInject, result.Outcome);
            Assert.Equal(InjectionOmissionReason.ToolUnavailable, Assert.Single(result.Omitted).Reason);
            Assert.Equal(0, result.Session!.RecordsUsed);
            Assert.Equal(0, result.Session.BytesUsed);
            Assert.Equal(0, result.Session.TrackedRecords);
        });
        Assert.Equal(2, harness.Results.Count);
    }

    // ---- Acceptance: no delivery in the access log or the run's exposure --------------------------------

    [Fact]
    public async Task A_gated_owned_record_is_not_injected_and_not_recorded_as_a_run_exposure()
    {
        var capture = new RecordingCaptureService(new InMemoryExperienceCaptureService(
            new DefaultSanitizer(new SanitizationOptions(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal))),
            new CaptureLimits(10, 50, 10_000, 10_000)));
        var harness = new Harness { ReceivingAgent = new ReceivingAgentCapabilities { AvailableTools = Set("a") } };
        harness.World.Publish(Verified(InjectionRecords.Id(1), "b"), relevance: 1d);

        var agent = harness.Agent()
            .AsBuilder()
            .UseExperienceCapture(capture, new ExperienceCaptureOptions
            {
                ResolveRun = _ => new ExperienceRunDescriptor("triage-ticket", TestScope, "Triage a refund ticket"),
            })
            .Build();

        await agent.RunAsync("refund ticket stuck on a lock");

        Assert.Equal(new OmittedExperience(InjectionRecords.Id(1), InjectionOmissionReason.ToolUnavailable), Assert.Single(harness.Last.Omitted));
        Assert.Empty(harness.Last.InjectedExperienceIds);
        Assert.Null(harness.InjectedText());
        Assert.Empty(capture.RecordedExposures);

        // The control: the same wiring with a record the agent can carry out does record the exposure.
        harness.World.Publish(Verified(InjectionRecords.Id(2), "a"), relevance: 0.5d);
        await agent.RunAsync("refund ticket stuck on a lock");
        Assert.Equal([InjectionRecords.Id(2)], harness.Last.InjectedExperienceIds);
        var exposure = Assert.Single(Assert.Single(capture.RecordedExposures).Exposures);
        Assert.Equal(InjectionRecords.Id(2), exposure.ExperienceId);
    }

    // ---- Borrowed records --------------------------------------------------------------------------

    [Fact]
    public async Task A_borrowed_record_whose_grant_withholds_the_approach_passes_so_the_lenders_tools_cannot_be_probed()
    {
        var owner = TestScope with { TeamId = "team-a" };
        var id = InjectionRecords.Id(1);
        var harness = new Harness
        {
            Scope = TestScope with { TeamId = "team-b" },
            ReceivingAgent = new ReceivingAgentCapabilities { AvailableTools = Set() },
        };
        harness.World.Publish(InjectionRecords.Record(id, owner, attempts: Attempts("lender_private_tool")));
        harness.World.Grant(id, harness.Scope, ExperienceGrantDisclosure.LessonOnly);

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        Assert.Equal([id], harness.Last.InjectedExperienceIds);
        Assert.DoesNotContain("lender_private_tool", harness.InjectedText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_borrowed_record_whose_grant_shows_the_approach_is_gated_but_was_still_delivered_to_the_reader()
    {
        var owner = TestScope with { TeamId = "team-a" };
        var id = InjectionRecords.Id(1);
        var log = new InMemoryGrantAccessLog();
        var harness = new Harness
        {
            Scope = TestScope with { TeamId = "team-b" },
            ReceivingAgent = new ReceivingAgentCapabilities { AvailableTools = Set() },
        };
        harness.World.Auditing = new ExperienceGrantAuditing(log, _ => { });
        harness.World.Publish(InjectionRecords.Record(id, owner, attempts: Attempts("lender_tool")));
        harness.World.Grant(id, harness.Scope, ExperienceGrantDisclosure.LessonAndApproach);

        await harness.Agent().RunAsync("refund ticket stuck on a lock");

        Assert.Equal(new OmittedExperience(id, InjectionOmissionReason.ToolUnavailable), Assert.Single(harness.Last.Omitted));

        // The gate runs on the re-read, and the re-read is the delivery a grant's access log records, as
        // for a record the host's DecideInjection then denies.
        Assert.Equal(id, Assert.Single(log.Rows).ExperienceId);
    }

    // ---- Construction -------------------------------------------------------------------------------

    [Fact]
    public async Task Editing_the_declaration_after_construction_changes_nothing()
    {
        var tools = new HashSet<string>(StringComparer.Ordinal) { "a" };
        var harness = new Harness { ReceivingAgent = new ReceivingAgentCapabilities { AvailableTools = tools } };
        harness.World.Publish(Verified(InjectionRecords.Id(1), "b"));
        var agent = harness.Agent();
        tools.Add("b");

        await agent.RunAsync("refund ticket stuck on a lock");

        Assert.Equal(InjectionOmissionReason.ToolUnavailable, Assert.Single(harness.Last.Omitted).Reason);
    }

    [Fact]
    public async Task Editing_the_risk_classes_after_construction_changes_nothing()
    {
        var classes = Classes(("a", ToolRiskClass.Critical));
        var harness = new Harness { ReceivingAgent = new ReceivingAgentCapabilities { MaxRiskClass = ToolRiskClass.Low, ToolRiskClasses = classes } };
        harness.World.Publish(Verified(InjectionRecords.Id(1), "a"));
        var agent = harness.Agent();
        classes["a"] = ToolRiskClass.Low;

        await agent.RunAsync("refund ticket stuck on a lock");

        Assert.Equal(InjectionOmissionReason.RiskClassExceeded, Assert.Single(harness.Last.Omitted).Reason);
    }

    // ---- Session: AlreadyDelivered comes first ---------------------------------------------------------

    [Fact]
    public async Task A_record_the_session_already_holds_is_reported_already_delivered_not_gated()
    {
        var id = InjectionRecords.Id(1);
        var open = new Harness();
        open.World.Publish(Verified(id, "b"));
        var session = await open.Agent().CreateSessionAsync();
        await open.Agent().RunAsync("refund ticket stuck on a lock", session);
        Assert.Equal([id], open.Last.InjectedExperienceIds);

        // A second provider over the same world and session, whose agent lacks "b".
        var gated = new Harness { World = open.World, ReceivingAgent = new ReceivingAgentCapabilities { AvailableTools = Set("a") } };
        await gated.Agent().RunAsync("refund ticket stuck on a lock", session);

        Assert.Equal(new OmittedExperience(id, InjectionOmissionReason.AlreadyDelivered), Assert.Single(gated.Last.Omitted) with { Detail = null });
    }

    [Fact]
    public void An_undefined_risk_class_or_a_null_or_blank_tool_name_is_refused_at_construction()
    {
        Assert.Throws<ArgumentException>(() => new Harness
        {
            ReceivingAgent = new ReceivingAgentCapabilities { MaxRiskClass = (ToolRiskClass)42 },
        }.Provider());
        Assert.Throws<ArgumentException>(() => new Harness
        {
            ReceivingAgent = new ReceivingAgentCapabilities { MaxRiskClass = ToolRiskClass.Low, ToolRiskClasses = Classes(("a", (ToolRiskClass)(-1))) },
        }.Provider());
        Assert.Throws<ArgumentException>(() => new Harness
        {
            ReceivingAgent = new ReceivingAgentCapabilities { AvailableTools = new HashSet<string>(StringComparer.Ordinal) { null! } },
        }.Provider());

        foreach (var blank in new[] { "", " ", "\t" })
        {
            Assert.Throws<ArgumentException>(() => new Harness
            {
                ReceivingAgent = new ReceivingAgentCapabilities { AvailableTools = Set(blank) },
            }.Provider());
            Assert.Throws<ArgumentException>(() => new Harness
            {
                ReceivingAgent = new ReceivingAgentCapabilities { ToolRiskClasses = Classes((blank, ToolRiskClass.Low)) },
            }.Provider());
        }

        // A host dictionary type that can hold a null key: refused with ArgumentException, not the indexer's ArgumentNullException.
        var nullKey = Assert.Throws<ArgumentException>(() => new Harness
        {
            ReceivingAgent = new ReceivingAgentCapabilities { ToolRiskClasses = new NullKeyDictionary() },
        }.Provider());
        Assert.IsNotType<ArgumentNullException>(nullKey);
    }

    [Fact]
    public void The_new_omission_reasons_are_appended_so_existing_values_are_stable()
    {
        Assert.Equal(6, (int)InjectionOmissionReason.OverSessionBudget);
        Assert.Equal(7, (int)InjectionOmissionReason.ToolUnavailable);
        Assert.Equal(8, (int)InjectionOmissionReason.RiskClassExceeded);
        Assert.Equal([0, 1, 2, 3], Enum.GetValues<ToolRiskClass>().Select(value => (int)value));
    }

    // ---- Helpers -------------------------------------------------------------------------------------

    private static HashSet<string> Set(params string[] tools) => new(tools, StringComparer.Ordinal);

    private static Dictionary<string, ToolRiskClass> Classes(params (string Tool, ToolRiskClass Class)[] entries) =>
        entries.ToDictionary(entry => entry.Tool, entry => entry.Class, StringComparer.Ordinal);

    private static ExperienceRecord Verified(Guid id, params string[] tools) =>
        InjectionRecords.Record(id, TestScope, attempts: Attempts(tools));

    /// <summary>One error-free final attempt calling <paramref name="tools"/> in order.</summary>
    private static IReadOnlyList<Attempt> Attempts(params string[] tools) =>
    [
        new Attempt(
            AttemptId: Guid.Parse("22222222-0000-0000-0000-000000000001"),
            SequenceNumber: 0,
            StartedAt: InjectionRecords.Now,
            Duration: TimeSpan.FromSeconds(1),
            ToolCalls: [.. tools.Select((tool, i) => new ToolCallRecord(
                ToolCallId: Guid.NewGuid(),
                SequenceNumber: i,
                ToolName: tool,
                Arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
                StartedAt: InjectionRecords.Now,
                Duration: TimeSpan.FromMilliseconds(5),
                Result: null,
                Error: null))],
            Result: null,
            Error: null),
    ];

    private sealed class Harness
    {
        private readonly List<ExperienceInjectionResult> _results = [];

        public FakeExperienceWorld World { get; init; } = new();

        public RecordingChatClient Client { get; } = new();

        public Scope Scope { get; init; } = TestScope;

        public ReceivingAgentCapabilities? ReceivingAgent { get; init; }

        public Func<ExperienceInjectionDecisionContext, InjectionDecision>? Decide { get; init; }

        public IReadOnlyList<ExperienceInjectionResult> Results
        {
            get
            {
                lock (_results)
                {
                    return _results.ToList();
                }
            }
        }

        public ExperienceInjectionResult Last => Results[^1];

        public ChatClientAgent Agent() => new(Client, new ChatClientAgentOptions { AIContextProviders = [Provider()] });

        public string? InjectedText() => Client.LastMessages
            ?.FirstOrDefault(m => m.AdditionalProperties?.ContainsKey(ExperienceContextProvider.HistoricalReferenceKey) == true)
            ?.Text;

        public ExperienceContextProvider Provider()
        {
            var clock = new FrozenTimeProvider(InjectionRecords.Now);
            return new(
                new ExperienceRetrievalService(World, RetrievalPolicy.Default, RankingWeights.Default, clock),
                World,
                new ExperienceInjectionOptions
                {
                    ResolveRequest = _ => new RetrieveExperienceRequest(Authorization, Scope, "refund ticket stuck on a lock", CorrelationId: "corr-1"),
                    DecideInjection = Decide,
                    TimeProvider = clock,
                    ReceivingAgent = ReceivingAgent,
                    OnContextInjected = result =>
                    {
                        lock (_results)
                        {
                            _results.Add(result);
                        }
                    },
                });
        }
    }

    /// <summary>A read-only dictionary with one null key, which <see cref="Dictionary{TKey, TValue}"/> cannot hold.</summary>
    private sealed class NullKeyDictionary : IReadOnlyDictionary<string, ToolRiskClass>
    {
        private static readonly KeyValuePair<string, ToolRiskClass>[] Entries = [new(null!, ToolRiskClass.Low)];

        public int Count => 1;

        public IEnumerable<string> Keys => Entries.Select(entry => entry.Key);

        public IEnumerable<ToolRiskClass> Values => Entries.Select(entry => entry.Value);

        public ToolRiskClass this[string key] => throw new KeyNotFoundException();

        public bool ContainsKey(string key) => false;

        public bool TryGetValue(string key, out ToolRiskClass value)
        {
            value = default;
            return false;
        }

        public IEnumerator<KeyValuePair<string, ToolRiskClass>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, ToolRiskClass>>)Entries).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

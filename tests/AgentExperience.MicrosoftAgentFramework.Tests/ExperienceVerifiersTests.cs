using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Verification;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.MicrosoftAgentFramework.Reflections;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// Story 21.2: the built-in verifiers (<see cref="ExperienceVerifiers"/>) and tool-result classifiers
/// (<see cref="ExperienceToolResultClassifiers"/>), unit-tested through <see cref="ExperienceVerificationContext.Create"/>,
/// and end to end through the README quick start's one-call setup.
/// </summary>
public sealed class ExperienceVerifiersTests
{
    private const string RunTests = "run_tests";

    private static readonly ExperienceIdentity Caller = new(
        new AuthorizationContext("contoso", "svc", [], DateTimeOffset.UnixEpoch),
        new Scope("contoso", "support", "tickets"));

    private static readonly Guid RoundId = Guid.Parse("21212121-0000-0000-0000-000000000002");

    // ---- ToolSucceeded ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ToolSucceeded_passes_when_the_last_call_returned_although_an_earlier_one_failed()
    {
        var context = Context(
            Attempt(0, Call(0, RunTests, "exit 1"), Call(1, "other", null)),
            Attempt(1, Call(0, RunTests, "exit 1"), Call(1, RunTests, null), Call(2, "other", "exit 9")));

        var verification = await ExperienceVerifiers.ToolSucceeded(RunTests)(context, CancellationToken.None);

        Assert.NotNull(verification);
        Assert.Equal([new RequiredCheck(RunTests, "ToolOutcome")], verification.RequiredChecks);
        var evidence = Assert.Single(verification.Evidence);
        Assert.Equal(CheckResult.Pass, evidence.Result);
        Assert.Equal(RunTests, evidence.CheckId);
        Assert.Equal("ToolOutcome", evidence.Kind);
        Assert.Equal("tool:run_tests", evidence.Producer);
        Assert.Equal(RoundId, evidence.VerificationRoundId);
        Assert.Equal(ExperienceVerifiers.UnversionedRevision, evidence.ArtifactRevision);
        Assert.Equal("unversioned", verification.ArtifactRevision);
    }

    [Fact]
    public async Task ToolSucceeded_fails_when_the_last_call_failed_and_orders_by_sequence_not_list_order()
    {
        // Listed out of order: the last by sequence is the classified failure in attempt 1.
        var context = Context(
            Attempt(1, Call(3, RunTests, "exit 1"), Call(0, RunTests, null)),
            Attempt(0, Call(5, RunTests, null)));

        var verification = await ExperienceVerifiers.ToolSucceeded(RunTests, checkId: "tests-pass", artifactRevision: "build-42")(context, CancellationToken.None);

        var evidence = Assert.Single(verification!.Evidence);
        Assert.Equal(CheckResult.Fail, evidence.Result);
        Assert.Equal("tests-pass", evidence.CheckId);
        Assert.Equal("build-42", evidence.ArtifactRevision);
        Assert.Equal("build-42", verification.ArtifactRevision);
        Assert.Equal([new RequiredCheck("tests-pass", "ToolOutcome")], verification.RequiredChecks);
        Assert.Contains("exit 1", evidence.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolSucceeded_reads_only_the_error_so_a_redacted_result_still_counts()
    {
        var context = Context(Attempt(0, Call(0, RunTests, null, result: null)));

        var verification = await ExperienceVerifiers.ToolSucceeded(RunTests)(context, CancellationToken.None);

        Assert.Equal(CheckResult.Pass, Assert.Single(verification!.Evidence).Result);
    }

    [Fact]
    public async Task ToolSucceeded_returns_null_when_the_tool_was_never_called_and_matches_names_ordinally()
    {
        var context = Context(Attempt(0, Call(0, "Run_Tests", null), Call(1, "other", null)));

        Assert.Null(await ExperienceVerifiers.ToolSucceeded(RunTests)(context, CancellationToken.None));
        Assert.Null(await ExperienceVerifiers.ToolSucceeded(RunTests)(Context(), CancellationToken.None));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    [InlineData(" ", null, null)]
    [InlineData(RunTests, " ", null)]
    [InlineData(RunTests, null, "")]
    public void ToolSucceeded_refuses_blank_arguments(string? toolName, string? checkId, string? revision) =>
        Assert.ThrowsAny<ArgumentException>(() => ExperienceVerifiers.ToolSucceeded(toolName!, checkId, revision));

    // ---- Check ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, CheckResult.Pass)]
    [InlineData(false, CheckResult.Fail)]
    public async Task Check_turns_the_hosts_verdict_into_evidence(bool passed, CheckResult expected)
    {
        var verification = await ExperienceVerifiers.Check("lint", (_, _) => ValueTask.FromResult<bool?>(passed))(Context(), CancellationToken.None);

        Assert.Equal([new RequiredCheck("lint", "HostCheck")], verification!.RequiredChecks);
        var evidence = Assert.Single(verification.Evidence);
        Assert.Equal(expected, evidence.Result);
        Assert.Equal("HostCheck", evidence.Kind);
        Assert.Equal("host", evidence.Producer);
        Assert.Equal(ExperienceVerifiers.UnversionedRevision, evidence.ArtifactRevision);
    }

    [Fact]
    public async Task Check_returning_null_stores_nothing() =>
        Assert.Null(await ExperienceVerifiers.Check("lint", (_, _) => ValueTask.FromResult<bool?>(null))(Context(), CancellationToken.None));

    [Fact]
    public void Check_refuses_a_blank_id_or_a_null_check()
    {
        Assert.ThrowsAny<ArgumentException>(() => ExperienceVerifiers.Check(" ", (_, _) => ValueTask.FromResult<bool?>(true)));
        Assert.Throws<ArgumentNullException>(() => ExperienceVerifiers.Check("lint", null!));
    }

    // ---- Approval -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, CheckResult.Pass)]
    [InlineData(false, CheckResult.Fail)]
    public async Task Approval_turns_a_persons_decision_into_evidence_produced_by_the_approver(bool approved, CheckResult expected)
    {
        var verification = await ExperienceVerifiers.Approval(
            "reviewed",
            (_, _) => ValueTask.FromResult<ExperienceApproval?>(new ExperienceApproval("alice@contoso", approved)),
            artifactRevision: "pr-7")(Context(), CancellationToken.None);

        Assert.Equal([new RequiredCheck("reviewed", "HumanApproval")], verification!.RequiredChecks);
        var evidence = Assert.Single(verification.Evidence);
        Assert.Equal(expected, evidence.Result);
        Assert.Equal("HumanApproval", evidence.Kind);
        Assert.Equal("alice@contoso", evidence.Producer);
        Assert.Equal("pr-7", verification.ArtifactRevision);
    }

    [Fact]
    public async Task Approval_returning_null_stores_nothing() =>
        Assert.Null(await ExperienceVerifiers.Approval("reviewed", (_, _) => ValueTask.FromResult<ExperienceApproval?>(null))(Context(), CancellationToken.None));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Approval_with_a_blank_approver_throws_so_the_run_is_not_stored(string? approver)
    {
        var verify = ExperienceVerifiers.Approval("reviewed", (_, _) => ValueTask.FromResult<ExperienceApproval?>(new ExperienceApproval(approver!, true)));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await verify(Context(), CancellationToken.None));
    }

    // ---- AllOf / AnyOf --------------------------------------------------------------------------------------

    [Fact]
    public async Task AllOf_a_pass_and_a_fail_is_one_verification_with_both_checks()
    {
        var verification = await ExperienceVerifiers.AllOf(Fixed("a", true), Fixed("b", false))(Context(), CancellationToken.None);

        Assert.Equal(["a", "b"], verification!.RequiredChecks.Select(check => check.CheckId));
        Assert.Equal([CheckResult.Pass, CheckResult.Fail], verification.Evidence.Select(evidence => evidence.Result));
        Assert.Equal(ExperienceVerifiers.UnversionedRevision, verification.ArtifactRevision);
    }

    [Fact]
    public async Task AllOf_stops_at_the_first_null_and_returns_null()
    {
        var ran = new List<string>();
        var verify = ExperienceVerifiers.AllOf(
            Fixed("a", true, ran),
            Fixed("b", null, ran),
            Fixed("c", true, ran));

        Assert.Null(await verify(Context(), CancellationToken.None));
        Assert.Equal(["a", "b"], ran);
    }

    [Fact]
    public async Task AllOf_refuses_two_different_explicit_revisions_and_duplicate_checks()
    {
        var revisions = ExperienceVerifiers.AllOf(Fixed("a", true, revision: "build-1"), Fixed("b", true, revision: "build-2"));
        var duplicates = ExperienceVerifiers.AllOf(Fixed("a", true), Fixed("a", false));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await revisions(Context(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await duplicates(Context(), CancellationToken.None));
    }

    [Fact]
    public async Task AllOf_takes_the_explicit_revision_over_the_default_and_restamps_the_defaulted_evidence()
    {
        var verification = await ExperienceVerifiers.AllOf(Fixed("a", true), Fixed("b", true, revision: "build-2"), Fixed("c", false))(
            Context(), CancellationToken.None);

        Assert.Equal("build-2", verification!.ArtifactRevision);
        Assert.All(verification.Evidence, evidence => Assert.Equal("build-2", evidence.ArtifactRevision));
        Assert.All(verification.Evidence, evidence => Assert.Equal(RoundId, evidence.VerificationRoundId));
    }

    [Fact]
    public async Task AllOf_refuses_a_null_required_check()
    {
        var verify = ExperienceVerifiers.AllOf(
            Fixed("a", true),
            (context, _) => ValueTask.FromResult<ExperienceVerification?>(new ExperienceVerification([null!], [], ExperienceVerifiers.UnversionedRevision)));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await verify(Context(), CancellationToken.None));
    }

    [Fact]
    public async Task AnyOf_does_not_count_a_verification_missing_evidence_for_a_required_check_as_passed()
    {
        // All its evidence passed, but the second required check has none (and the third's is of a kind it refuses).
        Func<ExperienceVerificationContext, CancellationToken, ValueTask<ExperienceVerification?>> incomplete = (context, _) =>
            ValueTask.FromResult<ExperienceVerification?>(new ExperienceVerification(
                [new RequiredCheck("a", "HostCheck"), new RequiredCheck("b", "HostCheck"), new RequiredCheck("c", "TestResult")],
                [
                    context.CreateEvidence("a", "HostCheck", CheckResult.Pass, "host", ExperienceVerifiers.UnversionedRevision),
                    context.CreateEvidence("c", "HostCheck", CheckResult.Pass, "host", ExperienceVerifiers.UnversionedRevision),
                ],
                ExperienceVerifiers.UnversionedRevision));
        Func<ExperienceVerificationContext, CancellationToken, ValueTask<ExperienceVerification?>> anyKind = (context, _) =>
            ValueTask.FromResult<ExperienceVerification?>(new ExperienceVerification(
                [new RequiredCheck("d", RequiredCheck.AnyKind)],
                [context.CreateEvidence("d", "Whatever", CheckResult.Pass, "host", ExperienceVerifiers.UnversionedRevision)],
                ExperienceVerifiers.UnversionedRevision));

        var chosen = await ExperienceVerifiers.AnyOf(incomplete, anyKind)(Context(), CancellationToken.None);
        Assert.Equal("d", Assert.Single(chosen!.RequiredChecks).CheckId);

        var fallback = await ExperienceVerifiers.AnyOf(incomplete)(Context(), CancellationToken.None);
        Assert.Equal(3, fallback!.RequiredChecks.Count);
    }

    [Fact]
    public async Task The_cancellation_token_reaches_the_hosts_delegates_through_every_verifier()
    {
        using var source = new CancellationTokenSource();
        var token = source.Token;
        var seen = new List<CancellationToken>();
        var check = ExperienceVerifiers.Check("c", (_, cancellationToken) =>
        {
            seen.Add(cancellationToken);
            return ValueTask.FromResult<bool?>(true);
        });
        var approval = ExperienceVerifiers.Approval("a", (_, cancellationToken) =>
        {
            seen.Add(cancellationToken);
            return ValueTask.FromResult<ExperienceApproval?>(new ExperienceApproval("user-1", true));
        });

        await check(Context(), token);
        await approval(Context(), token);
        await ExperienceVerifiers.AllOf(check, approval)(Context(), token);
        await ExperienceVerifiers.AnyOf(check)(Context(), token);

        Assert.Equal(5, seen.Count);
        Assert.All(seen, cancellationToken => Assert.Equal(token, cancellationToken));
    }

    [Fact]
    public async Task AnyOf_returns_the_first_passing_verification_and_stops_there()
    {
        var ran = new List<string>();
        var verification = await ExperienceVerifiers.AnyOf(
            Fixed("a", false, ran),
            Fixed("b", null, ran),
            Fixed("c", true, ran),
            Fixed("d", true, ran))(Context(), CancellationToken.None);

        Assert.Equal("c", Assert.Single(verification!.RequiredChecks).CheckId);
        Assert.Equal(["a", "b", "c"], ran);
    }

    [Fact]
    public async Task AnyOf_with_none_passing_returns_the_first_verification_and_with_all_null_returns_null()
    {
        var failed = await ExperienceVerifiers.AnyOf(Fixed("a", null), Fixed("b", false), Fixed("c", false))(Context(), CancellationToken.None);
        Assert.Equal("b", Assert.Single(failed!.RequiredChecks).CheckId);

        Assert.Null(await ExperienceVerifiers.AnyOf(Fixed("a", null), Fixed("b", null))(Context(), CancellationToken.None));
    }

    [Fact]
    public void AllOf_and_AnyOf_refuse_no_verifiers_or_a_null_one()
    {
        Assert.Throws<ArgumentException>(() => ExperienceVerifiers.AllOf());
        Assert.Throws<ArgumentException>(() => ExperienceVerifiers.AnyOf());
        Assert.Throws<ArgumentException>(() => ExperienceVerifiers.AllOf(Fixed("a", true), null!));
        Assert.Throws<ArgumentNullException>(() => ExperienceVerifiers.AnyOf(null!));
    }

    [Fact]
    public void Every_verifier_is_assignable_to_Verify()
    {
        var options = new AgentExperienceOptions
        {
            Verify = ExperienceVerifiers.ToolSucceeded(RunTests),
        };
        options.Verify = ExperienceVerifiers.Check("c", (_, _) => ValueTask.FromResult<bool?>(true));
        options.Verify = ExperienceVerifiers.Approval("a", (_, _) => ValueTask.FromResult<ExperienceApproval?>(null));
        options.Verify = ExperienceVerifiers.AllOf(ExperienceVerifiers.ToolSucceeded(RunTests), options.Verify);
        options.Verify = ExperienceVerifiers.AnyOf(options.Verify);
        Assert.NotNull(options.Verify);
    }

    // ---- Classifiers ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("exit=0", null)]
    [InlineData("exit=3", "exit 3")]
    [InlineData("{\"exitCode\":2}", "exit 2")]
    [InlineData("EXIT CODE 4", "exit 4")]
    [InlineData("ok", null)]
    [InlineData("exit 7", "exit 7")]
    [InlineData("tests finished, exit code: 12", "exit 12")]
    [InlineData("{\"exit_code\": 5, \"stdout\": \"exit 0\"}", "exit 5")]
    [InlineData("{\"ExitCode\":\"0\"}", null)]
    [InlineData("{\"stdout\":\"exit 3\"}", null)]
    [InlineData("Result: {\"exitCode\": 9, truncated", "exit 9")]
    [InlineData("exit=-1", "exit -1")]
    [InlineData("exit=007", "exit 7")]
    [InlineData("exit=000", null)]
    [InlineData("exit=1073741819", ExperienceCaptureOptions.ToolReportedErrorClass)]
    [InlineData("exit=123456789012345678901234567890", ExperienceCaptureOptions.ToolReportedErrorClass)]
    [InlineData("exit 1.0", ExperienceCaptureOptions.ToolReportedErrorClass)]
    [InlineData("exit 0.0", null)]
    [InlineData("{\"exitCode\":1.0}", ExperienceCaptureOptions.ToolReportedErrorClass)]
    [InlineData("{\"exitCode\":1e0}", ExperienceCaptureOptions.ToolReportedErrorClass)]
    [InlineData("{\"exitCode\":0.0}", null)]
    [InlineData("{\"exitCode\":123456789012345678901234567890}", ExperienceCaptureOptions.ToolReportedErrorClass)]
    [InlineData("step: exit 0, then more work... final: exit=1", "exit 1")]
    [InlineData("exit status 2", "exit 2")]
    [InlineData("process exited with code 3", "exit 3")]
    [InlineData("returncode=4", "exit 4")]
    [InlineData("{\"returncode\": 5}", "exit 5")]
    [InlineData("{\"return_code\": 6}", "exit 6")]
    [InlineData("{\"returnCode\": \"7\"}", "exit 7")]
    [InlineData("partial {\"returnCode\": 8, truncated", "exit 8")]
    [InlineData("{\"exitCode\":\"n/a\",\"exit_code\":3}", "exit 3")]
    [InlineData("[{\"exitCode\":2}]", null)]
    [InlineData("[\"exit=3\"]", null)]
    [InlineData("", null)]
    public void ExitCode_classifies_a_non_zero_exit_code(string result, string? expected) =>
        Assert.Equal(expected, ExperienceToolResultClassifiers.ExitCode()(Result(result)));

    [Theory]
    [InlineData("HTTP 503", "HTTP 503")]
    [InlineData("{\"status\":200}", null)]
    [InlineData("{\"statusCode\":404}", "HTTP 404")]
    [InlineData("HTTP/1.1 500 Internal Server Error", "HTTP 500")]
    [InlineData("status 429", "HTTP 429")]
    [InlineData("Status code: 401", "HTTP 401")]
    [InlineData("http 302", null)]
    [InlineData("the page said HTTP 503", null)]
    [InlineData("{\"Status\":\"502\"}", "HTTP 502")]
    [InlineData("{\"status\":\"ok\"}", null)]
    [InlineData("{\"status\":700}", null)]
    [InlineData("{\"status\":\"error\",\"statusCode\":500}", "HTTP 500")]
    [InlineData("{\"status\":\"failed\",\"status_code\":\"503\"}", "HTTP 503")]
    [InlineData("[{\"statusCode\":404}]", null)]
    [InlineData("ok", null)]
    public void HttpStatus_classifies_an_error_status(string result, string? expected) =>
        Assert.Equal(expected, ExperienceToolResultClassifiers.HttpStatus()(Result(result)));

    [Fact]
    public void Both_classifiers_read_a_JsonElement_and_a_serialized_object()
    {
        var exitElement = JsonSerializer.Deserialize<JsonElement>("{\"exitCode\":2}");
        var httpElement = JsonSerializer.Deserialize<JsonElement>("{\"statusCode\":404}");
        var stringElement = JsonSerializer.Deserialize<JsonElement>("\"exit=3\"");

        Assert.Equal("exit 2", ExperienceToolResultClassifiers.ExitCode()(Result(exitElement)));
        Assert.Equal("exit 3", ExperienceToolResultClassifiers.ExitCode()(Result(stringElement)));
        Assert.Equal("HTTP 404", ExperienceToolResultClassifiers.HttpStatus()(Result(httpElement)));
        Assert.Equal("exit 6", ExperienceToolResultClassifiers.ExitCode()(Result(new ProcessOutcome(6, "boom"))));
        Assert.Null(ExperienceToolResultClassifiers.ExitCode()(Result(new ProcessOutcome(0, "exit 3"))));
        Assert.Equal("HTTP 503", ExperienceToolResultClassifiers.HttpStatus()(Result(new HttpOutcome(503))));
        Assert.Null(ExperienceToolResultClassifiers.HttpStatus()(Result(new HttpOutcome(204))));
        Assert.Null(ExperienceToolResultClassifiers.ExitCode()(Result(null)));
        Assert.Null(ExperienceToolResultClassifiers.ExitCode()(Result(42)));
    }

    [Fact]
    public void A_JSON_object_is_read_by_its_properties_whatever_its_size_and_text_by_its_last_match()
    {
        var output = new string('x', 9000) + " exit 0";
        var json = JsonSerializer.Serialize(new { stdout = output, exitCode = 1 });
        Assert.True(json.Length > 8192);
        Assert.True(json.IndexOf("exitCode", StringComparison.Ordinal) > 8192);

        Assert.Equal("exit 1", ExperienceToolResultClassifiers.ExitCode()(Result(json)));
        Assert.Equal("exit 1", ExperienceToolResultClassifiers.ExitCode()(Result(JsonSerializer.Deserialize<JsonElement>(json))));
        Assert.Equal("exit 1", ExperienceToolResultClassifiers.ExitCode()(Result(new LateOutcome(output, 1))));
        Assert.Null(ExperienceToolResultClassifiers.ExitCode()(Result(new LateOutcome(new string('x', 9000) + " exit 3", 0))));

        // Text past 1 MiB: the last 64 KiB still holds the final exit code.
        var huge = "exit 0\n" + new string('y', 2 * 1024 * 1024) + "\nexit=2";
        Assert.Equal("exit 2", ExperienceToolResultClassifiers.ExitCode()(Result(huge)));
        Assert.Equal("exit 2", ExperienceToolResultClassifiers.ExitCode()(Result(new string('z', 20_000) + " exit=2")));
    }

    [Fact]
    public void Text_content_is_read_and_a_JSON_array_names_no_code_in_either_form()
    {
        Assert.Equal("exit 3", ExperienceToolResultClassifiers.ExitCode()(Result(new TextContent("exit=3"))));
        Assert.Equal("exit 3", ExperienceToolResultClassifiers.ExitCode()(Result(new List<AIContent> { new TextContent("running"), new TextContent("exit=3") })));
        Assert.Equal("HTTP 502", ExperienceToolResultClassifiers.HttpStatus()(Result(new TextContent("HTTP 502 Bad Gateway"))));

        var array = "[{\"exitCode\":2},\"exit=3\"]";
        Assert.Null(ExperienceToolResultClassifiers.ExitCode()(Result(array)));
        Assert.Null(ExperienceToolResultClassifiers.ExitCode()(Result(JsonSerializer.Deserialize<JsonElement>(array))));
        Assert.Null(ExperienceToolResultClassifiers.ExitCode()(Result(new[] { new ProcessOutcome(2, "x") })));
    }

    [Fact]
    public void Given_tool_names_a_classifier_applies_to_those_tools_only()
    {
        var exit = ExperienceToolResultClassifiers.ExitCode(RunTests, "build");
        var http = ExperienceToolResultClassifiers.HttpStatus("fetch");

        Assert.Equal("exit 3", exit(Result("exit=3", RunTests)));
        Assert.Equal("exit 3", exit(Result("exit=3", "build")));
        Assert.Null(exit(Result("exit=3", "Run_Tests")));
        Assert.Null(exit(Result("exit=3", "grep")));
        Assert.Equal("HTTP 404", http(Result("HTTP 404", "fetch")));
        Assert.Null(http(Result("HTTP 404", RunTests)));
        Assert.Equal("exit 3", ExperienceToolResultClassifiers.ExitCode()(Result("exit=3", "anything")));
        Assert.ThrowsAny<ArgumentException>(() => ExperienceToolResultClassifiers.ExitCode(RunTests, " "));
        Assert.Throws<ArgumentNullException>(() => ExperienceToolResultClassifiers.HttpStatus(null!));
    }

    [Fact]
    public void Every_class_the_classifiers_return_is_a_single_error_class_token()
    {
        var exit = ExperienceToolResultClassifiers.ExitCode();
        var http = ExperienceToolResultClassifiers.HttpStatus();
        string[] exits = ["exit=1", "exit=-1", "exit=999999", "exit=-999999", "exit=1234567", "{\"exitCode\":255}", "exit 1.5", "{\"exitCode\":1e3}"];
        string[] statuses = ["HTTP 400", "HTTP 599", "status 500", "{\"status\":404}"];

        var classes = exits.Select(value => exit(Result(value))).Concat(statuses.Select(value => http(Result(value)))).ToList();

        Assert.All(classes, value => Assert.True(ErrorClass.IsSingleToken(value), value));
    }

    // ---- End to end -----------------------------------------------------------------------------------------

    /// <summary>
    /// The README quick start: the one-call setup with <c>ToolSucceeded</c> and the <c>ExitCode()</c> classifier. A tool
    /// that fails once by exit code and then succeeds is stored Validated, and the next run's block names both calls.
    /// </summary>
    [Fact]
    public async Task The_quick_start_stores_a_run_whose_last_test_run_passed_and_the_next_run_sees_both_calls()
    {
        var host = new QuickStartHost();
        await using var services = host.Build(options => options.Verify = ExperienceVerifiers.ToolSucceeded(RunTests));
        var exits = new Queue<string>(["exit=3", "exit=0", "exit=0", "exit=0"]);
        var runTests = AIFunctionFactory.Create(() => exits.Dequeue(), RunTests);
        var model = new TwoCallModel();
        AIAgent agent = new ChatClientAgent(model, name: "triage-ticket", tools: [runTests]).AsBuilder().UseAgentExperience(services).Build();

        await agent.RunAsync("Ticket #4812: a refund is stuck on a lock. Triage it.");

        Assert.Empty(host.Failures);
        var first = Assert.Single(host.Finalized);
        Assert.Equal(FinalizationOutcome.Validated, first.Outcome);

        await agent.RunAsync("Ticket #4812: a refund is stuck on a lock. Triage it.");

        Assert.Empty(host.Failures);
        Assert.Equal([first.ExperienceId!.Value], host.Injections[1].InjectedExperienceIds);
        var block = Assert.Single(model.FirstCalls[1], IsHistoricalReference).Text;
        Assert.Contains("run_tests [failed: exit 3], run_tests [returned]", block, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_whose_last_test_run_failed_is_stored_quarantined()
    {
        var host = new QuickStartHost();
        await using var services = host.Build(options => options.Verify = ExperienceVerifiers.ToolSucceeded(RunTests));
        var runTests = AIFunctionFactory.Create(() => "exit=1", RunTests);
        AIAgent agent = new ChatClientAgent(new TwoCallModel(), name: "triage-ticket", tools: [runTests]).AsBuilder().UseAgentExperience(services).Build();

        await agent.RunAsync("Ticket #4812: a refund is stuck on a lock. Triage it.");

        Assert.Empty(host.Failures);
        Assert.Equal(FinalizationOutcome.Quarantined, Assert.Single(host.Finalized).Outcome);
    }

    [Fact]
    public async Task A_run_whose_last_test_run_threw_is_stored_quarantined()
    {
        var host = new QuickStartHost();
        await using var services = host.Build(options => options.Verify = ExperienceVerifiers.ToolSucceeded(RunTests));
        var runTests = AIFunctionFactory.Create(new Func<string>(() => throw new InvalidOperationException("the runner crashed")), RunTests);
        AIAgent agent = new ChatClientAgent(new TwoCallModel(), name: "triage-ticket", tools: [runTests]).AsBuilder().UseAgentExperience(services).Build();

        await agent.RunAsync("Ticket #4812: a refund is stuck on a lock. Triage it.");

        Assert.Empty(host.Failures);
        Assert.Equal(FinalizationOutcome.Quarantined, Assert.Single(host.Finalized).Outcome);
    }

    [Fact]
    public async Task A_run_that_never_called_the_tool_is_not_stored_and_nothing_is_reported()
    {
        var host = new QuickStartHost();
        await using var services = host.Build(options => options.Verify = ExperienceVerifiers.ToolSucceeded(RunTests));
        AIAgent agent = new ChatClientAgent(new TwoCallModel { ToolCalls = 0 }, name: "triage-ticket").AsBuilder().UseAgentExperience(services).Build();

        await agent.RunAsync("Ticket #4812: a refund is stuck on a lock. Triage it.");

        Assert.Empty(host.Failures);
        Assert.Empty(host.Finalized);
    }

    public static TheoryData<string> Refused() => new() { "blank approver", "differing revisions" };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_refused_verification_is_reported_through_the_Verify_failure_path_and_not_stored(string scenario)
    {
        var host = new QuickStartHost();
        await using var services = host.Build(options => options.Verify = scenario == "blank approver"
            ? ExperienceVerifiers.Approval("reviewed", (_, _) => ValueTask.FromResult<ExperienceApproval?>(new ExperienceApproval(" ", true)))
            : ExperienceVerifiers.AllOf(ExperienceVerifiers.ToolSucceeded(RunTests, artifactRevision: "build-1"), ExperienceVerifiers.ToolSucceeded(RunTests, "again", "build-2")));
        var runTests = AIFunctionFactory.Create(() => "exit=0", RunTests);
        AIAgent agent = new ChatClientAgent(new TwoCallModel(), name: "triage-ticket", tools: [runTests]).AsBuilder().UseAgentExperience(services).Build();

        var response = await agent.RunAsync("Ticket #4812: a refund is stuck on a lock. Triage it.");

        Assert.Equal("Done.", response.Text);
        Assert.Empty(host.Finalized);
        var failure = Assert.Single(host.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.Finalization, failure.Stage);
        Assert.IsType<InvalidOperationException>(failure.Exception);
    }

    // ---- Helpers --------------------------------------------------------------------------------------------

    private sealed record ProcessOutcome(int ExitCode, string Stdout);

    private sealed record HttpOutcome(int StatusCode);

    private sealed record LateOutcome(string Stdout, int ExitCode);

    private static ExperienceToolResultContext Result(object? result, string toolName = RunTests) => new()
    {
        ToolCallId = Guid.NewGuid(),
        ToolName = toolName,
        Arguments = new Dictionary<string, object?>(),
        Result = result,
    };

    private static ToolCallRecord Call(int sequence, string toolName, string? error, string? result = "out") => new(
        Guid.NewGuid(), sequence, toolName, new Dictionary<string, object?>(), DateTimeOffset.UnixEpoch, TimeSpan.FromMilliseconds(1), result, error);

    private static Attempt Attempt(int sequence, params ToolCallRecord[] calls) =>
        new(Guid.NewGuid(), sequence, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), calls, "done", null);

    private static ExperienceVerificationContext Context(params Attempt[] attempts) => ExperienceVerificationContext.Create(
        new ExperienceRun(
            Guid.NewGuid(), "triage", null, Caller.Scope,
            new EnvironmentFingerprint("host", "net10.0", "os", null, new Dictionary<string, string>()),
            new Provenance("test", null, DateTimeOffset.UnixEpoch, null),
            attempts, RunExecutionStatus.Completed, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
        RoundId,
        Caller);

    /// <summary>A host check that returns <paramref name="passed"/>, noting that it ran.</summary>
    private static Func<ExperienceVerificationContext, CancellationToken, ValueTask<ExperienceVerification?>> Fixed(
        string checkId, bool? passed, List<string>? ran = null, string? revision = null) =>
        ExperienceVerifiers.Check(
            checkId,
            (_, _) =>
            {
                ran?.Add(checkId);
                return ValueTask.FromResult(passed);
            },
            revision);

    private static bool IsHistoricalReference(ChatMessage message) =>
        message.AdditionalProperties?.ContainsKey(ExperienceContextProvider.HistoricalReferenceKey) == true;

    /// <summary>A model that calls <c>run_tests</c> <see cref="ToolCalls"/> times, one per turn, then answers.</summary>
    private sealed class TwoCallModel : IChatClient
    {
        public int ToolCalls { get; init; } = 2;

        /// <summary>The messages of each run's first model call.</summary>
        public List<List<ChatMessage>> FirstCalls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            var results = list.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Count();
            if (results == 0)
            {
                FirstCalls.Add(list);
            }

            return Task.FromResult(new ChatResponse(results < ToolCalls
                ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call-{results}", RunTests, new Dictionary<string, object?>())])
                : new ChatMessage(ChatRole.Assistant, "Done.")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>The README quick start's setup, with the exit-code classifier, reporting everything back.</summary>
    private sealed class QuickStartHost
    {
        public List<ExperienceCaptureFailure> Failures { get; } = [];

        public List<FinalizeExperienceResult> Finalized { get; } = [];

        public List<ExperienceInjectionResult> Injections { get; } = [];

        public ServiceProvider Build(Action<AgentExperienceOptions> configure)
        {
            var collection = new ServiceCollection();
            collection.AddSingleton<IHostEnvironment>(new AgentExperienceFiveLineSetupTests.FakeHostEnvironment(Environments.Development));
            collection
                .AddAgentExperience(options =>
                {
                    options.UseDevelopmentIdentity();
                    configure(options);
                    options.Capture = capture =>
                    {
                        capture.ClassifyToolResult = ExperienceToolResultClassifiers.ExitCode(RunTests);
                        capture.OnCaptureFailure = Failures.Add;
                        capture.OnRunFinalized = Finalized.Add;
                    };
                    options.Injection = injection => injection.OnContextInjected = Injections.Add;
                })
                .UseInMemoryStorageForDevelopment();
            return collection.BuildServiceProvider();
        }
    }
}

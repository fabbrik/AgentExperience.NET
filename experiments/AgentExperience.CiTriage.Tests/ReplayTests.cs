using AgentExperience.CiTriage.GitHub;
using AgentExperience.CiTriage.Model;
using AgentExperience.CiTriage.Scripted;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Holdout;
using Microsoft.Extensions.AI;

namespace AgentExperience.CiTriage.Tests;

public sealed class ReplayTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static CiFailure Failure(long id, int failedHour, int? greenHour, LabelKind kind = LabelKind.Code)
    {
        var run = new CiRun(id, "b" + id, "f" + id, "failure", T0.AddHours(failedHour).AddMinutes(-5), T0.AddHours(failedHour), 1);
        var green = greenHour is { } g ? new CiRun(id + 100, "b" + id, "g" + id, "success", T0.AddHours(g).AddMinutes(-5), T0.AddHours(g), 1) : null;
        return new CiFailure(run, new FailureLabel(kind, kind == LabelKind.Code ? ["a.cs"] : [], green));
    }

    [Fact]
    public void Events_run_in_time_order_and_a_triage_comes_before_a_fix_at_the_same_instant()
    {
        var a = Failure(1, failedHour: 1, greenHour: 3);
        var b = Failure(2, failedHour: 3, greenHour: 4);

        var events = Replay.Schedule([b, a]);

        Assert.Equal(
            ["triage 1", "triage 2", "finalize 1", "finalize 2"],
            events.Select(e => (e is TriageEvent ? "triage " : "finalize ") + e.Failure.Run.Id));
    }

    [Fact]
    public void A_green_run_that_finished_before_the_failure_is_finalized_after_its_triage()
    {
        var early = Failure(1, failedHour: 5, greenHour: 2);
        var events = Replay.Schedule([early]);
        Assert.IsType<TriageEvent>(events[0]);
        Assert.IsType<FinalizeEvent>(events[1]);
        Assert.Equal(events[0].At, events[1].At);
    }

    [Fact]
    public void Only_code_and_flaky_failures_are_scheduled()
    {
        var events = Replay.Schedule([Failure(1, 1, 2, LabelKind.Ambiguous), Failure(2, 1, null, LabelKind.Pending), Failure(3, 1, 2, LabelKind.Flaky)]);
        Assert.All(events, e => Assert.Equal(3, e.Failure.Run.Id));
    }

    /// <summary>
    /// A fails, B fails, A's fix lands, C fails. B's injection must never name A's lesson; C's can, and here does
    /// (injected or withheld by the holdout, either way named).
    /// </summary>
    [Fact]
    public async Task A_lesson_reaches_only_failures_after_its_fix_was_known()
    {
        var ci = new FakeCi();
        var log = ScriptedScenario.CompileLog("Area0");
        string[] changed = ["src/AgentExperience.Core/Area0/Area0Options.cs"];
        var a = ci.AddFailure("feat/a", "a1", T0.AddHours(1), changed, log, "a2", T0.AddHours(3), changed);
        var b = ci.AddFailure("feat/b", "b1", T0.AddHours(2), changed, log, "b2", T0.AddHours(5), changed);
        var c = ci.AddFailure("feat/c", "c1", T0.AddHours(4), changed, log, "c2", T0.AddHours(6), changed);

        var (_, result) = await RunAsync(ci);

        var lessonA = result.Triages.Single(t => t.Failure.Run.Id == a.Id);
        Assert.Equal(FinalizationOutcome.Validated, lessonA.Finalized);
        Assert.NotNull(lessonA.ExperienceId);
        Assert.DoesNotContain(lessonA.ExperienceId!.Value, result.Triages.Single(t => t.Failure.Run.Id == b.Id).NamedExperienceIds);
        Assert.Contains(lessonA.ExperienceId!.Value, result.Triages.Single(t => t.Failure.Run.Id == c.Id).NamedExperienceIds);
        Assert.Empty(lessonA.NamedExperienceIds);
    }

    [Fact]
    public async Task The_scripted_replay_records_both_arms_finalizes_every_triage_and_the_estimate_counts_them()
    {
        var ci = ScriptedScenario.Create();

        var (failures, result) = await RunAsync(ci);

        Assert.Equal(20, failures.Count);
        Assert.Equal(17, result.Triages.Count);
        Assert.All(result.Triages, t => Assert.Contains(t.Finalized, new FinalizationOutcome?[] { FinalizationOutcome.Validated, FinalizationOutcome.Quarantined }));
        Assert.All(result.Triages, t => Assert.Equal(t.Correct, t.Finalized == FinalizationOutcome.Validated));
        Assert.Contains(result.Rows, row => row.Predicted == "skipped: ambiguous");
        Assert.Contains(result.Rows, row => row.Predicted == "skipped: pending");
        Assert.Contains(result.Rows, row => row.Predicted == "skipped: no-log");

        var treatment = result.Rows.Where(row => row.Arm == "treatment").ToList();
        var holdout = result.Rows.Where(row => row.Arm == "holdout").ToList();
        Assert.NotEmpty(treatment);
        Assert.NotEmpty(holdout);

        var overall = result.Estimate.Overall;
        Assert.Equal(treatment.Count, overall.Treatment.Runs);
        Assert.Equal(holdout.Count, overall.Holdout.Runs);
        Assert.Equal(treatment.Count(row => row.Correct == "true"), overall.Treatment.Successes);
        Assert.Equal(holdout.Count(row => row.Correct == "true"), overall.Holdout.Successes);
        Assert.Equal(0, overall.Pending);
        Assert.Equal(0, overall.Unverified);
        Assert.Equal(ExperienceBenefitVerdict.NoEvidence, overall.Verdict);
    }

    [Fact]
    public async Task An_unparseable_reply_is_finalized_failed_and_noted()
    {
        var ci = new FakeCi();
        ci.AddFailure("feat/a", "a1", T0.AddHours(1), ["src/A.cs"], "error CS1002: ; expected", "a2", T0.AddHours(2), ["src/A.cs"]);

        var (_, result) = await RunAsync(ci, new FixedReplyModel("I am not sure."));

        var row = Assert.Single(result.Rows);
        Assert.Equal("unparsed", row.Predicted);
        Assert.Equal("false", row.Correct);
        Assert.Contains("parse failure", row.Note, StringComparison.Ordinal);
        Assert.Equal(FinalizationOutcome.Quarantined, Assert.Single(result.Triages).Finalized);
    }

    [Fact]
    public async Task A_budget_cap_stops_triage_cleanly_and_still_finalizes_what_ran()
    {
        var ci = ScriptedScenario.Create();

        var (_, result) = await RunAsync(ci, budget: Budget.Default with { MaxModelCalls = 7 });

        Assert.NotNull(result.StoppedReason);
        Assert.Contains(result.Rows, row => row.Predicted == "skipped: stopped");
        Assert.All(result.Triages.Where(t => t.Prediction is not null), t => Assert.NotNull(t.Finalized));
        Assert.True(result.ModelCalls <= 7);
    }

    [Fact]
    public async Task A_provider_that_keeps_failing_stops_the_pass_after_three_errors()
    {
        var (_, result) = await RunAsync(ScriptedScenario.Create(), new ThrowingModel());

        Assert.Equal($"{Replay.MaxErrorsInARow} triages in a row ended in a provider error.", result.StoppedReason);
        Assert.Equal(Replay.MaxErrorsInARow, result.Rows.Count(row => row.Note.StartsWith("error: InvalidOperationException", StringComparison.Ordinal)));
        Assert.Contains(result.Rows, row => row.Predicted == "skipped: stopped");
    }

    [Fact]
    public async Task An_errored_triage_is_not_finalized_has_no_arm_and_stays_pending_in_the_library()
    {
        var ci = new FakeCi();
        var log = ScriptedScenario.CompileLog("Area0");
        string[] changed = ["src/AgentExperience.Core/Area0/Area0Options.cs"];
        ci.AddFailure("feat/a", "a1", T0.AddHours(1), changed, log, "a2", T0.AddHours(2), changed);
        var b = ci.AddFailure("feat/b", "b1", T0.AddHours(3), changed, log, "b2", T0.AddHours(4), changed);

        var (_, result) = await RunAsync(ci, new ThrowOnBranchModel("feat/b"));

        var row = result.Rows.Single(r => r.RunId == b.Id);
        Assert.StartsWith("error: InvalidOperationException", row.Note, StringComparison.Ordinal);
        Assert.Equal("-", row.Arm);
        Assert.Equal("-", row.Correct);
        Assert.DoesNotContain("finalized", row.Note, StringComparison.Ordinal);
        var triage = result.Triages.Single(t => t.Failure.Run.Id == b.Id);
        Assert.True(triage.Errored);
        Assert.Null(triage.Finalized);
        Assert.NotEmpty(triage.NamedExperienceIds);          // it had a lesson, so the holdout assigned it
        Assert.Equal(1, result.Estimate.Overall.Pending);    // never finalized: Pending, in neither arm's rates
        Assert.Equal(0, result.Estimate.Overall.Treatment.Runs + result.Estimate.Overall.Holdout.Runs);
        Assert.False(row.Final);
    }

    [Fact]
    public async Task A_skipped_failure_does_not_reset_the_count_of_provider_errors()
    {
        var ci = new FakeCi();
        ci.AddFailure("feat/1", "e1", T0.AddHours(1), ["a.cs"], "error x", "g1", T0.AddHours(20), ["a.cs"]);
        ci.AddFailure("feat/2", "n2", T0.AddHours(2), ["a.cs"], null, "g2", T0.AddHours(20), ["a.cs"]);
        ci.AddFailure("feat/3", "e3", T0.AddHours(3), ["a.cs"], "error x", "g3", T0.AddHours(20), ["a.cs"]);
        ci.AddFailure("feat/4", "e4", T0.AddHours(4), ["a.cs"], "error x", "g4", T0.AddHours(20), ["a.cs"]);
        var last = ci.AddFailure("feat/5", "e5", T0.AddHours(5), ["a.cs"], "error x", "g5", T0.AddHours(20), ["a.cs"]);

        var (_, result) = await RunAsync(ci, new ThrowingModel());

        Assert.NotNull(result.StoppedReason);
        Assert.Equal("skipped: stopped", result.Rows.Single(r => r.RunId == last.Id).Predicted);
    }

    [Fact]
    public async Task A_resumed_pass_skips_failures_before_the_latest_fix_an_earlier_pass_finalized()
    {
        var ci = new FakeCi();
        var log = ScriptedScenario.CompileLog("Area0");
        string[] changed = ["src/AgentExperience.Core/Area0/Area0Options.cs"];
        var a = ci.AddFailure("feat/a", "a1", T0.AddHours(1), changed, log, "a2", T0.AddHours(5), changed);
        var b = ci.AddFailure("feat/b", "b1", T0.AddHours(3), changed, log, "b2", T0.AddHours(6), changed);
        var c = ci.AddFailure("feat/c", "c1", T0.AddHours(7), changed, log, "c2", T0.AddHours(8), changed);
        LedgerRow[] earlier = [new(a.Id, a.CompletedAt, "feat/a", "code", "none", "code:x", "true", 0m, "finalized Validated")];

        var (failures, result) = await RunAsync(ci, ci, null, null, earlier);

        Assert.Equal(T0.AddHours(5), TriagePass.ResumeCutoff(failures, earlier));
        Assert.DoesNotContain(result.Rows, r => r.RunId == a.Id);
        var skipped = result.Rows.Single(r => r.RunId == b.Id);
        Assert.Equal("skipped: before-resume", skipped.Predicted);
        Assert.True(skipped.Final);
        Assert.Contains(result.Triages, t => t.Failure.Run.Id == c.Id);
    }

    [Fact]
    public async Task The_tools_only_ever_read_the_failing_commit()
    {
        var ci = ScriptedScenario.Create();
        var recorder = new RecordingRepository(ci);

        var (failures, result) = await RunAsync(ci, recorder, null, null, []);

        Assert.NotEmpty(recorder.ToolShas);
        var failing = failures.Select(f => f.Run.HeadSha).ToHashSet();
        Assert.All(recorder.ToolShas, sha => Assert.Contains(sha, failing));
        Assert.Contains(recorder.ToolShas, sha => sha.StartsWith("fail-", StringComparison.Ordinal));
        Assert.True(result.Triages.Count > 0);
    }

    private static Task<(IReadOnlyList<CiFailure>, ReplayResult)> RunAsync(FakeCi ci, IChatClient? model = null, Budget? budget = null) =>
        RunAsync(ci, ci, model, budget, []);

    private static Task<(IReadOnlyList<CiFailure>, ReplayResult)> RunAsync(FakeCi ci, IRepository repository, IChatClient? model, Budget? budget, IReadOnlyList<LedgerRow> earlier) =>
        TriagePass.RunAsync(ci, repository, model ?? new ScriptedTriageModel(), budget ?? Budget.Default, null, earlier, TriagePass.SeededIds(7), [], CancellationToken.None);

    private sealed class RecordingRepository(IRepository inner) : IRepository
    {
        public List<string> ToolShas { get; } = [];

        public Task<IReadOnlyList<string>?> ChangedFilesAsync(string sha, CancellationToken cancellationToken)
        {
            ToolShas.Add(sha);
            return inner.ChangedFilesAsync(sha, cancellationToken);
        }

        public Task<IReadOnlyList<string>?> DiffNamesAsync(string fromSha, string toSha, CancellationToken cancellationToken) =>
            inner.DiffNamesAsync(fromSha, toSha, cancellationToken);

        public Task<string?> ShowFileAsync(string sha, string path, CancellationToken cancellationToken)
        {
            ToolShas.Add(sha);
            return inner.ShowFileAsync(sha, path, cancellationToken);
        }
    }

    private sealed class ThrowOnBranchModel(string branch) : IChatClient
    {
        private readonly ScriptedTriageModel _scripted = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            return list.Any(m => m.Text.Contains("branch " + branch + ".", StringComparison.Ordinal))
                ? throw new InvalidOperationException("HTTP 502")
                : _scripted.GetResponseAsync(list, options, cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingModel : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("HTTP 401");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FixedReplyModel(string reply) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

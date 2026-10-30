using AgentExperience.Core.Capture;
using AgentExperience.Core.Sanitization;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Story 28.4: capture keeps a host-vouched recipe excerpt only for a call that returned, sanitized as a result is,
/// then cut to <see cref="CaptureLimits.MaxRecipeExcerptLength"/>, never between a surrogate pair.
/// </summary>
public class RecipeExcerptCaptureTests
{
    private static SanitizationOptions Options(bool valueIsSecret = false, int maxValueLength = 10_000) =>
        new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
        {
            ["ToolArguments"] = new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), 5, 20, 10_000, 100),
            ["ToolResult"] = new(
                new HashSet<string>(StringComparer.Ordinal) { "value" },
                valueIsSecret ? new HashSet<string>(StringComparer.Ordinal) { "value" } : new HashSet<string>(StringComparer.Ordinal),
                2,
                5,
                maxValueLength,
                100),
        });

    private static async Task<(ToolCallRecord Call, AppendAttemptResult Result)> CaptureAsync(
        string? excerpt,
        string? error = null,
        int maxExcerpt = CaptureLimits.DefaultMaxRecipeExcerptLength,
        SanitizationOptions? sanitization = null)
    {
        var service = new InMemoryExperienceCaptureService(
            new DefaultSanitizer(sanitization ?? Options()),
            new CaptureLimits(10, 10, 10_000, 10_000) { MaxRecipeExcerptLength = maxExcerpt });
        var runId = Guid.NewGuid();
        service.StartRun(
            runId,
            "task-1",
            "a task",
            new Scope("tenant-1", "app-1", "project-1"),
            new EnvironmentFingerprint("host", "net10.0", "os", null, new Dictionary<string, string>()),
            new Provenance("tests", "1", DateTimeOffset.UnixEpoch, null),
            DateTimeOffset.UnixEpoch);
        var call = new RawToolCall(Guid.NewGuid(), "pin_dependency", new Dictionary<string, object?>(), DateTimeOffset.UnixEpoch, TimeSpan.Zero, error is null ? "pinned lib to 2.4.1" : null, error)
        {
            RecipeExcerpt = excerpt,
        };
        var result = await service.AppendAttemptAsync(runId, new AppendAttemptRequest(Guid.NewGuid(), DateTimeOffset.UnixEpoch, TimeSpan.Zero, [call], "done", null));
        Assert.Equal(AppendAttemptOutcome.Recorded, result.Outcome);
        Assert.True(service.TryGetRun(runId, out var run));
        return (Assert.Single(Assert.Single(run!.Attempts).ToolCalls), result);
    }

    [Fact]
    public async Task A_returned_call_keeps_its_excerpt_and_a_call_without_one_has_none()
    {
        Assert.Equal("lib 2.4.1", (await CaptureAsync("lib 2.4.1")).Call.RecipeExcerpt);
        Assert.Null((await CaptureAsync(null)).Call.RecipeExcerpt);
        Assert.Null((await CaptureAsync(string.Empty)).Call.RecipeExcerpt);
    }

    [Fact]
    public async Task A_failed_call_never_gets_an_excerpt()
    {
        var (call, _) = await CaptureAsync("lib 2.4.1", error: "TimeoutException");
        Assert.Equal("TimeoutException", call.Error);
        Assert.Null(call.RecipeExcerpt);
    }

    [Fact]
    public async Task The_sanitizer_applies_a_redacted_excerpt_is_none_and_a_refused_one_is_dropped_without_the_attempt()
    {
        Assert.Null((await CaptureAsync("sk-live-secret", sanitization: Options(valueIsSecret: true))).Call.RecipeExcerpt);

        var (call, result) = await CaptureAsync(new string('x', 50), sanitization: Options(maxValueLength: 40));
        Assert.Null(call.RecipeExcerpt);
        Assert.Contains(result.TruncatedFields, field => field.FieldPath == "ToolCalls[0].RecipeExcerpt");
    }

    [Fact]
    public async Task A_long_excerpt_is_cut_after_sanitization_and_never_between_a_surrogate_pair()
    {
        var (cut, result) = await CaptureAsync("abcdefgh", maxExcerpt: 5);
        Assert.Equal("abcde", cut.RecipeExcerpt);
        Assert.Contains(result.TruncatedFields, field => field.FieldPath == "ToolCalls[0].RecipeExcerpt");

        // "abcd" + U+1F600 (two UTF-16 units): a cut at 5 would split the pair, so it keeps four.
        var (pair, _) = await CaptureAsync("abcd\U0001F600xyz", maxExcerpt: 5);
        Assert.Equal("abcd", pair.RecipeExcerpt);

        var (exact, exactResult) = await CaptureAsync("abcde", maxExcerpt: 5);
        Assert.Equal("abcde", exact.RecipeExcerpt);
        Assert.Empty(exactResult.TruncatedFields);
    }

    [Fact]
    public void The_bound_defaults_to_200_and_must_be_positive()
    {
        Assert.Equal(200, new CaptureLimits(1, 1, 1, 1).MaxRecipeExcerptLength);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureLimits(1, 1, 1, 1) { MaxRecipeExcerptLength = 0 });
    }
}

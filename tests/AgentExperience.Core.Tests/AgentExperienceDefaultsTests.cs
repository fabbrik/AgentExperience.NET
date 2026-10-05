using AgentExperience.Core.DependencyInjection;

namespace AgentExperience.Core.Tests;

/// <summary>
/// The one-call setup's defaults: no tool argument value is kept until the host allowlists it, results are kept, and
/// secret-named fields are redacted whatever their case.
/// </summary>
public sealed class AgentExperienceDefaultsTests
{
    private static readonly DefaultSanitizer Sanitizer = new(AgentExperienceDefaults.Sanitization);

    [Fact]
    public async Task No_tool_argument_value_is_kept_and_secret_named_ones_are_redacted()
    {
        var sanitized = await Sanitizer.SanitizeAsync(new RawPayload(SanitizationKinds.ToolArguments, new Dictionary<string, object?>
        {
            ["ticketId"] = "4812",
            ["query"] = "refund stuck on a lock",
            ["apiKey"] = "sk-not-a-real-key",
            ["PASSWORD"] = "hunter2",
            ["api_key"] = "also-secret",
            ["Authorization"] = "Bearer abc",
            ["connectionString"] = "Host=db;Password=x",
            ["token"] = "t",
            ["secret"] = "s",
        }));

        Assert.Equal(SanitizationDecision.Allowed, sanitized.Decision);
        Assert.DoesNotContain(sanitized.Fields.Values, value => value is string text && (text == "4812" || text.Contains("refund", StringComparison.Ordinal)));
        Assert.Equal(["query", "ticketId"], sanitized.OmittedFieldPaths.Order(StringComparer.Ordinal));
        Assert.Equal(7, sanitized.RedactedFieldPaths.Count);
        foreach (var value in sanitized.Fields.Values)
        {
            Assert.DoesNotContain(value as string ?? string.Empty, new[] { "sk-not-a-real-key", "hunter2", "also-secret", "Bearer abc", "Host=db;Password=x" });
        }
    }

    [Fact]
    public async Task A_result_is_kept()
    {
        var sanitized = await Sanitizer.SanitizeAsync(new RawPayload(SanitizationKinds.ToolResult, new Dictionary<string, object?>
        {
            [SanitizationKinds.ValueField] = "refund released",
        }));

        Assert.Equal(SanitizationDecision.Allowed, sanitized.Decision);
        Assert.Equal("refund released", sanitized.Fields[SanitizationKinds.ValueField]);
    }

    [Fact]
    public async Task Long_tool_output_and_arguments_are_truncated_or_omitted_not_rejected()
    {
        var service = new InMemoryExperienceCaptureService(Sanitizer, AgentExperienceDefaults.CaptureLimits);
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        service.StartRun(
            runId,
            "task",
            null,
            new Scope("tenant", "app", "project"),
            new EnvironmentFingerprint("host", "net10.0", "os", null, new Dictionary<string, string>()),
            new Provenance("test", null, now, null),
            now);
        var longText = new string('x', 10_000);

        var appended = await service.AppendAttemptAsync(runId, new AppendAttemptRequest(
            Guid.NewGuid(),
            now,
            TimeSpan.FromSeconds(1),
            [new RawToolCall(Guid.NewGuid(), "search", new Dictionary<string, object?> { ["query"] = longText }, now, TimeSpan.Zero, Result: longText, Error: longText)],
            Result: longText,
            Error: null));

        Assert.Equal(AppendAttemptOutcome.Recorded, appended.Outcome);
        Assert.True(service.TryGetRun(runId, out var run));
        var attempt = Assert.Single(run.Attempts);
        var call = Assert.Single(attempt.ToolCalls);
        Assert.Empty(call.Arguments);
        Assert.NotNull(call.Result);
        Assert.True(call.Result.Length <= AgentExperienceDefaults.CaptureLimits.MaxResultLength);
        Assert.True(call.Error!.Length <= AgentExperienceDefaults.CaptureLimits.MaxErrorLength);
        Assert.True(attempt.Result!.Length <= AgentExperienceDefaults.CaptureLimits.MaxResultLength);
        Assert.DoesNotContain(longText, call.Result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SanitizationAllowing_keeps_the_named_arguments_and_still_redacts_secrets()
    {
        var sanitizer = new DefaultSanitizer(AgentExperienceDefaults.SanitizationAllowing("ticketId", "apiKey"));

        var sanitized = await sanitizer.SanitizeAsync(new RawPayload(SanitizationKinds.ToolArguments, new Dictionary<string, object?>
        {
            ["ticketId"] = "4812",
            ["query"] = "refund",
            ["apiKey"] = "sk-not-a-real-key",
        }));

        Assert.Equal("4812", sanitized.Fields["ticketId"]);
        Assert.False(sanitized.Fields.ContainsKey("query"));
        Assert.NotEqual("sk-not-a-real-key", sanitized.Fields["apiKey"]);
    }

    [Fact]
    public void The_limits_and_policies_are_the_documented_ones()
    {
        Assert.Equal(new CaptureLimits(10, 50, 4_000, 4_000), AgentExperienceDefaults.CaptureLimits);
        Assert.Equal([SanitizationKinds.ToolArguments, SanitizationKinds.ToolResult], AgentExperienceDefaults.Sanitization.Policies.Keys.Order(StringComparer.Ordinal));

        foreach (var policy in AgentExperienceDefaults.Sanitization.Policies.Values)
        {
            Assert.Equal(4, policy.MaxDepth);
            Assert.Equal(50, policy.MaxFieldCount);
            Assert.Equal(1_000_000, policy.MaxValueLength);
            Assert.True(policy.SecretFieldNames.Contains("APIKEY"));
        }

        Assert.Empty(AgentExperienceDefaults.Sanitization.Policies[SanitizationKinds.ToolArguments].AllowedFieldNames);
        Assert.Equal([SanitizationKinds.ValueField], AgentExperienceDefaults.Sanitization.Policies[SanitizationKinds.ToolResult].AllowedFieldNames);

        // The kinds are the ones capture submits.
        Assert.Equal("ToolArguments", SanitizationKinds.ToolArguments);
        Assert.Equal("ToolResult", SanitizationKinds.ToolResult);
    }
}

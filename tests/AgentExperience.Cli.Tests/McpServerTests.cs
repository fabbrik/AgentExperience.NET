using System.IO.Pipelines;
using System.Text.Json;
using AgentExperience.Cli.Configuration;
using AgentExperience.Cli.Engine;
using AgentExperience.Cli.Mcp;
using AgentExperience.MicrosoftAgentFramework.Injection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentExperience.Cli.Tests;

/// <summary>
/// <c>aexp mcp</c> through the MCP SDK's own client, in process over a pair of pipes: the tools it lists, and the same
/// learn-then-recall scenario as the CLI's.
/// </summary>
public sealed class McpServerTests
{
    private const string Task = "Fix the failing build in the payments service";

    [Fact]
    public async Task It_lists_the_five_tools_and_says_their_output_is_untrusted()
    {
        using var project = new TempProject();
        await using var session = await McpTestSession.StartAsync(project);

        var tools = await session.Client.ListToolsAsync();
        Assert.Equal(
            ["experience_failure_hint", "experience_finish", "experience_lessons", "experience_recall", "experience_record_call"],
            tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
        foreach (var name in new[] { "experience_recall", "experience_failure_hint", "experience_lessons" })
        {
            Assert.Contains("untrusted reference material", tools.Single(tool => tool.Name == name).Description);
        }

        // No tool takes identity, scope, a path or a command.
        foreach (var tool in tools)
        {
            var parameters = tool.JsonSchema.TryGetProperty("properties", out var properties)
                ? properties.EnumerateObject().Select(property => property.Name).ToList()
                : [];
            Assert.DoesNotContain(parameters, name => name is "tenant" or "scope" or "project" or "user" or "path" or "file" or "command");
        }
    }

    [Fact]
    public async Task A_session_learns_and_a_later_session_is_given_the_lesson_and_the_hint()
    {
        using var project = new TempProject();
        await using (var first = await McpTestSession.StartAsync(project))
        {
            Assert.Equal("No verified lesson applies to this task.", await first.CallAsync("experience_recall", new() { ["task"] = Task }));
            await first.CallAsync("experience_record_call", new()
            {
                ["tool"] = "run_tests",
                ["arguments"] = new Dictionary<string, object?> { ["filter"] = "payments" },
                ["error"] = "Process exited with code 2: error CS0246",
            });
            await first.CallAsync("experience_record_call", new() { ["tool"] = "add_reference", ["result"] = "added Ledger" });
            await first.CallAsync("experience_record_call", new() { ["tool"] = "run_tests", ["result"] = "all passed" });
            project.Fix();
            var finished = await first.CallAsync("experience_finish", new());
            Assert.Contains("verifier exit 0 (pass); finalization Validated", finished);

            var lessons = await first.CallAsync("experience_lessons", new());
            Assert.Contains("Validated", lessons);
            Assert.Contains(HistoricalReferenceWriter.BlockBegin, lessons);
        }

        await using (var second = await McpTestSession.StartAsync(project))
        {
            var block = await second.CallAsync("experience_recall", new() { ["task"] = Task });
            Assert.StartsWith(HistoricalReferenceWriter.BlockBegin, block);
            Assert.Contains("add_reference [returned]", block);
        }

        await using (var third = await McpTestSession.StartAsync(project))
        {
            // No recall first: the run starts lazily with the task given here, and words that match nothing get no block.
            var hint = await third.CallAsync("experience_failure_hint", new()
            {
                ["tool"] = "run_tests",
                ["error"] = "exit 2",
                ["task"] = "Rotate the staging certificates",
            });
            Assert.StartsWith(HistoricalReferenceWriter.RecallBegin, hint);
            Assert.Contains("add_reference [returned]", hint);
        }
    }

    [Fact]
    public async Task Errors_are_tool_errors_and_the_server_keeps_serving()
    {
        using var project = new TempProject();
        await using var session = await McpTestSession.StartAsync(project);

        var finish = await session.Client.CallToolAsync("experience_finish", new Dictionary<string, object?>());
        Assert.True(finish.IsError == true);
        Assert.Contains("No run is open", Text(finish));

        var record = await session.Client.CallToolAsync("experience_record_call", new Dictionary<string, object?> { ["tool"] = "x", ["result"] = "y" });
        Assert.True(record.IsError == true);

        // Still serving.
        Assert.Equal("No verified lesson applies to this task.", await session.CallAsync("experience_recall", new() { ["task"] = Task }));
    }

    [Fact]
    public void Outputs_are_bounded()
    {
        var bounded = AexpMcpServer.Bound(new string('é', AexpMcpServer.MaxOutputBytes));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(bounded) <= AexpMcpServer.MaxOutputBytes);
        Assert.EndsWith(AexpMcpServer.Truncated, bounded);
        Assert.Equal("short", AexpMcpServer.Bound("short"));
    }

    private static string Text(CallToolResult result) => McpTestSession.Text(result);
}

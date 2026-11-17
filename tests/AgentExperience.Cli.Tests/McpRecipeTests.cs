using System.Text.RegularExpressions;

namespace AgentExperience.Cli.Tests;

/// <summary>Story 29.5: the documented any-MCP-client recipe stays true to the server it describes.</summary>
public sealed class McpRecipeTests
{
    [Fact]
    public async Task The_recipe_names_only_real_tools_and_the_sequence_it_describes_works_over_MCP()
    {
        var recipe = ReadRecipe();
        var named = Regex.Matches(recipe, @"experience_[a-z_]+").Select(match => match.Value).Distinct().ToList();

        using var project = new TempProject();
        await using var session = await McpTestSession.StartAsync(project);
        var offered = (await session.Client.ListToolsAsync()).Select(tool => tool.Name).ToHashSet();

        Assert.All(named, name => Assert.Contains(name, offered));
        // The recipe covers every tool of the loop; experience_lessons is documented outside it, for the operator.
        Assert.Equal(
            new[] { "experience_failure_hint", "experience_finish", "experience_recall", "experience_record_call" },
            named.Order(StringComparer.Ordinal));

        // The sequence the recipe gives, in its order: recall, a failed call with a hint, a call that works, finish.
        Assert.Equal("No verified lesson applies to this task.", await session.CallAsync("experience_recall", new() { ["task"] = "Fix the failing build" }));
        await session.CallAsync("experience_record_call", new() { ["tool"] = "run_tests", ["error"] = "exit 2: error CS0246" });
        await session.CallAsync("experience_failure_hint", new() { ["tool"] = "run_tests", ["error"] = "exit 2: error CS0246" });
        await session.CallAsync("experience_record_call", new() { ["tool"] = "run_tests", ["result"] = "all passed" });
        project.Fix();
        Assert.Contains("verifier exit 0 (pass)", await session.CallAsync("experience_finish", new()));
    }

    private static string ReadRecipe()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentExperience.NET.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var text = File.ReadAllText(Path.Combine(directory.FullName, "docs", "guide", "cli-and-mcp.md"));
        var begin = text.IndexOf("<!-- recipe:begin -->", StringComparison.Ordinal);
        var end = text.IndexOf("<!-- recipe:end -->", StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin, "The recipe markers are missing from docs/guide/cli-and-mcp.md.");
        return text[begin..end];
    }
}

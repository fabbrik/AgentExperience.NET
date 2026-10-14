using System.Runtime.CompilerServices;
using System.Text;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.Tests.Shared;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// The README's first screen: its setup is <see cref="ReadmeQuickStart"/>, which this project compiles and runs; its
/// shortened "What an agent sees" block is cut from the block that setup's second run really hands the model; and the
/// measured result and the comparison link sit within <see cref="ReadmeFirstScreen.Lines"/> lines.
/// </summary>
/// <remarks>
/// To change the README block, change <c>ReadmeQuickStart.cs</c> first, then paste the block this test reports into the
/// README. The shortened block is checked against the live block rather than a golden file, because the block's
/// <c>Preconditions:</c> lines name the machine's runtime and operating system.
/// </remarks>
public class ReadmeQuickStartTests
{
    private const string SourceResourceName = "AgentExperience.MicrosoftAgentFramework.Tests.ReadmeQuickStart.cs";
    private const string ReadmeResourceName = "AgentExperience.MicrosoftAgentFramework.Tests.RootREADME.md";
    private const string Begin = "// README: begin";
    private const string End = "// README: end";

    [Fact]
    public void The_readme_shows_the_compiled_quick_start_verbatim_on_its_first_screen()
    {
        var block = ReadmeBlock();
        var readme = Read(ReadmeResourceName).Replace("\r\n", "\n", StringComparison.Ordinal);

        var fenced = "```csharp\n" + block + "```\n";
        var index = readme.IndexOf(fenced, StringComparison.Ordinal);
        Assert.True(index >= 0, "The README does not show ReadmeQuickStart.cs's block verbatim. Expected:\n" + fenced);

        var lastLine = readme.AsSpan(0, index + fenced.Length - 1).Count('\n') + 1;
        Assert.True(lastLine <= ReadmeFirstScreen.Lines, $"The setup block ends on README line {lastLine}, past the first screen.");
    }

    [Fact]
    public async Task The_second_run_gets_the_first_runs_lesson_and_the_readme_shows_it_shortened()
    {
        // The block reads the environment from these variables (see ReadmeQuickStart's remarks); it cannot be given one.
        foreach (var variable in new[] { "DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            Assert.True(
                string.IsNullOrEmpty(value) || value.Equals("Development", StringComparison.OrdinalIgnoreCase) || value.Equals("Test", StringComparison.OrdinalIgnoreCase) || value.Equals("Testing", StringComparison.OrdinalIgnoreCase),
                $"{variable}={value}: the README quick start runs only in Development, Test or Testing.");
        }

        var model = new TwoCallModel();

        await ReadmeQuickStart.RunAsync(model, ["exit=3", "exit=0", "exit=0", "exit=0"]);

        Assert.Equal(2, model.FirstCalls.Count);
        Assert.DoesNotContain(model.FirstCalls[0], IsHistoricalReference);
        var block = Assert.Single(model.FirstCalls[1], IsHistoricalReference).Text;
        Assert.Contains("run_tests [failed: exit 3], run_tests [returned]", block, StringComparison.Ordinal);

        var readme = ReadmeFirstScreen.Split(Read(ReadmeResourceName));
        var start = ReadmeFirstScreen.LineOf(readme, line => line == "## What an agent sees");
        Assert.True(start > 0, "The README has no 'What an agent sees' section.");
        var open = Array.IndexOf(readme, "```text", start);
        var close = Array.IndexOf(readme, "```", open + 1);
        Assert.True(open > 0 && close > open, "'What an agent sees' has no ```text block.");
        Assert.True(close + 1 <= ReadmeFirstScreen.Lines, $"The shortened block ends on README line {close + 1}, past the first screen.");

        ReadmeFirstScreen.AssertExcerptOf(readme[(open + 1)..close], block.TrimEnd('\n').Split('\n'));
    }

    [Fact]
    public void The_measured_result_and_the_comparison_link_are_on_the_first_screen()
    {
        var readme = ReadmeFirstScreen.Split(Read(ReadmeResourceName));

        var header = ReadmeFirstScreen.LineOf(readme, line => line.StartsWith("| Model | No memory | Memory |", StringComparison.Ordinal));
        Assert.True(header > 0, "The README has no measured-result table.");
        var last = header;
        while (last < readme.Length && readme[last].StartsWith('|'))
        {
            last++;
        }

        Assert.True(last - header >= 3, "The measured-result table has fewer than two models.");
        Assert.True(last <= ReadmeFirstScreen.Lines, $"The measured-result table ends on README line {last}, past the first screen.");

        var link = ReadmeFirstScreen.LineOf(readme, line => line.Contains("(docs/compared-with-maf-memory.md)", StringComparison.Ordinal));
        Assert.True(link is > 0 and <= ReadmeFirstScreen.Lines, $"The comparison link is on README line {link}, not on the first screen.");
    }

    /// <summary>The source's <c>using</c> lines, a blank line, then the marked region with one indent level removed.</summary>
    private static string ReadmeBlock()
    {
        var lines = Read(SourceResourceName).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var usings = lines.TakeWhile(line => line.StartsWith("using ", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, usings.Count);

        var start = Array.FindIndex(lines, line => line.Trim() == Begin);
        var end = Array.FindIndex(lines, line => line.Trim() == End);
        Assert.True(start > 0 && end > start, "ReadmeQuickStart.cs has lost its README markers.");

        var indent = lines[start].Length - lines[start].TrimStart().Length;
        var body = lines[(start + 1)..end].Select(line => line.Length >= indent ? line[indent..] : line.TrimStart());

        var block = new StringBuilder();
        foreach (var line in usings.Append(string.Empty).Concat(body))
        {
            block.Append(line).Append('\n');
        }

        return block.ToString();
    }

    private static bool IsHistoricalReference(ChatMessage message) =>
        message.AdditionalProperties?.ContainsKey(ExperienceContextProvider.HistoricalReferenceKey) == true;

    private static string Read(string name)
    {
        using var stream = typeof(ReadmeQuickStartTests).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"'{name}' is not embedded in the test assembly.");
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return reader.ReadToEnd();
    }

    /// <summary>A model that calls <c>run_tests</c> twice, one call per turn, then answers.</summary>
    private sealed class TwoCallModel : IChatClient
    {
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

            return Task.FromResult(new ChatResponse(results < 2
                ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call-{results}", "run_tests", new Dictionary<string, object?>())])
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
}

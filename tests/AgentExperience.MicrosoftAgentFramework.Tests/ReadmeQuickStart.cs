using AgentExperience.MicrosoftAgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// The repository README's quick-start block, compiled. <see cref="ReadmeQuickStartTests"/> checks that the README shows
/// this file's four <c>using</c> lines and the lines between the <c>README: begin</c> and <c>README: end</c> markers
/// (one indent level removed) verbatim, so the block a reader copies is the one this commit compiles and runs.
/// </summary>
/// <remarks>
/// The block builds its own container, so nothing here can register an <c>IHostEnvironment</c> into it without changing
/// what the README shows: <c>UseDevelopmentIdentity()</c> and the in-memory storage therefore read
/// <c>DOTNET_ENVIRONMENT</c>, else <c>ASPNETCORE_ENVIRONMENT</c>, and run when neither is set or either names
/// <c>Development</c>, <c>Test</c> or <c>Testing</c>. <see cref="ReadmeQuickStartTests"/> checks that before running it.
/// </remarks>
internal static class ReadmeQuickStart
{
    /// <summary>Runs the README block; <paramref name="testRuns"/> is what <c>run_tests</c> returns, call by call.</summary>
    public static async Task RunAsync(IChatClient chatClient, IEnumerable<string> testRuns)
    {
        var results = new Queue<string>(testRuns);

        // README: begin
        var services = new ServiceCollection();
        services.AddAgentExperience(options =>
        {
            options.UseDevelopmentIdentity();                                 // one local identity: development and tests only
            options.Verify = ExperienceVerifiers.ToolSucceeded("run_tests");  // your own check decides, never the model
            options.Capture = capture =>                                      // "exit=3" in the tool's result marks it failed
                capture.ClassifyToolResult = ExperienceToolResultClassifiers.ExitCode("run_tests");
        }).UseInMemoryStorageForDevelopment();
        await using var provider = services.BuildServiceProvider();
        AIAgent agent = new ChatClientAgent(chatClient, name: "triage-ticket", tools: [AIFunctionFactory.Create(RunTestsAsync, "run_tests")])
            .AsBuilder().UseAgentExperience(provider).Build();
        await agent.RunAsync("Ticket #4812: a refund is stuck on a lock. Triage it.");
        await agent.RunAsync("Ticket #4977: a refund is stuck on a lock again. Triage it.");  // gets the lesson
        // README: end

        // The reader's own test run, here a stand-in that returns the next given result.
        Task<string> RunTestsAsync() => Task.FromResult(results.Dequeue());
    }
}

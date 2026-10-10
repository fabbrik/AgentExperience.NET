using System.ClientModel;
using System.Text;
using AgentExperience.Abstractions;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Verification;
using AgentExperience.MicrosoftAgentFramework;
using AgentExperience.Sample.QuickStart;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;

// The model. By default a scripted stand-in (ScriptedModel.cs): no key, no network, the same output every time.
// With OPENAI_API_KEY set, a real model on any OpenAI-compatible endpoint: OPENAI_MODEL picks it, OPENAI_BASE_URL
// points elsewhere (Gemini's or Ollama's OpenAI-compatible URL, for example). The key is never printed.
Console.OutputEncoding = Encoding.UTF8;
var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
var modelName = Environment.GetEnvironmentVariable("OPENAI_MODEL") is { Length: > 0 } named ? named : "gpt-4.1-mini";
var clientOptions = new OpenAIClientOptions();
if (Environment.GetEnvironmentVariable("OPENAI_BASE_URL") is { Length: > 0 } baseUrl)
{
    clientOptions.Endpoint = new Uri(baseUrl);
}

using var model = string.IsNullOrEmpty(key)
    ? new ScriptedModel()
    : new OpenAIClient(new ApiKeyCredential(key), clientOptions).GetChatClient(modelName).AsIChatClient();
var label = string.IsNullOrEmpty(key)
    ? QuickStart.StandInLabel
    : $"{modelName} at {clientOptions.Endpoint?.Host ?? "api.openai.com"}";

await QuickStart.RunAsync(model, label, Console.Out);

/// <summary>The README quick start's wiring, run twice on the same ticket.</summary>
public static class QuickStart
{
    /// <summary>The task, word for word the same in both runs: today's text search needs every word of it to match.</summary>
    public const string Ticket = "Ticket #4812: a refund is stuck on a lock. Triage it.";

    public const string StandInLabel = "a scripted stand-in, not a real model (set OPENAI_API_KEY to use one)";

    public static async Task<IReadOnlyList<QuickStartRun>> RunAsync(IChatClient model, string modelLabel, TextWriter output)
    {
        var desk = new RefundDesk();   // the tool, and the state Verify reads
        var services = new ServiceCollection();
        services.AddAgentExperience(options =>
        {
            // Who the run is for: from your own authentication, never from model output.
            options.ResolveIdentity = (context, cancellationToken) => ValueTask.FromResult<ExperienceIdentity?>(new(
                new AuthorizationContext("contoso", "svc-support-agent", Roles: [], IssuedAt: DateTimeOffset.UtcNow),
                new Scope("contoso", "support", "tickets")));
            options.TaskId = "triage-ticket";
            // The host's own check: is the refund released? The desk's state decides, never the model's word.
            options.Verify = (context, cancellationToken) =>
            {
                var result = desk.RefundReleased ? CheckResult.Pass : CheckResult.Fail;
                return ValueTask.FromResult<ExperienceVerification?>(new(
                    RequiredChecks: [new RequiredCheck("refund-released", "ToolExitCode")],
                    Evidence: [context.CreateEvidence("refund-released", "ToolExitCode", result, producer: "refund-desk", "quick-start")],
                    ArtifactRevision: "quick-start"));
            };
            // Keep the strategy argument at capture, and let the injected Tried: line show it.
            options.Sanitization = AgentExperienceDefaults.SanitizationAllowing("strategy");
            options.Injection = injection => injection.ApproachArguments[RefundDesk.ToolName] = ["strategy"];
            // A fixed environment, so the output is the same on every machine. A real host keeps the default.
            options.Capture = capture => capture.Environment = RefundDesk.Environment;
        }).UseInMemoryStorageForDevelopment();
        await using var provider = services.BuildServiceProvider();

        var log = new ModelInputLog(model);   // keeps what the model was sent, to print it below
        var injection = provider.GetAgentExperienceContextProvider();
        AIAgent agent = new ChatClientAgent(log, new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions
            {
                Instructions = "You triage refund tickets. Release a stuck refund with run_refund_check, one strategy per call, "
                    + "and stop once a call exits 0.",
                Tools = [desk.Tool],
            },
            AIContextProviders = [injection],
        }).AsBuilder().UseAgentExperience(provider).Build();

        await output.WriteLineAsync($"Model: {modelLabel}");
        await output.WriteLineAsync($"Task (both runs): {Ticket}");
        await output.WriteLineAsync();

        var runs = new List<QuickStartRun>();
        for (var number = 1; number <= 2; number++)
        {
            desk.Reset();
            log.Reset();
            await agent.RunAsync(Ticket);

            var run = new QuickStartRun([.. desk.Attempts], log.Block);
            runs.Add(run);
            var tried = run.Attempts.Count == 0
                ? "nothing"
                : string.Join(" → ", run.Attempts.Select(attempt => attempt.Strategy + (attempt.Succeeded ? " ✓" : " ✗")));
            var failed = run.FailedAttempts == 1 ? "1 failed attempt" : $"{run.FailedAttempts} failed attempts";
            await output.WriteLineAsync($"Run {number} ({(run.Block is null ? "no memory yet" : "lesson injected")}): tried {tried}  ({failed})");
        }

        await output.WriteLineAsync();
        await output.WriteLineAsync("What run 2's model was handed, besides the ticket:");
        await output.WriteLineAsync();
        await output.WriteLineAsync(runs[1].Block?.TrimEnd() ?? "(nothing: no lesson was injected)");
        await output.WriteLineAsync();
        await output.WriteLineAsync("Run 1 was verified by the desk's own state and stored as a lesson; run 2 found it by its task text.");
        await output.WriteLineAsync("With the stand-in, this shows the loop working, not that it helps a real model: see \"Does it help?\"");
        await output.WriteLineAsync("in the repository README for a live experiment.");
        return runs;
    }
}

/// <summary>One run: every call it made, and the Historical Reference block its model was sent, if any.</summary>
public sealed record QuickStartRun(IReadOnlyList<RefundAttempt> Attempts, string? Block)
{
    public int FailedAttempts => Attempts.Count(attempt => !attempt.Succeeded);
}

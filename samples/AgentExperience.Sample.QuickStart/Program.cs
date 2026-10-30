using System.ClientModel;
using System.Text;
using AgentExperience.Abstractions;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Verification;
using AgentExperience.MicrosoftAgentFramework;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.Sample.QuickStart;
using AgentExperience.Storage.Sqlite;
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

// --recall shows recall at the moment of failure instead: run 2 is a different ticket, so no lesson is injected before
// it starts, and the lesson reaches the model when its first call fails the way run 1's did.
// --recipe shows the recipe: one AgentExperienceOptions.Recipe call consents to the strategy argument and to an excerpt
// of what the working call returned, and run 2's block shows that excerpt on the attempt that worked.
if (args.Contains("--recall", StringComparer.Ordinal))
{
    await QuickStart.RunRecallAsync(model, label, Console.Out);
}
else if (args.Contains("--recipe", StringComparer.Ordinal))
{
    await QuickStart.RunRecipeAsync(model, label, Console.Out);
}
else if (Array.IndexOf(args, "--sqlite") is var at and >= 0)
{
    // --sqlite <path>: one run per process, its memory in a local file. Run the sample twice: the second process is handed
    // the lesson the first one stored.
    if (at + 1 >= args.Length)
    {
        Console.Error.WriteLine("--sqlite needs a database file path.");
        return 1;
    }

    await QuickStart.RunSqliteAsync(model, label, Console.Out, args[at + 1]);
}
else
{
    await QuickStart.RunAsync(model, label, Console.Out);
}

return 0;

/// <summary>The README quick start's wiring, run twice on the same ticket.</summary>
public static class QuickStart
{
    /// <summary>The task, word for word the same in both runs: today's text search needs every word of it to match.</summary>
    public const string Ticket = "Ticket #4812: a refund is stuck on a lock. Triage it.";

    public const string StandInLabel = "a scripted stand-in, not a real model (set OPENAI_API_KEY to use one)";

    /// <summary>Run 2's ticket under <c>--recall</c>: none of its words is in run 1's ticket or lesson, so no block is injected.</summary>
    public const string OtherTicket = "Payout #5120 blocked overnight; please unblock.";

    /// <summary>The strategy that works under <c>--recall</c>: the last one the stand-in would try on its own.</summary>
    public const string RecallWorkingStrategy = "skip-ledger-check";

    public static Task<IReadOnlyList<QuickStartRun>> RunAsync(IChatClient model, string modelLabel, TextWriter output) =>
        RunAsync(model, modelLabel, output, recall: false);

    /// <summary>
    /// <c>--recipe</c>: the same two runs, wired with one <see cref="AgentExperienceOptions.Recipe"/> call in place of the
    /// separate sanitization and injection allowlists. It also vouches for the working call's result, so run 2's block
    /// shows <c>[returned: "exit=0 the refund is released"]</c> on the attempt that worked.
    /// </summary>
    public static Task<IReadOnlyList<QuickStartRun>> RunRecipeAsync(IChatClient model, string modelLabel, TextWriter output) =>
        RunAsync(model, modelLabel, output, recall: false, recipe: true);

    /// <summary>
    /// <c>--recall</c>: the same wiring with <c>options.Recall</c> on. Run 1 learns on <see cref="Ticket"/>; run 2 is
    /// <see cref="OtherTicket"/>, whose words match nothing stored, so it starts with no lesson, and is shown run 1's fix
    /// when its first call fails the same way.
    /// </summary>
    public static Task<IReadOnlyList<QuickStartRun>> RunRecallAsync(IChatClient model, string modelLabel, TextWriter output) =>
        RunAsync(model, modelLabel, output, recall: true);

    /// <summary>
    /// <c>--sqlite &lt;path&gt;</c>: the same wiring with <c>UseSqlite(path)</c>, and one run of <see cref="Ticket"/> per call,
    /// as one process would make it. The first process learns and stores the lesson in the file; the next one, with
    /// nothing in memory, is handed it.
    /// </summary>
    public static async Task<QuickStartRun> RunSqliteAsync(IChatClient model, string modelLabel, TextWriter output, string databasePath)
    {
        // Explicit, once per start: journaled and idempotent, so it applies nothing when the file is current.
        await ExperienceSqliteSchema.MigrateAsync(databasePath);
        return (await RunAsync(model, modelLabel, output, recall: false, sqlitePath: databasePath))[0];
    }

    private static async Task<IReadOnlyList<QuickStartRun>> RunAsync(IChatClient model, string modelLabel, TextWriter output, bool recall, bool recipe = false, string? sqlitePath = null)
    {
        // The tool, and the state Verify reads. Under --recall the working strategy is the last one tried, so the hint shows.
        var desk = recall ? new RefundDesk(RecallWorkingStrategy) : new RefundDesk();
        var services = new ServiceCollection();
        var builder = services.AddAgentExperience(options =>
        {
            // Who the run is for: one fixed local identity, refused outside Development/Test. In production set
            // options.ResolveIdentity from your own authentication, never from model output.
            options.UseDevelopmentIdentity();
            // The host's own check: is the refund released? The desk's state decides, never the model's word.
            options.Verify = (context, cancellationToken) =>
            {
                var result = desk.RefundReleased ? CheckResult.Pass : CheckResult.Fail;
                return ValueTask.FromResult<ExperienceVerification?>(new(
                    RequiredChecks: [new RequiredCheck("refund-released", "ToolExitCode")],
                    Evidence: [context.CreateEvidence("refund-released", "ToolExitCode", result, producer: "refund-desk", "quick-start")],
                    ArtifactRevision: "quick-start"));
            };
            if (recipe)
            {
                // The recipe: one place to consent to what a later run may see of how this tool worked -- the strategy
                // argument, and the exit line the working call returned (a known, short shape; never free text).
                options.Recipe(RefundDesk.ToolName, arguments: ["strategy"], resultExcerpt: call => call.Result?.ToString());
            }
            else
            {
                // Keep the strategy argument at capture, and let the injected Tried: line show it.
                options.Sanitization = AgentExperienceDefaults.SanitizationAllowing("strategy");
                options.Injection = injection => injection.ApproachArguments[RefundDesk.ToolName] = ["strategy"];
            }

            // A fixed environment, so the output is the same on every machine. A real host keeps the default.
            options.Capture = capture => capture.Environment = RefundDesk.Environment;
            // Recall at the moment of failure: off by default.
            if (recall)
            {
                options.Recall = new ExperienceRecallOptions();
            }
        });
        if (sqlitePath is null)
        {
            builder.UseInMemoryStorageForDevelopment();
        }
        else
        {
            // Durable and local: the next process finds the lesson in the same file.
            builder.UseSqlite(sqlitePath);
        }

        await using var provider = services.BuildServiceProvider();

        var log = new ModelInputLog(model);   // keeps what the model was sent, to print it below
        // The tool throws on a wrong strategy, so a failed call is captured as failed. Let the model read the exit text,
        // and let it try every strategy without the function loop giving up on consecutive errors.
        var functions = new FunctionInvokingChatClient(log)
        {
            IncludeDetailedErrors = true,
            MaximumConsecutiveErrorsPerRequest = RefundDesk.Strategies.Count + 1,
        };
        // The agent's name is the task identifier its runs are stored under. UseAgentExperience wires capture and
        // injection both.
        AIAgent agent = new ChatClientAgent(functions, new ChatClientAgentOptions
        {
            Name = "triage-ticket",
            ChatOptions = new ChatOptions
            {
                Instructions = "You triage refund tickets. Release a stuck refund with run_refund_check, one strategy per call, "
                    + "and stop once a call exits 0.",
                Tools = [desk.Tool],
            },
        }).AsBuilder().UseAgentExperience(provider).Build();

        await output.WriteLineAsync($"Model: {modelLabel}");
        if (sqlitePath is not null)
        {
            return [await RunOnceAsync(agent, desk, log, output)];
        }

        await output.WriteLineAsync(recall ? $"Task (run 1): {Ticket}" : $"Task (both runs): {Ticket}");
        if (recall)
        {
            await output.WriteLineAsync($"Task (run 2): {OtherTicket}");
        }

        await output.WriteLineAsync();

        var runs = new List<QuickStartRun>();
        for (var number = 1; number <= 2; number++)
        {
            desk.Reset();
            log.Reset();
            await agent.RunAsync(recall && number == 2 ? OtherTicket : Ticket);

            var run = new QuickStartRun([.. desk.Attempts], log.Block) { Hint = log.Hint };
            runs.Add(run);
            var tried = run.Attempts.Count == 0
                ? "nothing"
                : string.Join(" → ", run.Attempts.Select(attempt => attempt.Strategy + (attempt.Succeeded ? " ✓" : " ✗")));
            var failed = run.FailedAttempts == 1 ? "1 failed attempt" : $"{run.FailedAttempts} failed attempts";
            var memory = run.Block is not null ? "lesson injected" : run.Hint is not null ? "hint after a failure" : number == 1 ? "no memory yet" : "no lesson matched the ticket";
            await output.WriteLineAsync($"Run {number} ({memory}): tried {tried}  ({failed})");
        }

        if (recall)
        {
            await output.WriteLineAsync();
            await output.WriteLineAsync("What run 2's model was shown after its first call failed, in its instructions:");
            await output.WriteLineAsync();
            await output.WriteLineAsync(runs[1].Hint?.TrimEnd() ?? "(nothing: no hint was given)");
            await output.WriteLineAsync();
            await output.WriteLineAsync("Run 2's words matched no stored lesson, so nothing was injected before it started; its first failure had");
            await output.WriteLineAsync("the same signature as one run 1 recovered from, so recall named what worked after it.");
            return runs;
        }

        await output.WriteLineAsync();
        await output.WriteLineAsync("What run 2's model was handed, besides the ticket:");
        await output.WriteLineAsync();
        await output.WriteLineAsync(runs[1].Block?.TrimEnd() ?? "(nothing: no lesson was injected)");
        await output.WriteLineAsync();
        if (recipe)
        {
            await output.WriteLineAsync("The recipe: the working call's own result, vouched for by the host at capture, is shown on the attempt");
            await output.WriteLineAsync("that worked; a failed call never carries one.");
        }

        await output.WriteLineAsync("Run 1 was verified by the desk's own state and stored as a lesson; run 2 found it by its task text.");
        await output.WriteLineAsync("With the stand-in, this shows the loop working, not that it helps a real model: see \"Does it help?\"");
        await output.WriteLineAsync("in the repository README for a live experiment.");
        return runs;
    }

    /// <summary>One run of <see cref="Ticket"/> under <c>--sqlite</c>, and what it was handed.</summary>
    private static async Task<QuickStartRun> RunOnceAsync(AIAgent agent, RefundDesk desk, ModelInputLog log, TextWriter output)
    {
        await output.WriteLineAsync($"Task: {Ticket}");
        await output.WriteLineAsync();
        desk.Reset();
        log.Reset();
        await agent.RunAsync(Ticket);
        var run = new QuickStartRun([.. desk.Attempts], log.Block) { Hint = log.Hint };
        var tried = run.Attempts.Count == 0
            ? "nothing"
            : string.Join(" → ", run.Attempts.Select(attempt => attempt.Strategy + (attempt.Succeeded ? " ✓" : " ✗")));
        var failed = run.FailedAttempts == 1 ? "1 failed attempt" : $"{run.FailedAttempts} failed attempts";
        await output.WriteLineAsync($"This run ({(run.Block is null ? "no lesson in the file yet" : "lesson injected from the file")}): tried {tried}  ({failed})");
        await output.WriteLineAsync(run.Block is null
            ? "Its lesson is stored in the file: run the sample again with the same --sqlite path."
            : "An earlier process stored this lesson; this one found it in the file by the ticket's text.");
        return run;
    }
}

/// <summary>One run: every call it made, and the Historical Reference block its model was sent, if any.</summary>
public sealed record QuickStartRun(IReadOnlyList<RefundAttempt> Attempts, string? Block)
{
    /// <summary>The recall hint run's model was shown after a failure (<c>--recall</c>), or <see langword="null"/>.</summary>
    public string? Hint { get; init; }

    public int FailedAttempts => Attempts.Count(attempt => !attempt.Succeeded);
}

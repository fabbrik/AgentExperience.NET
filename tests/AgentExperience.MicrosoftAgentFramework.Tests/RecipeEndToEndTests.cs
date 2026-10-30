using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Sanitization;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Host = AgentExperience.MicrosoftAgentFramework.Tests.RecallAtFailureTests.Host;

namespace AgentExperience.MicrosoftAgentFramework.Tests;

/// <summary>
/// Story 28.4 end to end, through the one-call setup over a real <c>ChatClientAgent</c> and the recall tests' scripted
/// model: <see cref="AgentExperienceOptions.Recipe"/> captures an excerpt for the call that worked (never for a failed
/// one), stores it, and the next run's block and recall hint show it on the working attempt.
/// </summary>
public sealed class RecipeEndToEndTests
{
    private const string Task1 = "Repair the failing alpha build quickly";
    private const string Tool = "run_tests";

    /// <summary>Vouches for every result it is shown, so a test can see which calls capture asked about.</summary>
    private static string? EchoResult(ExperienceToolResultContext context) => "result: " + context.Result;

    private static async Task<ExperienceRecord> LearnAsync(Host host, ServiceProvider services)
    {
        await host.Agent(services).RunAsync(Task1);
        var finalized = Assert.Single(host.Finalized);
        var identity = host.Verifications[^1].Identity;
        var stored = await services.GetRequiredService<IExperienceRecordStore>()
            .GetAsync(identity.Authorization, identity.Scope, finalized.ExperienceId!.Value, CancellationToken.None);
        return stored.Record!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_the_call_that_returned_gets_an_excerpt_and_the_next_block_shows_it_on_the_working_attempt(bool classified)
    {
        var host = new Host { Classified = classified };
        await using var services = host.Build(options => options.Recipe(Tool, ["fix"], EchoResult));
        var record = await LearnAsync(host, services);

        var calls = record.Attempts.SelectMany(attempt => attempt.ToolCalls).ToList();
        Assert.Equal(3, calls.Count);
        Assert.All(calls.Where(call => call.Error is not null), call => Assert.Null(call.RecipeExcerpt));
        Assert.Equal("result: exit=0 all tests pass", Assert.Single(calls, call => call.Error is null).RecipeExcerpt);

        // The next run on the same task is given the lesson, the working call with its excerpt.
        host.Reset();
        var agent = host.Agent(services);
        await agent.RunAsync(Task1);
        var block = host.Model.Calls[0].Messages.Select(m => m.Text).Single(text => text.Contains(HistoricalReferenceWriter.BlockBegin, StringComparison.Ordinal));
        Assert.Contains("run_tests(fix=\"pin-version\") [returned: \"result: exit=0 all tests pass\"] → completed", block, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(block, System.Text.RegularExpressions.Regex.Escape("[returned: ")));
        Assert.Empty(host.Failures);
    }

    [Fact]
    public async Task The_recall_hint_shows_the_excerpt_on_its_worked_line()
    {
        var host = new Host { Classified = true, FollowHints = false };
        await using var services = host.Build(options =>
        {
            options.Recipe(Tool, ["fix"], EchoResult);
            options.Recall = new ExperienceRecallOptions();
        });
        await LearnAsync(host, services);

        host.Reset();
        await host.Agent(services).RunAsync("Payments pipeline broke overnight");

        var hinted = host.Model.Calls.Select(call => call.Instructions ?? string.Empty).First(text => text.Contains(HistoricalReferenceWriter.RecallBegin, StringComparison.Ordinal));
        Assert.Contains("run_tests(fix=\"pin-version\") [returned: \"result: exit=0 all tests pass\"] → completed", hinted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_throwing_callback_records_nothing_and_is_reported_once_by_type()
    {
        var host = new Host();
        await using var services = host.Build(options => options.Recipe(Tool, ["fix"], _ => throw new FormatException("secret detail")));
        var record = await LearnAsync(host, services);

        Assert.All(record.Attempts.SelectMany(attempt => attempt.ToolCalls), call => Assert.Null(call.RecipeExcerpt));
        var failure = Assert.Single(host.Failures);
        Assert.Equal(ExperienceCaptureFailureStage.RecipeExcerpt, failure.Stage);
        Assert.Contains(typeof(FormatException).FullName!, failure.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("secret detail", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_recipe_nothing_is_captured_or_shown()
    {
        var host = new Host();
        await using var services = host.Build();
        var record = await LearnAsync(host, services);
        Assert.All(record.Attempts.SelectMany(attempt => attempt.ToolCalls), call => Assert.Null(call.RecipeExcerpt));
    }

    [Fact]
    public async Task One_Recipe_call_sets_the_capture_allowlist_the_approach_arguments_and_the_excerpt_callback_and_allowlist()
    {
        ExperienceInjectionOptions? injection = null;
        ExperienceCaptureOptions? capture = null;
        var options = new AgentExperienceOptions();
        options.UseDevelopmentIdentity();
        options.Recipe("pin_dependency", ["package", "options.mode", "targets.0"], context => "pinned " + context.Result);
        options.Recipe("lookup", ["query"]);
        options.Injection = settings => injection = settings;
        options.Capture = settings => capture = settings;

        var sanitization = options.EffectiveSanitization();
        Assert.Equal(
            new[] { "mode", "options", "package", "query", "targets" },
            sanitization.Policies[SanitizationKinds.ToolArguments].AllowedFieldNames.Order(StringComparer.Ordinal));
        Assert.Equal(AgentExperienceDefaults.SecretFieldNames, sanitization.Policies[SanitizationKinds.ToolArguments].SecretFieldNames);

        var collection = new ServiceCollection();
        collection.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new AgentExperienceFiveLineSetupTests.FakeHostEnvironment("Development"));
        collection.AddAgentExperience(configure =>
        {
            configure.UseDevelopmentIdentity();
            configure.Recipe("pin_dependency", ["package", "options.mode", "targets.0"], context => "pinned " + context.Result);
            configure.Recipe("lookup", ["query"]);
            configure.Injection = settings => injection = settings;
            configure.Capture = settings => capture = settings;
        }).UseInMemoryStorageForDevelopment();
        await using var services = collection.BuildServiceProvider();
        _ = services.GetRequiredService<ExperienceContextProvider>();
        var registration = AgentExperienceRegistration.From(services);
        _ = registration.CreateCaptureOptions(services);

        Assert.NotNull(injection);
        Assert.Equal(["package", "options.mode", "targets.0"], injection!.ApproachArguments["pin_dependency"]);
        Assert.Equal(["query"], injection.ApproachArguments["lookup"]);
        Assert.Equal(["pin_dependency"], injection.RecipeExcerpts);

        var excerpt = capture!.RecipeResultExcerpt!;
        var context = new ExperienceToolResultContext { ToolCallId = Guid.NewGuid(), ToolName = "pin_dependency", Arguments = new Dictionary<string, object?>(), Result = "lib 2" };
        Assert.Equal("pinned lib 2", excerpt(context));
        Assert.Null(excerpt(new ExperienceToolResultContext { ToolCallId = Guid.NewGuid(), ToolName = "lookup", Arguments = new Dictionary<string, object?>(), Result = "x" }));

        Assert.Throws<ArgumentException>(() => options.Recipe("pin_dependency"));
        Assert.Throws<ArgumentException>(() => options.Recipe(" "));
        Assert.Throws<ArgumentException>(() => options.Recipe("other", ["a", "a"]));
    }

    [Fact]
    public void With_no_recipe_the_sanitization_is_the_hosts_own_instance()
    {
        var options = new AgentExperienceOptions();
        Assert.Same(options.Sanitization, options.EffectiveSanitization());
        options.Recipe("only_excerpt", resultExcerpt: _ => "x");
        Assert.Same(options.Sanitization, options.EffectiveSanitization());
    }
}

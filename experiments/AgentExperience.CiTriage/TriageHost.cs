using AgentExperience.Abstractions;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.MicrosoftAgentFramework;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.CiTriage;

/// <summary>
/// The tool wired the way a user would wire the library: <c>AddAgentExperience</c> with a fixed identity, one task id,
/// the in-memory store or PostgreSQL, and <c>UseAgentExperience</c> on a <see cref="ChatClientAgent"/>. No
/// <c>Verify</c>: a failure's real outcome arrives later (its green run), so runs are finalized out of band by the
/// replay. Injection runs a 0.5 holdout. Capture and injection share the one scope the identity names.
/// </summary>
internal sealed class TriageHost : IAsyncDisposable
{
    public const double HoldoutRate = 0.5;
    public const string Tenant = "agentexperience-net";
    public const string Project = "ci-triage";

    private readonly SimulatedClock _clock;

    private TriageHost(ServiceProvider services, AIAgent agent, SimulatedClock clock)
    {
        Services = services;
        Agent = agent;
        _clock = clock;
    }

    public ServiceProvider Services { get; }

    public AIAgent Agent { get; }

    /// <summary>What injection did on the most recent invocation; reset by the replay before each triage.</summary>
    public ExperienceInjectionResult? LastInjection { get; set; }

    /// <summary>Capture problems, by stage, as the library reported them.</summary>
    public List<string> CaptureFailures { get; } = [];

    /// <summary>The scope every run is captured in and every lesson is read from.</summary>
    public static Scope Scope { get; } = new(Tenant, Project, Project);

    /// <summary>The fixed caller: this tool, in its own tenant and project. Never from model output.</summary>
    public AuthorizationContext Authorization => new(Tenant, "ci-triage-tool", Roles: [], IssuedAt: _clock.GetUtcNow(), ApplicationId: Project, ProjectId: Project);

    /// <summary>The fixed environment every run is recorded under, so nothing about the machine is stored.</summary>
    public static EnvironmentFingerprint Environment { get; } = new(
        HostName: "ci-triage",
        RuntimeVersion: "net10.0",
        OperatingSystem: "any",
        ApplicationVersion: "26.1",
        Metadata: new Dictionary<string, string>(StringComparer.Ordinal));

    /// <param name="model">The model client, metered; the function loop is added here.</param>
    /// <param name="workspace">The failure the tools read from.</param>
    /// <param name="clock">The replay's clock, shared with the container (one instance, as the library requires).</param>
    /// <param name="postgresConnectionString">PostgreSQL, already migrated; <see langword="null"/> for the in-memory store.</param>
    /// <param name="newId">Identifier source for capture (run ids decide holdout arms); <see langword="null"/> for random ones.</param>
    public static TriageHost Create(IChatClient model, TriageWorkspace workspace, SimulatedClock clock, string? postgresConnectionString, Func<Guid>? newId)
    {
        TriageHost? host = null;
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        var builder = services.AddAgentExperience(options =>
        {
            options.ResolveIdentity = (_, _) => ValueTask.FromResult<ExperienceIdentity?>(new ExperienceIdentity(host!.Authorization, Scope));
            options.TaskId = TriageAgent.Name;
            options.TimeProvider = clock;
            // Keep read_file's path at capture, and let a lesson's Tried: line show it: which files a verified triage read.
            options.Sanitization = AgentExperienceDefaults.SanitizationAllowing("path");
            // Simulated time jumps days between a failure and its fix: never let retention drop a run before it is finalized.
            options.CaptureLimits = AgentExperienceDefaults.CaptureLimits with
            {
                CompletedRunRetention = TimeSpan.FromDays(3650),
                MaxOpenRunAge = null,
            };
            options.Injection = injection =>
            {
                injection.Holdout = new ExperienceHoldoutOptions { Rate = HoldoutRate };
                injection.ApproachArguments[TriageAgent.ReadFileTool] = ["path"];
                injection.OnContextInjected = result => host!.LastInjection = result;
            };
            options.Capture = capture =>
            {
                capture.Environment = Environment;
                capture.OnCaptureFailure = failure => host!.CaptureFailures.Add(failure.Stage.ToString());
                if (newId is not null)
                {
                    capture.NewId = newId;
                }
            };
        });

        if (postgresConnectionString is null)
        {
            builder.UseInMemoryStorageForDevelopment();
        }
        else
        {
            builder.UsePostgres(postgresConnectionString);
        }

        var provider = services.BuildServiceProvider();
        var functions = new FunctionInvokingChatClient(model)
        {
            IncludeDetailedErrors = true,
            MaximumIterationsPerRequest = 10,
        };
        AIAgent agent = new ChatClientAgent(functions, new ChatClientAgentOptions
        {
            Name = TriageAgent.Name,
            ChatOptions = new ChatOptions
            {
                Instructions = TriageAgent.Instructions,
                Tools = TriageAgent.Tools(workspace),
            },
        }).AsBuilder().UseAgentExperience(provider).Build();

        host = new TriageHost(provider, agent, clock);
        return host;
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}

using AgentExperience.Abstractions;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Finalization;
using AgentExperience.MicrosoftAgentFramework;
using AgentExperience.Core.ReflectiveExecution;
using AgentExperience.Core.Verification;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.MicrosoftAgentFramework.ReflectiveExecution;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.Sample.Reflective;

/// <summary>What one run of the demo did, for the program to print and the tests to assert.</summary>
public sealed record DemoRun(string Task, int ModelCalls, IReadOnlyList<string> FixesTried, ReflectiveExecutionEvent? Reflection, bool BuildGreen, bool LessonInjected);

/// <summary>
/// The two samples in one process. Sample A: the first attempt applies a fix that breaks the build; reflection proposes a
/// correction, the retry applies it, and the host's own build check passes. Sample B: a related task starts with the
/// lesson that run stored, applies the right fix at once, and needs no reflection. Everything is deterministic.
/// </summary>
public static class ReflectiveDemo
{
    public const string FirstTask = "Repair the failing alpha build quickly";
    public const string SecondTask = "Repair the failing beta build quickly";

    private const string WrongFix = "bump-version";
    private const string RightFix = "pin-version";

    public static async Task<IReadOnlyList<DemoRun>> RunAsync(IChatClient model, TextWriter output)
    {
        await using var session = new Session(model, reflection: true);
        var runs = new List<DemoRun>();
        foreach (var task in new[] { FirstTask, SecondTask })
        {
            var run = await session.RunTaskAsync(task);
            runs.Add(run);
            await output.WriteLineAsync($"{task}");
            await output.WriteLineAsync($"  fixes tried: {string.Join(", ", run.FixesTried)}");
            await output.WriteLineAsync($"  lesson injected before the run: {run.LessonInjected}");
            await output.WriteLineAsync($"  reflection: {(run.Reflection is { } r ? $"{r.Attempts} attempt(s), stop reason {r.StopReason}, retry verified: {r.RetryReachedVerifiedSuccess}" : "none")}");
            await output.WriteLineAsync($"  build green (the host's own check): {run.BuildGreen}");
        }

        return runs;
    }

    /// <summary>One arm of <see cref="CompareArmsAsync"/>.</summary>
    /// <param name="Arm">Which arm.</param>
    /// <param name="Verified">How many of the tasks ended with the host's build check green.</param>
    /// <param name="Tasks">How many tasks.</param>
    /// <param name="ModelCalls">The model calls used in total.</param>
    public sealed record ArmResult(string Arm, int Verified, int Tasks, int ModelCalls);

    /// <summary>
    /// The four arms on the same deterministic tasks: neither, reflection only, memory only, both. This shows the
    /// mechanics work as designed against a scripted model that obeys what it is handed. It is NOT evidence that
    /// reflection or memory helps a real model: that needs a real model, a pre-registered plan and the holdout.
    /// </summary>
    public static async Task<IReadOnlyList<ArmResult>> CompareArmsAsync(TextWriter output)
    {
        string[] tasks = ["Repair the failing alpha build quickly", "Repair the failing beta build quickly", "Repair the failing gamma build quickly", "Repair the failing delta build quickly"];
        var results = new List<ArmResult>();
        foreach (var (name, reflection, memory) in new[] { ("baseline", false, false), ("reflection only", true, false), ("memory only", false, true), ("reflection + memory", true, true) })
        {
            var verified = 0;
            var calls = 0;
            if (memory)
            {
                using var model = new ScriptedModel();
                await using var session = new Session(model, reflection: true);
                await session.RunTaskAsync(tasks[0]);   // the teaching run, always reflective: it is what stores the lesson
                session.Reflection = reflection;
                var before = model.Calls;
                foreach (var task in tasks.Skip(1))
                {
                    verified += (await session.RunTaskAsync(task)).BuildGreen ? 1 : 0;
                }

                calls = model.Calls - before;
                results.Add(new ArmResult(name, verified, tasks.Length - 1, calls));
            }
            else
            {
                foreach (var task in tasks.Skip(1))
                {
                    // A fresh memory per task: nothing carries over.
                    using var model = new ScriptedModel();
                    await using var session = new Session(model, reflection);
                    verified += (await session.RunTaskAsync(task)).BuildGreen ? 1 : 0;
                    calls += model.Calls;
                }

                results.Add(new ArmResult(name, verified, tasks.Length - 1, calls));
            }

            await output.WriteLineAsync($"{name,-22} verified {results[^1].Verified}/{results[^1].Tasks}, model calls {results[^1].ModelCalls}");
        }

        return results;
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly ScriptedModel _model;
        private readonly Project _project = new();
        private readonly List<ReflectiveExecutionEvent> _events = [];
        private readonly List<ExperienceInjectionResult> _injections = [];
        private readonly ServiceProvider _provider;
        private readonly AIAgent _agent;
        private ReflectiveExecutionOptions? _options;

        public Session(IChatClient model, bool reflection)
        {
            _model = model as ScriptedModel ?? new ScriptedModel();
            var project = _project;
            var services = new ServiceCollection();
            services
                .AddAgentExperience(options =>
                {
                    options.UseDevelopmentIdentity();
                    // The host's own check decides what is stored as verified: the build, never the model's word.
                    options.Verify = (context, _) => ValueTask.FromResult<ExperienceVerification?>(new(
                        RequiredChecks: [new RequiredCheck("build", "ToolExitCode")],
                        Evidence: [context.CreateEvidence("build", "ToolExitCode", project.Green ? CheckResult.Pass : CheckResult.Fail, producer: "ci", "demo")],
                        ArtifactRevision: "demo"));
                    options.Sanitization = AgentExperienceDefaults.SanitizationAllowing("fix");
                    options.Injection = injection =>
                    {
                        injection.ApproachArguments[Project.ToolName] = ["fix"];
                        injection.OnContextInjected = _injections.Add;
                    };
                    options.Capture = capture => capture.ClassifyToolResult = call =>
                        call.Result?.ToString() is { } text && text.StartsWith("exit=1", StringComparison.Ordinal) ? "exit 1" : null;
                })
                .UseInMemoryStorageForDevelopment();

            // Reflective execution: off unless Enabled. Retries need an evaluator AND an authorizer; the default denies.
            services.AddReflectiveExecution(options =>
            {
                _options = options;
                options.Limits = new ReflectionExecutionOptions { Enabled = true, MaxAttempts = 3, SideEffects = SideEffectPolicy.Idempotent };
                options.Evaluator = new DeterministicReflectionEvaluator([
                    context => context.Outcome.FailedCalls.Any(call => call.ErrorClass == "exit 1") ? "pin the dependency to the previous minor version instead of bumping it" : null,
                ]);
                // Safe here only because every action is a local, repeatable edit of a scratch project.
                options.Authorizer = new AllowRetries();
                // The host's own check, run after each attempt: is the build green? Never a model's claim.
                options.TrustedCheck = (_, _) => ValueTask.FromResult<bool?>(project.Green);
                options.OnEvent = _events.Add;
            });

            _provider = services.BuildServiceProvider();
            var functions = new FunctionInvokingChatClient(_model) { IncludeDetailedErrors = true };
            // UseReflectiveExecution first: it must be the outermost layer so every attempt is one captured invocation.
            _agent = new ChatClientAgent(functions, new ChatClientAgentOptions
            {
                Name = "build-fixer",
                ChatOptions = new ChatOptions { Instructions = "You repair builds with apply_fix.", Tools = [_project.Tool] },
            }).AsBuilder().UseReflectiveExecution(_provider).UseAgentExperience(_provider).Build();
            Reflection = reflection;
        }

        /// <summary>Whether the reflection loop runs; off makes the wrapper a pass-through.</summary>
        public bool Reflection
        {
            set => _options!.Limits = _options.Limits with { Enabled = value };
        }

        public async Task<DemoRun> RunTaskAsync(string task)
        {
            _project.Reset();
            _events.Clear();
            _injections.Clear();
            var callsBefore = _model.Calls;
            await _agent.RunAsync(task);
            return new DemoRun(
                task,
                _model.Calls - callsBefore,
                [.. _project.Tried],
                _events.SingleOrDefault(),
                _project.Green,
                _injections.Any(i => i.Outcome == InjectionOutcome.Injected));
        }

        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }

    private sealed class AllowRetries : IRetryAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(ReflectionAttemptContext context, ReflectionEvaluation evaluation, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    /// <summary>A scratch project the tool edits; its build is green only with the right fix.</summary>
    private sealed class Project
    {
        public const string ToolName = "apply_fix";

        public List<string> Tried { get; } = [];

        public bool Green => Tried.Contains(RightFix);

        public void Reset() => Tried.Clear();

        public AIFunction Tool => AIFunctionFactory.Create(
            (string fix) =>
            {
                Tried.Add(fix);
                return fix == RightFix ? "exit=0 build green" : "exit=1 build failed: incompatible dependency versions";
            },
            ToolName);
    }

    /// <summary>
    /// A model stand-in. It applies the wrong fix unless what it was handed (a correction note, or a lesson injected from
    /// an earlier verified run) names the right one. Swap it for any IChatClient.
    /// </summary>
    public sealed class ScriptedModel : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var list = messages.ToList();

            // The session keeps earlier attempts' messages, so only this turn's tool results (after the last user message) count.
            var lastUser = list.FindLastIndex(m => m.Role == ChatRole.User);
            var thisTurn = list.Skip(lastUser + 1).SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();
            if (thisTurn.Count > 0)
            {
                var green = thisTurn.Any(r => r.Result?.ToString()?.StartsWith("exit=0", StringComparison.Ordinal) == true);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, green ? "Build is green." : "The build is still failing.")));
            }

            var informed = list.Any(m => m.Text.Contains("pin the dependency", StringComparison.Ordinal) || m.Text.Contains($"fix=\"{RightFix}\"", StringComparison.Ordinal));
            var call = new FunctionCallContent("call-" + Calls, Project.ToolName, new Dictionary<string, object?> { ["fix"] = informed ? RightFix : WrongFix });
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Cli.Configuration;
using AgentExperience.Core.Capture;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Verification;
using AgentExperience.MicrosoftAgentFramework;
using AgentExperience.MicrosoftAgentFramework.Export;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.Cli.Engine;

/// <summary>What <see cref="AexpEngine.FinalizeAsync"/> did: the verdict and what the library made of it.</summary>
internal sealed record FinalizeSummary(Guid RunId, bool Passed, string Verdict, FinalizationOutcome Outcome, Guid? ExperienceId, ExperienceStatus? Status, string? Reason)
{
    public string Describe() =>
        $"Run {RunId:D}: verifier {Verdict} ({(Passed ? "pass" : "fail")}); finalization {Outcome}"
        + (ExperienceId is { } id ? $", record {id:D} ({Status})" : string.Empty)
        + (Reason is null ? string.Empty : $": {Reason}");
}

/// <summary>
/// Every operation the CLI and the MCP server offer, over the library, under one configuration. Each operation builds
/// the library afresh and disposes it, so an operation in a later process sees only what the store and the run journal
/// hold: nothing depends on one process living from <c>run start</c> to <c>finalize</c>.
/// </summary>
internal sealed class AexpEngine(AexpSettings settings, TimeProvider clock, Func<string, string?> environment)
{
    /// <summary>The source capture names on every run this tool captures.</summary>
    public const string ProvenanceSource = "AgentExperience.Cli";

    /// <summary>The evidence kind the verifier's exit code is.</summary>
    public const string EvidenceKind = "ToolExitCode";

    /// <summary>The longest task text a run takes, as the MAF adapter derives it.</summary>
    public const int MaxTaskLength = ExperienceTaskText.DefaultMaxLength;

    private readonly RunJournalStore _journals = new(settings.RunsDirectory);

    public AexpSettings Settings { get; } = settings;

    private Task<ServiceProvider> BuildAsync(ProbeSettings? probe, CancellationToken cancellationToken) =>
        AexpHost.BuildAsync(Settings, clock, environment, probe, cancellationToken);

    /// <summary>Opens a run: prints nothing but its identifier, which every later command names.</summary>
    public async Task<Guid> StartRunAsync(string task, string? taskId, CancellationToken cancellationToken)
    {
        task = RequireTask(task);
        var resolvedTaskId = string.IsNullOrWhiteSpace(taskId) ? Settings.TaskId : RequireIdentifier(taskId, "task ID");

        // The task text is kept with the run and becomes its description, so it passes the same text rules a tool result
        // does (secret redaction, the length bound) before it is written down.
        string sanitizedTask;
        await using (var services = await BuildAsync(null, cancellationToken).ConfigureAwait(false))
        {
            sanitizedTask = (await SanitizeAsync(services, "aexp.task", [], task, null, cancellationToken).ConfigureAwait(false)).Result
                ?? throw new AexpUsageException("The task text was refused by the sanitization rules.");
        }

        var journal = new RunJournal
        {
            RunId = Guid.NewGuid(),
            TaskId = resolvedTaskId,
            Task = sanitizedTask,
            Scope = Settings.Scope,
            StartedAt = clock.GetUtcNow(),
        };
        await _journals.CreateAsync(journal, cancellationToken).ConfigureAwait(false);
        return journal.RunId;
    }

    /// <summary>
    /// Adds one tool call to an open run. Its arguments, result and error pass through the library's capture rules (the
    /// configured sanitization, the capture limits, the recipe consent) before anything is written down.
    /// </summary>
    public async Task RecordCallAsync(Guid runId, string tool, string? argumentsJson, string? result, string? error, CancellationToken cancellationToken)
    {
        tool = RequireTool(tool);
        if (result is not null && error is not null)
        {
            throw new AexpUsageException("A call either returned (--result) or failed (--error), not both.");
        }

        var arguments = ParseArguments(argumentsJson);
        await using var services = await BuildAsync(null, cancellationToken).ConfigureAwait(false);
        var call = await SanitizeAsync(services, tool, arguments, result, error, cancellationToken).ConfigureAwait(false);
        await _journals.UpdateAsync(runId, journal =>
        {
            RequireScope(journal);
            journal.Calls.Add(call);
            return Task.FromResult(0);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The Historical Reference block for <paramref name="task"/>, exactly as the library would put it in front of a model:
    /// the same retrieval, eligibility checks, writer and limits. With <paramref name="runId"/>, the run's holdout arm
    /// decides whether it is shown, and what is shown is recorded as delivered into the run.
    /// </summary>
    public async Task<string?> RecallBlockAsync(string? task, Guid? runId, CancellationToken cancellationToken)
    {
        if (runId is not { } id)
        {
            var text = RequireTask(task ?? throw new AexpUsageException("Name the task (--task), or the run (--run) whose task it is."));
            await using var services = await BuildAsync(new ProbeSettings { TaskId = Settings.TaskId }, cancellationToken).ConfigureAwait(false);
            return (await Probe.RunAsync(services, text, null, null, [], cancellationToken).ConfigureAwait(false)).Block;
        }

        return await _journals.UpdateAsync(id, async journal =>
        {
            RequireScope(journal);
            if (journal.BlockRequested)
            {
                // Delivered once per run; asking again repeats what was delivered and records nothing new.
                return journal.Block;
            }

            var text = task is null ? journal.Task : RequireTask(task);
            var probe = new ProbeSettings { RunId = id, TaskId = journal.TaskId };
            await using var services = await BuildAsync(probe, cancellationToken).ConfigureAwait(false);
            var client = await Probe.RunAsync(services, text, null, null, [], cancellationToken).ConfigureAwait(false);
            journal.BlockRequested = true;
            journal.Block = client.Block;
            MergeExposures(services, id, journal);
            return client.Block;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The failure hint for one failed call of an open run, by the library's recall rules: a resolved signature only, the hint's
    /// byte and record bounds, one answer per signature and at most the configured number per run, and the run's holdout
    /// arm. <see langword="null"/> when there is none to give.
    /// </summary>
    public async Task<string?> FailureHintAsync(Guid runId, string tool, string error, CancellationToken cancellationToken)
    {
        tool = RequireTool(tool);
        if (string.IsNullOrWhiteSpace(error))
        {
            throw new AexpUsageException("Name the error the call failed with (--error).");
        }

        if (!Settings.RecallEnabled)
        {
            return null;
        }

        return await _journals.UpdateAsync(runId, async journal =>
        {
            RequireScope(journal);
            await using var services = await BuildAsync(null, cancellationToken).ConfigureAwait(false);
            var stored = (await SanitizeAsync(services, tool, [], null, error, cancellationToken).ConfigureAwait(false)).Error;
            if (stored is null)
            {
                return null;
            }

            // Each signature is answered once per run, and the run answers at most MaxHintsPerRun of them.
            var key = SignatureKey(tool, stored);
            var maxAnswers = Settings.MaxHintsPerRun ?? ExperienceRecallOptions.DefaultMaxHintsPerRun;
            if (key is null || journal.AnsweredSignatures.Contains(key) || journal.AnsweredSignatures.Count >= maxAnswers)
            {
                return null;
            }

            var probe = new ProbeSettings { RunId = runId, TaskId = journal.TaskId, SuppressBlock = true, FailedCallError = stored };
            await using var probeServices = await BuildAsync(probe, cancellationToken).ConfigureAwait(false);
            var delivered = journal.Exposures.Select(exposure => exposure.ExperienceId).ToArray();
            var client = await Probe.RunAsync(probeServices, journal.Task, tool, stored, delivered, cancellationToken).ConfigureAwait(false);
            if (probe.Recall is { Outcome: ExperienceRecallOutcome.Hinted or ExperienceRecallOutcome.Withheld })
            {
                journal.AnsweredSignatures.Add(key);
            }

            MergeExposures(probeServices, runId, journal);
            return client.Hint;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends an open run: capture records its calls as one attempt, with what the library delivered into it, and completes
    /// it, which writes it to the run store. Finalization can then happen in this process or any later one.
    /// </summary>
    public async Task EndRunAsync(Guid runId, RunExecutionStatus status, CancellationToken cancellationToken)
    {
        await _journals.UpdateAsync(runId, async journal =>
        {
            RequireScope(journal);
            await using var services = await BuildAsync(null, cancellationToken).ConfigureAwait(false);
            var capture = services.GetRequiredService<IExperienceCaptureService>();
            var now = clock.GetUtcNow();
            var started = capture.StartRun(
                runId,
                journal.TaskId,
                journal.Task,
                journal.Scope,
                Environment(),
                new Provenance(ProvenanceSource, Version, journal.StartedAt, CorrelationId: null),
                journal.StartedAt);
            if (started.Outcome != StartRunOutcome.Started)
            {
                throw new AexpUsageException($"The run could not be opened for capture ({started.Outcome}).");
            }

            if (journal.Exposures.Count > 0)
            {
                capture.RecordExposure(runId, journal.Exposures);
            }

            var calls = journal.Calls.Select(call => new RawToolCall(
                call.ToolCallId,
                call.ToolName,
                ToDictionary(call.Arguments),
                call.StartedAt,
                TimeSpan.Zero,
                call.Error is null ? call.Result : null,
                call.Error)
            {
                RecipeExcerpt = call.Error is null ? call.RecipeExcerpt : null,
            }).ToList();

            var appended = await capture.AppendAttemptAsync(
                runId,
                new AppendAttemptRequest(DeterministicId(runId, "attempt"), journal.StartedAt, Positive(now - journal.StartedAt), calls, Result: null, Error: null),
                cancellationToken).ConfigureAwait(false);
            if (appended.Outcome is not (AppendAttemptOutcome.Recorded or AppendAttemptOutcome.DuplicateNoOp))
            {
                throw new AexpUsageException($"Capture refused the run's calls ({appended.Outcome}).", appended.Reason);
            }

            var completed = await capture.CompleteRunAsync(runId, DeterministicId(runId, "completion"), status, now, cancellationToken).ConfigureAwait(false);
            if (completed.Outcome is not (CompleteRunOutcome.Recorded or CompleteRunOutcome.DuplicateNoOp))
            {
                throw new AexpUsageException($"Capture could not complete the run ({completed.Outcome}).");
            }

            if (completed.RunStoreFailure is { } failure)
            {
                throw new AexpUsageException("The run could not be stored for finalization.", failure);
            }

            return 0;
        }, cancellationToken).ConfigureAwait(false);
        _journals.Delete(runId);
    }

    /// <summary>
    /// Verifies an ended run with the configured verifier (or with the exit code the host reports, when the config allows
    /// it) and finalizes it: the verdict becomes the run's evidence, and a verified run becomes a lesson. Works in any
    /// process after <c>run end</c>: the run comes back from the run store.
    /// </summary>
    public async Task<FinalizeSummary> FinalizeAsync(Guid runId, int? reportedExitCode, CancellationToken cancellationToken)
    {
        if (_journals.Exists(runId))
        {
            throw new AexpUsageException($"Run {runId:D} is still open: end it first with 'aexp run end'.");
        }

        if (reportedExitCode is not null && !Settings.Verifier.AcceptExitCode)
        {
            throw new AexpUsageException(
                "This project's config does not accept a reported exit code (verify.acceptExitCode is false): aexp runs the verifier itself.");
        }

        // The config names what runs (and whether a reported verdict counts): only a config a person approved may.
        RequireTrusted();

        // The run is checked before anything is spawned: it exists in this scope, it ended, and it is not finalized yet.
        await using var services = await BuildAsync(null, cancellationToken).ConfigureAwait(false);
        await RequireEndedRunAsync(services, runId, cancellationToken).ConfigureAwait(false);

        VerifierResult verdict;
        if (reportedExitCode is { } code)
        {
            verdict = new VerifierResult(code, TimedOut: false);
        }
        else
        {
            var program = VerifierRunner.ResolveProgram(Settings.Verifier, Settings.ProjectRoot, environment);
            verdict = await VerifierRunner.RunAsync(Settings.Verifier, program, cancellationToken).ConfigureAwait(false);
        }

        if (verdict.TimedOut)
        {
            throw new AexpUsageException(
                $"The verifier did not finish within {Settings.Verifier.Timeout}; nothing was finalized. Run 'aexp finalize --run {runId:D}' again.");
        }

        var passed = verdict.ExitCode == 0;
        var now = clock.GetUtcNow();
        var roundId = Guid.NewGuid();
        var revision = Settings.Verifier.Revision;
        var evidence = new Evidence(
            EvidenceId: Guid.NewGuid(),
            VerificationRoundId: roundId,
            ArtifactRevision: revision,
            CheckId: Settings.Verifier.CheckId,
            Kind: EvidenceKind,
            Result: passed ? CheckResult.Pass : CheckResult.Fail,
            Producer: reportedExitCode is null ? "aexp-verifier" : "aexp-reported-exit-code",
            Detail: $"exit {verdict.ExitCode.ToString(CultureInfo.InvariantCulture)}",
            CapturedAt: now);

        var result = await services.GetRequiredService<ExperienceFinalizationService>().FinalizeAsync(
            new FinalizeExperienceRequest(
                RunId: runId,
                Authorization: Settings.Authorization(now),
                ClosedRound: new ClosedVerificationRound(roundId, revision),
                RequiredChecks: [new RequiredCheck(Settings.Verifier.CheckId, EvidenceKind)],
                Evidence: [evidence],
                CurrentArtifactRevision: revision,
                StorageDecision: StorageDecision.Permit,
                FinalizedAt: now),
            cancellationToken).ConfigureAwait(false);

        return new FinalizeSummary(
            runId,
            passed,
            $"exit {verdict.ExitCode.ToString(CultureInfo.InvariantCulture)}",
            result.Outcome,
            result.ExperienceId,
            result.Status,
            result.Reason ?? result.RunStoreFailure);
    }

    /// <summary>Deletes open runs started more than <paramref name="age"/> ago that no command is using.</summary>
    public Task<int> PruneRunsAsync(TimeSpan age, CancellationToken cancellationToken) =>
        _journals.PruneAsync(clock.GetUtcNow() - age, cancellationToken);

    /// <summary>Whether a person approved this config's exact bytes with <c>aexp trust</c>.</summary>
    public bool IsTrusted() => ConfigTrust.ForUser(environment).IsTrusted(Settings.ConfigPath, Settings.ConfigSha256);

    /// <summary>Records this config's path and hash as approved by the person running <c>aexp trust</c>.</summary>
    public void Trust() => ConfigTrust.ForUser(environment).Approve(Settings.ConfigPath, Settings.ConfigSha256, clock.GetUtcNow());

    /// <summary>The verifier's program as it would be run: resolved, or why it cannot be.</summary>
    public string DescribeVerifierProgram()
    {
        try
        {
            return VerifierRunner.ResolveProgram(Settings.Verifier, Settings.ProjectRoot, environment);
        }
        catch (AexpUsageException ex)
        {
            return "(cannot run: " + ex.Message + ")";
        }
    }

    private void RequireTrusted()
    {
        if (!IsTrusted())
        {
            throw new AexpUntrustedConfigException();
        }
    }

    private async Task RequireEndedRunAsync(IServiceProvider services, Guid runId, CancellationToken cancellationToken)
    {
        var store = services.GetService<IExperienceRunStore>()
            ?? throw new AexpUsageException("The configured store keeps no runs, so a run cannot be finalized.");
        var read = await store.GetAsync(Settings.Authorization(clock.GetUtcNow()), runId, cancellationToken).ConfigureAwait(false);
        if (read.Outcome != ExperienceRunStoreOutcome.Found || read.Run is not { } stored || stored.Run.Scope != Settings.Scope)
        {
            throw new AexpUsageException($"No ended run {runId:D} is in this project's store; nothing was run.");
        }

        if (stored.Run.ExecutionStatus is null || stored.Run.EndedAt is null)
        {
            throw new AexpUsageException($"Run {runId:D} has not ended; nothing was run.");
        }

        if (stored.FinalizedAt is not null)
        {
            throw new AexpUsageException($"Run {runId:D} is already finalized; nothing was run.");
        }
    }

    /// <summary>The lessons in this project's scope, as the library's lessons view lists them.</summary>
    public async Task<ExperienceLessonsPage> LessonsAsync(bool includeIneligible, CancellationToken cancellationToken)
    {
        await using var services = await BuildAsync(null, cancellationToken).ConfigureAwait(false);
        return await services.GetAgentExperienceLessons().ListAsync(
            Identity(),
            new ExperienceLessonsQuery { IncludeIneligible = includeIneligible, IncludeMeasuredBenefit = true },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Exports the lessons the holdout measured as helping to a Markdown file.</summary>
    public async Task<ExperienceLessonExportResult> ExportMarkdownAsync(string path, CancellationToken cancellationToken)
    {
        await using var services = await BuildAsync(null, cancellationToken).ConfigureAwait(false);
        return await services.GetAgentExperienceLessons().ExportMeasuredAsync(
            Identity(),
            new MarkdownLessonFileSink(path, clock),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The holdout's estimate of whether the lessons help, in this project's scope.</summary>
    public async Task<Core.Holdout.ExperienceBenefitEstimate> BenefitAsync(CancellationToken cancellationToken)
    {
        await using var services = await BuildAsync(null, cancellationToken).ConfigureAwait(false);
        return await AgentExperienceBenefit.EstimateAsync(
            services, Settings.Authorization(clock.GetUtcNow()), Settings.Scope, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private ExperienceIdentity Identity() => new(Settings.Authorization(clock.GetUtcNow()), Settings.Scope);

    /// <summary>
    /// One call as capture would store it: through the configured sanitizer, the capture limits and the recipe consent,
    /// by a scratch capture service built from the same registrations. Nothing raw reaches the run file.
    /// </summary>
    private async Task<JournalCall> SanitizeAsync(
        IServiceProvider services,
        string tool,
        Dictionary<string, object?> arguments,
        string? result,
        string? error,
        CancellationToken cancellationToken)
    {
        var limits = services.GetRequiredService<CaptureLimits>();
        var scratch = new InMemoryExperienceCaptureService(services.GetRequiredService<ISanitizer>(), limits, clock);
        var scratchRun = Guid.NewGuid();
        var now = clock.GetUtcNow();
        scratch.StartRun(scratchRun, Settings.TaskId, null, Settings.Scope, Environment(), new Provenance(ProvenanceSource, Version, now, null), now);

        // A failed call is recorded as the MAF adapter records a call its ClassifyToolResult marks failed: by its error
        // class alone (exit 2, HTTP 503, TimeoutException, ...), never the error's own words, which may carry anything.
        var cutError = error is null ? null : ErrorClassOf(error);
        var recipe = Settings.Recipes.FirstOrDefault(r => string.Equals(r.Tool, tool, StringComparison.Ordinal));
        var raw = new RawToolCall(Guid.NewGuid(), tool, arguments, now, TimeSpan.Zero, error is null ? result : null, cutError)
        {
            RecipeExcerpt = recipe is { ResultExcerpt: true } && error is null ? result : null,
        };

        var appended = await scratch.AppendAttemptAsync(
            scratchRun,
            new AppendAttemptRequest(Guid.NewGuid(), now, TimeSpan.Zero, [raw], null, null),
            cancellationToken).ConfigureAwait(false);
        if (appended.Outcome != AppendAttemptOutcome.Recorded || !scratch.TryGetRun(scratchRun, out var run))
        {
            throw new AexpUsageException($"Capture refused the call ({appended.Outcome}); nothing was recorded.", appended.Reason);
        }

        var stored = run.Attempts[0].ToolCalls[0];
        return new JournalCall(
            stored.ToolCallId,
            stored.ToolName,
            JsonSerializer.SerializeToNode(stored.Arguments, RunJournal.Json) as JsonObject ?? [],
            stored.StartedAt,
            stored.Result,
            stored.Error,
            stored.RecipeExcerpt);
    }

    /// <summary>
    /// The one error-class token the library's shared rule finds first in <paramref name="error"/>, or
    /// <see cref="ExperienceCaptureOptions.ToolReportedErrorClass"/> when it finds none: what a failed call is stored as.
    /// </summary>
    internal static string ErrorClassOf(string error)
    {
        var first = MicrosoftAgentFramework.Reflections.ErrorClass.Of(error).Split(", ", 2)[0].Trim();
        return MicrosoftAgentFramework.Reflections.ErrorClass.IsSingleToken(first) ? first : ExperienceCaptureOptions.ToolReportedErrorClass;
    }

    private static string? SignatureKey(string tool, string error)
    {
        try
        {
            return ExperienceFailureSignature.From(tool, error)?.Key;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>What the probe's capture recorded as delivered into the run, added to what the run file holds.</summary>
    private static void MergeExposures(IServiceProvider services, Guid runId, RunJournal journal)
    {
        if (!services.GetRequiredService<IExperienceCaptureService>().TryGetRun(runId, out var run))
        {
            return;
        }

        foreach (var exposure in run.Provenance.ExposedTo)
        {
            if (!journal.Exposures.Any(held => held.ExperienceId == exposure.ExperienceId))
            {
                journal.Exposures.Add(exposure);
            }
        }
    }

    private void RequireScope(RunJournal journal)
    {
        if (journal.Scope != Settings.Scope)
        {
            throw new AexpUsageException("The run was opened under another scope than this config's; it can only be continued under that one.");
        }
    }

    internal static string RequireTask(string task)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            throw new AexpUsageException("The task text is empty.");
        }

        task = task.Trim();
        return task.Length <= MaxTaskLength ? task : Cut(task, MaxTaskLength);
    }

    private static string RequireTool(string tool)
    {
        if (string.IsNullOrWhiteSpace(tool))
        {
            throw new AexpUsageException("Name the tool (--tool).");
        }

        tool = tool.Trim();
        if (tool.Length > ExperienceFailureSignature.MaxToolNameLength || tool.Any(char.IsControl))
        {
            throw new AexpUsageException($"A tool name is at most {ExperienceFailureSignature.MaxToolNameLength} characters, with no control characters.");
        }

        return tool;
    }

    private static string RequireIdentifier(string value, string what)
    {
        value = value.Trim();
        if (value.Length is 0 or > 128 || value.Any(char.IsControl))
        {
            throw new AexpUsageException($"The {what} is 1 to 128 characters, with no control characters.");
        }

        return value;
    }

    internal static Dictionary<string, object?> ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new AexpUsageException("--args is not JSON.", ex.Message);
        }

        return node is JsonObject obj
            ? ToDictionary(obj)
            : throw new AexpUsageException("--args must be a JSON object of argument names to values.");
    }

    private static Dictionary<string, object?> ToDictionary(JsonObject arguments) =>
        arguments.ToDictionary(pair => pair.Key, pair => pair.Value is null ? null : (object?)JsonSerializer.Deserialize<JsonElement>(pair.Value.ToJsonString()), StringComparer.Ordinal);

    internal static string Cut(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var cut = max;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut];
    }

    private static TimeSpan Positive(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    /// <summary>An identifier derived from the run, so a retried <c>run end</c> is an idempotent resubmission.</summary>
    private static Guid DeterministicId(Guid runId, string purpose)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(runId.ToString("D") + "/" + purpose));
        return new Guid(bytes.AsSpan(0, 16));
    }

    internal static string Version { get; } =
        typeof(AexpEngine).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private static EnvironmentFingerprint Environment() => new(
        HostName: System.Environment.MachineName,
        RuntimeVersion: RuntimeInformation.FrameworkDescription,
        OperatingSystem: RuntimeInformation.OSDescription,
        ApplicationVersion: null,
        Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ProcessArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
        });
}

/// <summary>
/// The config is not one a person approved (or it changed since): it may not drive the verifier. The message is fixed, so
/// it is safe to show a model, and tells the person what to do.
/// </summary>
internal sealed class AexpUntrustedConfigException() : Exception(
    "This project's .aexp config is not trusted, or it changed since it was trusted. It names the command aexp runs, so "
    + "a person must review it and approve it with 'aexp trust' (an edit, by anyone, revokes the approval). Nothing was run.");

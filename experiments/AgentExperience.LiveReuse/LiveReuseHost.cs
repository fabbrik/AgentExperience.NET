using AgentExperience.LiveReuse.Harness;

namespace AgentExperience.LiveReuse;

/// <summary>
/// The command line: reads the environment, runs the experiment, and writes the report and raw results. Separated from
/// <c>Program</c> so the tests can drive it with a fake environment.
/// </summary>
public static class LiveReuseHost
{
    public const int ExitSuccess = 0;
    public const int ExitConfigurationError = 2;
    public const int ExitStoppedAtBudget = 3;
    public const int ExitHarnessRefused = 4;

    public const int ExitUnexpected = 70;

    /// <summary>Selects the experiment: <see cref="ReuseExperiment"/> (story 9.1, the default) or <see cref="TransferExperimentName"/> (story 20.4).</summary>
    public const string ExperimentOption = "--experiment";

    public const string ReuseExperiment = "reuse";

    public const string TransferExperimentName = "transfer";

    /// <summary>
    /// Runs the command. Anything unexpected is reported by exception type only and never by its message or stack:
    /// the default unhandled-exception output would print both, and an SDK message is not ours to publish.
    /// </summary>
    public static async Task<int> RunAsync(
        string[] args,
        Func<string, string?> environment,
        TextWriter output,
        TextWriter error,
        TimeProvider clock,
        Func<LiveConfiguration, Microsoft.Extensions.AI.IChatClient>? createClient = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            return await RunCoreAsync(args, environment, output, error, clock, createClient, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync($"The experiment stopped on an unexpected {ex.GetType().Name}. Its message is not printed, because it may carry request details.").ConfigureAwait(false);
            return ExitUnexpected;
        }
    }

    private static async Task<int> RunCoreAsync(
        string[] args,
        Func<string, string?> environment,
        TextWriter output,
        TextWriter error,
        TimeProvider clock,
        Func<LiveConfiguration, Microsoft.Extensions.AI.IChatClient>? createClient = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Contains("--help") || args.Contains("-h"))
        {
            await output.WriteLineAsync("Usage: dotnet run --project experiments/AgentExperience.LiveReuse -c Release [-- [--experiment reuse|transfer] [--scripted]]").ConfigureAwait(false);
            await output.WriteLineAsync("See experiments/AgentExperience.LiveReuse/README.md for the variables, the cost and the design.").ConfigureAwait(false);
            return ExitSuccess;
        }

        // Only the two-argument form: an `--experiment=transfer` that silently ran the reuse experiment would spend money on the wrong design.
        if (args.Any(arg => arg.StartsWith(ExperimentOption + "=", StringComparison.Ordinal)))
        {
            await error.WriteLineAsync($"Configuration error: write `{ExperimentOption} {TransferExperimentName}`, with a space, not `{ExperimentOption}=...`.").ConfigureAwait(false);
            return ExitConfigurationError;
        }

        var experiment = args.ToList().IndexOf(ExperimentOption) is var at and >= 0
            ? args.ElementAtOrDefault(at + 1)
            : ReuseExperiment;
        if (experiment == TransferExperimentName)
        {
            return await RunTransferAsync(args, environment, output, error, clock, createClient, cancellationToken).ConfigureAwait(false);
        }

        if (experiment != ReuseExperiment)
        {
            await error.WriteLineAsync($"Configuration error: {ExperimentOption} takes '{ReuseExperiment}' (the default) or '{TransferExperimentName}'.").ConfigureAwait(false);
            return ExitConfigurationError;
        }

        LivePreregistration design;
        try
        {
            design = LivePreregistration.ReadEmbedded();
        }
        catch (PreregistrationException ex)
        {
            await error.WriteLineAsync("The pre-registration could not be read: " + ex.Message).ConfigureAwait(false);
            return ExitHarnessRefused;
        }

        if (args.Contains("--scripted"))
        {
            var scripted = await RunGuardedAsync(
                new LiveExperimentOptions
                {
                    Model = new ScriptedOperatorModel(),
                    Descriptor = new RunDescriptor("scripted", ScriptedOperatorModel.ModelId, "none (offline)", null, null, "none: scripted run, nothing is billed"),
                    Design = design,
                    Clock = clock,
                },
                error,
                cancellationToken).ConfigureAwait(false);

            if (scripted is null)
            {
                return ExitHarnessRefused;
            }

            await output.WriteAsync(LiveReuseReport.Markdown(scripted)).ConfigureAwait(false);
            return ExitSuccess;
        }

        var (outcome, configuration, message) = LiveConfiguration.Read(environment, design.DefaultMaxModelCalls, design.DefaultMaxTotalTokens);
        switch (outcome)
        {
            case ConfigurationOutcome.NotConfigured:
                await output.WriteLineAsync("SKIPPED: " + message).ConfigureAwait(false);
                return ExitSuccess;
            case ConfigurationOutcome.Invalid:
                await error.WriteLineAsync("Configuration error: " + message).ConfigureAwait(false);
                return ExitConfigurationError;
        }

        var descriptor = configuration!.Describe();
        var resultsDirectory = configuration.ResultsDirectory ?? DefaultResultsDirectory();
        if (resultsDirectory is null)
        {
            await error.WriteLineAsync($"Could not find the repository root (AgentExperience.NET.sln) above the current directory; set {LiveConfiguration.ResultsDirectoryVariable}.").ConfigureAwait(false);
            return ExitConfigurationError;
        }

        var budget = configuration.Budget ?? new LiveBudget(design.DefaultMaxModelCalls, design.DefaultMaxTotalTokens);
        await output.WriteLineAsync($"Live reuse experiment: {configuration}; budget {budget.MaxModelCalls} calls / {budget.MaxTotalTokens} tokens.").ConfigureAwait(false);
        if (!design.IsRegistered(descriptor.Provider, descriptor.RequestedModel))
        {
            await output.WriteLineAsync("This provider and model are not registered in preregistration.json, so this run is exploratory and its report says so.").ConfigureAwait(false);
        }

        // The ledger is written BEFORE the first model call, and every run -- complete, stopped, refused -- adds a line.
        // Only the first complete run per provider and model is the pre-registered confirmatory result, so a run that
        // went badly cannot quietly disappear and a later one take its place.
        Directory.CreateDirectory(resultsDirectory);
        var ledger = new RunLedger(Path.Combine(resultsDirectory, RunLedger.FileName));
        var earlier = ledger.CountFor(descriptor.Provider, descriptor.RequestedModel);
        var runId = clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        ledger.Append(runId, clock.GetUtcNow(), descriptor, design.GitBlobId, "started", "-");
        var partial = Path.Combine(resultsDirectory, runId + ".partial.jsonl");

        using var model = (createClient ?? (config => config.CreateChatClient()))(configuration);
        var result = await RunGuardedAsync(
            new LiveExperimentOptions
            {
                Model = model,
                Descriptor = descriptor,
                Design = design,
                Budget = budget,
                Clock = clock,
                MinimumCallInterval = configuration.MinimumCallInterval,
                Progress = output,

                // Every run, as it completes: if the experiment aborts, what was paid for is still on disk.
                OnRunRecorded = record => File.AppendAllText(partial, LiveReuseReport.JsonLine(record) + "\n"),
            },
            error,
            cancellationToken).ConfigureAwait(false);

        if (result is null)
        {
            ledger.Append(runId, clock.GetUtcNow(), descriptor, design.GitBlobId, "refused", "-");
            return ExitHarnessRefused;
        }

        result = result with { EarlierLedgerEntries = earlier };
        var baseName = LiveReuseReport.BaseName(result);
        var path = Path.Combine(resultsDirectory, baseName);
        for (var suffix = 2; File.Exists(path + ".md") || File.Exists(path + ".json"); suffix++)
        {
            path = Path.Combine(resultsDirectory, baseName + "-run" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        await File.WriteAllTextAsync(path + ".md", LiveReuseReport.Markdown(result), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(path + ".json", LiveReuseReport.Json(result), cancellationToken).ConfigureAwait(false);

        // The full raw results now hold everything the partial file did.
        File.Delete(partial);
        ledger.Append(runId, clock.GetUtcNow(), descriptor, design.GitBlobId, result.Complete ? "complete: " + result.Conclusion : "stopped at budget cap", Path.GetFileName(path) + ".md");

        await output.WriteLineAsync($"Conclusion: {result.Conclusion}. Reference {result.Reference.Verdict}; content {result.Content.Verdict}; negative control {result.NegativeControl.Verdict}.").ConfigureAwait(false);
        await output.WriteLineAsync($"Used {result.Total.ModelCalls} model calls, {result.Total.InputTokens} input and {result.Total.OutputTokens} output tokens.").ConfigureAwait(false);
        await output.WriteLineAsync($"Wrote {Path.GetFileName(path)}.md and .json to {resultsDirectory}.").ConfigureAwait(false);
        return result.Complete ? ExitSuccess : ExitStoppedAtBudget;
    }

    /// <summary>
    /// The transfer experiment (story 20.4): the same rules as the reuse experiment -- configuration from the environment
    /// only, SKIPPED when unconfigured, the ledger line before the first model call, no key or prompt in any report --
    /// with its own pre-registration, its own ledger (<c>results/transfer-ledger.tsv</c>) and its own report prefix.
    /// </summary>
    private static async Task<int> RunTransferAsync(
        string[] args,
        Func<string, string?> environment,
        TextWriter output,
        TextWriter error,
        TimeProvider clock,
        Func<LiveConfiguration, Microsoft.Extensions.AI.IChatClient>? createClient,
        CancellationToken cancellationToken)
    {
        TransferPreregistration design;
        try
        {
            design = TransferPreregistration.ReadEmbedded();
        }
        catch (PreregistrationException ex)
        {
            await error.WriteLineAsync("The transfer pre-registration could not be read: " + ex.Message).ConfigureAwait(false);
            return ExitHarnessRefused;
        }

        if (args.Contains("--scripted"))
        {
            var scripted = await RunTransferGuardedAsync(
                new TransferExperimentOptions
                {
                    Model = new ScriptedOperatorModel(),
                    Descriptor = new RunDescriptor("scripted", ScriptedOperatorModel.ModelId, "none (offline)", null, null, "none: scripted run, nothing is billed"),
                    Design = design,
                    Clock = clock,
                },
                error,
                cancellationToken).ConfigureAwait(false);

            if (scripted is null)
            {
                return ExitHarnessRefused;
            }

            await output.WriteAsync(TransferReport.Markdown(scripted)).ConfigureAwait(false);
            return ExitSuccess;
        }

        var (outcome, configuration, message) = LiveConfiguration.Read(environment, design.DefaultMaxModelCalls, design.DefaultMaxTotalTokens);
        switch (outcome)
        {
            case ConfigurationOutcome.NotConfigured:
                await output.WriteLineAsync("SKIPPED: " + message).ConfigureAwait(false);
                return ExitSuccess;
            case ConfigurationOutcome.Invalid:
                await error.WriteLineAsync("Configuration error: " + message).ConfigureAwait(false);
                return ExitConfigurationError;
        }

        var descriptor = configuration!.Describe();
        var resultsDirectory = configuration.ResultsDirectory ?? DefaultResultsDirectory();
        if (resultsDirectory is null)
        {
            await error.WriteLineAsync($"Could not find the repository root (AgentExperience.NET.sln) above the current directory; set {LiveConfiguration.ResultsDirectoryVariable}.").ConfigureAwait(false);
            return ExitConfigurationError;
        }

        var budget = configuration.Budget ?? new LiveBudget(design.DefaultMaxModelCalls, design.DefaultMaxTotalTokens);
        await output.WriteLineAsync($"Transfer experiment: {configuration}; budget {budget.MaxModelCalls} calls / {budget.MaxTotalTokens} tokens.").ConfigureAwait(false);

        // As in the reuse experiment: the ledger line is written BEFORE the first model call, and only the first complete
        // run per provider and model is the confirmatory result.
        Directory.CreateDirectory(resultsDirectory);
        var ledger = new RunLedger(Path.Combine(resultsDirectory, RunLedger.TransferFileName));
        var earlier = ledger.CountFor(descriptor.Provider, descriptor.RequestedModel);
        var runId = clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        ledger.Append(runId, clock.GetUtcNow(), descriptor, design.GitBlobId, "started", "-");
        var partial = Path.Combine(resultsDirectory, TransferReport.Prefix + runId + ".partial.jsonl");

        using var model = (createClient ?? (config => config.CreateChatClient()))(configuration);
        var result = await RunTransferGuardedAsync(
            new TransferExperimentOptions
            {
                Model = model,
                Descriptor = descriptor,
                Design = design,
                Budget = budget,
                Clock = clock,
                MinimumCallInterval = configuration.MinimumCallInterval,
                Progress = output,
                OnRunRecorded = record => File.AppendAllText(partial, LiveReuseReport.JsonLine(record) + "\n"),
            },
            error,
            cancellationToken).ConfigureAwait(false);

        if (result is null)
        {
            ledger.Append(runId, clock.GetUtcNow(), descriptor, design.GitBlobId, "refused", "-");
            return ExitHarnessRefused;
        }

        result = result with { EarlierLedgerEntries = earlier };
        var baseName = TransferReport.BaseName(result);
        var path = Path.Combine(resultsDirectory, baseName);
        for (var suffix = 2; File.Exists(path + ".md") || File.Exists(path + ".json"); suffix++)
        {
            path = Path.Combine(resultsDirectory, baseName + "-run" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        await File.WriteAllTextAsync(path + ".md", TransferReport.Markdown(result), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(path + ".json", TransferReport.Json(result), cancellationToken).ConfigureAwait(false);
        File.Delete(partial);
        ledger.Append(runId, clock.GetUtcNow(), descriptor, design.GitBlobId, result.Complete ? "complete: " + result.Conclusion : "stopped at budget cap", Path.GetFileName(path) + ".md");

        await output.WriteLineAsync($"Conclusion: {result.Conclusion}. Reference {result.Reference.Verdict}; content {result.Content.Verdict}; mismatched-trait control {result.MismatchedControl.Verdict}.").ConfigureAwait(false);
        await output.WriteLineAsync($"Used {result.Total.ModelCalls} model calls, {result.Total.InputTokens} input and {result.Total.OutputTokens} output tokens.").ConfigureAwait(false);
        await output.WriteLineAsync($"Wrote {Path.GetFileName(path)}.md and .json to {resultsDirectory}.").ConfigureAwait(false);
        return result.Complete ? ExitSuccess : ExitStoppedAtBudget;
    }

    private static async Task<TransferExperimentResult?> RunTransferGuardedAsync(TransferExperimentOptions options, TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            return await TransferExperiment.RunAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PreregistrationException or TaskSetException or HarnessIntegrityException)
        {
            await error.WriteLineAsync($"The harness refused to report: {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
            return null;
        }
    }

    private static async Task<LiveExperimentResult?> RunGuardedAsync(LiveExperimentOptions options, TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            return await LiveReuseExperiment.RunAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PreregistrationException or TaskSetException or HarnessIntegrityException)
        {
            // These messages are the harness's own and carry no request data.
            await error.WriteLineAsync($"The harness refused to report: {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
            return null;
        }
    }

    /// <summary><c>experiments/AgentExperience.LiveReuse/results</c> under the repository root found above the current directory.</summary>
    private static string? DefaultResultsDirectory()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AgentExperience.NET.sln")))
            {
                return Path.Combine(directory.FullName, "experiments", "AgentExperience.LiveReuse", "results");
            }
        }

        return null;
    }
}

/// <summary>
/// <c>results/ledger.tsv</c> (or, for the transfer experiment, <c>results/transfer-ledger.tsv</c>): one line when a run starts and one when it ends, appended and never rewritten. It holds the
/// provider, the model, the pre-registration's blob id and the outcome -- no key, no endpoint beyond the host.
/// </summary>
internal sealed class RunLedger(string path)
{
    public const string FileName = "ledger.tsv";

    /// <summary>The transfer experiment's own ledger, beside the reuse experiment's.</summary>
    public const string TransferFileName = "transfer-ledger.tsv";
    private const string Header = "run\tutc\tprovider\tmodel\thost\tpreregistration\tevent\treport";

    /// <summary>How many earlier runs (distinct run ids) this ledger holds for the provider and model.</summary>
    public int CountFor(string provider, string model) => !File.Exists(path)
        ? 0
        : File.ReadAllLines(path)
            .Skip(1)
            .Select(line => line.Split('\t'))
            .Where(fields => fields.Length >= 4 && fields[2] == provider && fields[3] == model)
            .Select(fields => fields[0])
            .Distinct(StringComparer.Ordinal)
            .Count();

    public void Append(string runId, DateTimeOffset at, RunDescriptor descriptor, string preregistration, string outcome, string report)
    {
        var line = string.Join('\t',
            runId,
            at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            descriptor.Provider,
            descriptor.RequestedModel,
            descriptor.EndpointHost,
            preregistration,
            outcome,
            report);
        File.AppendAllText(path, (File.Exists(path) ? string.Empty : Header + "\n") + line + "\n");
    }
}

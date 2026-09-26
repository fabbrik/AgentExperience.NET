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
            await output.WriteLineAsync("Usage: dotnet run --project experiments/AgentExperience.LiveReuse -c Release [-- --scripted]").ConfigureAwait(false);
            await output.WriteLineAsync("See experiments/AgentExperience.LiveReuse/README.md for the variables, the cost and the design.").ConfigureAwait(false);
            return ExitSuccess;
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
/// <c>results/ledger.tsv</c>: one line when a run starts and one when it ends, appended and never rewritten. It holds the
/// provider, the model, the pre-registration's blob id and the outcome -- no key, no endpoint beyond the host.
/// </summary>
internal sealed class RunLedger(string path)
{
    public const string FileName = "ledger.tsv";
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

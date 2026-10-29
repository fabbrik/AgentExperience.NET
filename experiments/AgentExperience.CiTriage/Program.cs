using System.Globalization;
using AgentExperience.CiTriage;
using AgentExperience.CiTriage.GitHub;
using AgentExperience.CiTriage.Model;
using AgentExperience.CiTriage.Scripted;
using AgentExperience.Storage.Postgres;
using Microsoft.Extensions.AI;
using Npgsql;

// Story 26.1: triage this repository's failed CI runs with the library learning from each one. See README.md.
return await CiTriageProgram.MainAsync(args, Console.Out, Console.Error);

internal static class CiTriageProgram
{
    public const string PostgresVariable = "AGENTEXPERIENCE_CITRIAGE_PG";
    public const string DefaultRepository = "fabbrik/AgentExperience.NET";
    public const int ScriptedSeed = 26_1;

    public static async Task<int> MainAsync(string[] args, TextWriter output, TextWriter error)
    {
        CommandLine options;
        try
        {
            options = CommandLine.Parse(args);
        }
        catch (ArgumentException ex)
        {
            await error.WriteLineAsync(ex.Message);
            await error.WriteLineAsync(CommandLine.Usage);
            return 1;
        }

        var root = RepositoryRoot();
        var projectDirectory = Path.Combine(root, "experiments", "AgentExperience.CiTriage");
        var results = options.Results ?? Path.Combine(projectDirectory, "results", options.Scripted ? "scripted" : string.Empty);
        var cache = options.Cache ?? Path.Combine(projectDirectory, ".cache");
        var postgres = options.Postgres ?? (Environment.GetEnvironmentVariable(PostgresVariable) is { Length: > 0 } fromEnvironment ? fromEnvironment : null);

        IGitHub gitHub;
        IRepository repository;
        IChatClient model;
        string modelName;
        if (options.Scripted)
        {
            var fake = ScriptedScenario.Create();
            (gitHub, repository, model, modelName) = (fake, fake, new ScriptedTriageModel(), ScriptedTriageModel.ModelId);
        }
        else
        {
            var key = Environment.GetEnvironmentVariable(OpenRouter.KeyVariable);
            if (string.IsNullOrWhiteSpace(key))
            {
                await error.WriteLineAsync($"{OpenRouter.KeyVariable} is not set. Set it for a live pass, or pass --scripted for the offline stand-in.");
                return 1;
            }

            modelName = Environment.GetEnvironmentVariable(OpenRouter.ModelVariable) is { Length: > 0 } named ? named : OpenRouter.DefaultModel;
            gitHub = new GhCliGitHub(options.Repository, cache);
            repository = new GitCliRepository(root);
            model = OpenRouter.Create(key, modelName);
        }

        var ledgerPath = Path.Combine(results, Ledger.FileName);
        IReadOnlyList<LedgerRow> earlier = [];
        if (postgres is not null)
        {
            await using (var owner = NpgsqlDataSource.Create(postgres))
            {
                await ExperienceSchemaMigrator.MigrateAsync(owner, CancellationToken.None);
            }

            earlier = Ledger.Read(ledgerPath);
        }

        var processed = earlier.Where(row => row.Final).Select(row => row.RunId).ToHashSet();
        await output.WriteLineAsync($"CI triage: model {modelName}, {(postgres is null ? "in-memory storage (full replay)" : "PostgreSQL")}, budget USD {options.Budget.MaxUsd.ToString(CultureInfo.InvariantCulture)}.");

        var (failures, result) = await TriagePass.RunAsync(
            gitHub, repository, model, options.Budget, postgres, processed, options.Scripted ? TriagePass.SeededIds(ScriptedSeed) : null, CancellationToken.None);

        var newIds = result.Rows.Select(row => row.RunId).ToHashSet();
        var ledger = earlier.Where(row => !newIds.Contains(row.RunId)).Concat(result.Rows).ToList();
        Ledger.Write(ledgerPath, ledger);
        var date = DateOnly.FromDateTime(DateTime.UtcNow);
        var reportPath = Path.Combine(results, Report.FileName(date));
        await File.WriteAllTextAsync(reportPath, Report.Render(new ReportContext(date, modelName, postgres is null ? "in-memory" : "PostgreSQL", options.Scripted), result, ledger));

        var o = result.Estimate.Overall;
        await output.WriteLineAsync($"{failures.Count} failures; triaged {result.Triages.Count}; treatment {o.Treatment.Successes}/{o.Treatment.Runs}, holdout {o.Holdout.Successes}/{o.Holdout.Runs}; verdict {o.Verdict}.");
        await output.WriteLineAsync($"Wrote {Shown(root, ledgerPath)} and {Shown(root, reportPath)}.");
        if (result.StoppedReason is { } stopped)
        {
            await error.WriteLineAsync("Stopped early: " + stopped);
            return 2;
        }

        return 0;
    }

    /// <summary>A path relative to the repository when it lies inside it, else as given.</summary>
    private static string Shown(string root, string path) =>
        Path.GetRelativePath(root, path) is var relative && !relative.StartsWith("..", StringComparison.Ordinal) ? relative : path;

    /// <summary>The repository root: the nearest directory up from here holding the solution file.</summary>
    private static string RepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AgentExperience.NET.sln")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new InvalidOperationException("Run from inside the repository: AgentExperience.NET.sln was not found above the current directory.");
    }
}

/// <summary>The command line.</summary>
internal sealed record CommandLine(bool Scripted, string? Postgres, string Repository, Budget Budget, string? Results, string? Cache)
{
    public const string Usage =
        "Usage: dotnet run --project experiments/AgentExperience.CiTriage -- [--scripted] [--postgres <conn>] [--repo owner/name]\n"
        + "       [--budget-usd 0.50] [--max-calls 400] [--max-tokens 4000000] [--input-price 0.10] [--output-price 0.50] [--results <dir>] [--cache <dir>]";

    public static CommandLine Parse(string[] args)
    {
        var scripted = false;
        string? postgres = null;
        var repository = CiTriageProgram.DefaultRepository;
        var budget = Budget.Default;
        string? results = null;
        string? cache = null;
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            decimal Decimal() => decimal.TryParse(Value(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value > 0
                ? value
                : throw new ArgumentException($"{args[i]} needs a positive number.");
            switch (args[i])
            {
                case "--scripted": scripted = true; break;
                case "--postgres": postgres = Value(); break;
                case "--repo": repository = Value(); break;
                case "--budget-usd": budget = budget with { MaxUsd = Decimal() }; break;
                case "--max-calls": budget = budget with { MaxModelCalls = (int)Decimal() }; break;
                case "--max-tokens": budget = budget with { MaxTotalTokens = (long)Decimal() }; break;
                case "--input-price": budget = budget with { InputUsdPerMillion = Decimal() }; break;
                case "--output-price": budget = budget with { OutputUsdPerMillion = Decimal() }; break;
                case "--results": results = Value(); break;
                case "--cache": cache = Value(); break;
                default: throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        return new CommandLine(scripted, postgres, repository, budget, results, cache);
    }
}

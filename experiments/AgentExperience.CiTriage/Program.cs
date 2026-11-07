using System.Globalization;
using AgentExperience.CiTriage;
using AgentExperience.CiTriage.GitHub;
using AgentExperience.CiTriage.Model;
using AgentExperience.CiTriage.Scripted;
using AgentExperience.Core.Holdout;
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
        // --scripted replays fake data: it never touches PostgreSQL, whatever --postgres or the variable say. A SQLite file
        // (--storage sqlite:<path>, story 28.7) is local, so a scripted pass may use one.
        var sqlite = options.Sqlite;
        var postgres = options.Scripted || sqlite is not null
            ? null
            : options.Postgres ?? (Environment.GetEnvironmentVariable(PostgresVariable) is { Length: > 0 } fromEnvironment ? fromEnvironment : null);
        var storageName = sqlite is not null ? "SQLite" : postgres is null ? "in-memory" : "PostgreSQL";

        IGitHub gitHub;
        IRepository repository;
        IChatClient model;
        string modelName;
        if (options.Scripted)
        {
            var fake = ScriptedScenario.Create(waitingFixed: options.ScriptedLater);
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
        else if (sqlite is not null)
        {
            // The file is migrated on first use; its run store keeps the runs still waiting for their fix.
            earlier = Ledger.Read(ledgerPath);
        }

        await output.WriteLineAsync($"CI triage: model {modelName}, {(storageName == "in-memory" ? "in-memory storage (full replay)" : storageName)}, budget USD {options.Budget.MaxUsd.ToString(CultureInfo.InvariantCulture)}.");

        var rows = new Dictionary<long, LedgerRow>();
        ReplayResult? result = null;
        string? failed = null;
        try
        {
            (var failures, result) = await TriagePass.RunAsync(
                gitHub, repository, model, options.Budget, postgres, earlier, options.Scripted ? TriagePass.SeededIds(ScriptedSeed) : null, rows, CancellationToken.None, sqlite);
            var o = result.Estimate.Overall;
            await output.WriteLineAsync($"{failures.Count} failures; triaged {result.Triages.Count}; treatment {o.Treatment.Successes}/{o.Treatment.Runs}, holdout {o.Holdout.Successes}/{o.Holdout.Runs}; verdict {o.Verdict}.");
        }
        catch (Exception ex)
        {
            failed = "the pass failed: " + ex.GetType().Name;
            throw;
        }
        finally
        {
            // Whatever ran is written, even when the pass threw: durable storage may already hold its lessons.
            result ??= new ReplayResult([.. rows.Values], [], ExperienceBenefitEstimator.Empty(), failed ?? "the pass did not finish", 0, 0, 0, 0m);
            var ledger = earlier.Where(row => !rows.ContainsKey(row.RunId)).Concat(rows.Values).ToList();
            Ledger.Write(ledgerPath, ledger);
            var date = DateOnly.FromDateTime(DateTime.UtcNow);
            var reportPath = Path.Combine(results, Report.FileName(date));
            await File.WriteAllTextAsync(reportPath, Report.Render(new ReportContext(date, modelName, storageName, options.Scripted), result, ledger));
            await output.WriteLineAsync($"Wrote {Shown(root, ledgerPath)} and {Shown(root, reportPath)}.");
        }

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
    /// <summary>Story 28.7: a SQLite file (<c>--storage sqlite:&lt;path&gt;</c>), whose run store keeps runs between passes.</summary>
    public string? Sqlite { get; init; }

    /// <summary>The scripted history a day later, when the failure waiting for its fix has one (<c>--scripted-later</c>).</summary>
    public bool ScriptedLater { get; init; }

    public const string Usage =
        "Usage: dotnet run --project experiments/AgentExperience.CiTriage -- [--scripted [--scripted-later]] [--storage memory|sqlite:<path>|postgres:<conn>] [--postgres <conn>] [--repo owner/name]\n"
        + "       [--budget-usd 0.50] [--max-calls 400] [--max-tokens 4000000] [--input-price 0.10] [--output-price 0.50] [--results <dir>] [--cache <dir>]";

    public static CommandLine Parse(string[] args)
    {
        var scripted = false;
        string? postgres = null;
        var repository = CiTriageProgram.DefaultRepository;
        var budget = Budget.Default;
        string? results = null;
        string? cache = null;
        string? sqlite = null;
        var scriptedLater = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            decimal Decimal() => decimal.TryParse(Value(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value > 0
                ? value
                : throw new ArgumentException($"{args[i]} needs a positive number.");
            int Int() => int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
                ? value
                : throw new ArgumentException($"{args[i]} needs a positive whole number.");
            long Long() => long.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
                ? value
                : throw new ArgumentException($"{args[i]} needs a positive whole number.");
            switch (args[i])
            {
                case "--scripted": scripted = true; break;
                case "--scripted-later": scriptedLater = true; break;
                case "--storage":
                    var storage = Value();
                    if (storage == "memory")
                    {
                        (sqlite, postgres) = (null, null);
                    }
                    else if (storage.StartsWith("sqlite:", StringComparison.Ordinal) && storage.Length > "sqlite:".Length)
                    {
                        sqlite = storage["sqlite:".Length..];
                    }
                    else if (storage.StartsWith("postgres:", StringComparison.Ordinal) && storage.Length > "postgres:".Length)
                    {
                        postgres = storage["postgres:".Length..];
                    }
                    else
                    {
                        throw new ArgumentException("--storage needs memory, sqlite:<path> or postgres:<conn>.");
                    }

                    break;
                case "--postgres": postgres = Value(); break;
                case "--repo": repository = Value(); break;
                case "--budget-usd": budget = budget with { MaxUsd = Decimal() }; break;
                case "--max-calls": budget = budget with { MaxModelCalls = Int() }; break;
                case "--max-tokens": budget = budget with { MaxTotalTokens = Long() }; break;
                case "--input-price": budget = budget with { InputUsdPerMillion = Decimal() }; break;
                case "--output-price": budget = budget with { OutputUsdPerMillion = Decimal() }; break;
                case "--results": results = Value(); break;
                case "--cache": cache = Value(); break;
                default: throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        if (scriptedLater && !scripted)
        {
            throw new ArgumentException("--scripted-later needs --scripted.");
        }

        return new CommandLine(scripted, postgres, repository, budget, results, cache) { Sqlite = sqlite, ScriptedLater = scriptedLater };
    }
}

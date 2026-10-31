using AgentExperience.Abstractions;
using AgentExperience.Cli.Configuration;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.MicrosoftAgentFramework;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.Storage.Postgres.DependencyInjection;
using AgentExperience.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.Cli.Engine;

/// <summary>
/// What one probe needs from the one-call setup: which run it continues, whether its block is shown, what a failed call
/// returned, and where recall's answer goes. <see langword="null"/> for commands that run no agent.
/// </summary>
internal sealed class ProbeSettings
{
    /// <summary>The run the probe continues, or <see langword="null"/> for a block asked for outside a run.</summary>
    public Guid? RunId { get; init; }

    /// <summary>The task identifier the run is stored under.</summary>
    public required string TaskId { get; init; }

    /// <summary>
    /// When <see langword="true"/>, retrieval for the block is asked about text no lesson holds, so the probe delivers no
    /// block (and records nothing for one) while recall still runs under the same identity and scope.
    /// </summary>
    public bool SuppressBlock { get; init; }

    /// <summary>The error the failing call returned, as capture stores it; the classifier marks that call failed with it.</summary>
    public string? FailedCallError { get; init; }

    /// <summary>Recall's answer for the failing call, set by <see cref="ExperienceRecallOptions.OnRecall"/>.</summary>
    public ExperienceRecallResult? Recall { get; set; }
}

/// <summary>
/// Builds the library for one command from the config: the MAF one-call setup over the configured store, with the
/// identity, scope and every policy taken from the config file. A probe additionally continues a given run, never
/// completes it, and classifies its one failing call.
/// </summary>
internal static class AexpHost
{
    /// <summary>Retrieval text no stored lesson can match (see <see cref="ProbeSettings.SuppressBlock"/>).</summary>
    internal const string NoBlockText = "aexpfailurehintprobewithoutblock";

    public static async Task<ServiceProvider> BuildAsync(
        AexpSettings settings,
        TimeProvider clock,
        Func<string, string?> environment,
        ProbeSettings? probe,
        CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddSingleton(clock);
        var identity = new ExperienceIdentity(settings.Authorization(clock.GetUtcNow()), settings.Scope);

        var builder = services.AddAgentExperience(options =>
        {
            options.TimeProvider = clock;
            options.ResolveIdentity = (_, _) => ValueTask.FromResult<ExperienceIdentity?>(identity with
            {
                Authorization = settings.Authorization(clock.GetUtcNow()),
            });
            options.TaskId = probe?.TaskId ?? settings.TaskId;
            options.Sanitization = AgentExperienceDefaults.SanitizationAllowing([.. settings.AllowArguments]);
            foreach (var recipe in settings.Recipes)
            {
                options.Recipe(
                    recipe.Tool,
                    recipe.Arguments,
                    recipe.ResultExcerpt ? static call => call.Result?.ToString() : null);
            }

            options.Injection = injection =>
            {
                if (settings.MaxRecords is not null || settings.MaxBytes is not null)
                {
                    injection.Limits = injection.Limits with
                    {
                        MaxRecords = settings.MaxRecords ?? injection.Limits.MaxRecords,
                        MaxBytes = settings.MaxBytes ?? injection.Limits.MaxBytes,
                    };
                }

                // The holdout arm is the run's: without a run there is nothing to measure against.
                if (settings.HoldoutRate > 0 && probe?.RunId is not null)
                {
                    injection.Holdout = new ExperienceHoldoutOptions
                    {
                        Rate = settings.HoldoutRate,
                        Salt = settings.HoldoutSalt ?? ExperienceHoldoutOptions.DefaultSalt,
                    };
                }

                if (probe is { SuppressBlock: true } && injection.ResolveRequestAsync is { } resolve)
                {
                    injection.ResolveRequestAsync = async (context, token) =>
                        await resolve(context, token).ConfigureAwait(false) is { } request ? request with { TaskText = NoBlockText } : null;
                }
            };

            if (settings.RecallEnabled && probe is not null)
            {
                var recall = new ExperienceRecallOptions
                {
                    AllowCoarseMatch = settings.AllowCoarseMatch,
                    OnRecall = result => probe.Recall = result,
                };
                recall = recall with
                {
                    MaxHintsPerRun = settings.MaxHintsPerRun ?? recall.MaxHintsPerRun,
                    MaxHintBytes = settings.MaxHintBytes ?? recall.MaxHintBytes,
                    LookupTimeout = settings.LookupTimeout ?? recall.LookupTimeout,
                };
                options.Recall = recall;
            }

            options.Capture = capture =>
            {
                if (probe is null)
                {
                    return;
                }

                // A probe asks the library a question inside the run; the run itself is captured by 'run end'.
                var resolveRun = capture.ResolveRun;
                if (probe.RunId is { } runId)
                {
                    capture.ResolveRun = context => resolveRun(context) with { ContinuesRunId = runId };
                }

                capture.ShouldCompleteRun = static _ => false;
                capture.ClassifyToolResult = _ => probe.FailedCallError;
            };
        });

        switch (settings.Store)
        {
            case StoreSetting.Sqlite sqlite:
                // Journaled and idempotent: applies nothing when the file is current, creates it (owner-only) when new.
                await ExperienceSqliteSchema.MigrateAsync(sqlite.Path, cancellationToken).ConfigureAwait(false);
                builder.UseSqlite(sqlite.Path);
                break;
            case StoreSetting.Postgres postgres:
                var connectionString = environment(postgres.Variable);
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    throw new AexpConfigException($"store: the environment variable {postgres.Variable} holds no connection string.");
                }

                builder.UsePostgres(connectionString);
                services.AddAgentExperiencePostgresRunStore();
                break;
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = true });
    }
}

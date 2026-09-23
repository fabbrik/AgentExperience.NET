using System.Globalization;
using AgentExperience.Abstractions;
using AgentExperience.Core.Capture;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Retrieval;
using AgentExperience.Core.Sanitization;
using AgentExperience.Sample.EndToEnd.Doubles;
using AgentExperience.Sample.EndToEnd.Fixtures;
using AgentExperience.Storage.Postgres;
using AgentExperience.Storage.Postgres.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AgentExperience.Sample.EndToEnd;

/// <summary>
/// The value of <c>AGENTEXPERIENCE_SAMPLE_POSTGRES</c> is not a connection string the driver can
/// parse. Thrown at the one place the sample tries to use it, so a bad connection string is never
/// confused with an <see cref="ArgumentException"/> from anywhere else in the run.
/// </summary>
internal sealed class SampleConnectionStringException(Exception inner)
    : Exception("The PostgreSQL connection string could not be parsed.", inner);

/// <summary>
/// One execution of the sample and the parts of it a caller may want to look at afterwards.
/// </summary>
/// <param name="Transcript">The seven stages and what they produced.</param>
/// <param name="Run">The run itself, which holds the capture snapshot, the record read back from the store, run B's session, and the injected block.</param>
/// <param name="Records">The in-memory record store, in the default mode only; <see langword="null"/> in PostgreSQL mode.</param>
/// <param name="Feedback">The in-memory reuse-feedback ledger, in the default mode only; <see langword="null"/> in PostgreSQL mode.</param>
internal sealed record SampleExecution(
    SampleTranscript Transcript,
    SampleRun Run,
    InMemoryRecordStore? Records,
    InMemoryReuseFeedbackStore? Feedback);

/// <summary>
/// Composes the sample and runs it: picks the storage mode, registers the ports, executes the seven
/// stages, and writes the transcript.
/// </summary>
/// <remarks>
/// <para>
/// The storage mode is the only branch in the whole sample. Everything the seven stages do is
/// identical in both modes, which is what keeps the in-memory mode honest about the real adapters.
/// </para>
/// <para>
/// The sample registers no <c>ActivityListener</c> and no metric exporter. It is a library
/// consumer, not a telemetry host: the loop's own <c>ActivitySource</c> and <c>Meter</c> emit only
/// when something is listening, and the sample behaves identically either way.
/// </para>
/// </remarks>
public static class SampleHost
{
    /// <summary>The environment variable that switches the sample onto the real PostgreSQL adapters.</summary>
    public const string PostgresEnvironmentVariable = "AGENTEXPERIENCE_SAMPLE_POSTGRES";

    /// <summary>The instant the sample's fixture clock starts at.</summary>
    private static readonly DateTimeOffset ClockStart = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>How far the fixture clock moves between readings.</summary>
    private static readonly TimeSpan ClockStep = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// The sanitization policies capture applies. Anything not named here is omitted from the
    /// captured run; anything named as a secret is redacted before it is ever stored.
    /// </summary>
    private static readonly SanitizationOptions Sanitization = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        ["ToolArguments"] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "ticketId", "strategy" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal) { SampleTools.SecretArgumentName },
            MaxDepth: 2,
            MaxFieldCount: 10,
            MaxValueLength: 4_000,
            MaxFieldNameLength: 100),
        ["ToolResult"] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "value" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal),
            MaxDepth: 2,
            MaxFieldCount: 5,
            MaxValueLength: 4_000,
            MaxFieldNameLength: 100),
    });

    private static readonly CaptureLimits Limits = new(
        MaxAttemptsPerRun: 10,
        MaxToolCallsPerAttempt: 50,
        MaxResultLength: 4_000,
        MaxErrorLength: 4_000);

    /// <summary>
    /// Runs the sample and returns the process exit code: 0 when the seven stages happened, 1 when
    /// they did not.
    /// </summary>
    /// <param name="output">Where the transcript is written.</param>
    /// <param name="postgresConnectionString">
    /// The value of <see cref="PostgresEnvironmentVariable"/>. Null, empty, or whitespace selects the
    /// in-memory demonstration doubles; anything else selects the PostgreSQL adapters and is never
    /// silently fallen back on, because a mode that quietly becomes another mode is a mode that lies.
    /// </param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public static Task<int> RunAsync(
        TextWriter output,
        string? postgresConnectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);

        return RunAsync(
            output,
            token => ExecuteAsync(output, postgresConnectionString, token),
            cancellationToken);
    }

    /// <summary>
    /// The exit-code and one-line-message half of <see cref="RunAsync(TextWriter, string?, CancellationToken)"/>,
    /// over whatever <paramref name="execute"/> does. Separated so every failure arm below can be
    /// reached by a test without a database, a network, or a corrupted store.
    /// </summary>
    /// <param name="output">Where the transcript, or the one-line failure, is written.</param>
    /// <param name="execute">Composes and runs the sample.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    internal static async Task<int> RunAsync(
        TextWriter output,
        Func<CancellationToken, Task<SampleExecution>> execute,
        CancellationToken cancellationToken)
    {
        try
        {
            var execution = await execute(cancellationToken).ConfigureAwait(false);
            output.Write(execution.Transcript.Render());
            return 0;
        }
        catch (SampleConnectionStringException ex)
        {
            output.WriteLine(PostgresFailure("does not hold a usable connection string", ex.InnerException ?? ex));
            return 1;
        }
        catch (ExperienceStoreException ex)
        {
            // The PostgreSQL mode fails loudly rather than falling back. Nothing was stored anywhere,
            // and the in-memory doubles were never registered.
            output.WriteLine(PostgresFailure("could not be prepared", ex));
            return 1;
        }
        catch (NpgsqlException ex)
        {
            output.WriteLine(PostgresFailure("could not be reached", ex));
            return 1;
        }
        catch (SampleStageFailedException ex)
        {
            output.WriteLine(ex.Message);
            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            output.WriteLine("The sample was cancelled before its seven stages finished. Nothing above this line is a complete run.");
            return 1;
        }
        catch (Exception ex)
        {
            // The doc above promises "1 when they did not", and the four arms above do not cover
            // everything that can reach here: Npgsql lets SocketException and TimeoutException out
            // unwrapped, and a fixture can always surprise us. An unhandled exception would end the
            // process with a stack trace full of this machine's absolute paths, in the one artifact
            // whose whole job is to look trustworthy on a stranger's machine. The type name is the
            // most that is printed -- never the message, which can carry a path or a host name.
            output.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "The sample failed before its seven stages finished ({0}). Nothing above this line is a complete run. Re-run with no environment variables set for the default in-memory mode, which needs no Docker, no database, and no network.",
                ex.GetType().Name));
            return 1;
        }
    }

    /// <summary>
    /// Composes the sample and executes its seven stages, returning what they produced. Failures
    /// propagate; <see cref="RunAsync(TextWriter, string?, CancellationToken)"/> is what turns them
    /// into an exit code and a one-line message.
    /// </summary>
    /// <param name="output">Where composition-time notes (the schema migration line) are written.</param>
    /// <param name="postgresConnectionString">The connection string, or null/blank for the in-memory doubles.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    internal static async Task<SampleExecution> ExecuteAsync(
        TextWriter output,
        string? postgresConnectionString,
        CancellationToken cancellationToken)
    {
        var mode = ModeFor(postgresConnectionString);
        NpgsqlDataSource? dataSource = null;

        try
        {
            var services = new ServiceCollection();

            // The fixtures, registered before anything that would otherwise take the system clock:
            // AddAgentExperienceRetrieval registers TimeProvider.System only if nothing else has.
            var clock = new SteppingTimeProvider(ClockStart, ClockStep);
            services.AddSingleton(clock);
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(new DeterministicIds());

            InMemoryRecordStore? records = null;
            InMemoryReuseFeedbackStore? feedback = null;

            if (mode == SampleStorageMode.Postgres)
            {
                dataSource = CreateDataSource(postgresConnectionString!);

                // The schema first, exactly as a host would on startup. The base schema needs no
                // pgvector and no superuser.
                var migration = await ExperienceSchemaMigrator.MigrateAsync(dataSource, cancellationToken).ConfigureAwait(false);
                output.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "PostgreSQL mode: schema migration applied {0} script(s).",
                    migration.AppliedScripts.Count));

                services.AddAgentExperiencePostgresStore(dataSource);
                services.AddAgentExperiencePostgresCandidateSource(dataSource);
                services.AddAgentExperiencePostgresReuseFeedbackStore(dataSource);
            }
            else
            {
                // The three ports the loop needs, as demonstration doubles. The candidate source
                // reads the record store's own records, the way the PostgreSQL candidate source
                // reads the table the PostgreSQL record store writes -- so neither mode needs an
                // extra "index this record" step and the seven stages stay identical.
                records = new InMemoryRecordStore();
                feedback = new InMemoryReuseFeedbackStore();
                services.AddSingleton<IExperienceRecordStore>(records);
                services.AddSingleton<IExperienceCandidateSource>(new InMemoryCandidateSource(records));
                services.AddSingleton<IExperienceReuseFeedbackStore>(feedback);
            }

            // Everything below this line is the same in both modes.
            services.AddAgentExperienceCore(Sanitization, Limits);
            services.AddAgentExperienceReuseFeedback();
            services.AddAgentExperienceRetrieval(RetrievalPolicy.Default with { Timeout = TimeSpan.FromSeconds(30) });

            await using var provider = services.BuildServiceProvider();
            var run = new SampleRun(provider, mode);
            var transcript = await run.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return new SampleExecution(transcript, run, records, feedback);
        }
        finally
        {
            if (dataSource is not null)
            {
                await dataSource.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Which ports a connection string selects. Whitespace is treated as unset, so an exported but
    /// empty variable is the default mode rather than a failure.
    /// </summary>
    /// <param name="postgresConnectionString">The value of <see cref="PostgresEnvironmentVariable"/>.</param>
    internal static SampleStorageMode ModeFor(string? postgresConnectionString) =>
        string.IsNullOrWhiteSpace(postgresConnectionString) ? SampleStorageMode.InMemory : SampleStorageMode.Postgres;

    /// <summary>
    /// Builds the data source, and is the only place a parse failure of the environment variable can
    /// come from. Filtering on the storage mode instead -- which this used to do -- reported every
    /// <see cref="ArgumentException"/> and every <see cref="ArgumentNullException"/> raised anywhere
    /// in the seven stages as a bad connection string.
    /// </summary>
    /// <param name="connectionString">The value of <see cref="PostgresEnvironmentVariable"/>.</param>
    private static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        try
        {
            return NpgsqlDataSource.Create(connectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or NotSupportedException)
        {
            throw new SampleConnectionStringException(ex);
        }
    }

    private static string PostgresFailure(string what, Exception ex) => string.Format(
        CultureInfo.InvariantCulture,
        "PostgreSQL mode failed: the database named by {0} {1} ({2}). The sample does not fall back to its in-memory doubles; unset {0} to run in the default mode.",
        PostgresEnvironmentVariable,
        what,
        ex.GetType().Name);
}

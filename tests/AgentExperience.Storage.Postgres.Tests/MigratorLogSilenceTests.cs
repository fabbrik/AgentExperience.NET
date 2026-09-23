using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 4.3, AD-E (deferred item 5 from story 2.6's review): the schema migrator emits nothing to the
/// host's log sinks -- not to the console, not to <see cref="Trace"/>/<see cref="Debug"/> listeners, not
/// to an <see cref="ILogger"/>, and not to an <c>AgentExperience.*</c> activity source -- on a clean run
/// and on a failing script alike. Before this, <c>.LogToNowhere()</c> appeared once and no test captured
/// any of those sinks, so a change that wired DbUp's <c>LogToConsole()</c> or <c>LogToTrace()</c> -- which
/// print every script name and, on a failure, the database's error text -- passed everything.
/// </summary>
/// <remarks>
/// <para>
/// Verified against dbup-core 6.1.1 when this was written: its engine builder starts with no logger at
/// all, so <c>.LogToNowhere()</c> is belt and braces today. Replacing it with <c>LogToConsole()</c> fails
/// both tests here on the console assertion, and with <c>LogToTrace()</c> on the trace assertion. A
/// future DbUp whose default logs somewhere is caught the same way.
/// </para>
/// <para>
/// Runs in its own collection with parallelization disabled, because the console and the trace
/// listeners are process-wide: a test in another collection writing to either at the same moment
/// would be attributed to the migrator. xUnit runs a non-parallel collection only after every parallel
/// one has finished, so nothing else is running while these sinks are captured.
/// </para>
/// <para>
/// <b>What is out of scope, stated rather than hidden:</b> the host's own Npgsql driver logging. When a
/// host hands the migrator a data source it built with <c>UseLoggerFactory</c>, Npgsql's
/// <c>Npgsql.Command</c> category logs each command's SQL text at <see cref="LogLevel.Information"/> --
/// the migration scripts included -- exactly as it does for every other query the host runs on that data
/// source. That is the host's configuration of its own driver, the scripts carry no row data, and the
/// migrator has no way to reach into it. What this test pins is that nothing <em>else</em> reaches the
/// logger: no category other than Npgsql's, and nothing from Npgsql at <see cref="LogLevel.Warning"/> or
/// above, which is where a failing script's error would surface if anything logged it.
/// </para>
/// </remarks>
[Collection(MigratorLogSilenceCollection.Name)]
public sealed class MigratorLogSilenceTests
{
    private const string FailingPrefix = "AgentExperience.Storage.Postgres.Tests.FailingMigrations.";

    private readonly PostgresFixture _fixture;

    public MigratorLogSilenceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_clean_migration_reaches_no_console_trace_logger_or_activity_sink()
    {
        var logger = new CapturingLoggerFactory();
        await using var dataSource = await CreateLoggedDatabaseAsync("silent_clean", logger);

        var captured = await CaptureAsync(() => ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None));

        Assert.Equal(PostgresExperienceRecordSchema.ScriptNames, ((ExperienceSchemaMigrationResult)captured.Result!).AppliedScripts);
        AssertSilent(captured, logger);
    }

    [Fact]
    public async Task A_failing_script_reaches_no_console_trace_logger_or_activity_sink()
    {
        var logger = new CapturingLoggerFactory();
        await using var dataSource = await CreateLoggedDatabaseAsync("silent_failing", logger);

        var captured = await CaptureAsync(() => ExperienceSchemaMigrator.MigrateAsync(
            dataSource, typeof(MigratorLogSilenceTests).Assembly, FailingPrefix, CancellationToken.None));

        // The failure is reported to the caller, through the documented exception, and nowhere else.
        var failure = Assert.IsType<ExperienceStoreException>(captured.Exception);
        Assert.Contains("0002_broken.sql", failure.Message, StringComparison.Ordinal);
        AssertSilent(captured, logger);
    }

    private static void AssertSilent(Captured captured, CapturingLoggerFactory logger)
    {
        Assert.True(captured.Console.Length == 0, $"The migrator wrote to the console:{Environment.NewLine}{captured.Console}");
        Assert.True(captured.Trace.Length == 0, $"The migrator wrote to a Trace/Debug listener:{Environment.NewLine}{captured.Trace}");
        Assert.True(captured.Activities.Count == 0, $"The migrator started AgentExperience activities: {string.Join(", ", captured.Activities)}");

        var foreign = logger.Entries
            .Where(entry => !entry.Category.StartsWith("Npgsql", StringComparison.Ordinal) || entry.Level >= LogLevel.Warning)
            .Select(entry => $"[{entry.Level}] {entry.Category}: {entry.Message}")
            .ToList();
        Assert.True(foreign.Count == 0, $"The migrator reached the host's ILogger:{Environment.NewLine}{string.Join(Environment.NewLine, foreign)}");
    }

    /// <summary>A fresh database, reached through a data source carrying the host's logger factory -- how a host that logs its driver would hand the migrator its data source.</summary>
    private Task<NpgsqlDataSource> CreateLoggedDatabaseAsync(string purpose, ILoggerFactory loggerFactory) =>
        _fixture.CreateDatabaseAsync(purpose, builder => builder.UseLoggerFactory(loggerFactory));

    /// <summary>Runs <paramref name="action"/> with the console, the trace listeners, and an activity listener captured.</summary>
    private static async Task<Captured> CaptureAsync<T>(Func<Task<T>> action)
    {
        var console = new StringWriter();
        var trace = new StringBuilderListener();
        var activities = new ConcurrentBag<string>();
        var originalOut = Console.Out;
        var originalError = Console.Error;

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("AgentExperience", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => activities.Add($"{activity.Source.Name}/{activity.OperationName}"),
        };

        Console.SetOut(console);
        Console.SetError(console);
        Trace.Listeners.Add(trace);
        ActivitySource.AddActivityListener(activityListener);

        object? result = null;
        Exception? exception = null;
        try
        {
            result = await action();
        }
        catch (Exception ex)
        {
            exception = ex;
        }
        finally
        {
            Trace.Listeners.Remove(trace);
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        return new Captured(result, exception, console.ToString(), trace.Text, [.. activities]);
    }

    private sealed record Captured(object? Result, Exception? Exception, string Console, string Trace, IReadOnlyList<string> Activities);

    private sealed class StringBuilderListener : TraceListener
    {
        private readonly StringBuilder _text = new();

        public string Text
        {
            get
            {
                lock (_text)
                {
                    return _text.ToString();
                }
            }
        }

        public override void Write(string? message)
        {
            lock (_text)
            {
                _text.Append(message);
            }
        }

        public override void WriteLine(string? message)
        {
            lock (_text)
            {
                _text.AppendLine(message);
            }
        }
    }

    internal sealed record LogEntry(string Category, LogLevel Level, string Message);

    /// <summary>A host's logger factory, in miniature: every level enabled, every entry kept.</summary>
    internal sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => [.. _entries];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception)));
        }
    }
}

/// <summary>
/// Its own container, and never run alongside another collection: the sinks the migrator must not reach
/// are process-wide, so nothing else may be writing to them while they are captured.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MigratorLogSilenceCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "MigratorLogSilence";
}

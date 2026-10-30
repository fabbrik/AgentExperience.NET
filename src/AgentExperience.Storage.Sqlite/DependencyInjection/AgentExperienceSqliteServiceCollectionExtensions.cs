using AgentExperience.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentExperience.Storage.Sqlite.DependencyInjection;

/// <summary>Registers the SQLite stores in a <see cref="IServiceCollection"/>.</summary>
public static class AgentExperienceSqliteServiceCollectionExtensions
{
    /// <summary>
    /// Registers, over the database file at <paramref name="databasePath"/>, <see cref="SqliteExperienceRecordStore"/> as the
    /// singleton <see cref="IExperienceRecordStore"/>, <see cref="SqliteExperienceCandidateSource"/> as the singleton
    /// <see cref="IExperienceCandidateSource"/>, <see cref="SqliteExperienceSignatureSource"/> as the singleton
    /// <see cref="IExperienceSignatureSource"/>, a SQLite reuse-feedback ledger as the singleton
    /// <see cref="IExperienceReuseFeedbackStore"/>, and <see cref="SqliteExperienceRunStore"/> as the singleton
    /// <see cref="IExperienceRunStore"/>, so the capture service keeps each completed run and finalization can read it in a
    /// later process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The schema is not migrated here unless <see cref="SqliteStorageOptions.MigrateSchemaOnFirstUse"/> is set: call
    /// <see cref="ExperienceSqliteSchema.MigrateAsync(string, CancellationToken)"/> first.
    /// </para>
    /// <para>
    /// Everything but the record store is added with <c>TryAdd</c>, so ones the host registered first are kept. It will not
    /// mix with another record store: the candidate and signature sources search this file's records, so registering this
    /// beside a different <see cref="IExperienceRecordStore"/> throws. Calling it again with the same path changes nothing.
    /// </para>
    /// <para>
    /// Sharing grants, the grant access log, encryption, vectors and retention sweeps are not provided.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="databasePath">The database file. It is created when missing.</param>
    /// <param name="configure">Sets <see cref="SqliteStorageOptions"/>; called once, here.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="databasePath"/> is blank.</exception>
    /// <exception cref="InvalidOperationException">A different record store, or the SQLite storage over another file, is already registered.</exception>
    public static IServiceCollection AddAgentExperienceSqliteStorage(
        this IServiceCollection services,
        string databasePath,
        Action<SqliteStorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var connectionString = ExperienceSqliteSchema.ConnectionStringFor(databasePath);
        var existing = services
            .Where(descriptor => !descriptor.IsKeyedService)
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<SqliteStorageRegistration>()
            .FirstOrDefault();
        if (existing is not null)
        {
            if (!string.Equals(existing.ConnectionString, connectionString, StringComparison.Ordinal) || configure is not null)
            {
                throw new InvalidOperationException(
                    $"{nameof(AddAgentExperienceSqliteStorage)} was already called; call it once, with its options.");
            }

            return services;
        }

        if (services.Any(descriptor => descriptor.ServiceType == typeof(IExperienceRecordStore)))
        {
            throw new InvalidOperationException(
                $"An {nameof(IExperienceRecordStore)} is already registered. {nameof(AddAgentExperienceSqliteStorage)} registers its own "
                + "record store, which its candidate and signature sources search; beside another record store they would find "
                + "nothing the host writes. Register one storage package, not both.");
        }

        var options = new SqliteStorageOptions();
        configure?.Invoke(options);
        var registration = new SqliteStorageRegistration(connectionString, options);
        services.AddSingleton(registration);

        // One database for every store of this registration: its schema is checked (or, with the opt-in, migrated) once.
        services.AddSingleton(provider =>
            new SqliteExperienceRecordStore(registration.Database, provider.GetService<TimeProvider>()));
        services.AddSingleton<IExperienceRecordStore>(provider => provider.GetRequiredService<SqliteExperienceRecordStore>());
        services.TryAddSingleton<IExperienceCandidateSource>(provider =>
            new SqliteExperienceCandidateSource(provider.GetRequiredService<SqliteExperienceRecordStore>()));
        services.TryAddSingleton<IExperienceSignatureSource>(provider =>
            new SqliteExperienceSignatureSource(provider.GetRequiredService<SqliteExperienceRecordStore>()));
        services.TryAddSingleton<IExperienceReuseFeedbackStore>(_ => new SqliteExperienceReuseFeedbackStore(registration.Database));
        services.TryAddSingleton<IExperienceRunStore>(provider =>
            new SqliteExperienceRunStore(registration.Database, provider.GetService<TimeProvider>(), options.RunSaveBusyTimeout));

        return services;
    }
}

/// <summary>One registration's connection string and options, and the one database its stores share.</summary>
internal sealed class SqliteStorageRegistration(string connectionString, SqliteStorageOptions options)
{
    private readonly Lazy<SqliteDatabase> _database = new(() =>
    {
        if (options.MigrateSchemaOnFirstUse)
        {
            // The first operation migrates; the file is created owner-only now, before SQLite would create it.
            ExperienceSqliteSchema.CreateOwnerOnly(new SqliteDatabase(connectionString).DataSource);
        }

        return new SqliteDatabase(connectionString, options.MigrateSchemaOnFirstUse);
    });

    /// <summary>The database's connection string.</summary>
    public string ConnectionString => connectionString;

    /// <summary>The database every store of this registration runs on.</summary>
    public SqliteDatabase Database => _database.Value;
}

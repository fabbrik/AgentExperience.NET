using AgentExperience.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace AgentExperience.Storage.Postgres.DependencyInjection;

/// <summary>
/// Registers the PostgreSQL Experience Record store in a <see cref="IServiceCollection"/>. The
/// adapter owns its own registration, exactly as Core owns <c>AddAgentExperienceCore</c>, so a host
/// wires the two together without either package knowing the other's concrete types.
/// </summary>
public static class AgentExperiencePostgresServiceCollectionExtensions
{
    /// <summary>
    /// Turns on crypto-shredding for every PostgreSQL component these extensions register: registers one
    /// <see cref="ExperienceEncryption"/> over <paramref name="keyStore"/>, which the record store, the
    /// candidate source, the grant store, the reuse-feedback store and the vectors package's embedding index
    /// all pick up, however the registrations are ordered.
    /// </summary>
    /// <remarks>
    /// <b>The key store must keep its keys outside the database's backup domain</b>, or erasing a record
    /// destroys nothing that a restored backup cannot bring back. See <see cref="IExperienceKeyStore"/> and
    /// docs/guide/crypto-shredding.md, "Key custody". A component constructed directly, without the same
    /// <see cref="ExperienceEncryption"/>, runs in plaintext mode.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="keyStore">Custody of the per-record data keys.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="keyStore"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperiencePostgresEncryption(this IServiceCollection services, IExperienceKeyStore keyStore)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(keyStore);

        services.TryAddSingleton(new ExperienceEncryption(keyStore));

        return services;
    }

    /// <summary>
    /// Registers <see cref="PostgresExperienceRecordStore"/> as the singleton
    /// <see cref="IExperienceRecordStore"/>, over an <see cref="NpgsqlDataSource"/> resolved from the
    /// container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The host owns the data source's lifetime and the store never disposes it. The schema is not
    /// applied here: call
    /// <see cref="ExperienceSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/> once
    /// at startup.
    /// </para>
    /// <para>
    /// If an <see cref="ExperienceGrantAuditing"/> is registered -- which
    /// <see cref="AddAgentExperiencePostgresGrantAccessLog(IServiceCollection, Action{ExperienceGrantAccessFailure}, ExperienceGrantAuditingMode)"/>
    /// does -- the store picks it up and records the reads a grant delivers. Registration order does
    /// not matter: the store is built from the container when it is first resolved. With nothing
    /// registered, auditing is off and reads behave exactly as before.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperiencePostgresStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IExperienceRecordStore>(provider =>
            new PostgresExperienceRecordStore(
                provider.GetRequiredService<NpgsqlDataSource>(),
                onGrantsUnavailable: null,
                auditing: provider.GetService<ExperienceGrantAuditing>(),
                encryption: provider.GetService<ExperienceEncryption>()));

        return services;
    }

    /// <summary>
    /// Registers <see cref="PostgresExperienceRecordStore"/> as the singleton
    /// <see cref="IExperienceRecordStore"/> over <paramref name="dataSource"/>, for a host that keeps
    /// its data source outside the container.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="dataSource">The host-owned data source the store opens connections from. Never disposed by the store.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperiencePostgresStore(this IServiceCollection services, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSource);

        // A factory rather than a ready-made instance, so a registered ExperienceGrantAuditing is
        // picked up however the two registrations are ordered. Still one singleton either way.
        services.TryAddSingleton<IExperienceRecordStore>(provider =>
            new PostgresExperienceRecordStore(
                dataSource,
                onGrantsUnavailable: null,
                auditing: provider.GetService<ExperienceGrantAuditing>(),
                encryption: provider.GetService<ExperienceEncryption>()));

        return services;
    }

    /// <summary>
    /// Registers <see cref="PostgresExperienceGrantStore"/> as the singleton
    /// <see cref="IExperienceGrantStore"/>, over an <see cref="NpgsqlDataSource"/> resolved from the
    /// container, so a host can administer explicit sharing grants.
    /// </summary>
    /// <remarks>
    /// Registered separately from the store and the candidate source: a host that never shares
    /// anything across scopes needs no grant administration, and the reads that honour grants do so
    /// through their own SQL predicate whether or not this registration is present. The schema is not
    /// applied here -- the grant tables live in <c>0005_create_experience_grants.sql</c>, applied by
    /// <see cref="ExperienceSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/> at
    /// startup like the rest of the schema.
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperiencePostgresGrantStore(this IServiceCollection services) =>
        services.AddAgentExperiencePostgresGrantStore(policy: null);

    /// <summary>
    /// Registers <see cref="PostgresExperienceGrantStore"/> as the singleton
    /// <see cref="IExperienceGrantStore"/> under an explicit policy, over an
    /// <see cref="NpgsqlDataSource"/> resolved from the container.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="policy">
    /// The bounds grants are administered under, chiefly the maximum lifetime a new grant may be
    /// issued with. <see langword="null"/> uses <see cref="PostgresExperienceGrantPolicy.Default"/>, a
    /// 90-day maximum. There is no unbounded option.
    /// </param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperiencePostgresGrantStore(
        this IServiceCollection services,
        PostgresExperienceGrantPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IExperienceGrantStore>(provider =>
            new PostgresExperienceGrantStore(
                provider.GetRequiredService<NpgsqlDataSource>(),
                policy,
                encryption: provider.GetService<ExperienceEncryption>()));

        return services;
    }

    /// <summary>
    /// Registers <see cref="PostgresExperienceGrantStore"/> as the singleton
    /// <see cref="IExperienceGrantStore"/> over <paramref name="dataSource"/>, for a host that keeps
    /// its data source outside the container.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="dataSource">The host-owned data source the grant store opens connections from. Never disposed by the store.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperiencePostgresGrantStore(this IServiceCollection services, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSource);

        // A factory, so a registered ExperienceEncryption is picked up however the registrations are ordered.
        services.TryAddSingleton<IExperienceGrantStore>(provider =>
            new PostgresExperienceGrantStore(dataSource, encryption: provider.GetService<ExperienceEncryption>()));

        return services;
    }

    /// <summary>
    /// Registers <see cref="PostgresExperienceGrantAccessLog"/> as the singleton
    /// <see cref="IExperienceGrantAccessLog"/> and the <see cref="ExperienceGrantAuditing"/> policy the
    /// record store reads, so reads a grant delivers are recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the switch that turns auditing on. Without it the store records nothing, grants work
    /// exactly as before, and <c>0009</c>'s table simply stays empty. Registration order relative to
    /// <see cref="AddAgentExperiencePostgresStore(IServiceCollection)"/> does not matter.
    /// </para>
    /// <para>
    /// What gets recorded is a <em>delivery</em>: a get that a grant permitted (the MAF provider's
    /// pre-injection re-read included), and every grant-permitted record the text and vector channels
    /// return, because a candidate carries the record read back in full. A search's rows are written in
    /// one statement. An owner's own records, and a read declared
    /// <see cref="ExperienceReadPurpose.ScopeCheck"/>, are not deliveries and write nothing.
    /// </para>
    /// <para>
    /// <b>It costs a round trip.</b> Every read that discloses something across a scope boundary does a
    /// second, synchronous write on a pooled connection before it returns, and under
    /// <see cref="ExperienceGrantAuditingMode.Required"/> read availability becomes a function of write
    /// availability. The <c>dataSource</c> overload exists largely so the ledger can have its own pool.
    /// </para>
    /// <para>
    /// <b>It binds the implementation that was registered.</b> These registrations use <c>TryAdd</c>, so
    /// a host that registered its own store, candidate source, or embedding index first keeps it -- and
    /// takes on the obligation to honour this policy itself. Registering the log does not make somebody
    /// else's store audit.
    /// </para>
    /// <para>
    /// The schema is not applied here: <c>0009_grant_access_log.sql</c> is applied by
    /// <see cref="ExperienceSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/> at
    /// startup like the rest of the schema.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="onNotRecorded">
    /// Called when an access row could not be written. Required, not optional: under
    /// <see cref="ExperienceGrantAuditingMode.BestEffort"/> the read still returns, so this is the only
    /// place a missing row is visible.
    /// </param>
    /// <param name="mode">
    /// What a failed write does to the read. <see cref="ExperienceGrantAuditingMode.BestEffort"/>
    /// returns the record anyway; <see cref="ExperienceGrantAuditingMode.Required"/> returns nothing.
    /// </param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperiencePostgresGrantAccessLog(
        this IServiceCollection services,
        Action<ExperienceGrantAccessFailure> onNotRecorded,
        ExperienceGrantAuditingMode mode = ExperienceGrantAuditingMode.BestEffort)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(onNotRecorded);

        services.TryAddSingleton<IExperienceGrantAccessLog>(provider =>
            new PostgresExperienceGrantAccessLog(provider.GetRequiredService<NpgsqlDataSource>()));

        services.TryAddSingleton(provider =>
            new ExperienceGrantAuditing(provider.GetRequiredService<IExperienceGrantAccessLog>(), onNotRecorded, mode));

        return services;
    }

    /// <summary>
    /// Registers <see cref="PostgresExperienceGrantAccessLog"/> and its
    /// <see cref="ExperienceGrantAuditing"/> policy over <paramref name="dataSource"/>, for a host that
    /// keeps its data source outside the container.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="dataSource">The host-owned data source the ledger opens connections from. Never disposed by the log.</param>
    /// <param name="onNotRecorded">Called when an access row could not be written. Required; see the other overload.</param>
    /// <param name="mode">What a failed write does to the read.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperiencePostgresGrantAccessLog(
        this IServiceCollection services,
        NpgsqlDataSource dataSource,
        Action<ExperienceGrantAccessFailure> onNotRecorded,
        ExperienceGrantAuditingMode mode = ExperienceGrantAuditingMode.BestEffort)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(onNotRecorded);

        services.TryAddSingleton<IExperienceGrantAccessLog>(new PostgresExperienceGrantAccessLog(dataSource));

        services.TryAddSingleton(provider =>
            new ExperienceGrantAuditing(provider.GetRequiredService<IExperienceGrantAccessLog>(), onNotRecorded, mode));

        return services;
    }

    /// <summary>
    /// Registers <see cref="PostgresExperienceReuseFeedbackStore"/> as the singleton
    /// <see cref="IExperienceReuseFeedbackStore"/>, over an <see cref="NpgsqlDataSource"/> resolved
    /// from the container, so Core's feedback service has a ledger to write exposure to.
    /// </summary>
    /// <remarks>
    /// Registered separately from the record store: recording reuse feedback is optional, and a host
    /// that never does it never needs the ledger. The schema is not applied here -- the two feedback
    /// tables live in <c>0008_reuse_feedback.sql</c>, applied by
    /// <see cref="ExperienceSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/> at
    /// startup like the rest of the schema.
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperiencePostgresReuseFeedbackStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IExperienceReuseFeedbackStore>(provider =>
            new PostgresExperienceReuseFeedbackStore(
                provider.GetRequiredService<NpgsqlDataSource>(),
                encryption: provider.GetService<ExperienceEncryption>()));

        return services;
    }

    /// <summary>
    /// Registers <see cref="PostgresExperienceReuseFeedbackStore"/> as the singleton
    /// <see cref="IExperienceReuseFeedbackStore"/> over <paramref name="dataSource"/>, for a host that
    /// keeps its data source outside the container.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="dataSource">The host-owned data source the ledger opens connections from. Never disposed by the store.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperiencePostgresReuseFeedbackStore(this IServiceCollection services, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSource);

        // A factory, so a registered ExperienceEncryption is picked up however the registrations are ordered.
        services.TryAddSingleton<IExperienceReuseFeedbackStore>(provider =>
            new PostgresExperienceReuseFeedbackStore(dataSource, encryption: provider.GetService<ExperienceEncryption>()));

        return services;
    }

    /// <summary>
    /// Registers <see cref="PostgresExperienceCandidateSource"/> as the singleton
    /// <see cref="IExperienceCandidateSource"/>, over an <see cref="NpgsqlDataSource"/> resolved from
    /// the container, so Core's retrieval service has something to search.
    /// </summary>
    /// <remarks>
    /// Registered separately from the store: the two are independent ports, and a host that only
    /// writes experience does not need the search index. The schema is not applied here -- the search
    /// column and its index live in <c>0003_add_experience_search.sql</c>, applied by
    /// <see cref="ExperienceSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/> at
    /// startup like the rest of the schema.
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperiencePostgresCandidateSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IExperienceCandidateSource>(provider =>
            new PostgresExperienceCandidateSource(
                provider.GetRequiredService<NpgsqlDataSource>(),
                onGrantsUnavailable: null,
                auditing: provider.GetService<ExperienceGrantAuditing>(),
                encryption: provider.GetService<ExperienceEncryption>()));

        return services;
    }

    /// <summary>
    /// Registers <see cref="PostgresExperienceCandidateSource"/> as the singleton
    /// <see cref="IExperienceCandidateSource"/> over <paramref name="dataSource"/>, for a host that
    /// keeps its data source outside the container.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="dataSource">The host-owned data source the search opens connections from. Never disposed by the source.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static IServiceCollection AddAgentExperiencePostgresCandidateSource(this IServiceCollection services, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSource);

        // A factory rather than a ready-made instance, so a registered ExperienceGrantAuditing is
        // picked up however the two registrations are ordered. Still one singleton either way.
        services.TryAddSingleton<IExperienceCandidateSource>(provider =>
            new PostgresExperienceCandidateSource(
                dataSource,
                onGrantsUnavailable: null,
                auditing: provider.GetService<ExperienceGrantAuditing>(),
                encryption: provider.GetService<ExperienceEncryption>()));

        return services;
    }
}
